using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ledger.Core.Events;

public sealed record ProjectionHashes(string Totals, string Adjustments, string Parked, string Loyalty, string Overall);

/// <summary>
/// SHA-256 fingerprints of the read models. Each model becomes one text line per row (fixed field order, integers only,
/// ISO dates, invariant culture); lines are sorted with an ordinal comparison, joined with \n and hashed as UTF-8.
/// The same functions fingerprint a table read from the database and a Projection computed in memory, so the two can be compared.
/// </summary>
public static class ProjectionHash
{
    public static string TotalsLine(string store, DateOnly day, DayDelta own, DayDelta adj) =>
        string.Join('|', store, Date(day), own.Sales, own.Returns, own.Voids, adj.Sales, adj.Returns, adj.Voids);

    public static string AdjustmentLine(string eventId, string store, DateOnly original, DateOnly applied, DayDelta d) =>
        string.Join('|', eventId, store, Date(original), Date(applied), d.Sales, d.Returns, d.Voids);

    /// <summary>Only events still waiting for their sale: who was parked and later released is history, not state.</summary>
    public static string ParkedLine(string eventId) => eventId;

    public static string LoyaltyLine(string accountId, long earned, long burned) => string.Join('|', accountId, earned, burned);

    public static ProjectionHashes Of(Projection p) => Of(
        p.Days.Select(d => TotalsLine(d.StoreId, d.Day, d.Own, d.Adjustments)),
        p.Adjustments.Select(a => AdjustmentLine(a.EventId, a.StoreId, a.OriginalDate, a.AppliedDate, a.Delta)),
        p.Parked.Where(x => x.ReleasedBy is null).Select(x => ParkedLine(x.EventId)),
        p.Loyalty.Select(l => LoyaltyLine(l.AccountId, l.Earned, l.Burned)));

    public static ProjectionHashes Of(
        IEnumerable<string> totalsLines, IEnumerable<string> adjustmentLines, IEnumerable<string> parkedLines, IEnumerable<string> loyaltyLines)
    {
        var totals = Sha(totalsLines);
        var adjustments = Sha(adjustmentLines);
        var parked = Sha(parkedLines);
        var loyalty = Sha(loyaltyLines);
        var overall = Sha([$"totals:{totals}", $"adjustments:{adjustments}", $"parked:{parked}", $"loyalty:{loyalty}"], sort: false);
        return new ProjectionHashes(totals, adjustments, parked, loyalty, overall);
    }

    private static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Sha(IEnumerable<string> lines, bool sort = true)
    {
        var list = lines.ToList();
        if (sort) list.Sort(StringComparer.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', list))));
    }
}
