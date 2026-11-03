using Ledger.Core.Events;
using Npgsql;

namespace Ledger.Data;

/// <summary>Appends a single already-validated event inside a caller's transaction. Used by the synchronous (online) paths.</summary>
internal static class EventAppender
{
    /// <summary>Inserts the event. False means an event with this id already exists (nothing was written).</summary>
    public static async Task<bool> TryInsertAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, ValidEvent e, DateOnly businessDate, DateTime receivedAtUtc, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO events (event_id, store_id, terminal_id, type, occurred_at, received_at, sale_id, amount_minor, business_date,
                                card_id, lease_id, account_id, points)
            VALUES (@id, @store, @terminal, @type, @occ, @received, @sale, @amount, @day, @card, @lease, @account, @points)
            ON CONFLICT (event_id) DO NOTHING
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", e.EventId);
        cmd.Parameters.AddWithValue("store", e.StoreId);
        cmd.Parameters.AddWithValue("terminal", e.TerminalId);
        cmd.Parameters.AddWithValue("type", e.Type);
        cmd.Parameters.AddWithValue("occ", e.OccurredAtUtc.UtcDateTime);
        cmd.Parameters.AddWithValue("received", receivedAtUtc);
        cmd.Parameters.AddWithValue("sale", (object?)e.SaleId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("amount", (object?)e.AmountMinor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("day", businessDate);
        cmd.Parameters.AddWithValue("card", (object?)e.CardId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("lease", (object?)e.LeaseId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("account", (object?)e.AccountId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("points", (object?)e.Points ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>True when the stored event with this id has exactly this content (a harmless retry rather than a conflict).</summary>
    public static async Task<bool> SameAsStoredAsync(NpgsqlConnection conn, NpgsqlTransaction tx, ValidEvent e, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT store_id, terminal_id, type, occurred_at, sale_id, amount_minor, card_id, lease_id, account_id, points
            FROM events WHERE event_id = @id
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", e.EventId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return false;

        var stored = new ValidEvent(
            e.EventId, reader.GetString(0), reader.GetString(1), reader.GetString(2),
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc)),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetInt64(5),
            reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetInt64(9));
        return stored == e;
    }
}
