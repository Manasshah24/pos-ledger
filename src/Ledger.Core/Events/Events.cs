namespace Ledger.Core.Events;

public static class EventType
{
    public const string Sale = "sale";
    public const string Return = "return";
    public const string Void = "void";

    /// <summary>Types the ledger can currently ingest. Gift card and loyalty types join in their own stages.</summary>
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal) { Sale, Return, Void };
}

/// <summary>Why an event was refused. Stored with the raw event so nothing is silently dropped.</summary>
public static class RejectReason
{
    public const string MalformedEvent = "malformed_event";
    public const string MissingEventId = "missing_event_id";
    public const string EventIdTooLong = "event_id_too_long";
    public const string MissingStoreId = "missing_store_id";
    public const string MissingTerminalId = "missing_terminal_id";
    public const string MissingType = "missing_type";
    public const string UnsupportedType = "unsupported_type";
    public const string MissingOccurredAt = "missing_occurred_at";
    public const string InvalidOccurredAt = "invalid_occurred_at";
    public const string MissingSaleId = "missing_sale_id";
    public const string InvalidAmount = "invalid_amount";
    public const string InvalidCharacters = "invalid_characters";
    public const string UnknownStore = "unknown_store";

    /// <summary>The same event id arrived again with different content. The first version wins; the conflict is recorded.</summary>
    public const string EventIdConflict = "event_id_conflict";
}

/// <summary>An event exactly as received, before any trust is placed in it.</summary>
public sealed record IncomingEvent(
    string? EventId,
    string? StoreId,
    string? TerminalId,
    string? Type,
    string? OccurredAt,
    string? SaleId,
    long? AmountMinor);

/// <summary>An event that passed validation. OccurredAtUtc is truncated to microseconds, the precision the database stores.</summary>
public sealed record ValidEvent(
    string EventId,
    string StoreId,
    string TerminalId,
    string Type,
    DateTimeOffset OccurredAtUtc,
    string? SaleId,
    long? AmountMinor);

public sealed record RejectedEvent(int Index, string? EventId, string Reason);

public sealed record IngestResult(int Inserted, int Duplicates, IReadOnlyList<RejectedEvent> Rejected);

/// <summary>One element of an uploaded batch. Event is null when the element could not even be read as an event.</summary>
public sealed record RawEvent(int Index, string RawJson, IncomingEvent? Event);
