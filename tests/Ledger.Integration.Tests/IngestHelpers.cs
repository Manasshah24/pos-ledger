using System.Globalization;
using System.Text.Json;
using Dapper;
using Ledger.Core;
using Ledger.Core.Events;
using Ledger.Data;
using Ledger.Harness;
using Npgsql;

namespace Ledger.Integration.Tests;

internal static class IngestHelpers
{
    public static readonly DateTimeOffset Noon = new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);

    public static EventIngestor Ingestor(NpgsqlDataSource db, IClock? clock = null) =>
        new(db, clock ?? new ManualClock(Noon.AddDays(1)), new LedgerOptions());

    public static async Task RegisterStoresAsync(NpgsqlDataSource db, params StoreInfo[] stores)
    {
        var registry = new StoreRegistry(db);
        foreach (var s in stores) await registry.UpsertAsync(s.StoreId, s.TimeZoneId, "USD");
    }

    public static IncomingEvent Incoming(string id, string store = "S1", string type = EventType.Sale, string? at = null, long amount = 1000, string? sale = null) =>
        new(id, store, store + "-T1", type, at ?? "2026-01-05T12:00:00Z", sale ?? "sale-" + id, amount);

    public static RawEvent Raw(int index, IncomingEvent e) => new(index, JsonSerializer.Serialize(e), e);

    public static List<RawEvent> Raws(params IncomingEvent[] events) => events.Select((e, i) => Raw(i, e)).ToList();

    public static IncomingEvent ToIncoming(HarnessEvent e) =>
        new(e.EventId, e.StoreId, e.TerminalId, e.Type, e.OccurredAt.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture), e.SaleId, e.AmountMinor);

    public static string ToBatchJson(IEnumerable<HarnessEvent> events) =>
        JsonSerializer.Serialize(new { events = events.Select(ToIncoming) }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static async Task<int> CountAsync(NpgsqlDataSource db, string table)
    {
        await using var conn = await db.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<int>($"SELECT count(*) FROM {table}");
    }

    public static async Task<List<string>> EventIdsAsync(NpgsqlDataSource db)
    {
        await using var conn = await db.OpenConnectionAsync();
        return (await conn.QueryAsync<string>("SELECT event_id FROM events ORDER BY event_id")).ToList();
    }
}
