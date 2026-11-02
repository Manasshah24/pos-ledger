using FsCheck;
using FsCheck.Fluent;
using Ledger.Core.Events;

namespace Ledger.Core.Tests;

public class BusinessDayTests
{
    private static DateTimeOffset Utc(int y, int mo, int d, int h, int mi = 0, int s = 0) => new(y, mo, d, h, mi, s, TimeSpan.Zero);

    private static DateOnly Day(string tz, DateTimeOffset at, int cutoff = 2) =>
        BusinessDay.For(at, TimeZoneInfo.FindSystemTimeZoneById(tz), cutoff);

    [Fact]
    public void Day_rolls_over_exactly_at_02_00_store_local_time()
    {
        Assert.Equal(new DateOnly(2026, 1, 5), Day("UTC", Utc(2026, 1, 6, 1, 59, 59)));
        Assert.Equal(new DateOnly(2026, 1, 6), Day("UTC", Utc(2026, 1, 6, 2, 0, 0)));
        // Kolkata is UTC+5:30: 02:00 local is 20:30 UTC the day before.
        Assert.Equal(new DateOnly(2026, 1, 5), Day("Asia/Kolkata", Utc(2026, 1, 5, 20, 29, 59)));
        Assert.Equal(new DateOnly(2026, 1, 6), Day("Asia/Kolkata", Utc(2026, 1, 5, 20, 30, 0)));
    }

    [Fact]
    public void Dst_changes_do_not_break_the_cutoff()
    {
        // New York spring forward 2026-03-08 (02:00 never happens): new day starts at the first instant after the jump.
        Assert.Equal(new DateOnly(2026, 3, 7), Day("America/New_York", Utc(2026, 3, 8, 6, 59, 59)));
        Assert.Equal(new DateOnly(2026, 3, 8), Day("America/New_York", Utc(2026, 3, 8, 7, 0, 0)));
        // Fall back 2026-11-01: the repeated 01:00-02:00 hour stays in the old day; the new day starts at 02:00 EST.
        Assert.Equal(new DateOnly(2026, 10, 31), Day("America/New_York", Utc(2026, 11, 1, 6, 59, 59)));
        Assert.Equal(new DateOnly(2026, 11, 1), Day("America/New_York", Utc(2026, 11, 1, 7, 0, 0)));
    }

    [Fact]
    public void Property_business_date_never_goes_backwards_in_time()
    {
        string[] zones = ["UTC", "Asia/Kolkata", "America/New_York", "Europe/London", "Australia/Sydney"];
        var gen =
            from zone in Gen.Elements(zones)
            from a in Gen.Choose(0, 5_000_000)
            from gap in Gen.Choose(0, 3 * 24 * 60)
            select (zone, A: Utc(2020, 1, 1, 0).AddMinutes(a), B: Utc(2020, 1, 1, 0).AddMinutes(a + gap));

        Prop.ForAll(gen.ToArbitrary(), t => Day(t.zone, t.A) <= Day(t.zone, t.B)).QuickCheckThrowOnFailure();
    }

    [Fact]
    public void Day_end_is_the_first_instant_of_the_next_business_day()
    {
        var ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        Assert.Equal(Utc(2026, 1, 6, 2), BusinessDay.EndUtc(new DateOnly(2026, 1, 5), TimeZoneInfo.Utc, 2));
        Assert.Equal(Utc(2026, 3, 8, 7), BusinessDay.EndUtc(new DateOnly(2026, 3, 7), ny, 2)); // spring forward: 03:00 EDT
        Assert.Equal(Utc(2026, 11, 1, 7), BusinessDay.EndUtc(new DateOnly(2026, 10, 31), ny, 2)); // fall back: 02:00 EST
    }

    [Fact]
    public void Property_an_instant_is_in_a_business_day_exactly_when_it_is_before_that_days_end()
    {
        string[] zones = ["UTC", "Asia/Kolkata", "America/New_York", "Europe/London", "Australia/Sydney", "Pacific/Auckland"];
        var gen = from zone in Gen.Elements(zones) from m in Gen.Choose(0, 5_000_000) select (zone, T: Utc(2020, 1, 1, 0).AddMinutes(m));

        Prop.ForAll(gen.ToArbitrary(), x =>
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(x.zone);
            var day = BusinessDay.For(x.T, tz, 2);
            return x.T < BusinessDay.EndUtc(day, tz, 2) && x.T >= BusinessDay.EndUtc(day.AddDays(-1), tz, 2);
        }).QuickCheckThrowOnFailure();
    }
}
