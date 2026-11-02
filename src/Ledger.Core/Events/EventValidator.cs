using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Ledger.Core.Events;

public static partial class EventValidator
{
    public const int MaxIdLength = 128;

    // Strict ISO 8601 with an explicit offset. A timestamp without one is ambiguous (whose local time?), so it is refused.
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex IsoWithOffset();

    public static bool TryValidate(IncomingEvent e, [NotNullWhen(true)] out ValidEvent? valid, [NotNullWhen(false)] out string? reason)
    {
        valid = null;

        if (string.IsNullOrWhiteSpace(e.EventId)) return Fail(RejectReason.MissingEventId, out reason);
        if (e.EventId.Length > MaxIdLength) return Fail(RejectReason.EventIdTooLong, out reason);
        if (string.IsNullOrWhiteSpace(e.StoreId)) return Fail(RejectReason.MissingStoreId, out reason);
        if (string.IsNullOrWhiteSpace(e.TerminalId)) return Fail(RejectReason.MissingTerminalId, out reason);
        if (string.IsNullOrWhiteSpace(e.Type)) return Fail(RejectReason.MissingType, out reason);
        if (!EventType.Supported.Contains(e.Type)) return Fail(RejectReason.UnsupportedType, out reason);
        if (string.IsNullOrWhiteSpace(e.OccurredAt)) return Fail(RejectReason.MissingOccurredAt, out reason);

        if (!IsoWithOffset().IsMatch(e.OccurredAt)
            || !DateTimeOffset.TryParse(e.OccurredAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return Fail(RejectReason.InvalidOccurredAt, out reason);

        // sale, return and void all reference a sale and carry a positive amount of money.
        if (string.IsNullOrWhiteSpace(e.SaleId)) return Fail(RejectReason.MissingSaleId, out reason);
        if (e.AmountMinor is null or <= 0) return Fail(RejectReason.InvalidAmount, out reason);

        // Postgres text cannot hold NUL; refuse it here instead of failing the whole batch at the database.
        if (HasNul(e.EventId) || HasNul(e.StoreId) || HasNul(e.TerminalId) || HasNul(e.SaleId))
            return Fail(RejectReason.InvalidCharacters, out reason);

        var utc = parsed.ToUniversalTime();
        utc = utc.AddTicks(-(utc.Ticks % 10)); // Postgres timestamptz keeps microseconds; keep ours identical.

        valid = new ValidEvent(e.EventId, e.StoreId, e.TerminalId, e.Type, utc, e.SaleId, e.AmountMinor);
        reason = null;
        return true;
    }

    private static bool HasNul(string s) => s.Contains('\0');

    private static bool Fail(string why, out string? reason)
    {
        reason = why;
        return false;
    }
}
