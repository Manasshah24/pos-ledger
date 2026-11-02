using Ledger.Harness;

namespace Ledger.Harness.Tests;

public class GeneratorTests
{
    private static readonly GeneratedData Default = Generator.Generate(new GeneratorOptions { Seed = 42 });

    [Fact]
    public void Same_seed_gives_identical_stream()
    {
        var again = Generator.Generate(new GeneratorOptions { Seed = 42 });

        Assert.Equal(
            Default.Deliveries.Select(d => (d.Event.EventId, d.ArrivedAt, d.IsDuplicate)),
            again.Deliveries.Select(d => (d.Event.EventId, d.ArrivedAt, d.IsDuplicate)));
        Assert.Equal(Default.UniqueEvents, again.UniqueEvents);
    }

    [Fact]
    public void Different_seed_gives_different_stream()
    {
        var other = Generator.Generate(new GeneratorOptions { Seed = 43 });

        Assert.NotEqual(
            Default.Deliveries.Take(50).Select(d => d.Event.EventId + d.ArrivedAt),
            other.Deliveries.Take(50).Select(d => d.Event.EventId + d.ArrivedAt));
    }

    [Fact]
    public void Matches_the_harness_profile_in_the_problem_statement()
    {
        Assert.Equal(100_000, Default.Deliveries.Count);
        Assert.Equal(12_000, Default.Deliveries.Count(d => d.IsDuplicate));
        Assert.Equal(88_000, Default.UniqueEvents.Count);
        Assert.Equal(2_640, Default.OrphanReturnEventIds.Count); // 3% of unique events
        Assert.Equal(20, Default.Outages.Count);
        Assert.Equal(300, Default.Stores.Count);
    }

    [Fact]
    public void Unique_events_have_unique_ids_and_duplicates_repeat_an_existing_event()
    {
        var ids = Default.UniqueEvents.Select(e => e.EventId).ToHashSet();
        Assert.Equal(Default.UniqueEvents.Count, ids.Count);
        Assert.All(Default.Deliveries.Where(d => d.IsDuplicate), d => Assert.Contains(d.Event.EventId, ids));
        Assert.Equal(ids, Default.Deliveries.Select(d => d.Event.EventId).ToHashSet());
    }

    [Fact]
    public void Every_delivery_arrives_within_48_hours_of_when_it_happened()
    {
        Assert.All(Default.Deliveries, d =>
        {
            Assert.True(d.ArrivedAt >= d.Event.OccurredAt, $"{d.Event.EventId} arrived before it happened");
            Assert.True(d.ArrivedAt - d.Event.OccurredAt <= TimeSpan.FromHours(48), $"{d.Event.EventId} is more than 48h late");
        });
    }

    [Fact]
    public void Deliveries_are_sorted_by_arrival_time()
    {
        var times = Default.Deliveries.Select(d => d.ArrivedAt).ToList();
        Assert.Equal(times.OrderBy(t => t), times);
    }

    [Fact]
    public void Orphan_returns_first_arrive_before_their_sale()
    {
        var firstArrival = Default.Deliveries.GroupBy(d => d.Event.EventId).ToDictionary(g => g.Key, g => g.Min(d => d.ArrivedAt));
        var saleArrival = Default.UniqueEvents.Where(e => e.Type == EventTypes.Sale).ToDictionary(e => e.SaleId, e => firstArrival[e.EventId]);

        var orphans = Default.UniqueEvents.Where(e => Default.OrphanReturnEventIds.Contains(e.EventId)).ToList();
        Assert.Equal(Default.OrphanReturnEventIds.Count, orphans.Count);
        Assert.All(orphans, r =>
        {
            Assert.Equal(EventTypes.Return, r.Type);
            Assert.True(firstArrival[r.EventId] < saleArrival[r.SaleId], $"{r.EventId} should arrive before sale {r.SaleId}");
        });
    }

    [Fact]
    public void Non_orphan_returns_and_voids_arrive_after_their_sale()
    {
        var firstArrival = Default.Deliveries.GroupBy(d => d.Event.EventId).ToDictionary(g => g.Key, g => g.Min(d => d.ArrivedAt));
        var saleArrival = Default.UniqueEvents.Where(e => e.Type == EventTypes.Sale).ToDictionary(e => e.SaleId, e => firstArrival[e.EventId]);

        var normal = Default.UniqueEvents.Where(e => e.Type != EventTypes.Sale && !Default.OrphanReturnEventIds.Contains(e.EventId));
        Assert.All(normal, e => Assert.True(firstArrival[e.EventId] > saleArrival[e.SaleId]));
    }

    [Fact]
    public void A_sale_is_never_both_voided_and_returned_and_refunds_never_exceed_the_sale()
    {
        var sales = Default.UniqueEvents.Where(e => e.Type == EventTypes.Sale).ToDictionary(e => e.SaleId);
        var byRef = Default.UniqueEvents.Where(e => e.Type != EventTypes.Sale).GroupBy(e => e.SaleId);

        Assert.All(byRef, g =>
        {
            Assert.Single(g); // at most one void or return per sale
            var e = g.Single();
            if (e.Type == EventTypes.Return) Assert.InRange(e.AmountMinor, 1, sales[e.SaleId].AmountMinor);
            else Assert.Equal(sales[e.SaleId].AmountMinor, e.AmountMinor);
        });
    }

    [Fact]
    public void Events_created_during_an_outage_arrive_after_it_ends()
    {
        var firstArrival = Default.Deliveries.GroupBy(d => d.Event.EventId).ToDictionary(g => g.Key, g => g.Min(d => d.ArrivedAt));

        foreach (var outage in Default.Outages)
        {
            var held = Default.UniqueEvents.Where(e => e.StoreId == outage.StoreId && e.OccurredAt >= outage.From && e.OccurredAt < outage.To);
            Assert.All(held, e => Assert.True(firstArrival[e.EventId] >= outage.To, $"{e.EventId} escaped outage of {outage.StoreId}"));
        }
    }

    [Fact]
    public void All_amounts_are_positive_whole_minor_units()
    {
        Assert.All(Default.UniqueEvents, e => Assert.True(e.AmountMinor > 0));
    }

    [Fact]
    public void ToBatches_covers_every_delivery_once_in_order()
    {
        var batches = Default.ToBatches(500).ToList();

        Assert.Equal(200, batches.Count);
        Assert.Equal(Default.Deliveries, batches.SelectMany(b => b));
    }

    [Fact]
    public void Impossible_rates_are_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            Generator.Generate(new GeneratorOptions { TotalDeliveries = 1000, ReturnRate = 0.6, VoidRate = 0.3 }));
    }
}
