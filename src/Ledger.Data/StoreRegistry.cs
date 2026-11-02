using Npgsql;

namespace Ledger.Data;

public enum StoreUpsertResult
{
    Created,
    Unchanged,

    /// <summary>The store already exists with a different time zone. Changing it would silently re-date history.</summary>
    Conflict,
}

public sealed class StoreRegistry(NpgsqlDataSource db)
{
    public async Task<StoreUpsertResult> UpsertAsync(string storeId, string timeZoneId, string currency, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO stores (store_id, tz, currency) VALUES (@id, @tz, @cur) ON CONFLICT (store_id) DO NOTHING", conn))
        {
            insert.Parameters.AddWithValue("id", storeId);
            insert.Parameters.AddWithValue("tz", timeZoneId);
            insert.Parameters.AddWithValue("cur", currency);
            if (await insert.ExecuteNonQueryAsync(ct) == 1) return StoreUpsertResult.Created;
        }

        await using var select = new NpgsqlCommand("SELECT tz FROM stores WHERE store_id = @id", conn);
        select.Parameters.AddWithValue("id", storeId);
        var existing = (string?)await select.ExecuteScalarAsync(ct);
        return existing == timeZoneId ? StoreUpsertResult.Unchanged : StoreUpsertResult.Conflict;
    }
}
