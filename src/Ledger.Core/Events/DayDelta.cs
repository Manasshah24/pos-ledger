namespace Ledger.Core.Events;

/// <summary>What one event adds to its store-day. Net = Sales - Returns - Voids.</summary>
public readonly record struct DayDelta(long Sales, long Returns, long Voids)
{
    public static DayDelta For(ValidEvent e) => e.Type switch
    {
        EventType.Sale => new(e.AmountMinor!.Value, 0, 0),
        EventType.Return => new(0, e.AmountMinor!.Value, 0),
        EventType.Void => new(0, 0, e.AmountMinor!.Value),
        _ => throw new ArgumentOutOfRangeException(nameof(e), e.Type, "Event type has no effect on daily totals."),
    };

    public static DayDelta operator +(DayDelta a, DayDelta b) => new(a.Sales + b.Sales, a.Returns + b.Returns, a.Voids + b.Voids);

    public long Net => Sales - Returns - Voids;
}
