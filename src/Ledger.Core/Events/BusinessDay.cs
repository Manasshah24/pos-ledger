namespace Ledger.Core.Events;

public static class BusinessDay
{
    /// <summary>
    /// The store's business date for an instant: the store-local date of (local wall-clock time minus the cut-off hour).
    /// With a 02:00 cut-off, 01:59 local still belongs to the previous business day and 02:00 starts the new one.
    /// On a DST spring-forward night, when 02:00 never happens, the new day starts at the first instant after the jump.
    /// </summary>
    public static DateOnly For(DateTimeOffset occurredAt, TimeZoneInfo zone, int cutoffHour)
    {
        if (cutoffHour is < 0 or > 23) throw new ArgumentOutOfRangeException(nameof(cutoffHour));

        var local = TimeZoneInfo.ConvertTimeFromUtc(occurredAt.UtcDateTime, zone);
        return DateOnly.FromDateTime(local.AddHours(-cutoffHour));
    }

    /// <summary>
    /// The first instant that belongs to the day after <paramref name="businessDate"/>, i.e. the moment that day closes.
    /// Consistent with <see cref="For"/>: an instant is in the day exactly when it is before this.
    /// </summary>
    public static DateTimeOffset EndUtc(DateOnly businessDate, TimeZoneInfo zone, int cutoffHour)
    {
        if (cutoffHour is < 0 or > 23) throw new ArgumentOutOfRangeException(nameof(cutoffHour));

        var local = businessDate.AddDays(1).ToDateTime(new TimeOnly(cutoffHour, 0));
        if (zone.IsInvalidTime(local)) local = local.AddHours(1); // spring-forward gap: the day ends at the first instant after the jump

        // A wall-clock time that happens twice (fall back) is taken at its first occurrence, the way For() sees it.
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
