using Dapper;
using Ledger.Core;
using Ledger.Core.Events;
using Ledger.Harness;
using Npgsql;
using static Ledger.Integration.Tests.IngestHelpers;

namespace Ledger.Integration.Tests;

public class EventIngestorTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;

    public EventIngestorTests(PostgresFixture pg) => _pg = pg;

    private async Task<NpgsqlDataSource> DbWithStores()
    {
        var db = await _pg.NewMigratedDataSourceAsync();
        await RegisterStoresAsync(db, new("S1", "UTC"), new("S2", "Asia/Kolkata"));
        return db;
    }

    [Fact]
    public async Task Valid_events_are_appended_with_their_business_date()
    {
        await using var db = await DbWithStores();

        var result = await Ingestor(db).IngestAsync(Raws(
            Incoming("e1", "S1", at: "2026-01-06T01:59:59Z"),
            Incoming("e2", "S1", at: "2026-01-06T02:00:00Z"),
            Incoming("e3", "S2", at: "2026-01-05T20:30:00Z")));

        Assert.Equal(3, result.Inserted);
        await using var conn = await db.OpenConnectionAsync();
        var days = (await conn.QueryAsync<DateOnly>("SELECT business_date FROM events ORDER BY event_id")).ToList();
        Assert.Equal([new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 6), new DateOnly(2026, 1, 6)], days);
    }

    [Fact]
    public async Task Sending_the_same_batch_five_times_has_the_effect_of_sending_it_once()
    {
        await using var db = await DbWithStores();
        var ingestor = Ingestor(db);
        var batch = Raws(Incoming("e1"), Incoming("e2"), Incoming("e3"));

        var results = new List<IngestResult>();
        for (var i = 0; i < 5; i++) results.Add(await ingestor.IngestAsync(batch));

        Assert.Equal(3, results[0].Inserted);
        Assert.All(results.Skip(1), r => Assert.Equal((0, 3), (r.Inserted, r.Duplicates)));
        Assert.Equal(3, await CountAsync(db, "events"));
    }

    [Fact]
    public async Task Same_event_id_with_different_content_is_a_conflict_and_the_first_version_stays()
    {
        await using var db = await DbWithStores();
        var ingestor = Ingestor(db);
        await ingestor.IngestAsync(Raws(Incoming("e1", amount: 1000)));

        var later = await ingestor.IngestAsync(Raws(Incoming("e1", amount: 9999)));

        Assert.Equal([new RejectedEvent(0, "e1", RejectReason.EventIdConflict)], later.Rejected);
        await using var conn = await db.OpenConnectionAsync();
        Assert.Equal(1000, await conn.ExecuteScalarAsync<long>("SELECT amount_minor FROM events WHERE event_id = 'e1'"));
    }

    [Fact]
    public async Task Bad_events_are_rejected_individually_and_kept_while_good_ones_still_commit()
    {
        await using var db = await DbWithStores();
        var items = new List<RawEvent>
        {
            Raw(0, Incoming("ok-1")),
            Raw(1, Incoming("bad-amount", amount: -5)),
            new(2, "42", null), // not even an object
            Raw(3, Incoming("ghost", store: "NOPE")),
            Raw(4, Incoming("ok-2")),
        };

        var result = await Ingestor(db).IngestAsync(items);

        Assert.Equal(2, result.Inserted);
        Assert.Equal(
        [
            new RejectedEvent(1, "bad-amount", RejectReason.InvalidAmount),
            new RejectedEvent(2, null, RejectReason.MalformedEvent),
            new RejectedEvent(3, "ghost", RejectReason.UnknownStore),
        ], result.Rejected);
        Assert.Equal(3, await CountAsync(db, "rejected_events")); // nothing dropped silently
    }

    [Fact]
    public async Task Eight_concurrent_senders_over_two_replicas_insert_each_event_exactly_once()
    {
        await using var db = await DbWithStores();
        var batch = Raws(Enumerable.Range(0, 2000).Select(i => Incoming($"e{i:D5}")).ToArray());
        var replicas = new[] { Ingestor(db), Ingestor(db) }; // two ingestors on one database stand in for two replicas

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => replicas[i % 2].IngestAsync(batch))));

        Assert.Equal(2000, results.Sum(r => r.Inserted)); // none inserted twice, none lost
        Assert.Equal(7 * 2000, results.Sum(r => r.Duplicates));
        Assert.Equal(2000, await CountAsync(db, "events"));
    }

    [Fact]
    public async Task Overlapping_batches_in_opposite_orders_do_not_deadlock()
    {
        await using var db = await DbWithStores();
        var forward = Raws(Enumerable.Range(0, 500).Select(i => Incoming($"e{i:D4}")).ToArray());
        var backward = Raws(Enumerable.Range(0, 500).Reverse().Select(i => Incoming($"e{i:D4}")).ToArray());
        var ingestor = Ingestor(db);

        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(() => ingestor.IngestAsync(i % 2 == 0 ? forward : backward))));

        Assert.Equal(500, results.Sum(r => r.Inserted));
    }

    [Fact]
    public async Task A_failure_in_the_middle_of_a_batch_leaves_nothing_behind_and_a_retry_lands_it_once()
    {
        await using var db = await DbWithStores();
        await using (var conn = await db.OpenConnectionAsync())
        {
            // Simulates the replica dying (or the database erroring) after some rows were already written.
            await conn.ExecuteAsync("""
                CREATE FUNCTION fail_on_poison() RETURNS trigger LANGUAGE plpgsql AS
                $$ BEGIN IF NEW.event_id = 'poison' THEN RAISE EXCEPTION 'simulated crash'; END IF; RETURN NEW; END $$;
                CREATE TRIGGER poison BEFORE INSERT ON events FOR EACH ROW EXECUTE FUNCTION fail_on_poison();
                """);
        }

        var batch = Raws(Enumerable.Range(0, 100).Select(i => Incoming($"e{i:D3}")).Append(Incoming("poison")).ToArray());
        await Assert.ThrowsAsync<PostgresException>(() => Ingestor(db).IngestAsync(batch));
        Assert.Equal(0, await CountAsync(db, "events")); // all or nothing
        Assert.Equal(0, await CountAsync(db, "store_day_totals")); // and the totals never moved

        await using (var conn = await db.OpenConnectionAsync()) await conn.ExecuteAsync("DROP TRIGGER poison ON events");
        Assert.Equal(101, (await Ingestor(db).IngestAsync(batch)).Inserted); // the terminal resends the batch
    }

    [Fact]
    public async Task Random_duplication_and_reordering_of_batches_always_yields_the_same_log()
    {
        // Seeded randomized property: however a fixed set of events is split, repeated and shuffled, the log is the same set.
        for (ulong seed = 1; seed <= 8; seed++)
        {
            await using var db = await DbWithStores();
            var rng = new SplitMix64(seed);
            var universe = Enumerable.Range(0, 60).Select(i => Incoming($"e{i:D3}", store: i % 2 == 0 ? "S1" : "S2", amount: 100 + i)).ToList();
            var stream = universe.Concat(Enumerable.Range(0, rng.Next(80)).Select(_ => universe[rng.Next(universe.Count)])).ToList();
            rng.Shuffle(stream);
            var batches = new List<List<RawEvent>>();
            for (var i = 0; i < stream.Count;)
            {
                var size = rng.Next(12) + 1;
                batches.Add(stream.Skip(i).Take(size).Select((e, k) => Raw(k, e)).ToList());
                i += size;
            }

            var ingestor = Ingestor(db);
            await Task.WhenAll(batches.Select(b => Task.Run(() => ingestor.IngestAsync(b))));

            Assert.Equal(universe.Select(e => e.EventId!).Order(StringComparer.Ordinal), await EventIdsAsync(db));
        }
    }
}
