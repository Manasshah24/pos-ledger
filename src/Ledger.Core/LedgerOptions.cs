namespace Ledger.Core;

/// <summary>
/// Tunable business rules. All money values are integer minor units (cents/paise).
/// </summary>
public sealed record LedgerOptions
{
    /// <summary>Store-local hour at which a business day closes (02:00 means 01:59 still belongs to the previous day).</summary>
    public int BusinessDayCutoffHour { get; init; } = 2;

    /// <summary>Events older than this relative to arrival are rejected as implausible.</summary>
    public int MaxLateHours { get; init; } = 48;

    /// <summary>
    /// Hard bound on how much one gift card may be overspent by offline redemptions, in minor units.
    /// 0 means offline redemption is only allowed against a secured lease.
    /// </summary>
    public long OfflineUnsecuredLimitMinor { get; init; } = 0;
}
