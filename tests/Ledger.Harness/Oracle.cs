using System.Security.Cryptography;
using System.Text;

namespace Ledger.Harness;

public readonly record struct StoreDay(string StoreId, DateOnly BusinessDate) : IComparable<StoreDay>
{
    public int CompareTo(StoreDay other)
    {
        var c = string.CompareOrdinal(StoreId, other.StoreId);
        return c != 0 ? c : BusinessDate.CompareTo(other.BusinessDate);
    }
}

/// <summary>
/// Independent reference implementation of "what the totals should be". It stands in for the panel's
/// checksum until the real harness is available, and is deliberately written without sharing code with
/// the service so a bug cannot hide on both sides.
///
/// Rules: events are de-duplicated by EventId; sale adds, return and void subtract; each event is booked to
/// the store-local business date of its own OccurredAt, where the day rolls over at the cut-off hour.
/// The result does not depend on arrival order.
/// </summary>
public static class Oracle
{
    public static SortedDictionary<StoreDay, long> NetTotals(
        IEnumerable<HarnessEvent> events, IEnumerable<StoreInfo> stores, int cutoffHour = 2)
    {
        var zones = stores.ToDictionary(s => s.StoreId, s => TimeZoneInfo.FindSystemTimeZoneById(s.TimeZoneId), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var totals = new SortedDictionary<StoreDay, long>();

        foreach (var ev in events)
        {
            if (!seen.Add(ev.EventId)) continue;

            var day = new StoreDay(ev.StoreId, BusinessDate(ev.OccurredAt, zones[ev.StoreId], cutoffHour));
            var delta = ev.Type == EventTypes.Sale ? ev.AmountMinor : -ev.AmountMinor;
            totals[day] = totals.GetValueOrDefault(day) + delta;
        }

        return totals;
    }

    public static DateOnly BusinessDate(DateTimeOffset occurredAt, TimeZoneInfo zone, int cutoffHour)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(occurredAt.UtcDateTime, zone);
        return DateOnly.FromDateTime(local.AddHours(-cutoffHour));
    }

    /// <summary>
    /// Canonical SHA-256 over the totals: one line per store-day, "store|yyyy-MM-dd|net", sorted by store then date,
    /// joined with \n, UTF-8. The same function is reused for the rebuild-identical-hash check.
    /// </summary>
    public static string Hash(SortedDictionary<StoreDay, long> totals)
    {
        var sb = new StringBuilder();
        foreach (var (day, net) in totals)
            sb.Append(day.StoreId).Append('|').Append(day.BusinessDate.ToString("yyyy-MM-dd")).Append('|').Append(net).Append('\n');

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
