using Ledger.Core.Events;

namespace Ledger.Core.Tests;

public class DayDeltaTests
{
    private static ValidEvent Of(string type, long amount) =>
        new("e", "S1", "T1", type, DateTimeOffset.UnixEpoch, "sale-1", amount);

    [Fact]
    public void Sale_adds_return_and_void_subtract()
    {
        var total = DayDelta.For(Of(EventType.Sale, 1000)) + DayDelta.For(Of(EventType.Return, 300)) + DayDelta.For(Of(EventType.Void, 200));

        Assert.Equal(new DayDelta(1000, 300, 200), total);
        Assert.Equal(500, total.Net);
    }

    [Fact]
    public void Amounts_near_the_long_limit_are_exact_integers()
    {
        var big = DayDelta.For(Of(EventType.Sale, long.MaxValue / 2));

        Assert.Equal(long.MaxValue / 2, big.Net);
    }
}
