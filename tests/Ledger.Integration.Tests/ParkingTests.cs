using Ledger.Core.Events;
using Ledger.Data;
using Ledger.Harness;
using Npgsql;
using static Ledger.Integration.Tests.IngestHelpers;

namespace Ledger.Integration.Tests;

public class ParkingTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;

    public ParkingTests(PostgresFixture pg) => _pg = pg;

    private async Task<NpgsqlDataSource> DbWithStore()
    {
        var db = await _pg.NewMigratedDataSourceAsync();
        await RegisterStoresAsync(db, new StoreInfo("S1", "UTC"));
        return db;
    }

    private static IncomingEvent Sale(string id, string sale, long amount) => Incoming(id, type: EventType.Sale, amount: amount, sale: sale);
    private static IncomingEvent Return(string id, string sale, long amount) => Incoming(id, type: EventType.Return, amount: amount, sale: sale);
    private static IncomingEvent Void(string id, string sale, long amount) => Incoming(id, type: EventType.Void, amount: amount, sale: sale);

    [Fact]
    public async Task A_return_or_void_before_its_sale_is_held_out_of_the_totals_then_applied_when_the_sale_arrives()
    {
        await using var db = await DbWithStore();
        var ingestor = Ingestor(db);
        var totals = new TotalsQuery(db);
        var parked = new ParkedQuery(db);

        await ingestor.IngestAsync(Raws(Return("r1", "s1", 300), Void("v1", "s2", 500)));

        Assert.Empty(await totals.GetAsync()); // nothing booked yet
        Assert.Equal(["r1", "v1"], (await parked.GetOpenAsync()).Select(p => p.EventId).Order());

        await ingestor.IngestAsync(Raws(Sale("e1", "s1", 1000)));

        Assert.Equal(new StoreDayTotal("S1", new DateOnly(2026, 1, 5), 1000, 300, 0, 700), Assert.Single(await totals.GetAsync()));
        Assert.Equal(["v1"], (await parked.GetOpenAsync()).Select(p => p.EventId)); // the other is still waiting, and visible

        await ingestor.IngestAsync(Raws(Sale("e2", "s2", 500)));

        Assert.Equal(new StoreDayTotal("S1", new DateOnly(2026, 1, 5), 1500, 300, 500, 700), Assert.Single(await totals.GetAsync()));
        Assert.Empty(await parked.GetOpenAsync());
    }

    [Fact]
    public async Task A_sale_id_only_matches_a_sale_in_the_same_store()
    {
        await using var db = await _pg.NewMigratedDataSourceAsync();
        await RegisterStoresAsync(db, new StoreInfo("S1", "UTC"), new StoreInfo("S2", "UTC"));
        var ingestor = Ingestor(db);

        // Receipt numbers repeat across stores: S2's sale "x" must not release or satisfy S1's return of its own sale "x".
        await ingestor.IngestAsync(Raws(Sale("s2-x", "x", 111) with { StoreId = "S2" }, Return("s1-r", "x", 300)));

        Assert.Equal(["s1-r"], (await new ParkedQuery(db).GetOpenAsync()).Select(p => p.EventId));

        await ingestor.IngestAsync(Raws(Sale("s1-x", "x", 1000)));

        Assert.Empty(await new ParkedQuery(db).GetOpenAsync());
        Assert.Equal(700, (await new TotalsQuery(db).GetAsync("S1")).Sum(t => t.NetMinor));
    }

    [Fact]
    public async Task Sale_and_return_in_the_same_batch_apply_in_either_order_without_parking()
    {
        await using var db = await DbWithStore();
        var ingestor = Ingestor(db);

        await ingestor.IngestAsync(Raws(Return("r1", "s1", 300), Sale("e1", "s1", 1000)));

        Assert.Equal(700, Assert.Single(await new TotalsQuery(db).GetAsync()).NetMinor);
        Assert.Empty(await new ParkedQuery(db).GetOpenAsync());
    }

    [Fact]
    public async Task A_parked_event_sent_again_is_not_applied_twice()
    {
        await using var db = await DbWithStore();
        var ingestor = Ingestor(db);

        await ingestor.IngestAsync(Raws(Return("r1", "s1", 300)));
        await ingestor.IngestAsync(Raws(Return("r1", "s1", 300))); // retry while still parked
        await ingestor.IngestAsync(Raws(Sale("e1", "s1", 1000)));
        await ingestor.IngestAsync(Raws(Return("r1", "s1", 300), Sale("e1", "s1", 1000))); // retry after release

        Assert.Equal(700, Assert.Single(await new TotalsQuery(db).GetAsync()).NetMinor);
        Assert.Empty(await new ParkedQuery(db).GetOpenAsync());
    }

    [Fact]
    public async Task Sales_and_their_returns_racing_on_two_replicas_never_leave_a_return_stuck()
    {
        await using var db = await DbWithStore();
        var replicas = new[] { Ingestor(db), Ingestor(db) };
        const int pairs = 150;

        // Every sale and every return is its own batch, all launched at once in random order, so each pair races.
        var batches = Enumerable.Range(0, pairs).SelectMany(i => new[]
        {
            Raws(Sale($"sale-evt-{i}", $"s{i}", 1000)),
            Raws(Return($"ret-evt-{i}", $"s{i}", 300)),
        }).ToList();
        new SplitMix64(11).Shuffle(batches);

        await Parallel.ForEachAsync(batches.Select((b, i) => (b, i)), new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (x, _) => await replicas[x.i % 2].IngestAsync(x.b));

        Assert.Empty(await new ParkedQuery(db).GetOpenAsync());
        var total = Assert.Single(await new TotalsQuery(db).GetAsync());
        Assert.Equal(pairs * 1000L, total.SalesMinor);
        Assert.Equal(pairs * 300L, total.ReturnsMinor);
    }
}
