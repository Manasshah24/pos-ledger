using Dapper;
using Ledger.Core;
using Ledger.Data;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Ledger")
    ?? throw new InvalidOperationException("ConnectionStrings:Ledger is not configured.");

builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton(builder.Configuration.GetSection("Ledger").Get<LedgerOptions>() ?? new LedgerOptions());

var app = builder.Build();

// Fail fast: a replica that cannot reach or migrate the database must not serve traffic.
await Migrator.MigrateAsync(app.Services.GetRequiredService<NpgsqlDataSource>());

app.MapGet("/health", async (NpgsqlDataSource db) =>
{
    try
    {
        await using var conn = await db.OpenConnectionAsync();
        await conn.ExecuteScalarAsync<int>("SELECT 1");
        return Results.Ok(new { status = "ok", db = "up" });
    }
    catch (Exception)
    {
        return Results.Json(new { status = "degraded", db = "down" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.Run();

public partial class Program;
