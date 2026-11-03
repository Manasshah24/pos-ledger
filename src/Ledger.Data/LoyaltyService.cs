using System.Globalization;
using Ledger.Core;
using Ledger.Core.Events;
using Npgsql;

namespace Ledger.Data;

public enum BurnStatus { Approved, Replayed, Declined, AccountNotFound, StoreNotFound, Conflict, Invalid }

public sealed record LoyaltyView(string AccountId, long EarnedPoints, long BurnedPoints, long BalancePoints);

public sealed record BurnResult(BurnStatus Status, long BalancePoints, string? Reason = null);

/// <summary>
/// Loyalty balances. Earn and burn events arrive from many stores, offline, in any order and sometimes twice. An account is
/// two grow-only totals (points earned, points burned) summed over the distinct events in the log, so any merge order and
/// any repeat gives the same balance.
///
/// Negative balances: an offline store cannot know the balance, so an offline burn is always accepted and the account may go
/// negative. It is listed by <see cref="NegativeAsync"/>, later earns pay the debt down automatically, and an online burn is
/// refused unless the account can cover it, so a negative account cannot spend until it is back in credit.
/// </summary>
public sealed class LoyaltyService(NpgsqlDataSource db, IClock clock, LedgerOptions options)
{
    public async Task<LoyaltyView?> GetAsync(string accountId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT earned_points, burned_points, balance_points FROM loyalty_accounts WHERE account_id = @id");
        cmd.Parameters.AddWithValue("id", accountId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new LoyaltyView(accountId, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)) : null;
    }

    public async Task<List<LoyaltyView>> NegativeAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "SELECT account_id, earned_points, burned_points, balance_points FROM loyalty_accounts WHERE balance_points < 0 ORDER BY balance_points, account_id COLLATE \"C\"");
        var rows = new List<LoyaltyView>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(new LoyaltyView(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)));
        return rows;
    }

    /// <summary>Exact online burn: refused if the balance cannot cover it. Idempotent on eventId.</summary>
    public async Task<BurnResult> BurnOnlineAsync(
        string eventId, string storeId, string terminalId, string accountId, long points, DateTimeOffset? occurredAt, CancellationToken ct = default)
    {
        var incoming = new IncomingEvent(eventId, storeId, terminalId, EventType.LoyaltyBurn,
            (occurredAt ?? clock.UtcNow).ToString("O", CultureInfo.InvariantCulture), null, null, AccountId: accountId, Points: points);
        if (!EventValidator.TryValidate(incoming, out var valid, out var reason))
            return new BurnResult(BurnStatus.Invalid, 0, reason);

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        TimeZoneInfo zone;
        await using (var cmd = new NpgsqlCommand("SELECT tz FROM stores WHERE store_id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", storeId);
            if (await cmd.ExecuteScalarAsync(ct) is not string tz) return new BurnResult(BurnStatus.StoreNotFound, 0);
            zone = TimeZoneInfo.FindSystemTimeZoneById(tz);
        }

        // Event first, then the account row: the same lock order as batch ingest.
        var day = BusinessDay.For(valid.OccurredAtUtc, zone, options.BusinessDayCutoffHour);
        if (!await EventAppender.TryInsertAsync(conn, tx, valid, day, clock.UtcNow.UtcDateTime, ct))
        {
            if (!await EventAppender.SameAsStoredAsync(conn, tx, valid, ct)) return new BurnResult(BurnStatus.Conflict, 0, RejectReason.EventIdConflict);
            await using var read = new NpgsqlCommand("SELECT balance_points FROM loyalty_accounts WHERE account_id = @id", conn, tx);
            read.Parameters.AddWithValue("id", accountId);
            return new BurnResult(BurnStatus.Replayed, await read.ExecuteScalarAsync(ct) as long? ?? 0);
        }

        long balance;
        await using (var cmd = new NpgsqlCommand("SELECT balance_points FROM loyalty_accounts WHERE account_id = @id FOR UPDATE", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", accountId);
            if (await cmd.ExecuteScalarAsync(ct) is not long b) return new BurnResult(BurnStatus.AccountNotFound, 0); // rolls the event back
            balance = b;
        }

        if (points > balance) return new BurnResult(BurnStatus.Declined, balance); // rollback: nothing recorded

        await using (var update = new NpgsqlCommand("UPDATE loyalty_accounts SET burned_points = burned_points + @p WHERE account_id = @id", conn, tx))
        {
            update.Parameters.AddWithValue("p", points);
            update.Parameters.AddWithValue("id", accountId);
            await update.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return new BurnResult(BurnStatus.Approved, balance - points);
    }
}
