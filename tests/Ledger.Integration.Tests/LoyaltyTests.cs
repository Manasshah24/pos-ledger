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

public class LoyaltyTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;

    public LoyaltyTests(PostgresFixture pg) => _pg = pg;

    private static IncomingEvent Earn(string id, string store, string account, long points) =>
        new(id, store, store + "-T1", EventType.LoyaltyEarn, "2026-01-05T12:00:00Z", null, null, AccountId: account, Points: points);

    private static IncomingEvent Burn(string id, string store, string account, long points) =>
        new(id, store, store + "-T1", EventType.LoyaltyBurn, "2026-01-05T12:30:00Z", null, null, AccountId: account, Points: points);

    private async Task<NpgsqlDataSource> NewDb()
    {
        var db = await _pg.NewMigratedDataSourceAsync();
        await RegisterStoresAsync(db, new StoreInfo("S1", "UTC"), new StoreInfo("S2", "Asia/Kolkata"), new StoreInfo("S3", "UTC"));
        return db;
    }

    [Fact]
    public async Task Balances_converge_whatever_order_stores_report_and_however_often_events_repeat()
    {
        for (ulong seed = 1; seed <= 6; seed++)
        {
            await using var db = await NewDb();
            var rng = new SplitMix64(seed);
            var universe = Enumerable.Range(0, 80).Select(i => rng.Next(2) == 0
                ? Earn($"e{i:D3}", $"S{rng.Next(3) + 1}", $"acct-{rng.Next(6)}", rng.Range(1, 500))
                : Burn($"e{i:D3}", $"S{rng.Next(3) + 1}", $"acct-{rng.Next(6)}", rng.Range(1, 500))).ToList();

            // Cover every event, add random repeats, shuffle, cut into random batches, upload concurrently through two replicas.
            var stream = universe.Concat(Enumerable.Range(0, rng.Next(120)).Select(_ => universe[rng.Next(universe.Count)])).ToList();
            rng.Shuffle(stream);
            var batches = new List<List<RawEvent>>();
            for (var i = 0; i < stream.Count;)
            {
                var size = rng.Next(9) + 1;
                batches.Add(stream.Skip(i).Take(size).Select((e, k) => Raw(k, e)).ToList());
                i += size;
            }
            var replicas = new[] { Ingestor(db), Ingestor(db) };
            await Parallel.ForEachAsync(batches.Select((b, i) => (b, i)), new ParallelOptions { MaxDegreeOfParallelism = 8 },
                async (x, _) => await replicas[x.i % 2].IngestAsync(x.b));

            var expected = universe.GroupBy(e => e.AccountId!).ToDictionary(
                g => g.Key,
                g => (Earned: g.Where(e => e.Type == EventType.LoyaltyEarn).Sum(e => e.Points!.Value), Burned: g.Where(e => e.Type == EventType.LoyaltyBurn).Sum(e => e.Points!.Value)));
            var loyalty = new LoyaltyService(db, new ManualClock(Noon), new LedgerOptions());
            foreach (var (account, (earned, burned)) in expected)
                Assert.Equal(new LoyaltyView(account, earned, burned, earned - burned), await loyalty.GetAsync(account));
        }
    }

    [Fact]
    public async Task A_balance_that_goes_negative_offline_is_reported_repaid_by_later_earns_and_blocks_online_burns()
    {
        await using var db = await NewDb();
        var ingestor = Ingestor(db);
        var loyalty = new LoyaltyService(db, new ManualClock(Noon), new LedgerOptions());
        await ingestor.IngestAsync(Raws(Earn("e1", "S1", "a1", 100)));

        // Two stores, both offline, each burn 80 against what they believed was a balance of 100.
        await ingestor.IngestAsync(Raws(Burn("b1", "S1", "a1", 80)));
        await ingestor.IngestAsync(Raws(Burn("b2", "S2", "a1", 80)));

        Assert.Equal(-60, (await loyalty.GetAsync("a1"))!.BalancePoints); // accepted, not refused: the purchases already happened
        Assert.Equal(["a1"], (await loyalty.NegativeAsync()).Select(a => a.AccountId));
        Assert.Equal(BurnStatus.Declined, (await loyalty.BurnOnlineAsync("on0", "S1", "T1", "a1", 10, null)).Status); // cannot spend while in debt

        await ingestor.IngestAsync(Raws(Earn("e2", "S3", "a1", 100))); // a later earn pays the debt down by itself

        Assert.Equal(40, (await loyalty.GetAsync("a1"))!.BalancePoints);
        Assert.Empty(await loyalty.NegativeAsync());
        Assert.Equal(BurnStatus.Approved, (await loyalty.BurnOnlineAsync("on1", "S1", "T1", "a1", 30, null)).Status);
        Assert.Equal(BurnStatus.Replayed, (await loyalty.BurnOnlineAsync("on1", "S1", "T1", "a1", 30, null)).Status); // retry takes nothing more
        Assert.Equal(BurnStatus.Conflict, (await loyalty.BurnOnlineAsync("on1", "S1", "T1", "a1", 31, null)).Status);
        Assert.Equal(10, (await loyalty.GetAsync("a1"))!.BalancePoints);
        Assert.Equal(BurnStatus.AccountNotFound, (await loyalty.BurnOnlineAsync("on2", "S1", "T1", "ghost", 1, null)).Status);
    }

    [Fact]
    public async Task Online_burns_never_overdraw_an_account_under_concurrency()
    {
        await using var db = await NewDb();
        var loyalty = new LoyaltyService(db, new ManualClock(Noon), new LedgerOptions());
        await Ingestor(db).IngestAsync(Raws(Earn("e1", "S1", "a1", 100)));
        var results = new System.Collections.Concurrent.ConcurrentBag<BurnStatus>();

        await Parallel.ForEachAsync(Enumerable.Range(0, 40), new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (i, _) => results.Add((await loyalty.BurnOnlineAsync($"on-{i}", "S1", "T1", "a1", 10, null)).Status));

        Assert.Equal(10, results.Count(r => r == BurnStatus.Approved));
        Assert.Equal(30, results.Count(r => r == BurnStatus.Declined));
        Assert.Equal(0, (await loyalty.GetAsync("a1"))!.BalancePoints);
    }

    [Fact]
    public async Task Loyalty_balances_are_part_of_the_rebuild_and_survive_losing_the_read_model()
    {
        await using var db = await NewDb();
        var ingestor = Ingestor(db);
        var rebuilder = new Rebuilder(db, new ManualClock(Noon));
        await ingestor.IngestAsync(Raws(
            Incoming("sale1", "S1", amount: 1000, sale: "s1"), Earn("e1", "S1", "a1", 100), Earn("e2", "S2", "a1", 50),
            Burn("b1", "S3", "a1", 400), Earn("e3", "S3", "a2", 7)));

        var live = await rebuilder.HashAsync();
        await using (var conn = await db.OpenConnectionAsync()) await conn.ExecuteAsync("TRUNCATE loyalty_accounts, store_day_totals");
        var rebuilt = await rebuilder.RebuildAsync();

        Assert.False(rebuilt.Identical); // the wipe was real
        Assert.Equal(live, rebuilt.After); // and the log restores every account and every day exactly
        Assert.Equal(-250, (await new LoyaltyService(db, new ManualClock(Noon), new LedgerOptions()).GetAsync("a1"))!.BalancePoints);
        Assert.True((await rebuilder.RebuildAsync()).Identical);
    }

    [Fact]
    public async Task The_http_api_takes_loyalty_events_and_serves_balances_and_the_negative_list()
    {
        var cs = await _pg.NewDatabaseAsync();
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Ledger", cs));
        var client = app.CreateClient();
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await client.PutAsJsonAsync("/stores/S1", new { tz = "UTC" });

        var batch = JsonSerializer.Serialize(new { events = new object[]
        {
            new { eventId = "e1", storeId = "S1", terminalId = "T1", type = "loyalty_earn", occurredAt = "2026-01-05T10:00:00Z", accountId = "a1", points = 100 },
            new { eventId = "b1", storeId = "S1", terminalId = "T1", type = "loyalty_burn", occurredAt = "2026-01-05T11:00:00Z", accountId = "a1", points = 130 },
            new { eventId = "bad", storeId = "S1", terminalId = "T1", type = "loyalty_burn", occurredAt = "2026-01-05T11:00:00Z", points = 5 },
        } }, web);
        var posted = await (await client.PostAsync("/events/batch", new StringContent(batch, Encoding.UTF8, "application/json"))).Content.ReadAsStringAsync();

        Assert.Contains("missing_account_id", posted);
        Assert.Equal(-30, (await client.GetFromJsonAsync<LoyaltyView>("/loyalty/a1", web))!.BalancePoints);
        Assert.Equal(["a1"], (await client.GetFromJsonAsync<List<LoyaltyView>>("/loyalty/negative", web))!.Select(a => a.AccountId));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/loyalty/a1/burn", new { eventId = "on1", storeId = "S1", terminalId = "T1", points = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/loyalty/nobody")).StatusCode);
    }
}
