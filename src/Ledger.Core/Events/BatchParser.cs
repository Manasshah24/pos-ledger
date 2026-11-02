using System.Text.Json;

namespace Ledger.Core.Events;

public sealed record BatchParseResult(IReadOnlyList<RawEvent>? Events, string? Error);

/// <summary>
/// Reads an upload body of the form {"events":[...]}. A bad element does not poison the batch: it becomes a RawEvent
/// with a null Event and is rejected individually later. Only a body that is not a usable batch at all is an error.
/// </summary>
public static class BatchParser
{
    public const int MaxBatchSize = 5_000;

    public const string InvalidJson = "invalid_json";
    public const string MissingEventsArray = "missing_events_array";
    public const string BatchTooLarge = "batch_too_large";

    // The Web defaults allow numbers written as strings; money must arrive as a real JSON number.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict,
    };

    public static BatchParseResult Parse(string body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return new(null, InvalidJson);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("events", out var array)
                || array.ValueKind != JsonValueKind.Array)
                return new(null, MissingEventsArray);

            if (array.GetArrayLength() > MaxBatchSize) return new(null, BatchTooLarge);

            var items = new List<RawEvent>(array.GetArrayLength());
            var index = 0;
            foreach (var element in array.EnumerateArray())
            {
                items.Add(new RawEvent(index++, element.GetRawText(), Read(element)));
            }

            return new(items, null);
        }
    }

    private static IncomingEvent? Read(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        try
        {
            return element.Deserialize<IncomingEvent>(Options);
        }
        catch (JsonException)
        {
            return null; // e.g. amountMinor given as 12.5 or "12": money must be a whole number
        }
    }
}
