namespace Ledger.Harness;

/// <summary>
/// Tiny deterministic PRNG. System.Random is not guaranteed to give the same sequence across
/// runtime versions; the harness must, so a seed always reproduces the exact same data.
/// </summary>
public sealed class SplitMix64(ulong seed)
{
    private ulong _state = seed;

    public ulong NextULong()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform integer in [min, max] inclusive.</summary>
    public long Range(long min, long max)
    {
        if (max < min) throw new ArgumentOutOfRangeException(nameof(max));
        return min + (long)(NextULong() % (ulong)(max - min + 1));
    }

    public int Next(int exclusiveMax) => (int)Range(0, exclusiveMax - 1);

    /// <summary>In-place Fisher-Yates shuffle.</summary>
    public void Shuffle<T>(IList<T> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}
