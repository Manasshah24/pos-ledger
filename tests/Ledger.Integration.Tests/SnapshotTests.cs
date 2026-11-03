using Dapper;
using Ledger.Core;
using Ledger.Core.Events;
using Ledger.Data;
using Ledger.Harness;
using Npgsql;
using static Ledger.Integration.Tests.IngestHelpers;

namespace Ledger.Integration.Tests;

public class SnapshotTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;

    public SnapshotTests(PostgresFixture pg) => _pg = pg;

    private static IncomingEvent Loyalty(string id, string store, string type, string account, long points) =>
        new(id, store, store + "-T1", type, "2026-01-05T12:00:00Z", null, null, AccountId: account, Points: points);

    private static async Task Wipe(NpgsqlDataSource db)
    {
        await using var conn = await db.OpenConnectionAsync();
        await conn.ExecuteAsync("TRUNCATE store_day_totals, adjustments, parked_events, loyalty_accounts");
    }

    [Fact]
    public async Task Rebuilding_from_a_snapshot_gives_the_same_result_as_rebuilding_from_the_whole_log_while_replaying_only_the_tail()
    {
        var data = Generator.Generate(new GeneratorOptions { Seed = 313, TotalDeliveries = 10_000, Stores = 50 });
        var db = await _pg.NewMigratedDataSourceAsync();
        await RegisterStoresAsync(db, data.Stores.ToArray());
        var clock = new ManualClock(Noon);
        var ingestor = Ingestor(db, clock);
        var closer = new DayCloser(db, clock, new LedgerOptions());
        var rebuilder = new Rebuilder(db, clock);
        var batches = data.ToBatches(250).Select(b => Raws(b.Select(d => ToIncoming(d.Event)).ToArray())).ToList();

        async Task Send(IEnumerable<List<RawEvent>> part) => await Parallel.ForEachAsync(part, new ParallelOptions { MaxDegreeOfParallelism = 6 },
            async (b, _) => await ingestor.IngestAsync(b));
        async Task CloseAll(string through) { foreach (var s in data.Stores) await closer.CloseThroughAsync(s.StoreId, DateOnly.Parse(through)); }

        // A realistic history: orphans parked, a close, loyalty activity ... then the snapshot ...
        await Send(batches.Take(batches.Count * 4 / 10));
        await ingestor.IngestAsync(Raws(Loyalty("l1", "S001", EventType.LoyaltyEarn, "a1", 100), Loyalty("l2", "S002", EventType.LoyaltyBurn, "a1", 30)));
        await CloseAll("2026-01-05");
        var snapshot = await rebuilder.CreateSnapshotAsync();

        // ... and more history after it: more events (late ones for closed days), another close, more loyalty.
        await Send(batches.Skip(batches.Count * 4 / 10).Take(batches.Count * 3 / 10));
        await CloseAll("2026-01-06");
        await ingestor.IngestAsync(Raws(Loyalty("l3", "S003", EventType.LoyaltyBurn, "a1", 90)));
        await Send(batches.Skip(batches.Count * 7 / 10));

        var live = await rebuilder.HashAsync();
        var full = await rebuilder.RebuildAsync();
        Assert.True(full.Identical);
        Assert.Equal(live, full.After);
        Assert.Null(full.SnapshotId);

        await Wipe(db);
        var fromSnapshot = await rebuilder.RebuildAsync(useSnapshot: true);

        Assert.Equal(snapshot.Id, fromSnapshot.SnapshotId);
        Assert.False(fromSnapshot.SnapshotRejected);
        Assert.Equal(live, fromSnapshot.After); // identical to the live state and to the full rebuild
        Assert.InRange(fromSnapshot.Events, 1, full.Events - 1); // it replayed only what came after the snapshot
        Assert.True(fromSnapshot.Events < full.Events * 0.7, $"replayed {fromSnapshot.Events} of {full.Events} events");

        Assert.Equal(live, (await rebuilder.RebuildAsync(useSnapshot: true)).After); // and it is repeatable
    }

    [Fact]
    public async Task A_snapshot_that_did_not_finish_is_never_used_and_rebuilds_fall_back_to_the_whole_log()
    {
        var db = await _pg.NewMigratedDataSourceAsync();
        await RegisterStoresAsync(db, new StoreInfo("S1", "UTC"));
        var clock = new ManualClock(Noon);
        var rebuilder = new Rebuilder(db, clock);
        await Ingestor(db, clock).IngestAsync(Raws(Incoming("e1", amount: 500), Loyalty("l1", "S1", EventType.LoyaltyEarn, "a1", 10)));
        await using (var conn = await db.OpenConnectionAsync())
            await conn.ExecuteAsync("""
                CREATE FUNCTION crash() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'simulated crash mid-snapshot'; END $$;
                CREATE TRIGGER crash BEFORE INSERT ON snapshot_loyalty FOR EACH ROW EXECUTE FUNCTION crash();
                """);

        await Assert.ThrowsAsync<PostgresException>(() => rebuilder.CreateSnapshotAsync()); // dies after copying the totals

        Assert.Equal(0, await CountAsync(db, "snapshots"));
        Assert.Equal(0, await CountAsync(db, "snapshot_days")); // nothing half-written survives
        var fallback = await rebuilder.RebuildAsync(useSnapshot: true);
        Assert.Null(fallback.SnapshotId);
        Assert.True(fallback.Identical);

        await using (var conn = await db.OpenConnectionAsync()) await conn.ExecuteAsync("DROP TRIGGER crash ON snapshot_loyalty");
        Assert.True((await rebuilder.CreateSnapshotAsync()).LogSeq > 0); // and it works once the fault is gone
    }

    [Fact]
    public async Task A_corrupted_snapshot_is_detected_ignored_and_reported()
    {
        var db = await _pg.NewMigratedDataSourceAsync();
        await RegisterStoresAsync(db, new StoreInfo("S1", "UTC"));
        var clock = new ManualClock(Noon);
        var rebuilder = new Rebuilder(db, clock);
        await Ingestor(db, clock).IngestAsync(Raws(Incoming("e1", amount: 500), Incoming("e2", amount: 70)));
        await rebuilder.CreateSnapshotAsync();
        await Ingestor(db, clock).IngestAsync(Raws(Incoming("e3", amount: 9)));
        await using (var conn = await db.OpenConnectionAsync())
            await conn.ExecuteAsync("UPDATE snapshot_days SET sales_minor = sales_minor + 1"); // bit rot, a bad restore, a stray UPDATE

        var live = await rebuilder.HashAsync();
        var result = await rebuilder.RebuildAsync(useSnapshot: true);

        Assert.True(result.SnapshotRejected);
        Assert.Null(result.SnapshotId); // the corrupt snapshot was not trusted ...
        Assert.Equal(live, result.After); // ... and the answer is still right, from the whole log
        Assert.Equal(3, result.Events);
    }
}
