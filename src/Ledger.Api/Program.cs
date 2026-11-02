using Dapper;
using Ledger.Core;
using Ledger.Core.Events;
using Ledger.Data;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Ledger")
    ?? throw new InvalidOperationException("ConnectionStrings:Ledger is not configured.");

builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton(builder.Configuration.GetSection("Ledger").Get<LedgerOptions>() ?? new LedgerOptions());
builder.Services.AddSingleton<EventIngestor>();
builder.Services.AddSingleton<StoreRegistry>();
builder.Services.AddSingleton<TotalsQuery>();
builder.Services.AddSingleton<ParkedQuery>();
builder.Services.AddSingleton<DayCloser>();
builder.Services.AddHostedService<Ledger.Api.DayCloseScheduler>();

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

app.MapPut("/stores/{storeId}", async (string storeId, StoreRequest body, StoreRegistry stores) =>
{
    if (string.IsNullOrWhiteSpace(storeId) || storeId.Length > EventValidator.MaxIdLength || storeId.Contains('\0'))
        return Results.BadRequest(new { error = "invalid_store_id" });
    if (string.IsNullOrWhiteSpace(body.Tz) || !TimeZoneInfo.TryFindSystemTimeZoneById(body.Tz, out _))
        return Results.BadRequest(new { error = "invalid_time_zone" });

    var currency = body.Currency ?? "USD";
    if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
        return Results.BadRequest(new { error = "invalid_currency" });

    return await stores.UpsertAsync(storeId, body.Tz, currency) switch
    {
        StoreUpsertResult.Created => Results.Created($"/stores/{storeId}", new { storeId, tz = body.Tz, currency }),
        StoreUpsertResult.Unchanged => Results.Ok(new { storeId, tz = body.Tz, currency }),
        _ => Results.Conflict(new { error = "time_zone_change_not_allowed" }),
    };
});

app.MapPost("/events/batch", async (HttpRequest request, EventIngestor ingestor, CancellationToken ct) =>
{
    using var reader = new StreamReader(request.Body);
    var parsed = BatchParser.Parse(await reader.ReadToEndAsync(ct));

    if (parsed.Error == BatchParser.BatchTooLarge)
        return Results.Json(new { error = parsed.Error, max = BatchParser.MaxBatchSize }, statusCode: StatusCodes.Status413PayloadTooLarge);
    if (parsed.Events is null)
        return Results.BadRequest(new { error = parsed.Error });

    var result = await ingestor.IngestAsync(parsed.Events, ct);
    return Results.Ok(new { inserted = result.Inserted, duplicates = result.Duplicates, rejected = result.Rejected });
});

app.MapGet("/totals", async (string? storeId, TotalsQuery totals, CancellationToken ct) =>
    Results.Ok(await totals.GetAsync(storeId, ct)));

app.MapPost("/admin/stores/{storeId}/close", async (string storeId, string through, DayCloser closer, CancellationToken ct) =>
{
    if (!DateOnly.TryParseExact(through, "yyyy-MM-dd", out var day))
        return Results.BadRequest(new { error = "invalid_date" });

    var result = await closer.CloseThroughAsync(storeId, day, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.MapPost("/admin/close-due", async (DayCloser closer, CancellationToken ct) => Results.Ok(await closer.CloseDueAsync(ct)));

app.MapGet("/parked", async (ParkedQuery parked, CancellationToken ct) => Results.Ok(await parked.GetOpenAsync(ct)));

app.MapGet("/stores/{storeId}/days/{date}", async (string storeId, string date, TotalsQuery totals, CancellationToken ct) =>
{
    if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", out var day))
        return Results.BadRequest(new { error = "invalid_date" });

    var row = (await totals.GetAsync(storeId, ct)).FirstOrDefault(t => t.BusinessDate == day);
    return row is null ? Results.NotFound() : Results.Ok(row);
});

app.Run();

public sealed record StoreRequest(string? Tz, string? Currency);

public partial class Program;
