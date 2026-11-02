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

    private static long Net(params HarnessEvent[] events) => Oracle.NetTotals(events, [Utc]).Values.Sum();

    [Fact]
    public void Sale_adds_return_subtracts_void_reverses_and_duplicates_count_once()
    {
        var sale = Ev("e1", EventTypes.Sale, 1000, Noon);

        Assert.Equal(1000, Net(sale, sale, sale));
        Assert.Equal(700, Net(sale, Ev("e2", EventTypes.Return, 300, Noon.AddHours(1))));
        Assert.Equal(0, Net(sale, Ev("e2", EventTypes.Void, 1000, Noon.AddMinutes(5))));
    }

    [Fact]
    public void Business_day_uses_the_cutoff_and_the_store_time_zone()
    {
        var kolkata = new StoreInfo("S1", "Asia/Kolkata"); // 02:00 local = 20:30 UTC the day before
        var before = Ev("e1", EventTypes.Sale, 50, new DateTimeOffset(2026, 1, 5, 20, 29, 59, TimeSpan.Zero), "s1", kolkata);
        var after = Ev("e2", EventTypes.Sale, 100, new DateTimeOffset(2026, 1, 5, 20, 30, 0, TimeSpan.Zero), "s2", kolkata);

        var totals = Oracle.NetTotals([before, after], [kolkata]);

        Assert.Equal(50, totals[new StoreDay("S1", new DateOnly(2026, 1, 5))]);
        Assert.Equal(100, totals[new StoreDay("S1", new DateOnly(2026, 1, 6))]);
    }

    [Fact]
    public void Result_does_not_depend_on_arrival_order_or_duplicates()
    {
        var data = Generator.Generate(new GeneratorOptions { Seed = 7, TotalDeliveries = 5_000, Stores = 20 });
        var expected = Oracle.Hash(Oracle.NetTotals(data.UniqueEvents, data.Stores));
        var shuffled = data.Deliveries.Select(d => d.Event).ToList();
        new SplitMix64(99).Shuffle(shuffled);

        Assert.Equal(expected, Oracle.Hash(Oracle.NetTotals(data.Deliveries.Select(d => d.Event), data.Stores)));
        Assert.Equal(expected, Oracle.Hash(Oracle.NetTotals(shuffled, data.Stores)));
    }

    [Fact]
    public void Hash_uses_the_documented_canonical_format()
    {
        var totals = Oracle.NetTotals([Ev("e1", EventTypes.Sale, 1000, Noon), Ev("e2", EventTypes.Return, 300, Noon)], [Utc]);

        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("S1|2026-01-05|700\n")));

        Assert.Equal(expected, Oracle.Hash(totals));
    }
}
