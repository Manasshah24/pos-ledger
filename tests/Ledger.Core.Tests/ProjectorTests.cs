using FsCheck;
using FsCheck.Fluent;
using Ledger.Core.Events;

namespace Ledger.Core.Tests;

public class ProjectorTests
{
    private static readonly DateOnly Jan5 = new(2026, 1, 5);
    private static readonly DateOnly Jan6 = new(2026, 1, 6);

    private static LogEvent Ev(long seq, string id, string store, string type, string sale, long amount, DateOnly? day = null) =>
        new(seq, id, store, type, day ?? Jan5, sale, amount);

    [Fact]
    public void Parking_release_and_adjustments_follow_log_order_and_the_recorded_close()
    {
        var events = new[]
        {
            Ev(1, "s2-sale", "S2", EventType.Sale, "x", 111), // same sale id at another store must not release S1's return
            Ev(2, "r1", "S1", EventType.Return, "x", 300), // arrives before its sale: parked
            Ev(3, "a", "S1", EventType.Sale, "y", 1000),
            Ev(5, "b", "S1", EventType.Sale, "x", 500), // after the close of Jan 5: an adjustment
            Ev(6, "v", "S1", EventType.Void, "never", 700), // its sale never arrives
        };
        var closures = new[] { new ClosureMark("S1", LogSeqMark: 3, ThroughDate: Jan5) };

        var p = Projector.Replay(events, closures);

        Assert.Equal(new DayDelta(1000, 0, 0), p.Days.Single(d => d.StoreId == "S1" && d.Day == Jan5).Own); // frozen at the close
        Assert.Equal(new DayDelta(500, 300, 0), p.Days.Single(d => d.StoreId == "S1" && d.Day == Jan6).Adjustments); // sale b and the released return
        Assert.Equal(["b", "r1"], p.Adjustments.Select(a => a.EventId));
        Assert.All(p.Adjustments, a => Assert.Equal((Jan5, Jan6), (a.OriginalDate, a.AppliedDate)));
        Assert.Equal(["v"], p.Parked.Where(x => x.ReleasedBy is null).Select(x => x.EventId));
        Assert.Equal("b", p.Parked.Single(x => x.EventId == "r1").ReleasedBy);
    }

    [Fact]
    public void Events_are_replayed_in_log_order_whatever_order_they_are_supplied()
    {
        LogEvent[] events = [Ev(1, "a", "S1", EventType.Sale, "x", 100), Ev(2, "r", "S1", EventType.Return, "x", 40)];

        Assert.Equal(Projector.Replay(events, []).Days, Projector.Replay(events.Reverse(), []).Days);
    }

    [Fact]
    public void Property_without_closes_the_arrival_order_never_changes_the_totals()
    {
        // 30 sales over 3 stores, each returned or voided at most once; any log order must land on the same totals.
        var sales = Enumerable.Range(0, 30).Select(i => (Id: $"sale{i}", Store: $"S{i % 3}", Amount: 100L + i)).ToList();
        var events = sales.Select(s => (Id: $"e-{s.Id}", Store: s.Store, Type: EventType.Sale, Sale: s.Id, Amount: s.Amount))
            .Concat(sales.Where((_, i) => i % 3 == 0).Select(s => (Id: $"r-{s.Id}", s.Store, Type: EventType.Return, Sale: s.Id, Amount: 10L)))
            .Concat(sales.Where((_, i) => i % 5 == 1).Select(s => (Id: $"v-{s.Id}", s.Store, Type: EventType.Void, Sale: s.Id, Amount: s.Amount)))
            .ToList();

        List<ProjectedDay> Totals(int seed)
        {
            var shuffled = events.ToList();
            var rng = new Random(seed);
            for (var i = shuffled.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
            }

            return Projector.Replay(shuffled.Select((e, i) => new LogEvent(i + 1, e.Id, e.Store, e.Type, Jan5, e.Sale, e.Amount)), []).Days.ToList();
        }

        var expected = Totals(0);
        Prop.ForAll(Gen.Choose(1, 100_000).ToArbitrary(), seed => Totals(seed).SequenceEqual(expected)).QuickCheckThrowOnFailure();
    }
}
