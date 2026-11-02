using Dapper;
using Ledger.Data;
using Npgsql;

namespace Ledger.Integration.Tests;

public class MigratorTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;

    public MigratorTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task Applies_every_script_in_order_and_running_again_applies_nothing()
    {
        await using var db = NpgsqlDataSource.Create(await _pg.NewDatabaseAsync());

        Assert.Equal(["0001_baseline.sql", "0002_events.sql"], await Migrator.MigrateAsync(db));
        Assert.Empty(await Migrator.MigrateAsync(db));
    }

    [Fact]
    public async Task Eight_replicas_starting_at_once_apply_each_script_exactly_once()
    {
        var cs = await _pg.NewDatabaseAsync();
        var sources = Enumerable.Range(0, 8).Select(_ => NpgsqlDataSource.Create(cs)).ToList();

        var results = await Task.WhenAll(sources.Select(s => Task.Run(() => Migrator.MigrateAsync(s))));

        var worker = Assert.Single(results, r => r.Count > 0); // exactly one replica did the work
        await using var conn = await sources[0].OpenConnectionAsync();
        Assert.Equal(worker.Count, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM schema_migrations"));
        foreach (var s in sources) await s.DisposeAsync();
    }

    [Fact]
    public async Task A_failing_script_rolls_back_completely_and_is_not_recorded()
    {
        await using var db = NpgsqlDataSource.Create(await _pg.NewDatabaseAsync());
        var good = await Migrator.MigrateAsync(db);

        await Assert.ThrowsAnyAsync<Exception>(() => Migrator.MigrateAsync(db, [("9999_bad.sql", "CREATE TABLE half_done (id int); SELECT 1/0;")]));

        await using var conn = await db.OpenConnectionAsync();
        Assert.Null(await conn.ExecuteScalarAsync<string>("SELECT to_regclass('half_done')::text"));
        Assert.Equal(good.Count, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM schema_migrations"));
    }
}
