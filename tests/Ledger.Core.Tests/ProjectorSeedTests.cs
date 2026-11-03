using FsCheck;
using FsCheck.Fluent;
using Ledger.Core.Events;

namespace Ledger.Core.Tests;

public class ProjectorSeedTests
{
    private static readonly DateOnly Jan5 = new(2026, 1, 5);

    /// <summary>A random history: events and closes interleaved in the order they happened, like the live system sees them.</summary>
    private static (List<LogEvent> Events, List<ClosureMark> Closures, List<object> Timeline) RandomHistory(int seed)
    {
        var rng = new Random(seed);
        var events = new List<LogEvent>();
        var closures = new List<ClosureMark>();
        var timeline = new List<object>(); // LogEvent or ClosureMark, in time order
        var lastSeqOfStore = new Dictionary<string, long>();
        var pendingSales = new List<(string Store, string Sale)>();
        long seq = 0;

        for (var i = 0; i < 120; i++)
        {
            var store = $"S{rng.Next(3)}";
            var day = Jan5.AddDays(rng.Next(4));
            var roll = rng.Next(100);

            if (roll < 8)
            {
                // A close: its mark is the highest seq this store has used so far.
                var close = new ClosureMark(store, lastSeqOfStore.GetValueOrDefault(store), Jan5.AddDays(rng.Next(3)));
                closures.Add(close);
                timeline.Add(close);
                continue;
            }

            LogEvent ev;
            seq++;
            if (roll < 50 || pendingSales.Count == 0)
            {
                var sale = $"sale-{seq}";
                pendingSales.Add((store, sale));
                ev = new LogEvent(seq, $"e{seq}", store, EventType.Sale, day, sale, rng.Next(1, 900));
            }
            else if (roll < 80)
            {
                // A return or void; sometimes for a sale that has not happened yet, sometimes for one that never will.
                var type = rng.Next(2) == 0 ? EventType.Return : EventType.Void;
                var sale = rng.Next(4) == 0 ? $"sale-{seq + rng.Next(1, 6)}" : pendingSales[rng.Next(pendingSales.Count)].Sale;
                var owner = pendingSales.FirstOrDefault(p => p.Sale == sale).Store ?? store;
                ev = new LogEvent(seq, $"e{seq}", owner, type, day, sale, rng.Next(1, 500));
                store = owner;
            }
            else
            {
                var account = $"acct-{rng.Next(4)}";
                ev = new LogEvent(seq, $"e{seq}", store, rng.Next(2) == 0 ? EventType.LoyaltyEarn : EventType.LoyaltyBurn, day, "", 0, account, rng.Next(1, 100));
            }

            lastSeqOfStore[ev.StoreId] = seq;
            events.Add(ev);
            timeline.Add(ev);
        }

        return (events, closures, timeline);
    }

    [Fact]
    public void Property_replaying_a_tail_from_a_seed_equals_replaying_the_whole_history()
    {
        Prop.ForAll(Gen.Choose(1, 1_000_000).Select(s => (Seed: s, Cut: new Random(s).Next(1, 119))).ToArbitrary(), t =>
        {
            var (events, closures, timeline) = RandomHistory(t.Seed);
            var prefix = timeline.Take(t.Cut).ToList();
            var tail = timeline.Skip(t.Cut).ToList();
            var prefixEvents = prefix.OfType<LogEvent>().ToList();
            var prefixClosures = prefix.OfType<ClosureMark>().ToList();

            // The state a snapshot would hold after the prefix.
            var atSnapshot = Projector.Replay(prefixEvents, prefixClosures);
            var openParkedIds = atSnapshot.Parked.Where(p => p.ReleasedBy is null).Select(p => p.EventId).ToHashSet();
            var seed = new ProjectionSeed(
                atSnapshot.Days, atSnapshot.Adjustments, atSnapshot.Loyalty,
                prefixClosures.GroupBy(c => c.StoreId).ToDictionary(g => g.Key, g => g.Max(c => c.ThroughDate)),
                prefixEvents.Where(e => openParkedIds.Contains(e.EventId)).ToList(),
                prefixEvents.Where(e => e.Type == EventType.Sale).Select(e => (e.StoreId, e.SaleId)).ToHashSet());

            var whole = ProjectionHash.Of(Projector.Replay(events, closures));
            var resumed = ProjectionHash.Of(Projector.Replay(tail.OfType<LogEvent>(), tail.OfType<ClosureMark>(), seed, alreadyOrdered: true));

            return whole == resumed;
        }).QuickCheckThrowOnFailure();
    }
}
