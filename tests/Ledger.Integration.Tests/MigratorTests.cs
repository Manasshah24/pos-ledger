using Dapper;
using Ledger.Data;

namespace Ledger.Integration.Tests;

public class MigratorTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;

    public MigratorTests(PostgresFixture pg) => _pg = pg;

    private static async Task<string> NewDatabase(PostgresFixture pg)
    {
        // Each test needs a database no other test has migrated, so derive one from the fixture's server.
        var name = $"ledger_mig_{Guid.NewGuid():N}";
        await using var admin = Npgsql.NpgsqlDataSource.Create(pg.ConnectionString);
        await using var conn = await admin.OpenConnectionAsync();
        await conn.ExecuteAsync($"CREATE DATABASE {name}");
        return new Npgsql.NpgsqlConnectionStringBuilder(pg.ConnectionString) { Database = name }.ConnectionString;
    }

    [Fact]
    public async Task Applies_the_baseline_and_creates_the_stores_table()
    {
        await using var db = Npgsql.NpgsqlDataSource.Create(await NewDatabase(_pg));

        var applied = await Migrator.MigrateAsync(db);

        Assert.Equal(["0001_baseline.sql"], applied);
        await using var conn = await db.OpenConnectionAsync();
        await conn.ExecuteAsync("INSERT INTO stores (store_id, tz) VALUES ('S1', 'UTC')");
        Assert.Equal("USD", await conn.ExecuteScalarAsync<string>("SELECT currency FROM stores WHERE store_id = 'S1'"));
    }

    [Fact]
    public async Task Running_again_applies_nothing()
    {
        await using var db = Npgsql.NpgsqlDataSource.Create(await NewDatabase(_pg));
        await Migrator.MigrateAsync(db);

        var second = await Migrator.MigrateAsync(db);

        Assert.Empty(second);
        await using var conn = await db.OpenConnectionAsync();
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM schema_migrations"));
    }

    [Fact]
    public async Task Eight_replicas_starting_at_once_apply_each_script_exactly_once()
    {
        var cs = await NewDatabase(_pg);
        var sources = Enumerable.Range(0, 8).Select(_ => Npgsql.NpgsqlDataSource.Create(cs)).ToList();

        var results = await Task.WhenAll(sources.Select(s => Task.Run(() => Migrator.MigrateAsync(s))));

        Assert.Equal(1, results.Count(r => r.Count == 1)); // exactly one replica did the work
        Assert.Equal(7, results.Count(r => r.Count == 0));
        await using var conn = await sources[0].OpenConnectionAsync();
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM schema_migrations"));
        foreach (var s in sources) await s.DisposeAsync();
    }

    [Fact]
    public async Task A_failing_script_rolls_back_and_is_not_recorded()
    {
        await using var db = Npgsql.NpgsqlDataSource.Create(await NewDatabase(_pg));
        await Migrator.MigrateAsync(db);

        await Assert.ThrowsAnyAsync<Exception>(() => Migrator.MigrateAsync(db,
        [
            ("0002_half_done.sql", "CREATE TABLE half_done (id int); SELECT 1/0;"),
        ]));

        await using var conn = await db.OpenConnectionAsync();
        Assert.Null(await conn.ExecuteScalarAsync<string>("SELECT to_regclass('half_done')::text")); // statement 1 rolled back too
        Assert.Equal(["0001_baseline.sql"], (await conn.QueryAsync<string>("SELECT version FROM schema_migrations")).ToList());
    }

    [Fact]
    public async Task After_a_failure_a_later_corrected_run_still_works()
    {
        await using var db = Npgsql.NpgsqlDataSource.Create(await NewDatabase(_pg));
        await Migrator.MigrateAsync(db);
        await Assert.ThrowsAnyAsync<Exception>(() => Migrator.MigrateAsync(db, [("0002_x.sql", "SELECT 1/0;")]));

        var applied = await Migrator.MigrateAsync(db, [("0002_x.sql", "CREATE TABLE fixed_up (id int);")]);

        Assert.Equal(["0002_x.sql"], applied); // the failed attempt left the advisory lock released and nothing recorded
    }
}
