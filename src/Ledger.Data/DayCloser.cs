using Ledger.Core;
using Ledger.Core.Events;
using Npgsql;

namespace Ledger.Data;

public sealed record CloseResult(string StoreId, DateOnly? PreviousClosed, DateOnly ClosedThrough, bool Changed);

/// <summary>
/// Closes a store's business days. It takes the store's advisory lock exclusively: that waits for every in-flight ingest of the
/// store (they hold it shared until commit) and makes later ingests queue behind the close, so each event is unambiguously
/// either before the close (booked into its day) or after it (booked as an adjustment). Closing is idempotent.
/// </summary>
public sealed class DayCloser(NpgsqlDataSource db, IClock clock, LedgerOptions options)
{
    /// <summary>Closes every day up to and including <paramref name="through"/>. Null when the store does not exist.</summary>
    public async Task<CloseResult?> CloseThroughAsync(string storeId, DateOnly through, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended('store:' || @id, 0))", conn, tx))
        {
            lockCmd.Parameters.AddWithValue("id", storeId);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        DateOnly? previous;
        await using (var select = new NpgsqlCommand("SELECT last_closed_date FROM stores WHERE store_id = @id", conn, tx))
        {
            select.Parameters.AddWithValue("id", storeId);
            await using var reader = await select.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            previous = reader.IsDBNull(0) ? null : DateOnly.FromDateTime(reader.GetDateTime(0));
        }

        if (previous is { } last && last >= through)
            return new CloseResult(storeId, previous, last, false);

        await using (var update = new NpgsqlCommand("UPDATE stores SET last_closed_date = @through WHERE store_id = @id", conn, tx))
        {
            update.Parameters.AddWithValue("through", through);
            update.Parameters.AddWithValue("id", storeId);
            await update.ExecuteNonQueryAsync(ct);
        }

        await using (var record = new NpgsqlCommand("""
            INSERT INTO day_closures (store_id, through_date, closed_at, log_seq_mark)
            SELECT @id, @through, @now, coalesce(max(log_seq), 0) FROM events WHERE store_id = @id
            """, conn, tx))
        {
            record.Parameters.AddWithValue("id", storeId);
            record.Parameters.AddWithValue("through", through);
            record.Parameters.AddWithValue("now", clock.UtcNow.UtcDateTime);
            await record.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return new CloseResult(storeId, previous, through, true);
    }

    /// <summary>Closes, for every store, each business day that has ended according to the clock. Returns only the closes that happened.</summary>
    public async Task<List<CloseResult>> CloseDueAsync(CancellationToken ct = default)
    {
        var zones = new List<(string StoreId, TimeZoneInfo Zone)>();
        await using (var cmd = db.CreateCommand("SELECT store_id, tz FROM stores ORDER BY store_id"))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                zones.Add((reader.GetString(0), TimeZoneInfo.FindSystemTimeZoneById(reader.GetString(1))));

        var now = clock.UtcNow;
        var closed = new List<CloseResult>();
        foreach (var (storeId, zone) in zones)
        {
            // The business day containing "now" is still open; the one before it has ended.
            var through = BusinessDay.For(now, zone, options.BusinessDayCutoffHour).AddDays(-1);
            var result = await CloseThroughAsync(storeId, through, ct);
            if (result is { Changed: true }) closed.Add(result);
        }

        return closed;
    }
}
