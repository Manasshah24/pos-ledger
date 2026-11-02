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

    /// <summary>What one newly appended (or newly released) event does to its own business day.</summary>
    private sealed record Effect(string EventId, string StoreId, DateOnly Day, DayDelta Delta);

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

        // 3. Load each store (time zone and last closed day) under a shared store lock held until commit, so a day close
        //    cannot slip in between "this day is open" and the write that relies on it. An unregistered store is refused.
        var stores = await LoadStoresAsync(conn, tx, firstById.Values.Select(c => c.Event.StoreId).Distinct().ToArray(), ct);
        var toInsert = new List<Candidate>(firstById.Count);
        var days = new Dictionary<string, DateOnly>(StringComparer.Ordinal);
        foreach (var c in firstById.Values.OrderBy(c => c.Event.EventId, StringComparer.Ordinal))
        {
            if (stores.TryGetValue(c.Event.StoreId, out var store))
            {
                toInsert.Add(c);
                days[c.Event.EventId] = BusinessDay.For(c.Event.OccurredAtUtc, store.Zone, options.BusinessDayCutoffHour);
            }
            else rejects.Add(new(c.Index, c.Event.EventId, RejectReason.UnknownStore, c.Raw));
        }

        // 4. Append. Anything not returned already existed. Only newly appended events touch the totals,
        //    and they do so in this same transaction, so readers see the log and the totals move together.
        var inserted = await InsertAsync(conn, tx, toInsert, days, ct);
        await ProjectAsync(conn, tx, stores, toInsert.Where(c => inserted.Contains(c.Event.EventId)).Select(c => (c.Event, days[c.Event.EventId])).ToList(), ct);
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

    private sealed record StoreRow(TimeZoneInfo Zone, DateOnly? LastClosed);

    private static async Task<Dictionary<string, StoreRow>> LoadStoresAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string[] storeIds, CancellationToken ct)
    {
        var stores = new Dictionary<string, StoreRow>(StringComparer.Ordinal);
        if (storeIds.Length == 0) return stores;

        // A shared advisory lock per store, held to commit: ingests of the same store do not block each other, but a day close
        // (exclusive) waits for them and, unlike a row lock, makes later ingests queue behind it, so a close cannot be starved
        // by a steady stream of batches. Taken in a fixed order so batches touching several stores cannot deadlock.
        await using (var lockCmd = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock_shared(k)
            FROM (SELECT DISTINCT hashtextextended('store:' || s, 0) AS k FROM unnest(@ids) AS s) AS keys
            ORDER BY k
            """, conn, tx))
        {
            lockCmd.Parameters.AddWithValue("ids", storeIds);
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        await using var cmd = new NpgsqlCommand("SELECT store_id, tz, last_closed_date FROM stores WHERE store_id = ANY(@ids)", conn, tx);
        cmd.Parameters.AddWithValue("ids", storeIds);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            stores[reader.GetString(0)] = new StoreRow(
                TimeZoneInfo.FindSystemTimeZoneById(reader.GetString(1)),
                reader.IsDBNull(2) ? null : DateOnly.FromDateTime(reader.GetDateTime(2)));
        return stores;
    }

    private async Task<HashSet<string>> InsertAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, List<Candidate> rows, Dictionary<string, DateOnly> days, CancellationToken ct)
    {
        var inserted = new HashSet<string>(StringComparer.Ordinal);
        if (rows.Count == 0) return inserted;

        await using var cmd = new NpgsqlCommand("""
            INSERT INTO events (event_id, store_id, terminal_id, type, occurred_at, received_at, sale_id, amount_minor, business_date, card_id, lease_id)
            SELECT t.event_id, t.store_id, t.terminal_id, t.type, t.occurred_at, @received_at, t.sale_id, t.amount_minor, t.business_date, t.card_id, t.lease_id
            FROM unnest(@event_ids, @store_ids, @terminal_ids, @types, @occurred, @sale_ids, @amounts, @business_dates, @card_ids, @lease_ids)
                 WITH ORDINALITY AS t(event_id, store_id, terminal_id, type, occurred_at, sale_id, amount_minor, business_date, card_id, lease_id, ord)
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
        cmd.Parameters.AddWithValue("card_ids", rows.Select(r => r.Event.CardId).ToArray());
        cmd.Parameters.AddWithValue("lease_ids", rows.Select(r => r.Event.LeaseId).ToArray());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) inserted.Add(reader.GetString(0));
        return inserted;
    }

    /// <summary>
    /// Decides what each newly appended event does to the totals. A sale always applies. A return or void applies only if
    /// its sale is already in the log; otherwise it is parked. A new sale also releases anything parked for it.
    ///
    /// Per-sale (store, sale) advisory locks close the race where a return and its sale commit in different transactions at the same
    /// moment and each misses the other. Lock order is fixed (events, then sale locks, then totals rows) so it cannot deadlock.
    /// </summary>
    private async Task ProjectAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Dictionary<string, StoreRow> stores,
        List<(ValidEvent Event, DateOnly Day)> added, CancellationToken ct)
    {
        if (added.Count == 0) return;

        // Gift card redemptions are a tender, not revenue: they never touch the store-day totals.
        var gift = added.Where(a => a.Event.Type == EventType.GiftcardRedeem).Select(a => a.Event).ToList();
        var trade = added.Where(a => a.Event.Type != EventType.GiftcardRedeem).ToList();
        await ProjectTradeAsync(conn, tx, stores, trade, ct);
        await ApplyGiftCardsAsync(conn, tx, gift, ct);
    }

    private async Task ProjectTradeAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Dictionary<string, StoreRow> stores,
        List<(ValidEvent Event, DateOnly Day)> added, CancellationToken ct)
    {
        if (added.Count == 0) return;

        await LockSalesAsync(conn, tx, added.Select(a => (a.Event.StoreId, a.Event.SaleId!)).Distinct().ToList(), ct);

        var dependentSales = added.Where(a => a.Event.Type != EventType.Sale).Select(a => (a.Event.StoreId, a.Event.SaleId!)).Distinct().ToList();
        var known = dependentSales.Count == 0 ? [] : await KnownSalesAsync(conn, tx, dependentSales, ct);

        var effects = new List<Effect>();
        var parked = new List<ValidEvent>();
        foreach (var (e, day) in added)
        {
            if (e.Type == EventType.Sale || known.Contains((e.StoreId, e.SaleId!))) effects.Add(new Effect(e.EventId, e.StoreId, day, DayDelta.For(e)));
            else parked.Add(e);
        }

        var newSales = added.Where(a => a.Event.Type == EventType.Sale).Select(a => (a.Event.StoreId, a.Event.SaleId!)).Distinct().ToList();
        if (newSales.Count > 0) effects.AddRange(await ReleaseParkedAsync(conn, tx, newSales, ct));

        await ApplyEffectsAsync(conn, tx, stores, effects, ct);
        await InsertParkedAsync(conn, tx, parked, ct);
    }

    /// <summary>
    /// Books offline gift card redemptions in log order. The card rows (and the leases they cite) are locked in a fixed order,
    /// the balance is reduced, and every way the redemption broke the rules is recorded as a flag:
    ///   overspend      - it pushed the card's balance below zero (the part below zero is the amount);
    ///   no_lease       - no valid lease of this store for this card was cited;
    ///   exceeds_lease  - the store has now redeemed more than its lease allowed;
    ///   expired_lease  - the redemption happened after the lease expired.
    /// An unknown card is registered with a zero balance rather than dropped: the sale really happened.
    /// </summary>
    private async Task ApplyGiftCardsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, List<ValidEvent> gift, CancellationToken ct)
    {
        if (gift.Count == 0) return;

        var cards = gift.Select(g => g.CardId!).Distinct().Order(StringComparer.Ordinal).ToArray();
        await using (var create = new NpgsqlCommand(
            "INSERT INTO giftcards (card_id, issued_minor, balance_minor, created_at) SELECT c, 0, 0, @now FROM unnest(@cards) AS c ORDER BY c ON CONFLICT DO NOTHING", conn, tx))
        {
            create.Parameters.AddWithValue("now", clock.UtcNow.UtcDateTime);
            create.Parameters.AddWithValue("cards", cards);
            await create.ExecuteNonQueryAsync(ct);
        }

        var balances = new Dictionary<string, long>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand("SELECT card_id, balance_minor FROM giftcards WHERE card_id = ANY(@cards) ORDER BY card_id FOR UPDATE", conn, tx))
        {
            cmd.Parameters.AddWithValue("cards", cards);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) balances[reader.GetString(0)] = reader.GetInt64(1);
        }

        var leases = new Dictionary<long, GiftLease>();
        var leaseIds = gift.Where(g => g.LeaseId is not null).Select(g => g.LeaseId!.Value).Distinct().Order().ToArray();
        if (leaseIds.Length > 0)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT lease_id, card_id, store_id, secured_minor + unsecured_minor, spent_minor, expires_at FROM giftcard_leases WHERE lease_id = ANY(@ids) ORDER BY lease_id FOR UPDATE", conn, tx);
            cmd.Parameters.AddWithValue("ids", leaseIds);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                leases[reader.GetInt64(0)] = new GiftLease(reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4),
                    new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc)));
        }

        var flags = new List<(string EventId, string Kind, string CardId, long Amount)>();
        foreach (var e in gift)
        {
            var amount = e.AmountMinor!.Value;

            if (e.LeaseId is { } id && leases.TryGetValue(id, out var lease) && lease.CardId == e.CardId && lease.StoreId == e.StoreId)
            {
                var spentAfter = lease.Spent + amount;
                var excess = Math.Max(0, spentAfter - lease.Allowed) - Math.Max(0, lease.Spent - lease.Allowed);
                if (excess > 0) flags.Add((e.EventId, "exceeds_lease", e.CardId!, excess));
                if (e.OccurredAtUtc >= lease.ExpiresAt) flags.Add((e.EventId, "expired_lease", e.CardId!, amount));
                lease.Spent = spentAfter;
            }
            else flags.Add((e.EventId, "no_lease", e.CardId!, amount));

            var before = balances[e.CardId!];
            var after = before - amount;
            var newDeficit = Math.Max(0, -after) - Math.Max(0, -before);
            if (newDeficit > 0) flags.Add((e.EventId, "overspend", e.CardId!, newDeficit));
            balances[e.CardId!] = after;
        }

        await using (var cmd = new NpgsqlCommand(
            "UPDATE giftcards g SET balance_minor = t.b FROM unnest(@cards, @bals) AS t(c, b) WHERE g.card_id = t.c", conn, tx))
        {
            cmd.Parameters.AddWithValue("cards", balances.Keys.ToArray());
            cmd.Parameters.AddWithValue("bals", balances.Values.ToArray());
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (leases.Count > 0)
        {
            await using var cmd = new NpgsqlCommand(
                "UPDATE giftcard_leases l SET spent_minor = t.s FROM unnest(@ids, @spent) AS t(i, s) WHERE l.lease_id = t.i", conn, tx);
            cmd.Parameters.AddWithValue("ids", leases.Keys.ToArray());
            cmd.Parameters.AddWithValue("spent", leases.Values.Select(l => l.Spent).ToArray());
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (flags.Count > 0)
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO giftcard_flags (event_id, kind, card_id, amount_minor) SELECT e, k, c, a FROM unnest(@events, @kinds, @cards, @amounts) AS t(e, k, c, a)", conn, tx);
            cmd.Parameters.AddWithValue("events", flags.Select(f => f.EventId).ToArray());
            cmd.Parameters.AddWithValue("kinds", flags.Select(f => f.Kind).ToArray());
            cmd.Parameters.AddWithValue("cards", flags.Select(f => f.CardId).ToArray());
            cmd.Parameters.AddWithValue("amounts", flags.Select(f => f.Amount).ToArray());
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private sealed class GiftLease(string cardId, string storeId, long allowed, long spent, DateTimeOffset expiresAt)
    {
        public string CardId { get; } = cardId;
        public string StoreId { get; } = storeId;
        public long Allowed { get; } = allowed;
        public long Spent { get; set; } = spent;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
    }

    private static async Task LockSalesAsync(NpgsqlConnection conn, NpgsqlTransaction tx, List<(string Store, string Sale)> sales, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(k)
            FROM (SELECT DISTINCT hashtextextended('sale:' || t.st || chr(31) || t.sa, 0) AS k FROM unnest(@stores, @sales) AS t(st, sa)) AS keys
            ORDER BY k
            """, conn, tx);
        cmd.Parameters.AddWithValue("stores", sales.Select(s => s.Store).ToArray());
        cmd.Parameters.AddWithValue("sales", sales.Select(s => s.Sale).ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<HashSet<(string Store, string Sale)>> KnownSalesAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, List<(string Store, string Sale)> sales, CancellationToken ct)
    {
        var known = new HashSet<(string, string)>();
        await using var cmd = new NpgsqlCommand("""
            SELECT DISTINCT e.store_id, e.sale_id
            FROM events e JOIN unnest(@stores, @sales) AS t(st, sa) ON e.store_id = t.st AND e.sale_id = t.sa
            WHERE e.type = 'sale'
            """, conn, tx);
        cmd.Parameters.AddWithValue("stores", sales.Select(s => s.Store).ToArray());
        cmd.Parameters.AddWithValue("sales", sales.Select(s => s.Sale).ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) known.Add((reader.GetString(0), reader.GetString(1)));
        return known;
    }

    private async Task<List<Effect>> ReleaseParkedAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, List<(string Store, string Sale)> sales, CancellationToken ct)
    {
        var released = new List<Effect>();
        await using var cmd = new NpgsqlCommand("""
            UPDATE parked_events p SET resolved_at = @now
            FROM events e, unnest(@stores, @sales) AS t(st, sa)
            WHERE e.event_id = p.event_id AND p.resolved_at IS NULL AND e.store_id = t.st AND p.sale_id = t.sa
            RETURNING e.event_id, e.store_id, e.business_date, e.type, e.amount_minor
            """, conn, tx);
        cmd.Parameters.AddWithValue("now", clock.UtcNow.UtcDateTime);
        cmd.Parameters.AddWithValue("stores", sales.Select(s => s.Store).ToArray());
        cmd.Parameters.AddWithValue("sales", sales.Select(s => s.Sale).ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            released.Add(new Effect(reader.GetString(0), reader.GetString(1), DateOnly.FromDateTime(reader.GetDateTime(2)),
                DayDelta.For(reader.GetString(3), reader.GetInt64(4))));
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
    /// Books effects into the store-day totals. An effect for a day that is still open goes into that day. An effect for a day
    /// that has already closed never touches it: it becomes an adjustment, booked into the store's next open day and linked
    /// back to the original day. The store rows were read under a shared lock, so "closed" cannot change before we commit.
    /// Total rows are written in (store, day) order by the database so concurrent batches take row locks in the same order.
    /// </summary>
    private async Task ApplyEffectsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Dictionary<string, StoreRow> stores, List<Effect> effects, CancellationToken ct)
    {
        if (effects.Count == 0) return;

        var rows = new Dictionary<(string StoreId, DateOnly Day), (DayDelta Own, DayDelta Adj)>();
        var adjustments = new List<(Effect Effect, DateOnly AppliedDay)>();
        foreach (var e in effects)
        {
            if (stores[e.StoreId].LastClosed is { } closed && e.Day <= closed)
            {
                var target = closed.AddDays(1);
                adjustments.Add((e, target));
                var cur = rows.GetValueOrDefault((e.StoreId, target));
                rows[(e.StoreId, target)] = (cur.Own, cur.Adj + e.Delta);
            }
            else
            {
                var cur = rows.GetValueOrDefault((e.StoreId, e.Day));
                rows[(e.StoreId, e.Day)] = (cur.Own + e.Delta, cur.Adj);
            }
        }

        var list = rows.ToList();
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO store_day_totals (store_id, business_date, sales_minor, returns_minor, voids_minor,
                                          adj_sales_minor, adj_returns_minor, adj_voids_minor)
            SELECT t.store_id, t.business_date, t.sales, t.returns, t.voids, t.adj_sales, t.adj_returns, t.adj_voids
            FROM unnest(@stores, @days, @sales, @returns, @voids, @adj_sales, @adj_returns, @adj_voids)
                 AS t(store_id, business_date, sales, returns, voids, adj_sales, adj_returns, adj_voids)
            ORDER BY t.store_id, t.business_date
            ON CONFLICT (store_id, business_date) DO UPDATE SET
                sales_minor       = store_day_totals.sales_minor       + EXCLUDED.sales_minor,
                returns_minor     = store_day_totals.returns_minor     + EXCLUDED.returns_minor,
                voids_minor       = store_day_totals.voids_minor       + EXCLUDED.voids_minor,
                adj_sales_minor   = store_day_totals.adj_sales_minor   + EXCLUDED.adj_sales_minor,
                adj_returns_minor = store_day_totals.adj_returns_minor + EXCLUDED.adj_returns_minor,
                adj_voids_minor   = store_day_totals.adj_voids_minor   + EXCLUDED.adj_voids_minor
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("stores", list.Select(r => r.Key.StoreId).ToArray());
            cmd.Parameters.AddWithValue("days", list.Select(r => r.Key.Day).ToArray());
            cmd.Parameters.AddWithValue("sales", list.Select(r => r.Value.Own.Sales).ToArray());
            cmd.Parameters.AddWithValue("returns", list.Select(r => r.Value.Own.Returns).ToArray());
            cmd.Parameters.AddWithValue("voids", list.Select(r => r.Value.Own.Voids).ToArray());
            cmd.Parameters.AddWithValue("adj_sales", list.Select(r => r.Value.Adj.Sales).ToArray());
            cmd.Parameters.AddWithValue("adj_returns", list.Select(r => r.Value.Adj.Returns).ToArray());
            cmd.Parameters.AddWithValue("adj_voids", list.Select(r => r.Value.Adj.Voids).ToArray());
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (adjustments.Count == 0) return;

        await using var adj = new NpgsqlCommand("""
            INSERT INTO adjustments (event_id, store_id, original_date, applied_date, sales_minor, returns_minor, voids_minor, created_at)
            SELECT t.event_id, t.store_id, t.original_date, t.applied_date, t.sales, t.returns, t.voids, @now
            FROM unnest(@ids, @stores, @original, @applied, @sales, @returns, @voids)
                 AS t(event_id, store_id, original_date, applied_date, sales, returns, voids)
            """, conn, tx);
        adj.Parameters.AddWithValue("now", clock.UtcNow.UtcDateTime);
        adj.Parameters.AddWithValue("ids", adjustments.Select(a => a.Effect.EventId).ToArray());
        adj.Parameters.AddWithValue("stores", adjustments.Select(a => a.Effect.StoreId).ToArray());
        adj.Parameters.AddWithValue("original", adjustments.Select(a => a.Effect.Day).ToArray());
        adj.Parameters.AddWithValue("applied", adjustments.Select(a => a.AppliedDay).ToArray());
        adj.Parameters.AddWithValue("sales", adjustments.Select(a => a.Effect.Delta.Sales).ToArray());
        adj.Parameters.AddWithValue("returns", adjustments.Select(a => a.Effect.Delta.Returns).ToArray());
        adj.Parameters.AddWithValue("voids", adjustments.Select(a => a.Effect.Delta.Voids).ToArray());
        await adj.ExecuteNonQueryAsync(ct);
    }

    private static async Task<Dictionary<string, ValidEvent>> LoadStoredAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string[] ids, CancellationToken ct)
    {
        var stored = new Dictionary<string, ValidEvent>(StringComparer.Ordinal);

        await using var cmd = new NpgsqlCommand(
            "SELECT event_id, store_id, terminal_id, type, occurred_at, sale_id, amount_minor, card_id, lease_id FROM events WHERE event_id = ANY(@ids)", conn, tx);
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
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetInt64(8));
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
