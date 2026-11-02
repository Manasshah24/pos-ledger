namespace Ledger.Core.Events;

/// <summary>What one event adds to its store-day. Net = Sales - Returns - Voids.</summary>
public readonly record struct DayDelta(long Sales, long Returns, long Voids)
{
    public static DayDelta For(ValidEvent e) => For(e.Type, e.AmountMinor!.Value);

    public static DayDelta For(string type, long amountMinor) => type switch
    {
        EventType.Sale => new(amountMinor, 0, 0),
        EventType.Return => new(0, amountMinor, 0),
        EventType.Void => new(0, 0, amountMinor),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Event type has no effect on daily totals."),
    };

    public static DayDelta operator +(DayDelta a, DayDelta b) => new(a.Sales + b.Sales, a.Returns + b.Returns, a.Voids + b.Voids);

    public long Net => Sales - Returns - Voids;
}
