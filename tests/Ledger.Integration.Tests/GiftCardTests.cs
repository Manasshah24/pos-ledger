using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Ledger.Core;
using Ledger.Core.Events;
using Ledger.Data;
using Ledger.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using static Ledger.Integration.Tests.IngestHelpers;

namespace Ledger.Integration.Tests;

public class GiftCardTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;

    public GiftCardTests(PostgresFixture pg) => _pg = pg;

    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(6);

    private sealed record Rig(NpgsqlDataSource Db, ManualClock Clock, GiftCardService Cards, EventIngestor Ingestor);

    private async Task<Rig> NewRig(long limit, params string[] stores)
    {
        var db = await _pg.NewMigratedDataSourceAsync();
        await RegisterStoresAsync(db, stores.Select(s => new StoreInfo(s, "UTC")).ToArray());
        var clock = new ManualClock(T0);
        var options = new LedgerOptions { OfflineUnsecuredLimitMinor = limit };
        return new Rig(db, clock, new GiftCardService(db, clock, options), new EventIngestor(db, clock, options));
    }

    private static IncomingEvent Redeem(string id, string store, string card, long? lease, long amount, string? at = null) =>
        new(id, store, store + "-T1", EventType.GiftcardRedeem, at ?? "2026-01-05T12:10:00Z", null, amount, card, lease);

    private static async Task<long> Deficit(NpgsqlDataSource db, string card)
    {
        await using var conn = await db.OpenConnectionAsync();
        return Math.Max(0, -await conn.ExecuteScalarAsync<long>("SELECT balance_minor FROM giftcards WHERE card_id = @card", new { card }));
    }

    [Fact]
    public async Task Online_redemption_is_exact_under_concurrency_and_idempotent()
    {
        var rig = await NewRig(0, "S1");
        await rig.Cards.IssueAsync("c1", 5000);

        // 100 attempts, 16 at a time: opening a new connection to this local Postgres is slow, so a flood of 100 at once
        // would time out on connecting rather than test the redemption logic.
        var results = new System.Collections.Concurrent.ConcurrentBag<(int I, RedeemResult R)>();
        await Parallel.ForEachAsync(Enumerable.Range(0, 100), new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (i, _) => results.Add((i, await rig.Cards.RedeemOnlineAsync($"on-{i}", "S1", "T1", "c1", 100, null))));

        Assert.Equal(50, results.Count(r => r.R.Status == RedeemStatus.Approved));
        Assert.Equal(50, results.Count(r => r.R.Status == RedeemStatus.Declined));
        Assert.Equal(0, (await rig.Cards.GetAsync("c1"))!.BalanceMinor); // never below zero, never a cent left over

        var approved = results.First(x => x.R.Status == RedeemStatus.Approved).I;
        var replay = await rig.Cards.RedeemOnlineAsync($"on-{approved}", "S1", "T1", "c1", 100, null);
        var reused = await rig.Cards.RedeemOnlineAsync($"on-{approved}", "S1", "T1", "c1", 999, null);

        Assert.Equal(RedeemStatus.Replayed, replay.Status);
        Assert.Equal(RedeemStatus.Conflict, reused.Status); // same id, different money: refused
        Assert.Equal(0, (await rig.Cards.GetAsync("c1"))!.BalanceMinor); // the replay took nothing more
        Assert.Equal(50, await CountAsync(rig.Db, "events"));
    }

    [Fact]
    public async Task Concurrent_lease_requests_never_reserve_more_than_the_balance_plus_the_overspend_limit()
    {
        var rig = await NewRig(2000, "S1", "S2", "S3", "S4", "S5");
        await rig.Cards.IssueAsync("c1", 5000);

        var grants = (await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            Task.Run(() => rig.Cards.LeaseAsync("c1", $"S{i % 5 + 1}", 1000, Ttl))))).Where(r => r.Outcome == LeaseOutcome.Granted).Select(r => r.Grant!).ToList();

        Assert.Equal(5000, grants.Sum(g => g.SecuredMinor)); // exactly the balance, never more
        Assert.Equal(2000, grants.Sum(g => g.UnsecuredMinor)); // exactly the limit, never more
        var card = (await rig.Cards.GetAsync("c1"))!;
        Assert.Equal(0, card.AvailableMinor);
        Assert.Equal(RedeemStatus.Declined, (await rig.Cards.RedeemOnlineAsync("x", "S1", "T1", "c1", 1, null)).Status); // all of it is reserved offline
    }

    [Fact]
    public async Task Offline_overspend_stops_at_the_limit_and_every_rule_break_is_flagged()
    {
        var rig = await NewRig(300, "A", "B", "C");
        await rig.Cards.IssueAsync("c1", 1000);
        var a = (await rig.Cards.LeaseAsync("c1", "A", 800, Ttl)).Grant!; // secured 800
        var b = (await rig.Cards.LeaseAsync("c1", "B", 700, Ttl)).Grant!; // secured 200 (what is left) + unsecured 300 (the limit)
        Assert.Equal((800, 0, 200, 300), (a.SecuredMinor, a.UnsecuredMinor, b.SecuredMinor, b.UnsecuredMinor));

        // Both stores spend everything they were leased while offline; store C has no lease at all.
        var result = await rig.Ingestor.IngestAsync(Raws(
            Redeem("a1", "A", "c1", a.LeaseId, 800), Redeem("b1", "B", "c1", b.LeaseId, 500), Redeem("c-rogue", "C", "c1", null, 100)));

        Assert.Equal(3, result.Inserted);
        Assert.Equal(400, await Deficit(rig.Db, "c1")); // limit 300 + the 100 the unleased store took
        var flags = await rig.Cards.FlagsAsync("c1");
        Assert.Equal(400, flags.Where(f => f.Kind == "overspend").Sum(f => f.AmountMinor)); // every cent of overspend is flagged
        Assert.Equal([("c-rogue", "no_lease", 100)], flags.Where(f => f.Kind == "no_lease").Select(f => (f.EventId, f.Kind, f.AmountMinor))); // and the cause
        Assert.DoesNotContain(flags, f => f.Kind is "exceeds_lease" or "expired_lease"); // the two compliant stores broke nothing else

        // Spending beyond a lease, and after it expired, is flagged for what it is.
        var late = await rig.Ingestor.IngestAsync(Raws(
            Redeem("b2", "B", "c1", b.LeaseId, 50),
            Redeem("a2", "A", "c1", a.LeaseId, 10, at: "2026-01-05T19:00:00Z"))); // 19:00 is after the 18:00 expiry
        Assert.Equal(2, late.Inserted);
        flags = await rig.Cards.FlagsAsync("c1");
        Assert.Contains(flags, f => f is { EventId: "b2", Kind: "exceeds_lease", AmountMinor: 50 });
        Assert.Contains(flags, f => f is { EventId: "a2", Kind: "expired_lease", AmountMinor: 10 });
        Assert.Equal(await Deficit(rig.Db, "c1"), flags.Where(f => f.Kind == "overspend").Sum(f => f.AmountMinor));
    }

    [Fact]
    public async Task Forty_cards_redeemed_at_two_to_five_offline_stores_at_once_stay_within_the_limit_and_every_overspend_is_flagged()
    {
        const long balance = 3000, limit = 1000;
        var rig = await NewRig(limit, "S1", "S2", "S3", "S4", "S5");
        var rng = new SplitMix64(2026);
        var cards = Enumerable.Range(0, 40).Select(i => $"card-{i:D2}").ToList();
        foreach (var c in cards) await rig.Cards.IssueAsync(c, balance);

        // Phase 1: every store of every card asks for a big lease at the same time (they compete for the same money).
        var requests = cards.SelectMany(c => Enumerable.Range(1, 2 + rng.Next(4)).Select(s => (Card: c, Store: $"S{s}"))).ToList();
        var leaseResults = new System.Collections.Concurrent.ConcurrentBag<LeaseResult>();
        await Parallel.ForEachAsync(requests, new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (r, _) => leaseResults.Add(await rig.Cards.LeaseAsync(r.Card, r.Store, 2500, Ttl)));
        var granted = leaseResults.Where(x => x.Outcome == LeaseOutcome.Granted).Select(x => x.Grant!).OrderBy(g => g.LeaseId).ToList();

        foreach (var card in cards)
        {
            var mine = granted.Where(g => g.CardId == card).ToList();
            Assert.True(mine.Sum(g => g.SecuredMinor) <= balance); // the money is never promised twice
            Assert.True(mine.Sum(g => g.UnsecuredMinor) <= limit);
        }

        // Phase 2: every store spends its whole lease offline in three pieces. Every eighth card also has a rogue terminal that
        // spends 700 more than it was leased. Events are duplicated, shuffled and uploaded concurrently through two replicas.
        var events = new List<IncomingEvent>();
        foreach (var g in granted)
        {
            var total = g.SecuredMinor + g.UnsecuredMinor;
            var rogue = int.Parse(g.CardId[5..]) % 8 == 0 && g == granted.First(x => x.CardId == g.CardId);
            var pieces = new[] { total / 3, total / 3, total - 2 * (total / 3) }.Where(p => p > 0).ToList();
            if (rogue) pieces.Add(700);
            events.AddRange(pieces.Select((p, i) => Redeem($"{g.LeaseId}-{i}", g.StoreId, g.CardId, g.LeaseId, p)));
        }
        var stream = events.Concat(events.Where((_, i) => i % 5 == 0)).ToList(); // 20% repeats
        rng.Shuffle(stream);
        var ingestors = new[] { rig.Ingestor, new EventIngestor(rig.Db, rig.Clock, new LedgerOptions { OfflineUnsecuredLimitMinor = limit }) };
        var batches = stream.Chunk(7).Select(c => Raws(c)).ToList();
        await Parallel.ForEachAsync(batches.Select((b, i) => (b, i)), new ParallelOptions { MaxDegreeOfParallelism = 8 },
            async (x, _) => await ingestors[x.i % 2].IngestAsync(x.b));

        Assert.Equal(events.Count, await CountAsync(rig.Db, "events")); // repeats did not double-spend anything
        var flags = await rig.Cards.FlagsAsync();
        await using var conn = await rig.Db.OpenConnectionAsync();
        foreach (var card in cards)
        {
            var current = (await rig.Cards.GetAsync(card))!;
            var spent = await conn.ExecuteScalarAsync<long>("SELECT coalesce(sum(amount_minor), 0) FROM events WHERE card_id = @card", new { card });
            Assert.Equal(balance - spent, current.BalanceMinor); // the balance is exactly what the log says

            var mine = flags.Where(f => f.CardId == card).ToList();
            Assert.Equal(current.DeficitMinor, mine.Where(f => f.Kind == "overspend").Sum(f => f.AmountMinor)); // every overspend flagged, none invented
            if (mine.Any(f => f.Kind == "exceeds_lease"))
                Assert.True(current.DeficitMinor > 0); // a terminal that broke the rules is visible, and only those cards may exceed the bound
            else
                Assert.True(current.DeficitMinor <= limit, $"{card} overspent by {current.DeficitMinor}, limit {limit}"); // the hard bound
        }

        Assert.Contains(flags, f => f.Kind == "exceeds_lease"); // the rogue terminals were caught
        Assert.Contains(flags, f => f.Kind == "overspend"); // and legitimate overspend actually happened, so the bound was exercised

        // Gift redemptions are not revenue: rebuilding the totals from the log still works and nothing about them changed.
        Assert.True((await new Rebuilder(rig.Db).RebuildAsync()).Identical);
    }

    [Fact]
    public async Task Leased_money_stays_reserved_until_the_lease_expires_plus_the_maximum_upload_delay()
    {
        var rig = await NewRig(0, "S1");
        await rig.Cards.IssueAsync("c1", 1000);
        await rig.Cards.LeaseAsync("c1", "S1", 1000, TimeSpan.FromHours(1));

        rig.Clock.Advance(TimeSpan.FromHours(30)); // lease expired 29h ago, but the store may still upload what it spent
        Assert.Equal(RedeemStatus.Declined, (await rig.Cards.RedeemOnlineAsync("e1", "S1", "T1", "c1", 100, null)).Status);

        rig.Clock.Advance(TimeSpan.FromHours(20)); // now past expiry + 48h
        Assert.Equal(RedeemStatus.Approved, (await rig.Cards.RedeemOnlineAsync("e2", "S1", "T1", "c1", 100, null)).Status);
    }

    [Fact]
    public async Task The_http_api_walks_issue_lease_offline_batch_and_flags()
    {
        var cs = await _pg.NewDatabaseAsync();
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Ledger", cs));
        var client = app.CreateClient();
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await client.PutAsJsonAsync("/stores/S1", new { tz = "UTC" });

        Assert.Equal(HttpStatusCode.Created, (await client.PutAsJsonAsync("/giftcards/c1", new { balanceMinor = 1000 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/giftcards/c1", new { balanceMinor = 1000 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/giftcards/c1", new { balanceMinor = 5 })).StatusCode);

        var lease = await (await client.PostAsJsonAsync("/giftcards/c1/leases", new { storeId = "S1", amountMinor = 600 })).Content.ReadFromJsonAsync<LeaseGrant>(web);
        Assert.Equal(600, lease!.SecuredMinor);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/giftcards/c1/redeem",
            new { eventId = "on1", storeId = "S1", terminalId = "T1", amountMinor = 500 })).StatusCode); // only 400 is unreserved
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/giftcards/c1/redeem",
            new { eventId = "on2", storeId = "S1", terminalId = "T1", amountMinor = 400 })).StatusCode);

        var batch = JsonSerializer.Serialize(new { events = new[] { Redeem("off1", "S1", "c1", lease.LeaseId, 600, at: DateTimeOffset.UtcNow.ToString("O")), Redeem("bad", "S1", "c1", null, 1) with { CardId = null } } }, web);
        var posted = await (await client.PostAsync("/events/batch", new StringContent(batch, Encoding.UTF8, "application/json"))).Content.ReadAsStringAsync();
        Assert.Contains("missing_card_id", posted);

        var card = await client.GetFromJsonAsync<CardView>("/giftcards/c1", web);
        Assert.Equal(0, card!.BalanceMinor);
        Assert.Empty((await client.GetFromJsonAsync<List<GiftCardFlag>>("/giftcards/flags", web))!); // fully compliant: nothing to flag
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/giftcards/nope")).StatusCode);
    }
}
