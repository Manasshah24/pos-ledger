using System.Globalization;
using Ledger.Core;
using Ledger.Core.Events;
using Npgsql;

namespace Ledger.Data;

public enum IssueResult { Created, Unchanged, Conflict }

public enum LeaseOutcome { Granted, CardNotFound, StoreNotFound, NothingAvailable }

public enum RedeemStatus { Approved, Replayed, Declined, CardNotFound, StoreNotFound, Conflict, Invalid }

public sealed record CardView(
    string CardId, long BalanceMinor, long IssuedMinor,
    long SecuredOutstandingMinor, long UnsecuredOutstandingMinor, long DeficitMinor, long AvailableMinor);

public sealed record LeaseGrant(long LeaseId, string CardId, string StoreId, long SecuredMinor, long UnsecuredMinor, DateTimeOffset ExpiresAt);

public sealed record LeaseResult(LeaseOutcome Outcome, LeaseGrant? Grant);

public sealed record RedeemResult(RedeemStatus Status, long BalanceMinor, long AvailableMinor, string? Reason = null);

public sealed record GiftCardFlag(string EventId, string CardId, string Kind, long AmountMinor);

/// <summary>
/// Gift cards with a hard, configurable bound on offline overspend.
///
/// The scheme. A card has a central balance B. Before a store goes offline with a card it takes a lease from the card:
/// a secured part S carved out of B and an unsecured part U carved out of the card's overspend allowance (the configured
/// limit L). A terminal may redeem offline only up to the lease it holds. The service keeps, at all times:
///     Σ secured outstanding ≤ B     and     deficit(B) + Σ unsecured outstanding ≤ L
/// where deficit = max(0, -B). Online redemption may only spend what is not reserved (B minus secured outstanding), so it is
/// always exact. Offline spending is at most what was leased, so the final balance can never be lower than -L: overspend per
/// card is bounded by L, and every redemption that breaks a rule is flagged at ingest.
///
/// Reservation lasts until the lease expires PLUS the maximum upload delay, because a store may report its spending up to
/// that long after it happened; releasing the money earlier could let it be spent twice.
/// </summary>
public sealed class GiftCardService(NpgsqlDataSource db, IClock clock, LedgerOptions options)
{
    public async Task<IssueResult> IssueAsync(string cardId, long balanceMinor, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO giftcards (card_id, issued_minor, balance_minor, created_at) VALUES (@id, @b, @b, @now) ON CONFLICT (card_id) DO NOTHING", conn))
        {
            insert.Parameters.AddWithValue("id", cardId);
            insert.Parameters.AddWithValue("b", balanceMinor);
            insert.Parameters.AddWithValue("now", clock.UtcNow.UtcDateTime);
            if (await insert.ExecuteNonQueryAsync(ct) == 1) return IssueResult.Created;
        }

        await using var select = new NpgsqlCommand("SELECT issued_minor FROM giftcards WHERE card_id = @id", conn);
        select.Parameters.AddWithValue("id", cardId);
        return (long)(await select.ExecuteScalarAsync(ct))! == balanceMinor ? IssueResult.Unchanged : IssueResult.Conflict;
    }

    public async Task<CardView?> GetAsync(string cardId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);

        await using var cmd = new NpgsqlCommand("SELECT balance_minor, issued_minor FROM giftcards WHERE card_id = @id", conn, tx);
        cmd.Parameters.AddWithValue("id", cardId);
        long balance, issued;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            balance = reader.GetInt64(0);
            issued = reader.GetInt64(1);
        }

        var (secured, unsecured) = await OutstandingAsync(conn, tx, cardId, ct);
        return new CardView(cardId, balance, issued, secured, unsecured, Math.Max(0, -balance), balance - secured);
    }

    public async Task<LeaseResult> LeaseAsync(string cardId, string storeId, long amountMinor, TimeSpan ttl, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        long balance;
        await using (var cmd = new NpgsqlCommand("SELECT balance_minor FROM giftcards WHERE card_id = @id FOR UPDATE", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", cardId);
            if (await cmd.ExecuteScalarAsync(ct) is not long b) return new LeaseResult(LeaseOutcome.CardNotFound, null);
            balance = b;
        }

        await using (var cmd = new NpgsqlCommand("SELECT 1 FROM stores WHERE store_id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", storeId);
            if (await cmd.ExecuteScalarAsync(ct) is null) return new LeaseResult(LeaseOutcome.StoreNotFound, null);
        }

        var (securedOut, unsecuredOut) = await OutstandingAsync(conn, tx, cardId, ct);
        var available = Math.Max(0, balance - securedOut);
        var secured = Math.Min(amountMinor, available);
        var pool = Math.Max(0, options.OfflineUnsecuredLimitMinor - Math.Max(0, -balance) - unsecuredOut);
        var unsecured = Math.Min(amountMinor - secured, pool);
        if (secured + unsecured == 0) return new LeaseResult(LeaseOutcome.NothingAvailable, null);

        var now = clock.UtcNow;
        await using var insert = new NpgsqlCommand("""
            INSERT INTO giftcard_leases (card_id, store_id, secured_minor, unsecured_minor, granted_at, expires_at)
            VALUES (@card, @store, @s, @u, @now, @exp) RETURNING lease_id
            """, conn, tx);
        insert.Parameters.AddWithValue("card", cardId);
        insert.Parameters.AddWithValue("store", storeId);
        insert.Parameters.AddWithValue("s", secured);
        insert.Parameters.AddWithValue("u", unsecured);
        insert.Parameters.AddWithValue("now", now.UtcDateTime);
        insert.Parameters.AddWithValue("exp", (now + ttl).UtcDateTime);
        var id = (long)(await insert.ExecuteScalarAsync(ct))!;
        await tx.CommitAsync(ct);

        return new LeaseResult(LeaseOutcome.Granted, new LeaseGrant(id, cardId, storeId, secured, unsecured, now + ttl));
    }

    /// <summary>Exact online redemption: approved only if the amount is not reserved by a lease. Idempotent on eventId.</summary>
    public async Task<RedeemResult> RedeemOnlineAsync(
        string eventId, string storeId, string terminalId, string cardId, long amountMinor, DateTimeOffset? occurredAt, CancellationToken ct = default)
    {
        var incoming = new IncomingEvent(eventId, storeId, terminalId, EventType.GiftcardRedeem,
            (occurredAt ?? clock.UtcNow).ToString("O", CultureInfo.InvariantCulture), null, amountMinor, cardId);
        if (!EventValidator.TryValidate(incoming, out var valid, out var reason))
            return new RedeemResult(RedeemStatus.Invalid, 0, 0, reason);

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        TimeZoneInfo zone;
        await using (var cmd = new NpgsqlCommand("SELECT tz FROM stores WHERE store_id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", storeId);
            if (await cmd.ExecuteScalarAsync(ct) is not string tz) return new RedeemResult(RedeemStatus.StoreNotFound, 0, 0);
            zone = TimeZoneInfo.FindSystemTimeZoneById(tz);
        }

        // Event first, then the card row: the same lock order as batch ingest, so the two paths cannot deadlock.
        var day = BusinessDay.For(valid.OccurredAtUtc, zone, options.BusinessDayCutoffHour);
        if (!await EventAppender.TryInsertAsync(conn, tx, valid, day, clock.UtcNow.UtcDateTime, ct))
        {
            // The same redemption was already approved (a retry), or the id is being reused for something else.
            if (!await EventAppender.SameAsStoredAsync(conn, tx, valid, ct)) return new RedeemResult(RedeemStatus.Conflict, 0, 0, RejectReason.EventIdConflict);
            return new RedeemResult(RedeemStatus.Replayed, await ReadBalanceAsync(conn, tx, cardId, ct) ?? 0, 0);
        }

        long balance;
        await using (var cmd = new NpgsqlCommand("SELECT balance_minor FROM giftcards WHERE card_id = @id FOR UPDATE", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", cardId);
            if (await cmd.ExecuteScalarAsync(ct) is not long b) return new RedeemResult(RedeemStatus.CardNotFound, 0, 0); // tx rolls back the event
            balance = b;
        }

        var (secured, _) = await OutstandingAsync(conn, tx, cardId, ct);
        var available = balance - secured;
        if (amountMinor > available) return new RedeemResult(RedeemStatus.Declined, balance, Math.Max(0, available)); // rollback: nothing recorded

        await using (var update = new NpgsqlCommand("UPDATE giftcards SET balance_minor = balance_minor - @amt WHERE card_id = @id", conn, tx))
        {
            update.Parameters.AddWithValue("amt", amountMinor);
            update.Parameters.AddWithValue("id", cardId);
            await update.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return new RedeemResult(RedeemStatus.Approved, balance - amountMinor, available - amountMinor);
    }

    public async Task<List<GiftCardFlag>> FlagsAsync(string? cardId = null, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "SELECT event_id, card_id, kind, amount_minor FROM giftcard_flags WHERE @card::text IS NULL OR card_id = @card ORDER BY card_id COLLATE \"C\", event_id COLLATE \"C\", kind");
        cmd.Parameters.AddWithValue("card", (object?)cardId ?? DBNull.Value);
        var flags = new List<GiftCardFlag>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            flags.Add(new GiftCardFlag(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
        return flags;
    }

    private static async Task<long?> ReadBalanceAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string cardId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT balance_minor FROM giftcards WHERE card_id = @id", conn, tx);
        cmd.Parameters.AddWithValue("id", cardId);
        return await cmd.ExecuteScalarAsync(ct) as long?;
    }

    /// <summary>Secured and unsecured money still reserved by leases: not yet reported as spent, and within expiry + max upload delay.</summary>
    private async Task<(long Secured, long Unsecured)> OutstandingAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string cardId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT coalesce(sum(greatest(secured_minor - spent_minor, 0)), 0)::bigint,
                   coalesce(sum(greatest(unsecured_minor - greatest(spent_minor - secured_minor, 0), 0)), 0)::bigint
            FROM giftcard_leases
            WHERE card_id = @id AND @now < expires_at + make_interval(hours => @grace)
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", cardId);
        cmd.Parameters.AddWithValue("now", clock.UtcNow.UtcDateTime);
        cmd.Parameters.AddWithValue("grace", options.MaxLateHours);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }
}
