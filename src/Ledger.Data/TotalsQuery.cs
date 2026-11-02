using Npgsql;

namespace Ledger.Data;

/// <summary>
/// One store-day. NetMinor is the day's own events and never changes once the day is closed. Late events for a closed day
/// are booked as adjustments into the next open day: AdjustmentsInMinor is what landed in this day from earlier days, and
/// CorrectionsMinor is what later landed elsewhere but belongs to this day.
/// </summary>
public sealed record StoreDayTotal(
    string StoreId,
    DateOnly BusinessDate,
    long SalesMinor,
    long ReturnsMinor,
    long VoidsMinor,
    long NetMinor,
    bool Closed = false,
    long AdjustmentsInMinor = 0,
    long CorrectionsMinor = 0)
{
    /// <summary>What the day reports to finance: its own events plus adjustments booked into it.</summary>
    public long ReportedNetMinor => NetMinor + AdjustmentsInMinor;

    /// <summary>What the day truly sold: its own events plus every late event that belongs to it.</summary>
    public long CorrectedNetMinor => NetMinor + CorrectionsMinor;
}

public sealed class TotalsQuery(NpgsqlDataSource db)
{
    /// <summary>
    /// Totals for one store, or for every store. One statement means one snapshot: a caller sees a batch entirely or not at all.
    /// </summary>
    public async Task<List<StoreDayTotal>> GetAsync(string? storeId = null, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT coalesce(o.store_id, c.store_id), coalesce(o.business_date, c.business_date),
                   coalesce(o.sales_minor, 0), coalesce(o.returns_minor, 0), coalesce(o.voids_minor, 0), coalesce(o.net_minor, 0),
                   (s.last_closed_date IS NOT NULL AND coalesce(o.business_date, c.business_date) <= s.last_closed_date),
                   coalesce(o.adj_sales_minor - o.adj_returns_minor - o.adj_voids_minor, 0),
                   coalesce(c.sales - c.returns - c.voids, 0)::bigint
            FROM store_day_totals o
            FULL JOIN (SELECT store_id, original_date AS business_date,
                              sum(sales_minor) AS sales, sum(returns_minor) AS returns, sum(voids_minor) AS voids
                       FROM adjustments GROUP BY store_id, original_date) c
                   ON c.store_id = o.store_id AND c.business_date = o.business_date
            JOIN stores s ON s.store_id = coalesce(o.store_id, c.store_id)
            WHERE @store::text IS NULL OR s.store_id = @store
            ORDER BY coalesce(o.store_id, c.store_id) COLLATE "C", coalesce(o.business_date, c.business_date)
            """);
        cmd.Parameters.AddWithValue("store", (object?)storeId ?? DBNull.Value);

        var rows = new List<StoreDayTotal>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new StoreDayTotal(
                reader.GetString(0), DateOnly.FromDateTime(reader.GetDateTime(1)),
                reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
                reader.GetBoolean(6), reader.GetInt64(7), reader.GetInt64(8)));
        return rows;
    }
}
