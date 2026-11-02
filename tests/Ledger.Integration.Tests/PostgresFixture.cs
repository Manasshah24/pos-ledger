using Ledger.Data;
using Npgsql;

namespace Ledger.Integration.Tests;

/// <summary>
/// Hands out empty databases on a real Postgres server, and drops them all afterwards.
/// Set LEDGER_TEST_PG to a superuser-capable connection string, e.g.
///   Host=localhost;Port=54329;Username=postgres;Password=postgres;Database=postgres
/// Tests fail (not skip) when it is missing, so a green run always means the database was really exercised.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly List<string> _created = [];
    private string _adminConnectionString = "";

    /// <summary>A database owned by this fixture, empty on first use. Handy for tests that let the app migrate it.</summary>
    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        _adminConnectionString = Environment.GetEnvironmentVariable("LEDGER_TEST_PG")
            ?? throw new InvalidOperationException(
                "LEDGER_TEST_PG is not set. Point it at a running Postgres, e.g. " +
                "Host=localhost;Port=54329;Username=postgres;Password=postgres;Database=postgres");

        ConnectionString = await NewDatabaseAsync();
    }

    /// <summary>A brand-new empty database; returns its connection string.</summary>
    public async Task<string> NewDatabaseAsync()
    {
        var name = $"ledger_test_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlDataSourceBuilder(_adminConnectionString).Build();
        await using var cmd = admin.CreateCommand($"CREATE DATABASE {name}");
        await cmd.ExecuteNonQueryAsync();
        lock (_created) _created.Add(name);
        return new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = name, MaxPoolSize = 100 }.ConnectionString;
    }

    /// <summary>A brand-new database with every migration applied.</summary>
    public async Task<NpgsqlDataSource> NewMigratedDataSourceAsync()
    {
        var source = NpgsqlDataSource.Create(await NewDatabaseAsync());
        await Migrator.MigrateAsync(source);
        return source;
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlDataSourceBuilder(_adminConnectionString).Build();
        foreach (var name in _created)
        {
            await using var cmd = admin.CreateCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
