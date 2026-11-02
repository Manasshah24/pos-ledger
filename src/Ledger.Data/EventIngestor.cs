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
        foreach (var c in firstById.Values.OrderBy(c => c.Event.EventId, StringComparer.Ordinal))
        {
            if (zones.ContainsKey(c.Event.StoreId)) toInsert.Add(c);
            else rejects.Add(new(c.Index, c.Event.EventId, RejectReason.UnknownStore, c.Raw));
        }

        // 4. Append. Anything not returned already existed.
        var inserted = await InsertAsync(conn, tx, toInsert, zones, ct);
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
        NpgsqlConnection conn, NpgsqlTransaction tx, List<Candidate> rows, Dictionary<string, TimeZoneInfo> zones, CancellationToken ct)
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
        cmd.Parameters.AddWithValue("business_dates",
            rows.Select(r => BusinessDay.For(r.Event.OccurredAtUtc, zones[r.Event.StoreId], options.BusinessDayCutoffHour)).ToArray());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) inserted.Add(reader.GetString(0));
        return inserted;
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
