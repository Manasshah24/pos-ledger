using System.Reflection;
using Dapper;
using Npgsql;

namespace Ledger.Data;

/// <summary>
/// Applies embedded, versioned .sql scripts in order. Safe to run from several replicas at once:
/// a Postgres advisory lock makes them take turns, so each script is applied exactly once.
/// </summary>
public static class Migrator
{
    private const long AdvisoryLockKey = 7_201_511_001;
    private const string Marker = ".Migrations.";

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct = default) =>
        MigrateAsync(dataSource, LoadScripts(), ct);

    /// <summary>Applies the given (version, sql) scripts. Exposed so tests can feed a deliberately broken script.</summary>
    public static async Task<IReadOnlyList<string>> MigrateAsync(
        NpgsqlDataSource dataSource, IEnumerable<(string Version, string Sql)> scripts, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await conn.ExecuteAsync("SELECT pg_advisory_lock(@k)", new { k = AdvisoryLockKey });
        try
        {
            await conn.ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version    text PRIMARY KEY,
                    applied_at timestamptz NOT NULL DEFAULT now()
                )
                """);

            var done = (await conn.QueryAsync<string>("SELECT version FROM schema_migrations")).ToHashSet();
            var applied = new List<string>();

            foreach (var (version, sql) in scripts)
            {
                if (done.Contains(version)) continue;

                await using var tx = await conn.BeginTransactionAsync(ct);
                await conn.ExecuteAsync(sql, transaction: tx);
                await conn.ExecuteAsync("INSERT INTO schema_migrations (version) VALUES (@version)", new { version }, tx);
                await tx.CommitAsync(ct);
                applied.Add(version);
            }

            return applied;
        }
        finally
        {
            await conn.ExecuteAsync("SELECT pg_advisory_unlock(@k)", new { k = AdvisoryLockKey });
        }
    }

    private static IEnumerable<(string Version, string Sql)> LoadScripts()
    {
        var asm = typeof(Migrator).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var version = name[(name.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length)..];
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            yield return (version, reader.ReadToEnd());
        }
    }
}
