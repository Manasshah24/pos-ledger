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
    /// How often each replica checks whether any store's business day has ended and closes it. 0 turns the scheduler off
    /// (days are then closed only through the admin endpoints), which is what tests and a harness-driven clock want.
    /// </summary>
    public int AutoCloseIntervalSeconds { get; init; } = 0;

    /// <summary>
    /// Hard bound on how much one gift card may be overspent by offline redemptions, in minor units.
    /// 0 means offline redemption is only allowed against a secured lease.
    /// </summary>
    public long OfflineUnsecuredLimitMinor { get; init; } = 0;
}
