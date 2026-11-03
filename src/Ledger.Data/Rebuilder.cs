using Ledger.Core;
using Ledger.Core.Events;
using Npgsql;

namespace Ledger.Data;

public sealed record RebuildResult(
    ProjectionHashes Before, ProjectionHashes After, bool Identical,
    int Events, int StoreDays, int Adjustments, int Parked,
    long? SnapshotId = null, bool SnapshotRejected = false);

public sealed record SnapshotInfo(long Id, long LogSeq, long LastClosureId, ProjectionHashes Hashes);

/// <summary>
/// Rebuilds the read models from the event log, optionally starting from a snapshot, and takes the snapshots.
/// The result must be identical to what live ingest produced, and identical whether it started from the beginning of the
/// log or from a snapshot: the returned hashes let a caller check both.
/// </summary>
public sealed class Rebuilder(NpgsqlDataSource db, IClock clock)
{
    private const string TradeAndLoyalty = "('sale', 'return', 'void', 'loyalty_earn', 'loyalty_burn')";

    /// <summary>Fingerprint of the live read models, taken from one consistent snapshot.</summary>
    public async Task<ProjectionHashes> HashAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        return await HashTablesAsync(conn, tx, ct);
    }

    /// <summary>
    /// Copies the live read models into a snapshot. Taken while every store is locked, so the read models reflect exactly the
    /// events up to the highest log_seq. Costs a copy of the read models, not a replay of the log. All or nothing.
    /// </summary>
    public async Task<SnapshotInfo> CreateSnapshotAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await LockAllStoresAsync(conn, tx, ct);

        var hashes = await HashTablesAsync(conn, tx, ct);
        long id, logSeq, closureId;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO snapshots (log_seq, last_closure_id, created_at, hash_totals, hash_adjustments, hash_parked, hash_loyalty, hash_overall)
            VALUES ((SELECT coalesce(max(log_seq), 0) FROM events), (SELECT coalesce(max(id), 0) FROM day_closures), @now, @t, @a, @p, @l, @o)
            RETURNING id, log_seq, last_closure_id
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("now", clock.UtcNow.UtcDateTime);
            cmd.Parameters.AddWithValue("t", hashes.Totals);
            cmd.Parameters.AddWithValue("a", hashes.Adjustments);
            cmd.Parameters.AddWithValue("p", hashes.Parked);
            cmd.Parameters.AddWithValue("l", hashes.Loyalty);
            cmd.Parameters.AddWithValue("o", hashes.Overall);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            (id, logSeq, closureId) = (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
        }

        foreach (var sql in new[]
        {
            "INSERT INTO snapshot_days SELECT @id, store_id, business_date, sales_minor, returns_minor, voids_minor, adj_sales_minor, adj_returns_minor, adj_voids_minor FROM store_day_totals",
            "INSERT INTO snapshot_adjustments SELECT @id, event_id, store_id, original_date, applied_date, sales_minor, returns_minor, voids_minor FROM adjustments",
            "INSERT INTO snapshot_loyalty SELECT @id, account_id, earned_points, burned_points FROM loyalty_accounts",
            "INSERT INTO snapshot_parked SELECT @id, event_id FROM parked_events WHERE resolved_at IS NULL",
            "INSERT INTO snapshot_closed SELECT @id, store_id, last_closed_date FROM stores WHERE last_closed_date IS NOT NULL",
        })
        {
            await using var copy = new NpgsqlCommand(sql, conn, tx);
            copy.Parameters.AddWithValue("id", id);
            await copy.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return new SnapshotInfo(id, logSeq, closureId, hashes);
    }

    /// <summary>
    /// Replaces the read models with a rebuild from the log. With <paramref name="useSnapshot"/> it starts from the latest
    /// snapshot that passes verification and replays only the events after it; a snapshot that fails verification is ignored
    /// (and reported) and the rebuild falls back to the whole log.
    /// </summary>
    public async Task<RebuildResult> RebuildAsync(bool useSnapshot = false, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await LockAllStoresAsync(conn, tx, ct);

        var before = await HashTablesAsync(conn, tx, ct);

        SnapshotInfo? snapshot = null;
        ProjectionSeed? seed = null;
        var rejected = false;
        if (useSnapshot)
        {
            var loaded = await TryLoadSnapshotAsync(conn, tx, ct);
            (snapshot, seed, rejected) = (loaded.Snapshot, loaded.Seed, loaded.Rejected);
        }

        var closures = new List<ClosureMark>();
        await using (var cmd = new NpgsqlCommand("SELECT store_id, log_seq_mark, through_date FROM day_closures WHERE id > @after ORDER BY id", conn, tx))
        {
            cmd.Parameters.AddWithValue("after", snapshot?.LastClosureId ?? 0L);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                closures.Add(new ClosureMark(reader.GetString(0), reader.GetInt64(1), DateOnly.FromDateTime(reader.GetDateTime(2))));
        }

        var replayed = 0;
        Projection projection;
        await using (var eventsCmd = new NpgsqlCommand($"""
            SELECT log_seq, event_id, store_id, type, business_date, sale_id, amount_minor, account_id, points
            FROM events WHERE log_seq > @after AND type IN {TradeAndLoyalty} ORDER BY log_seq
            """, conn, tx))
        {
            eventsCmd.Parameters.AddWithValue("after", snapshot?.LogSeq ?? 0L);
            if (seed is null)
            {
                // From the start of the log: stream the events straight into the replay, never holding them all in memory.
                await using var reader = await eventsCmd.ExecuteReaderAsync(ct);
                projection = Projector.Replay(Stream(reader, () => replayed++), closures, alreadyOrdered: true);
            }
            else
            {
                var tail = new List<LogEvent>();
                await using (var reader = await eventsCmd.ExecuteReaderAsync(ct))
                    foreach (var e in Stream(reader, () => replayed++)) tail.Add(e);

                // Sales from before the snapshot that the tail's returns and voids refer to.
                var known = await KnownSalesAsync(conn, tx, snapshot!.LogSeq,
                    tail.Where(e => e.Type is EventType.Return or EventType.Void).Select(e => (e.StoreId, e.SaleId)).Distinct().ToList(), ct);
                projection = Projector.Replay(tail, closures, seed with { KnownSales = known }, alreadyOrdered: true);
            }
        }

        await using (var set = new NpgsqlCommand("SET LOCAL ledger.rebuild = 'on'", conn, tx)) await set.ExecuteNonQueryAsync(ct);
        foreach (var table in new[] { "adjustments", "store_day_totals", "parked_events", "loyalty_accounts" })
            await using (var del = new NpgsqlCommand($"DELETE FROM {table}", conn, tx)) await del.ExecuteNonQueryAsync(ct);

        await InsertTotalsAsync(conn, tx, projection.Days, ct);
        await InsertAdjustmentsAsync(conn, tx, projection.Adjustments, ct);
        var open = projection.Parked.Where(p => p.ReleasedBy is null).ToList();
        await InsertParkedAsync(conn, tx, open, ct);
        await InsertLoyaltyAsync(conn, tx, projection.Loyalty, ct);

        var after = await HashTablesAsync(conn, tx, ct);
        await tx.CommitAsync(ct);

        return new RebuildResult(before, after, before == after, replayed, projection.Days.Count, projection.Adjustments.Count, open.Count,
            snapshot?.Id, rejected);
    }

    private static IEnumerable<LogEvent> Stream(NpgsqlDataReader reader, Action counted)
    {
        while (reader.Read())
        {
            counted();
            yield return new LogEvent(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                DateOnly.FromDateTime(reader.GetDateTime(4)),
                reader.IsDBNull(5) ? "" : reader.GetString(5), reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? 0 : reader.GetInt64(8));
        }
    }

    private static async Task LockAllStoresAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        // Stop the world for ingest and close, store by store, until we commit: waits for in-flight batches, queues new ones.
        await using var lockCmd = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(k)
            FROM (SELECT hashtextextended('store:' || store_id, 0) AS k FROM stores) AS keys
            ORDER BY k
            """, conn, tx);
        await lockCmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<HashSet<(string Store, string Sale)>> KnownSalesAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long upToSeq, List<(string Store, string Sale)> pairs, CancellationToken ct)
    {
        var known = new HashSet<(string, string)>();
        if (pairs.Count == 0) return known;

        await using var cmd = new NpgsqlCommand("""
            SELECT DISTINCT e.store_id, e.sale_id
            FROM events e JOIN unnest(@stores, @sales) AS t(st, sa) ON e.store_id = t.st AND e.sale_id = t.sa
            WHERE e.type = 'sale' AND e.log_seq <= @seq
            """, conn, tx);
        cmd.Parameters.AddWithValue("stores", pairs.Select(p => p.Store).ToArray());
        cmd.Parameters.AddWithValue("sales", pairs.Select(p => p.Sale).ToArray());
        cmd.Parameters.AddWithValue("seq", upToSeq);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) known.Add((reader.GetString(0), reader.GetString(1)));
        return known;
    }

    /// <summary>Loads the latest snapshot and checks that its rows still hash to what was recorded when it was taken.</summary>
    private static async Task<(SnapshotInfo? Snapshot, ProjectionSeed? Seed, bool Rejected)> TryLoadSnapshotAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        SnapshotInfo? info = null;
        await using (var cmd = new NpgsqlCommand("""
            SELECT id, log_seq, last_closure_id, hash_totals, hash_adjustments, hash_parked, hash_loyalty, hash_overall
            FROM snapshots ORDER BY id DESC LIMIT 1
            """, conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct))
                info = new SnapshotInfo(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
                    new ProjectionHashes(reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7)));
        if (info is null) return (null, null, false);

        var days = new List<ProjectedDay>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT store_id, business_date, sales_minor, returns_minor, voids_minor, adj_sales_minor, adj_returns_minor, adj_voids_minor FROM snapshot_days WHERE snapshot_id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", info.Id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                days.Add(new ProjectedDay(reader.GetString(0), DateOnly.FromDateTime(reader.GetDateTime(1)),
                    new DayDelta(reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)),
                    new DayDelta(reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7))));
        }

        var adjustments = new List<ProjectedAdjustment>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT event_id, store_id, original_date, applied_date, sales_minor, returns_minor, voids_minor FROM snapshot_adjustments WHERE snapshot_id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", info.Id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                adjustments.Add(new ProjectedAdjustment(reader.GetString(0), reader.GetString(1),
                    DateOnly.FromDateTime(reader.GetDateTime(2)), DateOnly.FromDateTime(reader.GetDateTime(3)),
                    new DayDelta(reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6))));
        }

        var loyalty = new List<ProjectedLoyalty>();
        await using (var cmd = new NpgsqlCommand("SELECT account_id, earned_points, burned_points FROM snapshot_loyalty WHERE snapshot_id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", info.Id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) loyalty.Add(new ProjectedLoyalty(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        }

        var parkedIds = new List<string>();
        await using (var cmd = new NpgsqlCommand("SELECT event_id FROM snapshot_parked WHERE snapshot_id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", info.Id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) parkedIds.Add(reader.GetString(0));
        }

        // The snapshot is trusted only if its rows still hash to what was recorded when it was taken.
        var check = ProjectionHash.Of(
            days.Select(d => ProjectionHash.TotalsLine(d.StoreId, d.Day, d.Own, d.Adjustments)),
            adjustments.Select(a => ProjectionHash.AdjustmentLine(a.EventId, a.StoreId, a.OriginalDate, a.AppliedDate, a.Delta)),
            parkedIds.Select(ProjectionHash.ParkedLine),
            loyalty.Select(l => ProjectionHash.LoyaltyLine(l.AccountId, l.Earned, l.Burned)));
        if (check != info.Hashes) return (null, null, true);

        var lastClosed = new Dictionary<string, DateOnly>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand("SELECT store_id, last_closed FROM snapshot_closed WHERE snapshot_id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", info.Id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) lastClosed[reader.GetString(0)] = DateOnly.FromDateTime(reader.GetDateTime(1));
        }

        var parked = new List<LogEvent>();
        if (parkedIds.Count > 0)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT log_seq, event_id, store_id, type, business_date, sale_id, amount_minor FROM events WHERE event_id = ANY(@ids) ORDER BY log_seq", conn, tx);
            cmd.Parameters.AddWithValue("ids", parkedIds.ToArray());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                parked.Add(new LogEvent(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    DateOnly.FromDateTime(reader.GetDateTime(4)), reader.GetString(5), reader.GetInt64(6)));
        }

        return (info, new ProjectionSeed(days, adjustments, loyalty, lastClosed, parked, new HashSet<(string, string)>()), false);
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

        var loyalty = new List<string>();
        await using (var cmd = new NpgsqlCommand("SELECT account_id, earned_points, burned_points FROM loyalty_accounts", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) loyalty.Add(ProjectionHash.LoyaltyLine(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));

        return ProjectionHash.Of(totals, adjustments, parked, loyalty);
    }

    private static async Task InsertLoyaltyAsync(NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyList<ProjectedLoyalty> accounts, CancellationToken ct)
    {
        if (accounts.Count == 0) return;

        await using var cmd = new NpgsqlCommand(
            "INSERT INTO loyalty_accounts (account_id, earned_points, burned_points) SELECT * FROM unnest(@ids, @earned, @burned)", conn, tx);
        cmd.Parameters.AddWithValue("ids", accounts.Select(a => a.AccountId).ToArray());
        cmd.Parameters.AddWithValue("earned", accounts.Select(a => a.Earned).ToArray());
        cmd.Parameters.AddWithValue("burned", accounts.Select(a => a.Burned).ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
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
