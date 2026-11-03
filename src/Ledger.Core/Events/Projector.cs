namespace Ledger.Core.Events;

/// <summary>One event as the replay sees it. BusinessDate was fixed when the event was appended.</summary>
public sealed record LogEvent(
    long LogSeq, string EventId, string StoreId, string Type, DateOnly BusinessDate, string SaleId, long AmountMinor,
    string? AccountId = null, long Points = 0);

/// <summary>A day close: every day up to ThroughDate closed after the store's events with LogSeq at or below LogSeqMark.</summary>
public sealed record ClosureMark(string StoreId, long LogSeqMark, DateOnly ThroughDate);

public sealed record ProjectedDay(string StoreId, DateOnly Day, DayDelta Own, DayDelta Adjustments);

public sealed record ProjectedAdjustment(string EventId, string StoreId, DateOnly OriginalDate, DateOnly AppliedDate, DayDelta Delta);

/// <summary>A return/void that arrived before its sale. ReleasedBy is the sale event that freed it, or null while still waiting.</summary>
public sealed record ProjectedParked(string EventId, string StoreId, string SaleId, string? ReleasedBy);

/// <summary>A loyalty account as the sum of its distinct earn and burn events (a PN-counter: both only ever grow).</summary>
public sealed record ProjectedLoyalty(string AccountId, long Earned, long Burned);

public sealed record Projection(
    IReadOnlyList<ProjectedDay> Days,
    IReadOnlyList<ProjectedAdjustment> Adjustments,
    IReadOnlyList<ProjectedParked> Parked,
    IReadOnlyList<ProjectedLoyalty> Loyalty);

/// <summary>
/// Rebuilds every read model from the event log. It is a pure function of the log (events in log_seq order plus the recorded
/// closes): no clock, no randomness, no database. It encodes the same rules the live ingest path applies incrementally:
///   - a sale always counts; a return/void counts only once its sale (same store) is in the log, otherwise it is parked;
///   - a new sale releases whatever is parked for it;
///   - an effect for a day that is already closed never touches that day: it is booked as an adjustment into the next open
///     day, linked to its original day.
/// The two implementations are kept separate on purpose: the tests demand they agree, which is a far stronger check than one
/// piece of code agreeing with itself.
/// </summary>
public static class Projector
{
    public static Projection Replay(IEnumerable<LogEvent> events, IEnumerable<ClosureMark> closures)
    {
        var closuresByStore = closures.GroupBy(c => c.StoreId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => new Queue<ClosureMark>(g), StringComparer.Ordinal);
        var lastClosed = new Dictionary<string, DateOnly>(StringComparer.Ordinal);
        var knownSales = new HashSet<(string Store, string Sale)>();
        var parkedBySale = new Dictionary<(string Store, string Sale), List<LogEvent>>();
        var parkedRecords = new Dictionary<string, ProjectedParked>(StringComparer.Ordinal);
        var days = new Dictionary<(string Store, DateOnly Day), (DayDelta Own, DayDelta Adj)>();
        var adjustments = new List<ProjectedAdjustment>();
        var loyalty = new Dictionary<string, (long Earned, long Burned)>(StringComparer.Ordinal);

        void Apply(LogEvent e)
        {
            var delta = DayDelta.For(e.Type, e.AmountMinor);
            if (lastClosed.TryGetValue(e.StoreId, out var closed) && e.BusinessDate <= closed)
            {
                var target = closed.AddDays(1);
                adjustments.Add(new ProjectedAdjustment(e.EventId, e.StoreId, e.BusinessDate, target, delta));
                var cur = days.GetValueOrDefault((e.StoreId, target));
                days[(e.StoreId, target)] = (cur.Own, cur.Adj + delta);
            }
            else
            {
                var cur = days.GetValueOrDefault((e.StoreId, e.BusinessDate));
                days[(e.StoreId, e.BusinessDate)] = (cur.Own + delta, cur.Adj);
            }
        }

        foreach (var e in events.OrderBy(e => e.LogSeq))
        {
            // Closes that happened before this event was appended take effect first.
            if (closuresByStore.TryGetValue(e.StoreId, out var queue))
                while (queue.Count > 0 && queue.Peek().LogSeqMark < e.LogSeq)
                {
                    var through = queue.Dequeue().ThroughDate;
                    if (!lastClosed.TryGetValue(e.StoreId, out var cur) || through > cur) lastClosed[e.StoreId] = through;
                }

            if (EventType.IsLoyalty(e.Type))
            {
                var cur = loyalty.GetValueOrDefault(e.AccountId!);
                loyalty[e.AccountId!] = e.Type == EventType.LoyaltyEarn ? (cur.Earned + e.Points, cur.Burned) : (cur.Earned, cur.Burned + e.Points);
            }
            else if (e.Type == EventType.Sale)
            {
                Apply(e);
                knownSales.Add((e.StoreId, e.SaleId));
                if (parkedBySale.Remove((e.StoreId, e.SaleId), out var waiting))
                    foreach (var p in waiting)
                    {
                        Apply(p);
                        parkedRecords[p.EventId] = parkedRecords[p.EventId] with { ReleasedBy = e.EventId };
                    }
            }
            else if (knownSales.Contains((e.StoreId, e.SaleId)))
            {
                Apply(e);
            }
            else
            {
                if (!parkedBySale.TryGetValue((e.StoreId, e.SaleId), out var list)) parkedBySale[(e.StoreId, e.SaleId)] = list = [];
                list.Add(e);
                parkedRecords[e.EventId] = new ProjectedParked(e.EventId, e.StoreId, e.SaleId, null);
            }
        }

        return new Projection(
            days.OrderBy(d => d.Key.Store, StringComparer.Ordinal).ThenBy(d => d.Key.Day)
                .Select(d => new ProjectedDay(d.Key.Store, d.Key.Day, d.Value.Own, d.Value.Adj)).ToList(),
            adjustments.OrderBy(a => a.EventId, StringComparer.Ordinal).ToList(),
            parkedRecords.Values.OrderBy(p => p.EventId, StringComparer.Ordinal).ToList(),
            loyalty.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => new ProjectedLoyalty(l.Key, l.Value.Earned, l.Value.Burned)).ToList());
    }
}
