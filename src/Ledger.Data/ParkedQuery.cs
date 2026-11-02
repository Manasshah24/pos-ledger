using Npgsql;

namespace Ledger.Data;

public sealed record ParkedEvent(string EventId, string Type, string StoreId, string SaleId, long AmountMinor, DateTimeOffset ParkedAt);

public sealed class ParkedQuery(NpgsqlDataSource db)
{
    /// <summary>Returns and voids still waiting for their sale, oldest first, so nothing waits unseen.</summary>
    public async Task<List<ParkedEvent>> GetOpenAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT p.event_id, e.type, e.store_id, p.sale_id, e.amount_minor, p.parked_at
            FROM parked_events p JOIN events e USING (event_id)
            WHERE p.resolved_at IS NULL
            ORDER BY p.parked_at, p.event_id COLLATE "C"
            """);
        var rows = new List<ParkedEvent>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new ParkedEvent(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc))));
        return rows;
    }
}
