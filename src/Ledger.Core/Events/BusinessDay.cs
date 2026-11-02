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
}
