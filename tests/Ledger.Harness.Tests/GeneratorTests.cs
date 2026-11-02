using Ledger.Harness;

namespace Ledger.Harness.Tests;

public class GeneratorTests
{
    private static readonly GeneratedData Default = Generator.Generate(new GeneratorOptions { Seed = 42 });

    private static Dictionary<string, DateTimeOffset> FirstArrival() =>
        Default.Deliveries.GroupBy(d => d.Event.EventId).ToDictionary(g => g.Key, g => g.Min(d => d.ArrivedAt));

    [Fact]
    public void Same_seed_gives_identical_stream()
    {
        var again = Generator.Generate(new GeneratorOptions { Seed = 42 });

        Assert.Equal(
            Default.Deliveries.Select(d => (d.Event.EventId, d.ArrivedAt, d.IsDuplicate)),
            again.Deliveries.Select(d => (d.Event.EventId, d.ArrivedAt, d.IsDuplicate)));
    }

    [Fact]
    public void Matches_the_harness_profile_in_the_problem_statement()
    {
        Assert.Equal(100_000, Default.Deliveries.Count);
        Assert.Equal(12_000, Default.Deliveries.Count(d => d.IsDuplicate));
        Assert.Equal(2_640, Default.OrphanReturnEventIds.Count); // 3% of unique events
        Assert.Equal(20, Default.Outages.Count);
    }

    [Fact]
    public void Every_delivery_arrives_within_48_hours_of_when_it_happened()
    {
        Assert.All(Default.Deliveries, d =>
            Assert.InRange(d.ArrivedAt - d.Event.OccurredAt, TimeSpan.Zero, TimeSpan.FromHours(48)));
    }

    [Fact]
    public void Orphan_returns_arrive_before_their_sale_and_other_returns_after()
    {
        var arrival = FirstArrival();
        var saleArrival = Default.UniqueEvents.Where(e => e.Type == EventTypes.Sale).ToDictionary(e => e.SaleId, e => arrival[e.EventId]);

        foreach (var e in Default.UniqueEvents.Where(e => e.Type != EventTypes.Sale))
        {
            if (Default.OrphanReturnEventIds.Contains(e.EventId)) Assert.True(arrival[e.EventId] < saleArrival[e.SaleId]);
            else Assert.True(arrival[e.EventId] > saleArrival[e.SaleId]);
        }
    }

    [Fact]
    public void Refunds_never_exceed_their_sale_and_a_sale_is_touched_at_most_once()
    {
        var sales = Default.UniqueEvents.Where(e => e.Type == EventTypes.Sale).ToDictionary(e => e.SaleId);

        Assert.All(Default.UniqueEvents.Where(e => e.Type != EventTypes.Sale).GroupBy(e => e.SaleId), g =>
        {
            var e = Assert.Single(g);
            Assert.InRange(e.AmountMinor, 1, sales[e.SaleId].AmountMinor);
        });
    }

    [Fact]
    public void Events_created_during_an_outage_arrive_after_it_ends()
    {
        var arrival = FirstArrival();

        foreach (var o in Default.Outages)
            Assert.All(
                Default.UniqueEvents.Where(e => e.StoreId == o.StoreId && e.OccurredAt >= o.From && e.OccurredAt < o.To),
                e => Assert.True(arrival[e.EventId] >= o.To));
    }
}
