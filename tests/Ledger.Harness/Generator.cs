namespace Ledger.Harness;

public sealed record GeneratorOptions
{
    public ulong Seed { get; init; } = 1;

    /// <summary>Total deliveries including duplicates (the DoD says 100,000).</summary>
    public int TotalDeliveries { get; init; } = 100_000;

    public double DuplicateRate { get; init; } = 0.12;
    public double OrphanReturnRate { get; init; } = 0.03;
    public double ReturnRate { get; init; } = 0.08;
    public double VoidRate { get; init; } = 0.02;
    public int Stores { get; init; } = 300;
    public int TerminalsPerStore { get; init; } = 8;
    public int Days { get; init; } = 3;
    public int Outages { get; init; } = 20;
    public int MaxLateHours { get; init; } = 48;

    public DateTimeOffset Start { get; init; } = new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);
}

/// <summary>
/// Builds a seeded, reproducible stream of sale/return/void events and then damages it the way the
/// real world does: duplicates, arrival delays up to 48h, returns that arrive before their sale, and
/// store outages that hold events back. Stage 0 covers sale/return/void; gift card and loyalty events
/// are added in their own stages.
/// </summary>
public static class Generator
{
    private static readonly string[] TimeZones =
        ["Asia/Kolkata", "America/New_York", "Europe/London", "America/Los_Angeles", "Australia/Sydney"];

    private sealed class Slot(HarnessEvent ev, DateTimeOffset arrival)
    {
        public HarnessEvent Event { get; } = ev;
        public DateTimeOffset Arrival { get; set; } = arrival;
    }

    public static GeneratedData Generate(GeneratorOptions o)
    {
        var rng = new SplitMix64(o.Seed);
        var maxLate = TimeSpan.FromHours(o.MaxLateHours);

        var unique = o.TotalDeliveries - Round(o.TotalDeliveries * o.DuplicateRate);
        var duplicates = o.TotalDeliveries - unique;
        var orphans = Round(unique * o.OrphanReturnRate);
        var returns = Math.Max(orphans, Round(unique * o.ReturnRate));
        var voids = Round(unique * o.VoidRate);
        var sales = unique - returns - voids;
        if (sales < returns + voids)
            throw new ArgumentException("Rates leave fewer sales than returns plus voids.");

        var stores = Enumerable.Range(0, o.Stores)
            .Select(i => new StoreInfo($"S{i + 1:D3}", TimeZones[i % TimeZones.Length]))
            .ToList();

        var seq = 0;
        string NextEventId() => $"evt-{++seq:D8}";
        DateTimeOffset RandomMoment() => o.Start.AddSeconds(rng.Range(0, (long)o.Days * 86_400 - 1));
        TimeSpan Delay(long maxSeconds) => TimeSpan.FromSeconds(rng.Range(0, maxSeconds));

        // Sales.
        var saleSlots = new List<Slot>(sales);
        for (var i = 0; i < sales; i++)
        {
            var store = stores[rng.Next(stores.Count)];
            var terminal = $"{store.StoreId}-T{rng.Next(o.TerminalsPerStore) + 1:D2}";
            var occurred = RandomMoment();
            var ev = new HarnessEvent(NextEventId(), store.StoreId, terminal, EventTypes.Sale, occurred,
                $"sale-{i:D7}", rng.Range(100, 50_000));
            saleSlots.Add(new Slot(ev, occurred + Delay((long)maxLate.TotalSeconds)));
        }

        // Pick distinct sales to void and to return, so a refund can never exceed its sale.
        var order = Enumerable.Range(0, sales).ToList();
        rng.Shuffle(order);

        var dependants = new List<(Slot Slot, Slot Sale, bool Orphan)>();
        for (var k = 0; k < voids; k++)
        {
            var sale = saleSlots[order[k]];
            var occurred = sale.Event.OccurredAt.AddMinutes(rng.Range(1, 30));
            var ev = new HarnessEvent(NextEventId(), sale.Event.StoreId, sale.Event.TerminalId, EventTypes.Void,
                occurred, sale.Event.SaleId, sale.Event.AmountMinor);
            dependants.Add((new Slot(ev, occurred + Delay((long)maxLate.TotalSeconds)), sale, false));
        }

        for (var k = 0; k < returns; k++)
        {
            var sale = saleSlots[order[voids + k]];
            var occurred = sale.Event.OccurredAt.AddMinutes(rng.Range(60, 360));
            var isOrphan = k < orphans;
            var ev = new HarnessEvent(NextEventId(), sale.Event.StoreId, sale.Event.TerminalId, EventTypes.Return,
                occurred, sale.Event.SaleId, rng.Range(1, sale.Event.AmountMinor));
            var maxDelay = isOrphan ? 24 * 3600 : (long)maxLate.TotalSeconds;
            dependants.Add((new Slot(ev, occurred + Delay(maxDelay)), sale, isOrphan));
        }

        // Store outages: events created during an outage are buffered and arrive after it ends.
        var outages = new List<Outage>();
        for (var k = 0; k < o.Outages; k++)
        {
            var from = RandomMoment();
            outages.Add(new Outage(stores[rng.Next(stores.Count)].StoreId, from, from.AddMinutes(rng.Range(30, 360))));
        }

        var allSlots = saleSlots.Concat(dependants.Select(d => d.Slot)).ToList();
        foreach (var outage in outages)
        {
            foreach (var slot in allSlots)
            {
                if (slot.Event.StoreId != outage.StoreId) continue;
                if (slot.Event.OccurredAt < outage.From || slot.Event.OccurredAt >= outage.To) continue;
                var restored = outage.To.AddSeconds(rng.Range(0, 600));
                if (slot.Arrival < restored) slot.Arrival = restored;
            }
        }

        // Dependency order: a normal return/void arrives after its sale; an orphan return arrives before it.
        var orphanIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (slot, sale, orphan) in dependants)
        {
            if (orphan)
            {
                orphanIds.Add(slot.Event.EventId);
                if (sale.Arrival <= slot.Arrival)
                    sale.Arrival = slot.Arrival.AddSeconds(rng.Range(1, 3600));
            }
            else if (slot.Arrival <= sale.Arrival)
            {
                slot.Arrival = sale.Arrival.AddMinutes(1);
            }
        }

        // Duplicates: the terminal re-uploads an event some time after its first delivery.
        var deliveries = allSlots.Select(s => new Delivery(s.Event, s.Arrival, false)).ToList();
        for (var k = 0; k < duplicates; k++)
        {
            var slot = allSlots[rng.Next(allSlots.Count)];
            var room = (long)(slot.Event.OccurredAt + maxLate - slot.Arrival).TotalSeconds;
            deliveries.Add(new Delivery(slot.Event, slot.Arrival.AddSeconds(rng.Range(1, Math.Max(1, room))), true));
        }

        deliveries.Sort((a, b) =>
        {
            var c = a.ArrivedAt.CompareTo(b.ArrivedAt);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Event.EventId, b.Event.EventId);
            return c != 0 ? c : a.IsDuplicate.CompareTo(b.IsDuplicate);
        });

        return new GeneratedData(stores, allSlots.Select(s => s.Event).ToList(), deliveries, orphanIds, outages);
    }

    private static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
}
