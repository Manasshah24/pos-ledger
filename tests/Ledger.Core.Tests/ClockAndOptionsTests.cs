using Ledger.Core;

namespace Ledger.Core.Tests;

public class ClockAndOptionsTests
{
    [Fact]
    public void Manual_clock_only_moves_when_told()
    {
        var start = new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);
        var clock = new ManualClock(start);

        Assert.Equal(start, clock.UtcNow);
        clock.Advance(TimeSpan.FromHours(3));
        Assert.Equal(start.AddHours(3), clock.UtcNow);
    }

    [Fact]
    public void Default_options_match_the_problem_statement()
    {
        var o = new LedgerOptions();

        Assert.Equal(2, o.BusinessDayCutoffHour);
        Assert.Equal(0, o.OfflineUnsecuredLimitMinor); // strict by default: no unsecured offline spend
    }
}
