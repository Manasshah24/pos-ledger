namespace Ledger.Core;

/// <summary>
/// The only source of "now" in the system. Business logic never reads the system clock directly,
/// so day close and late-event rules are testable and a rebuild is reproducible.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Clock that only moves when told to. Used by tests and the harness.</summary>
public sealed class ManualClock(DateTimeOffset start) : IClock
{
    private long _ticks = start.UtcTicks;

    public DateTimeOffset UtcNow => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

    public void Set(DateTimeOffset to) => Interlocked.Exchange(ref _ticks, to.UtcTicks);
}
