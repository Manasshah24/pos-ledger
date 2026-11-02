namespace Ledger.Harness;

public static class EventTypes
{
    public const string Sale = "sale";
    public const string Return = "return";
    public const string Void = "void";
}

/// <summary>
/// One business event as a terminal emits it. For a sale, SaleId is its own id and AmountMinor is the basket total.
/// For a return, SaleId is the referenced sale and AmountMinor is the refunded part.
/// For a void, SaleId is the referenced sale and AmountMinor is that sale's full total.
/// </summary>
public sealed record HarnessEvent(
    string EventId,
    string StoreId,
    string TerminalId,
    string Type,
    DateTimeOffset OccurredAt,
    string SaleId,
    long AmountMinor);

/// <summary>An event as it reaches the central ledger: possibly late, out of order, or a repeat.</summary>
public sealed record Delivery(HarnessEvent Event, DateTimeOffset ArrivedAt, bool IsDuplicate);

public sealed record StoreInfo(string StoreId, string TimeZoneId);

/// <summary>A store lost connectivity for [From, To); events created inside were buffered until To.</summary>
public sealed record Outage(string StoreId, DateTimeOffset From, DateTimeOffset To);

public sealed record GeneratedData(
    IReadOnlyList<StoreInfo> Stores,
    IReadOnlyList<HarnessEvent> UniqueEvents,
    IReadOnlyList<Delivery> Deliveries,
    IReadOnlySet<string> OrphanReturnEventIds,
    IReadOnlyList<Outage> Outages)
{
    /// <summary>Splits the delivery stream into upload batches, preserving arrival order.</summary>
    public IEnumerable<IReadOnlyList<Delivery>> ToBatches(int batchSize) =>
        Deliveries.Chunk(batchSize).Select(c => (IReadOnlyList<Delivery>)c);
}
