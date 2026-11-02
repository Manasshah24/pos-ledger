using Npgsql;

namespace Ledger.Integration.Tests;

/// <summary>
/// Gives each test class its own empty database on a real Postgres server.
/// Set LEDGER_TEST_PG to a superuser-capable connection string, e.g.
///   Host=localhost;Port=54329;Username=postgres;Password=postgres;Database=postgres
/// Tests fail (not skip) when it is missing, so a green run always means the database was really exercised.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private string _adminConnectionString = "";
    private string _databaseName = "";

    public string ConnectionString { get; private set; } = "";

    public NpgsqlDataSource CreateDataSource() => NpgsqlDataSource.Create(ConnectionString);

    public async Task InitializeAsync()
    {
        _adminConnectionString = Environment.GetEnvironmentVariable("LEDGER_TEST_PG")
            ?? throw new InvalidOperationException(
                "LEDGER_TEST_PG is not set. Point it at a running Postgres, e.g. " +
                "Host=localhost;Port=54329;Username=postgres;Password=postgres;Database=postgres");

        _databaseName = $"ledger_test_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlDataSourceBuilder(_adminConnectionString).Build();
        await using var cmd = admin.CreateCommand($"CREATE DATABASE {_databaseName}");
        await cmd.ExecuteNonQueryAsync();

        ConnectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = _databaseName, Pooling = true }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlDataSourceBuilder(_adminConnectionString).Build();
        await using var cmd = admin.CreateCommand($"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)");
        await cmd.ExecuteNonQueryAsync();
    }
}
