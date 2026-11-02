using Npgsql;

namespace Ledger.Data;

public sealed record StoreDayTotal(string StoreId, DateOnly BusinessDate, long SalesMinor, long ReturnsMinor, long VoidsMinor, long NetMinor);

public sealed class TotalsQuery(NpgsqlDataSource db)
{
    /// <summary>
    /// Totals for one store, or for every store. One statement means one snapshot: a caller sees a batch entirely or not at all.
    /// </summary>
    public async Task<List<StoreDayTotal>> GetAsync(string? storeId = null, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT store_id, business_date, sales_minor, returns_minor, voids_minor, net_minor
            FROM store_day_totals
            WHERE @store::text IS NULL OR store_id = @store
            ORDER BY store_id COLLATE "C", business_date
            """);
        cmd.Parameters.AddWithValue("store", (object?)storeId ?? DBNull.Value);

        var rows = new List<StoreDayTotal>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new StoreDayTotal(reader.GetString(0), DateOnly.FromDateTime(reader.GetDateTime(1)), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5)));
        return rows;
    }
}
