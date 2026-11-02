using System.Net.Http.Json;
using Dapper;
using Ledger.Core;
using Ledger.Core.Events;
using Ledger.Data;
using Ledger.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using static Ledger.Integration.Tests.IngestHelpers;

namespace Ledger.Integration.Tests;

public class DayCloseTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;

    public DayCloseTests(PostgresFixture pg) => _pg = pg;

    private static readonly DateOnly Jan5 = new(2026, 1, 5);
    private static readonly DateOnly Jan6 = new(2026, 1, 6);
    private static readonly DateOnly Jan7 = new(2026, 1, 7);

    private async Task<NpgsqlDataSource> DbWithStore()
    {
        var db = await _pg.NewMigratedDataSourceAsync();
        await RegisterStoresAsync(db, new StoreInfo("S1", "UTC"));
        return db;
    }

    private static DayCloser Closer(NpgsqlDataSource db, IClock? clock = null) => new(db, clock ?? new ManualClock(Noon), new LedgerOptions());

    private static IncomingEvent Sale(string id, string sale, long amount, string at) => Incoming(id, type: EventType.Sale, amount: amount, sale: sale, at: at);
    private static IncomingEvent Return(string id, string sale, long amount, string at) => Incoming(id, type: EventType.Return, amount: amount, sale: sale, at: at);

    private static (long Sales, long Returns, long Voids, long Net) Own(StoreDayTotal t) => (t.SalesMinor, t.ReturnsMinor, t.VoidsMinor, t.NetMinor);

    [Fact]
    public async Task Late_events_for_a_closed_day_become_adjustments_in_the_next_open_day_and_the_closed_day_never_changes()
    {
        await using var db = await DbWithStore();
        var ingestor = Ingestor(db);
        var totals = new TotalsQuery(db);
        await ingestor.IngestAsync(Raws(Sale("e1", "s1", 1000, "2026-01-05T10:00:00Z"), Sale("e2", "s2", 500, "2026-01-05T10:30:00Z")));

        var close = await Closer(db).CloseThroughAsync("S1", Jan5);
        var snapshot = Assert.Single(await totals.GetAsync());

        Assert.True(close!.Changed);
        Assert.True(snapshot.Closed);
        Assert.Equal(1500, snapshot.NetMinor);

        // A new sale and a return of an existing sale, both for the closed day, plus a normal event for the next day.
        await ingestor.IngestAsync(Raws(
            Sale("e3", "s3", 200, "2026-01-05T11:00:00Z"),
            Return("r1", "s1", 100, "2026-01-05T12:00:00Z"),
            Sale("e4", "s4", 70, "2026-01-06T09:00:00Z")));

        var rows = await totals.GetAsync();
        var day5 = rows.Single(r => r.BusinessDate == Jan5);
        var day6 = rows.Single(r => r.BusinessDate == Jan6);

        Assert.Equal(Own(snapshot), Own(day5)); // the closed day's stored total did not move
        Assert.Equal(100, day5.CorrectionsMinor); // +200 sale, -100 return belong to it
        Assert.Equal(1600, day5.CorrectedNetMinor);
        Assert.Equal(70, day6.NetMinor); // its own, on-time event
        Assert.Equal(100, day6.AdjustmentsInMinor); // the late amounts landed here
        Assert.Equal(170, day6.ReportedNetMinor);
        Assert.False(day6.Closed);

        await using var conn = await db.OpenConnectionAsync();
        var links = (await conn.QueryAsync<(string e, DateOnly orig, DateOnly applied)>(
            "SELECT event_id, original_date, applied_date FROM adjustments ORDER BY event_id")).ToList();
        Assert.Equal([("e3", Jan5, Jan6), ("r1", Jan5, Jan6)], links); // each adjustment is linked to its original day
    }

    [Fact]
    public async Task A_late_event_lands_in_the_next_open_day_even_when_several_days_have_closed()
    {
        await using var db = await DbWithStore();
        var ingestor = Ingestor(db);
        await ingestor.IngestAsync(Raws(Sale("e1", "s1", 1000, "2026-01-05T10:00:00Z")));
        await Closer(db).CloseThroughAsync("S1", Jan6);

        await ingestor.IngestAsync(Raws(Sale("late", "s2", 500, "2026-01-05T11:00:00Z")));

        var day7 = Assert.Single(await new TotalsQuery(db).GetAsync(), r => r.BusinessDate == Jan7);
        Assert.Equal(500, day7.AdjustmentsInMinor);
    }

    [Fact]
    public async Task Closing_is_idempotent_never_reopens_and_unknown_stores_are_reported()
    {
        await using var db = await DbWithStore();
        var closer = Closer(db);

        Assert.True((await closer.CloseThroughAsync("S1", Jan6))!.Changed);
        Assert.False((await closer.CloseThroughAsync("S1", Jan6))!.Changed);
        var earlier = await closer.CloseThroughAsync("S1", Jan5);

        Assert.False(earlier!.Changed);
        Assert.Equal(Jan6, earlier.ClosedThrough); // closing an earlier date does not reopen later days
        Assert.Null(await closer.CloseThroughAsync("NOPE", Jan5));
    }

    [Fact]
    public async Task The_database_itself_refuses_to_change_a_closed_days_totals()
    {
        await using var db = await DbWithStore();
        await Ingestor(db).IngestAsync(Raws(Sale("e1", "s1", 1000, "2026-01-05T10:00:00Z")));
        await Closer(db).CloseThroughAsync("S1", Jan5);
        await using var conn = await db.OpenConnectionAsync();

        var update = await Assert.ThrowsAsync<PostgresException>(() =>
            conn.ExecuteAsync("UPDATE store_day_totals SET sales_minor = 0 WHERE store_id = 'S1'"));
        var insert = await Assert.ThrowsAsync<PostgresException>(() =>
            conn.ExecuteAsync("INSERT INTO store_day_totals (store_id, business_date) VALUES ('S1', '2026-01-04')"));

        Assert.Equal("23514", update.SqlState);
        Assert.Equal("23514", insert.SqlState);
    }

    [Fact]
    public async Task A_return_parked_before_the_close_and_released_after_it_is_booked_as_an_adjustment()
    {
        await using var db = await DbWithStore();
        var ingestor = Ingestor(db);
        await ingestor.IngestAsync(Raws(Return("r1", "s1", 300, "2026-01-05T12:00:00Z"))); // sale not here yet
        await Closer(db).CloseThroughAsync("S1", Jan5);

        await ingestor.IngestAsync(Raws(Sale("e1", "s1", 1000, "2026-01-05T10:00:00Z"))); // sale finally arrives, late

        var rows = await new TotalsQuery(db).GetAsync();
        Assert.Equal(700, rows.Single(r => r.BusinessDate == Jan5).CorrectedNetMinor);
        Assert.Equal(0, rows.Single(r => r.BusinessDate == Jan5).NetMinor); // closed day untouched
        Assert.Equal(700, rows.Single(r => r.BusinessDate == Jan6).AdjustmentsInMinor);
        Assert.Empty(await new ParkedQuery(db).GetOpenAsync());
    }

    [Fact]
    public async Task Closing_while_two_replicas_are_ingesting_freezes_exactly_what_was_committed_before_the_close()
    {
        await using var db = await DbWithStore();
        var replicas = new[] { Ingestor(db), Ingestor(db) };
        var totals = new TotalsQuery(db);
        var batches = Enumerable.Range(0, 300)
            .Select(b => Raws(Enumerable.Range(0, 10).Select(i => Sale($"b{b:D3}-{i}", $"s{b:D3}-{i}", 100, "2026-01-05T10:00:00Z")).ToArray()))
            .ToList(); // 3,000 events of 100 for the same day = 300,000
        var completed = 0;

        // Opening a brand-new connection to the local Postgres takes over a second, longer than the whole ingest run.
        // Keep eight connections warm in the pool so the close starts promptly instead of arriving after the last batch.
        var warm = new List<NpgsqlConnection>();
        for (var i = 0; i < 8; i++) warm.Add(await db.OpenConnectionAsync());
        foreach (var c in warm) await c.DisposeAsync();

        var ingest = Parallel.ForEachAsync(batches.Select((b, i) => (b, i)), new ParallelOptions { MaxDegreeOfParallelism = 6 }, async (x, _) =>
        {
            await replicas[x.i % 2].IngestAsync(x.b);
            Interlocked.Increment(ref completed);
        });
        while (Volatile.Read(ref completed) < 5) await Task.Yield(); // let a few batches land, then close in the middle of the run

        await Closer(db).CloseThroughAsync("S1", Jan5);
        var frozen = Own(Assert.Single(await totals.GetAsync(), r => r.BusinessDate == Jan5));
        await ingest;

        var rows = await totals.GetAsync();
        var day5 = rows.Single(r => r.BusinessDate == Jan5);
        Assert.Equal(frozen, Own(day5)); // nothing slipped into the day after it closed
        Assert.InRange(frozen.Net, 1, 299_999); // the close really did land in the middle of the run
        Assert.Equal(300_000, day5.CorrectedNetMinor); // and nothing was lost: the rest arrived as adjustments
        Assert.Equal(300_000, rows.Sum(r => r.ReportedNetMinor)); // reported across days adds up to the same money
    }

    [Fact]
    public async Task Close_due_closes_each_store_once_its_own_day_has_ended()
    {
        await using var db = await _pg.NewMigratedDataSourceAsync();
        await RegisterStoresAsync(db, new StoreInfo("UTC1", "UTC"), new StoreInfo("KOL", "Asia/Kolkata"));
        // 2026-01-06 20:45 UTC: Kolkata is 02:15 on the 7th (its 6th just ended); UTC is 20:45 on the 6th (its 5th ended).
        var closer = Closer(db, new ManualClock(new DateTimeOffset(2026, 1, 6, 20, 45, 0, TimeSpan.Zero)));

        var closed = await closer.CloseDueAsync();

        Assert.Equal([("KOL", Jan6), ("UTC1", Jan5)], closed.Select(c => (c.StoreId, c.ClosedThrough)).OrderBy(c => c.StoreId));
        Assert.Empty(await closer.CloseDueAsync()); // nothing new is due
    }

    private sealed record Closed(string StoreId, DateOnly ClosedThrough, bool Changed);

    private static WebApplicationFactory<Program> Replica(string cs) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Ledger", cs));

    [Fact]
    public async Task Harness_stream_with_a_mid_run_close_keeps_closed_days_frozen_and_corrected_totals_match_the_oracle()
    {
        var data = Generator.Generate(new GeneratorOptions { Seed = 77, TotalDeliveries = 10_000, Stores = 50 });
        var cs = await _pg.NewDatabaseAsync();
        await using var a = Replica(cs);
        await using var b = Replica(cs);
        var clients = new[] { a.CreateClient(), b.CreateClient() };
        foreach (var s in data.Stores) await clients[0].PutAsJsonAsync($"/stores/{s.StoreId}", new { tz = s.TimeZoneId });

        var batches = data.ToBatches(250).Select(x => ToBatchJson(x.Select(d => d.Event))).ToList();
        var split = batches.Count * 6 / 10;
        async Task Send(IEnumerable<string> bodies) => await Parallel.ForEachAsync(bodies.Select((body, i) => (body, i)),
            new ParallelOptions { MaxDegreeOfParallelism = 6 },
            async (x, _) => (await clients[x.i % 2].PostAsync("/events/batch",
                new StringContent(x.body, System.Text.Encoding.UTF8, "application/json"))).EnsureSuccessStatusCode());

        await Send(batches.Take(split));
        foreach (var s in data.Stores) (await clients[0].PostAsync($"/admin/stores/{s.StoreId}/close?through=2026-01-06", null)).EnsureSuccessStatusCode();
        var before = (await clients[0].GetFromJsonAsync<List<StoreDayTotal>>("/totals"))!;
        await Send(batches.Skip(split));
        var after = (await clients[0].GetFromJsonAsync<List<StoreDayTotal>>("/totals"))!;

        // Every closed day is exactly as it was when it closed.
        var frozen = before.Where(t => t.BusinessDate <= Jan6).ToDictionary(t => (t.StoreId, t.BusinessDate), Own);
        Assert.NotEmpty(frozen);
        Assert.All(after.Where(t => t.BusinessDate <= Jan6 && frozen.ContainsKey((t.StoreId, t.BusinessDate))),
            t => Assert.Equal(frozen[(t.StoreId, t.BusinessDate)], Own(t)));

        // Late events did arrive for closed days, and with them every store-day equals the oracle's answer.
        Assert.Contains(after, t => t.CorrectionsMinor != 0);
        var oracle = Oracle.NetTotals(data.UniqueEvents, data.Stores);
        var served = new SortedDictionary<StoreDay, long>();
        foreach (var t in after)
        {
            if (oracle.ContainsKey(new StoreDay(t.StoreId, t.BusinessDate))) served[new StoreDay(t.StoreId, t.BusinessDate)] = t.CorrectedNetMinor;
            else Assert.Equal((0, 0), (t.NetMinor, t.CorrectionsMinor)); // a day that only received adjustments has no events of its own
        }

        Assert.Equal(Oracle.Hash(oracle), Oracle.Hash(served));
        Assert.Equal(after.Sum(t => t.CorrectedNetMinor), after.Sum(t => t.ReportedNetMinor)); // no money created or lost
        Assert.Empty((await clients[0].GetFromJsonAsync<List<ParkedEvent>>("/parked"))!);
    }
}
