using System.Security.Cryptography;
using System.Text;
using Ledger.Harness;

namespace Ledger.Harness.Tests;

public class OracleTests
{
    private static readonly StoreInfo Utc = new("S1", "UTC");
    private static readonly DateTimeOffset Noon = new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);

    private static HarnessEvent Ev(string id, string type, long amount, DateTimeOffset at, string sale = "sale-1", StoreInfo? store = null) =>
        new(id, (store ?? Utc).StoreId, "T1", type, at, sale, amount);

    private static long Net(IEnumerable<HarnessEvent> events, StoreInfo? store = null) =>
        Oracle.NetTotals(events, [store ?? Utc]).Values.Sum();

    [Fact]
    public void Sale_adds_return_subtracts_and_void_reverses()
    {
        Assert.Equal(1000, Net([Ev("e1", EventTypes.Sale, 1000, Noon)]));
        Assert.Equal(700, Net([Ev("e1", EventTypes.Sale, 1000, Noon), Ev("e2", EventTypes.Return, 300, Noon.AddHours(1))]));
        Assert.Equal(0, Net([Ev("e1", EventTypes.Sale, 1000, Noon), Ev("e2", EventTypes.Void, 1000, Noon.AddMinutes(5))]));
    }

    [Fact]
    public void Duplicates_are_counted_once()
    {
        var sale = Ev("e1", EventTypes.Sale, 1000, Noon);

        Assert.Equal(1000, Net([sale, sale, sale]));
    }

    [Fact]
    public void Business_day_rolls_over_at_the_cutoff_hour()
    {
        var day5 = new DateOnly(2026, 1, 5);
        var before = Ev("e1", EventTypes.Sale, 100, new DateTimeOffset(2026, 1, 6, 1, 59, 59, TimeSpan.Zero), "s1");
        var at = Ev("e2", EventTypes.Sale, 200, new DateTimeOffset(2026, 1, 6, 2, 0, 0, TimeSpan.Zero), "s2");

        var totals = Oracle.NetTotals([before, at], [Utc]);

        Assert.Equal(100, totals[new StoreDay("S1", day5)]);
        Assert.Equal(200, totals[new StoreDay("S1", day5.AddDays(1))]);
    }

    [Fact]
    public void Business_day_uses_the_store_time_zone()
    {
        var kolkata = new StoreInfo("S1", "Asia/Kolkata"); // UTC+5:30
        // 20:30 UTC on the 5th is 02:00 local on the 6th, which is the first moment of business day the 6th.
        var ev = Ev("e1", EventTypes.Sale, 100, new DateTimeOffset(2026, 1, 5, 20, 30, 0, TimeSpan.Zero), "s1", kolkata);
        var justBefore = Ev("e2", EventTypes.Sale, 50, new DateTimeOffset(2026, 1, 5, 20, 29, 59, TimeSpan.Zero), "s2", kolkata);

        var totals = Oracle.NetTotals([ev, justBefore], [kolkata]);

        Assert.Equal(100, totals[new StoreDay("S1", new DateOnly(2026, 1, 6))]);
        Assert.Equal(50, totals[new StoreDay("S1", new DateOnly(2026, 1, 5))]);
    }

    [Fact]
    public void Stores_are_totalled_separately()
    {
        var other = new StoreInfo("S2", "UTC");
        var totals = Oracle.NetTotals(
            [Ev("e1", EventTypes.Sale, 100, Noon), Ev("e2", EventTypes.Sale, 40, Noon, "s2", other)], [Utc, other]);

        Assert.Equal(2, totals.Count);
    }

    [Fact]
    public void Result_does_not_depend_on_arrival_order_or_duplicates()
    {
        var data = Generator.Generate(new GeneratorOptions { Seed = 7, TotalDeliveries = 5_000, Stores = 20 });
        var expected = Oracle.Hash(Oracle.NetTotals(data.UniqueEvents, data.Stores));

        var fromDeliveries = Oracle.Hash(Oracle.NetTotals(data.Deliveries.Select(d => d.Event), data.Stores));
        var reversed = Oracle.Hash(Oracle.NetTotals(data.Deliveries.Select(d => d.Event).Reverse(), data.Stores));
        var shuffled = data.Deliveries.Select(d => d.Event).ToList();
        new SplitMix64(99).Shuffle(shuffled);

        Assert.Equal(expected, fromDeliveries);
        Assert.Equal(expected, reversed);
        Assert.Equal(expected, Oracle.Hash(Oracle.NetTotals(shuffled, data.Stores)));
    }

    [Fact]
    public void Grand_total_equals_sales_minus_returns_minus_voids()
    {
        var data = Generator.Generate(new GeneratorOptions { Seed = 11, TotalDeliveries = 5_000, Stores = 20 });

        var expected = data.UniqueEvents.Sum(e => e.Type == EventTypes.Sale ? e.AmountMinor : -e.AmountMinor);

        Assert.Equal(expected, Oracle.NetTotals(data.UniqueEvents, data.Stores).Values.Sum());
    }

    [Fact]
    public void Hash_uses_the_documented_canonical_format()
    {
        var totals = Oracle.NetTotals(
            [Ev("e1", EventTypes.Sale, 1000, Noon), Ev("e2", EventTypes.Return, 300, Noon, "sale-1")], [Utc]);

        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("S1|2026-01-05|700\n")));

        Assert.Equal(expected, Oracle.Hash(totals));
    }

    [Fact]
    public void Hash_changes_when_any_value_changes()
    {
        var a = Oracle.NetTotals([Ev("e1", EventTypes.Sale, 1000, Noon)], [Utc]);
        var b = Oracle.NetTotals([Ev("e1", EventTypes.Sale, 1001, Noon)], [Utc]);

        Assert.NotEqual(Oracle.Hash(a), Oracle.Hash(b));
    }
}
