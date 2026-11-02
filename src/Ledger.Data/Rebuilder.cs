using Ledger.Core;
using Ledger.Core.Events;
using Npgsql;

namespace Ledger.Data;

public sealed record RebuildResult(ProjectionHashes Before, ProjectionHashes After, bool Identical, int Events, int StoreDays, int Adjustments, int Parked);

/// <summary>
/// Throws away the read models and rebuilds them from the event log. The result must be identical to what live ingest
/// produced, and identical every time, which is what the returned hashes let a caller check.
/// </summary>
public sealed class Rebuilder(NpgsqlDataSource db)
{
    /// <summary>Fingerprint of the live read models, taken from one consistent snapshot.</summary>
    public async Task<ProjectionHashes> HashAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        return await HashTablesAsync(conn, tx, ct);
    }

    public async Task<RebuildResult> RebuildAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Stop the world for ingest and close, store by store, until we commit: waits for in-flight batches, queues new ones.
        await using (var lockCmd = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(k)
            FROM (SELECT hashtextextended('store:' || store_id, 0) AS k FROM stores) AS keys
            ORDER BY k
            """, conn, tx))
            await lockCmd.ExecuteNonQueryAsync(ct);

        var before = await HashTablesAsync(conn, tx, ct);

        var events = new List<LogEvent>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT log_seq, event_id, store_id, type, business_date, sale_id, amount_minor FROM events ORDER BY log_seq", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                events.Add(new LogEvent(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    DateOnly.FromDateTime(reader.GetDateTime(4)), reader.GetString(5), reader.GetInt64(6)));

        var closures = new List<ClosureMark>();
        await using (var cmd = new NpgsqlCommand("SELECT store_id, log_seq_mark, through_date FROM day_closures ORDER BY id", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                closures.Add(new ClosureMark(reader.GetString(0), reader.GetInt64(1), DateOnly.FromDateTime(reader.GetDateTime(2))));

        var projection = Projector.Replay(events, closures);

        await using (var set = new NpgsqlCommand("SET LOCAL ledger.rebuild = 'on'", conn, tx)) await set.ExecuteNonQueryAsync(ct);
        foreach (var table in new[] { "adjustments", "store_day_totals", "parked_events" })
            await using (var del = new NpgsqlCommand($"DELETE FROM {table}", conn, tx)) await del.ExecuteNonQueryAsync(ct);

        await InsertTotalsAsync(conn, tx, projection.Days, ct);
        await InsertAdjustmentsAsync(conn, tx, projection.Adjustments, ct);
        var open = projection.Parked.Where(p => p.ReleasedBy is null).ToList();
        await InsertParkedAsync(conn, tx, open, ct);

        var after = await HashTablesAsync(conn, tx, ct);
        await tx.CommitAsync(ct);

        return new RebuildResult(before, after, before == after, events.Count, projection.Days.Count, projection.Adjustments.Count, open.Count);
    }

    private static async Task<ProjectionHashes> HashTablesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        var totals = new List<string>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT store_id, business_date, sales_minor, returns_minor, voids_minor, adj_sales_minor, adj_returns_minor, adj_voids_minor
            FROM store_day_totals
            """, conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                totals.Add(ProjectionHash.TotalsLine(reader.GetString(0), DateOnly.FromDateTime(reader.GetDateTime(1)),
                    new DayDelta(reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)),
                    new DayDelta(reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7))));

        var adjustments = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT event_id, store_id, original_date, applied_date, sales_minor, returns_minor, voids_minor FROM adjustments", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                adjustments.Add(ProjectionHash.AdjustmentLine(reader.GetString(0), reader.GetString(1),
                    DateOnly.FromDateTime(reader.GetDateTime(2)), DateOnly.FromDateTime(reader.GetDateTime(3)),
                    new DayDelta(reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6))));

        var parked = new List<string>();
        await using (var cmd = new NpgsqlCommand("SELECT event_id FROM parked_events WHERE resolved_at IS NULL", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) parked.Add(ProjectionHash.ParkedLine(reader.GetString(0)));

        return ProjectionHash.Of(totals, adjustments, parked);
    }

    private static async Task InsertTotalsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyList<ProjectedDay> days, CancellationToken ct)
    {
        if (days.Count == 0) return;

        await using var cmd = new NpgsqlCommand("""
            INSERT INTO store_day_totals (store_id, business_date, sales_minor, returns_minor, voids_minor, adj_sales_minor, adj_returns_minor, adj_voids_minor)
            SELECT * FROM unnest(@stores, @days, @sales, @returns, @voids, @adj_sales, @adj_returns, @adj_voids)
            """, conn, tx);
        cmd.Parameters.AddWithValue("stores", days.Select(d => d.StoreId).ToArray());
        cmd.Parameters.AddWithValue("days", days.Select(d => d.Day).ToArray());
        cmd.Parameters.AddWithValue("sales", days.Select(d => d.Own.Sales).ToArray());
        cmd.Parameters.AddWithValue("returns", days.Select(d => d.Own.Returns).ToArray());
        cmd.Parameters.AddWithValue("voids", days.Select(d => d.Own.Voids).ToArray());
        cmd.Parameters.AddWithValue("adj_sales", days.Select(d => d.Adjustments.Sales).ToArray());
        cmd.Parameters.AddWithValue("adj_returns", days.Select(d => d.Adjustments.Returns).ToArray());
        cmd.Parameters.AddWithValue("adj_voids", days.Select(d => d.Adjustments.Voids).ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // created_at is bookkeeping, not state: a rebuilt adjustment is stamped with the time its event was received.
    private static async Task InsertAdjustmentsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyList<ProjectedAdjustment> adjustments, CancellationToken ct)
    {
        if (adjustments.Count == 0) return;

        await using var cmd = new NpgsqlCommand("""
            INSERT INTO adjustments (event_id, store_id, original_date, applied_date, sales_minor, returns_minor, voids_minor, created_at)
            SELECT t.event_id, t.store_id, t.original_date, t.applied_date, t.sales, t.returns, t.voids, e.received_at
            FROM unnest(@ids, @stores, @original, @applied, @sales, @returns, @voids)
                 AS t(event_id, store_id, original_date, applied_date, sales, returns, voids)
            JOIN events e ON e.event_id = t.event_id
            """, conn, tx);
        cmd.Parameters.AddWithValue("ids", adjustments.Select(a => a.EventId).ToArray());
        cmd.Parameters.AddWithValue("stores", adjustments.Select(a => a.StoreId).ToArray());
        cmd.Parameters.AddWithValue("original", adjustments.Select(a => a.OriginalDate).ToArray());
        cmd.Parameters.AddWithValue("applied", adjustments.Select(a => a.AppliedDate).ToArray());
        cmd.Parameters.AddWithValue("sales", adjustments.Select(a => a.Delta.Sales).ToArray());
        cmd.Parameters.AddWithValue("returns", adjustments.Select(a => a.Delta.Returns).ToArray());
        cmd.Parameters.AddWithValue("voids", adjustments.Select(a => a.Delta.Voids).ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertParkedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyList<ProjectedParked> open, CancellationToken ct)
    {
        if (open.Count == 0) return;

        await using var cmd = new NpgsqlCommand(
            "INSERT INTO parked_events (event_id, sale_id, parked_at) SELECT e.event_id, e.sale_id, e.received_at FROM events e WHERE e.event_id = ANY(@ids)", conn, tx);
        cmd.Parameters.AddWithValue("ids", open.Select(p => p.EventId).ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
