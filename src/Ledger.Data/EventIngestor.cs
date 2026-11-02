using Ledger.Core;
using Ledger.Core.Events;
using Npgsql;

namespace Ledger.Data;

/// <summary>
/// Appends a batch of events to the log exactly once per event id, however many times and from however many replicas
/// the batch arrives.
///
/// The unique index on event_id is the only arbiter: INSERT .. ON CONFLICT DO NOTHING either inserts a row or does not,
/// and nothing is kept in process memory. The whole batch is one transaction, so a replica that dies halfway leaves
/// nothing behind and the terminal simply resends. Rows are inserted in event_id order so two overlapping batches take
/// their index locks in the same order and cannot deadlock each other.
/// </summary>
public sealed class EventIngestor(NpgsqlDataSource db, IClock clock, LedgerOptions options)
{
    private sealed record Candidate(int Index, string Raw, ValidEvent Event);

    private sealed record Reject(int Index, string? EventId, string Reason, string Raw);

    public async Task<IngestResult> IngestAsync(IReadOnlyList<RawEvent> items, CancellationToken ct = default)
    {
        var rejects = new List<Reject>();
        var duplicates = 0;

        // 1. Validate each event on its own; one bad event never sinks the batch.
        var firstById = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item.Event is null)
            {
                rejects.Add(new(item.Index, null, RejectReason.MalformedEvent, item.RawJson));
                continue;
            }

            if (!EventValidator.TryValidate(item.Event, out var valid, out var reason))
            {
                rejects.Add(new(item.Index, item.Event.EventId, reason, item.RawJson));
                continue;
            }

            // 2. Repeats inside the same batch: identical is a duplicate, different is a conflict. First one wins.
            var candidate = new Candidate(item.Index, item.RawJson, valid);
            if (firstById.TryAdd(valid.EventId, candidate)) continue;

            if (firstById[valid.EventId].Event == valid) duplicates++;
            else rejects.Add(new(item.Index, valid.EventId, RejectReason.EventIdConflict, item.RawJson));
        }

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // 3. Resolve each store's time zone; an unregistered store cannot be given a business day.
        var zones = await LoadZonesAsync(conn, tx, firstById.Values.Select(c => c.Event.StoreId).Distinct().ToArray(), ct);
        var toInsert = new List<Candidate>(firstById.Count);
        var days = new Dictionary<string, DateOnly>(StringComparer.Ordinal);
        foreach (var c in firstById.Values.OrderBy(c => c.Event.EventId, StringComparer.Ordinal))
        {
            if (zones.TryGetValue(c.Event.StoreId, out var zone))
            {
                toInsert.Add(c);
                days[c.Event.EventId] = BusinessDay.For(c.Event.OccurredAtUtc, zone, options.BusinessDayCutoffHour);
            }
            else rejects.Add(new(c.Index, c.Event.EventId, RejectReason.UnknownStore, c.Raw));
        }

        // 4. Append. Anything not returned already existed. Only newly appended events touch the totals,
        //    and they do so in this same transaction, so readers see the log and the totals move together.
        var inserted = await InsertAsync(conn, tx, toInsert, days, ct);
        await ProjectAsync(conn, tx, toInsert.Where(c => inserted.Contains(c.Event.EventId)).Select(c => (c.Event, days[c.Event.EventId])).ToList(), ct);
        var existing = toInsert.Where(c => !inserted.Contains(c.Event.EventId)).ToList();

        // 5. Existing ids: same content is a harmless retry, different content is a conflict worth recording.
        if (existing.Count > 0)
        {
            var stored = await LoadStoredAsync(conn, tx, existing.Select(c => c.Event.EventId).ToArray(), ct);
            foreach (var c in existing)
            {
                if (stored.TryGetValue(c.Event.EventId, out var row) && row == c.Event) duplicates++;
                else rejects.Add(new(c.Index, c.Event.EventId, RejectReason.EventIdConflict, c.Raw));
            }
        }

        await InsertRejectsAsync(conn, tx, rejects, ct);
        await tx.CommitAsync(ct);

        return new IngestResult(
            inserted.Count,
            duplicates,
            rejects.OrderBy(r => r.Index).Select(r => new RejectedEvent(r.Index, r.EventId, r.Reason)).ToList());
    }

    private static async Task<Dictionary<string, TimeZoneInfo>> LoadZonesAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string[] storeIds, CancellationToken ct)
    {
        var zones = new Dictionary<string, TimeZoneInfo>(StringComparer.Ordinal);
        if (storeIds.Length == 0) return zones;

        await using var cmd = new NpgsqlCommand("SELECT store_id, tz FROM stores WHERE store_id = ANY(@ids)", conn, tx);
        cmd.Parameters.AddWithValue("ids", storeIds);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            zones[reader.GetString(0)] = TimeZoneInfo.FindSystemTimeZoneById(reader.GetString(1));
        return zones;
    }

    private async Task<HashSet<string>> InsertAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, List<Candidate> rows, Dictionary<string, DateOnly> days, CancellationToken ct)
    {
        var inserted = new HashSet<string>(StringComparer.Ordinal);
        if (rows.Count == 0) return inserted;

        await using var cmd = new NpgsqlCommand("""
            INSERT INTO events (event_id, store_id, terminal_id, type, occurred_at, received_at, sale_id, amount_minor, business_date)
            SELECT t.event_id, t.store_id, t.terminal_id, t.type, t.occurred_at, @received_at, t.sale_id, t.amount_minor, t.business_date
            FROM unnest(@event_ids, @store_ids, @terminal_ids, @types, @occurred, @sale_ids, @amounts, @business_dates)
                 WITH ORDINALITY AS t(event_id, store_id, terminal_id, type, occurred_at, sale_id, amount_minor, business_date, ord)
            ORDER BY t.ord
            ON CONFLICT (event_id) DO NOTHING
            RETURNING event_id
            """, conn, tx);

        cmd.Parameters.AddWithValue("received_at", clock.UtcNow.UtcDateTime);
        cmd.Parameters.AddWithValue("event_ids", rows.Select(r => r.Event.EventId).ToArray());
        cmd.Parameters.AddWithValue("store_ids", rows.Select(r => r.Event.StoreId).ToArray());
        cmd.Parameters.AddWithValue("terminal_ids", rows.Select(r => r.Event.TerminalId).ToArray());
        cmd.Parameters.AddWithValue("types", rows.Select(r => r.Event.Type).ToArray());
        cmd.Parameters.AddWithValue("occurred", rows.Select(r => r.Event.OccurredAtUtc.UtcDateTime).ToArray());
        cmd.Parameters.AddWithValue("sale_ids", rows.Select(r => r.Event.SaleId).ToArray());
        cmd.Parameters.AddWithValue("amounts", rows.Select(r => r.Event.AmountMinor).ToArray());
        cmd.Parameters.AddWithValue("business_dates", rows.Select(r => days[r.Event.EventId]).ToArray());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) inserted.Add(reader.GetString(0));
        return inserted;
    }

    /// <summary>
    /// Decides what each newly appended event does to the totals. A sale always applies. A return or void applies only if
    /// its sale is already in the log; otherwise it is parked. A new sale also releases anything parked for it.
    ///
    /// Per-sale advisory locks close the race where a return and its sale commit in different transactions at the same
    /// moment and each misses the other. Lock order is fixed (events, then sale locks, then totals rows) so it cannot deadlock.
    /// </summary>
    private async Task ProjectAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, List<(ValidEvent Event, DateOnly Day)> added, CancellationToken ct)
    {
        if (added.Count == 0) return;

        await LockSalesAsync(conn, tx, added.Select(a => a.Event.SaleId!).Distinct().ToArray(), ct);

        var dependentSales = added.Where(a => a.Event.Type != EventType.Sale).Select(a => a.Event.SaleId!).Distinct().ToArray();
        var known = dependentSales.Length == 0 ? [] : await KnownSalesAsync(conn, tx, dependentSales, ct);

        var apply = new List<(string StoreId, DateOnly Day, DayDelta Delta)>();
        var parked = new List<ValidEvent>();
        foreach (var (e, day) in added)
        {
            if (e.Type == EventType.Sale || known.Contains(e.SaleId!)) apply.Add((e.StoreId, day, DayDelta.For(e)));
            else parked.Add(e);
        }

        var newSales = added.Where(a => a.Event.Type == EventType.Sale).Select(a => a.Event.SaleId!).Distinct().ToArray();
        if (newSales.Length > 0) apply.AddRange(await ReleaseParkedAsync(conn, tx, newSales, ct));

        await ApplyTotalsAsync(conn, tx, apply, ct);
        await InsertParkedAsync(conn, tx, parked, ct);
    }

    private static async Task LockSalesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string[] saleIds, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(k)
            FROM (SELECT DISTINCT hashtextextended(s, 0) AS k FROM unnest(@sales) AS s) AS keys
            ORDER BY k
            """, conn, tx);
        cmd.Parameters.AddWithValue("sales", saleIds);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<HashSet<string>> KnownSalesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string[] saleIds, CancellationToken ct)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = new NpgsqlCommand("SELECT DISTINCT sale_id FROM events WHERE type = 'sale' AND sale_id = ANY(@ids)", conn, tx);
        cmd.Parameters.AddWithValue("ids", saleIds);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) known.Add(reader.GetString(0));
        return known;
    }

    private async Task<List<(string StoreId, DateOnly Day, DayDelta Delta)>> ReleaseParkedAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string[] saleIds, CancellationToken ct)
    {
        var released = new List<(string, DateOnly, DayDelta)>();
        await using var cmd = new NpgsqlCommand("""
            UPDATE parked_events p SET resolved_at = @now
            FROM events e
            WHERE e.event_id = p.event_id AND p.resolved_at IS NULL AND p.sale_id = ANY(@sales)
            RETURNING e.store_id, e.business_date, e.type, e.amount_minor
            """, conn, tx);
        cmd.Parameters.AddWithValue("now", clock.UtcNow.UtcDateTime);
        cmd.Parameters.AddWithValue("sales", saleIds);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            released.Add((reader.GetString(0), DateOnly.FromDateTime(reader.GetDateTime(1)), DayDelta.For(reader.GetString(2), reader.GetInt64(3))));
        return released;
    }

    private async Task InsertParkedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, List<ValidEvent> parked, CancellationToken ct)
    {
        if (parked.Count == 0) return;

        await using var cmd = new NpgsqlCommand(
            "INSERT INTO parked_events (event_id, sale_id, parked_at) SELECT e, s, @now FROM unnest(@ids, @sales) AS t(e, s)", conn, tx);
        cmd.Parameters.AddWithValue("now", clock.UtcNow.UtcDateTime);
        cmd.Parameters.AddWithValue("ids", parked.Select(p => p.EventId).ToArray());
        cmd.Parameters.AddWithValue("sales", parked.Select(p => p.SaleId!).ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Adds effects to the store-day totals. Rows are written in (store, day) order by the database so
    /// two concurrent batches touching the same days take their row locks in the same order and cannot deadlock.
    /// </summary>
    private static async Task ApplyTotalsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, List<(string StoreId, DateOnly Day, DayDelta Delta)> effects, CancellationToken ct)
    {
        if (effects.Count == 0) return;

        var perDay = effects
            .GroupBy(a => (a.StoreId, a.Day))
            .Select(g => (g.Key.StoreId, g.Key.Day, Delta: g.Aggregate(default(DayDelta), (sum, a) => sum + a.Delta)))
            .ToList();

        await using var cmd = new NpgsqlCommand("""
            INSERT INTO store_day_totals (store_id, business_date, sales_minor, returns_minor, voids_minor)
            SELECT t.store_id, t.business_date, t.sales, t.returns, t.voids
            FROM unnest(@stores, @days, @sales, @returns, @voids) AS t(store_id, business_date, sales, returns, voids)
            ORDER BY t.store_id, t.business_date
            ON CONFLICT (store_id, business_date) DO UPDATE SET
                sales_minor   = store_day_totals.sales_minor   + EXCLUDED.sales_minor,
                returns_minor = store_day_totals.returns_minor + EXCLUDED.returns_minor,
                voids_minor   = store_day_totals.voids_minor   + EXCLUDED.voids_minor
            """, conn, tx);
        cmd.Parameters.AddWithValue("stores", perDay.Select(p => p.StoreId).ToArray());
        cmd.Parameters.AddWithValue("days", perDay.Select(p => p.Day).ToArray());
        cmd.Parameters.AddWithValue("sales", perDay.Select(p => p.Delta.Sales).ToArray());
        cmd.Parameters.AddWithValue("returns", perDay.Select(p => p.Delta.Returns).ToArray());
        cmd.Parameters.AddWithValue("voids", perDay.Select(p => p.Delta.Voids).ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<Dictionary<string, ValidEvent>> LoadStoredAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string[] ids, CancellationToken ct)
    {
        var stored = new Dictionary<string, ValidEvent>(StringComparer.Ordinal);

        await using var cmd = new NpgsqlCommand(
            "SELECT event_id, store_id, terminal_id, type, occurred_at, sale_id, amount_minor FROM events WHERE event_id = ANY(@ids)", conn, tx);
        cmd.Parameters.AddWithValue("ids", ids);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetString(0);
            stored[id] = new ValidEvent(
                id,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc)),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6));
        }

        return stored;
    }

    private async Task InsertRejectsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, List<Reject> rejects, CancellationToken ct)
    {
        if (rejects.Count == 0) return;

        await using var cmd = new NpgsqlCommand("""
            INSERT INTO rejected_events (received_at, event_id, reason, raw)
            SELECT @received_at, t.event_id, t.reason, t.raw::jsonb
            FROM unnest(@event_ids, @reasons, @raws) AS t(event_id, reason, raw)
            """, conn, tx);
        cmd.Parameters.AddWithValue("received_at", clock.UtcNow.UtcDateTime);
        cmd.Parameters.AddWithValue("event_ids", rejects.Select(r => r.EventId).ToArray());
        cmd.Parameters.AddWithValue("reasons", rejects.Select(r => r.Reason).ToArray());
        // jsonb cannot store the \u0000 escape; swap it for the replacement character in the audit copy only.
        cmd.Parameters.AddWithValue("raws", rejects.Select(r => r.Raw.Replace("\\u0000", "\\uFFFD")).ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
