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
        Assert.Equal(start, clock.UtcNow);

        clock.Advance(TimeSpan.FromHours(3));
        Assert.Equal(start.AddHours(3), clock.UtcNow);

        clock.Set(start.AddDays(1));
        Assert.Equal(start.AddDays(1), clock.UtcNow);
    }

    [Fact]
    public async Task Manual_clock_is_safe_under_concurrent_advances()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);

        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++) clock.Advance(TimeSpan.FromSeconds(1));
        })));

        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(100_000), clock.UtcNow);
    }

    [Fact]
    public void System_clock_reports_utc()
    {
        Assert.Equal(TimeSpan.Zero, new SystemClock().UtcNow.Offset);
    }

    [Fact]
    public void Default_options_match_the_problem_statement()
    {
        var o = new LedgerOptions();

        Assert.Equal(2, o.BusinessDayCutoffHour);
        Assert.Equal(48, o.MaxLateHours);
        Assert.Equal(0, o.OfflineUnsecuredLimitMinor); // strict by default: no unsecured offline spend
    }
}
