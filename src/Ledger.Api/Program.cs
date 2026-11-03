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
builder.Services.AddSingleton<Rebuilder>();
builder.Services.AddSingleton<GiftCardService>();
builder.Services.AddSingleton<LoyaltyService>();
builder.Services.AddHostedService<Ledger.Api.DayCloseScheduler>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles(); // the small ops dashboard in wwwroot/

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

app.MapGet("/admin/hash", async (Rebuilder rebuilder, CancellationToken ct) => Results.Ok(await rebuilder.HashAsync(ct)));

app.MapPost("/admin/rebuild", async (bool? useSnapshot, Rebuilder rebuilder, CancellationToken ct) =>
    Results.Ok(await rebuilder.RebuildAsync(useSnapshot ?? false, ct)));

app.MapPost("/admin/snapshot", async (Rebuilder rebuilder, CancellationToken ct) => Results.Ok(await rebuilder.CreateSnapshotAsync(ct)));

app.MapPut("/giftcards/{cardId}", async (string cardId, CardRequest body, GiftCardService cards) =>
{
    if (string.IsNullOrWhiteSpace(cardId) || cardId.Length > EventValidator.MaxIdLength || cardId.Contains('\0') || body.BalanceMinor is null or < 0)
        return Results.BadRequest(new { error = "invalid_card" });

    return await cards.IssueAsync(cardId, body.BalanceMinor.Value) switch
    {
        IssueResult.Created => Results.Created($"/giftcards/{cardId}", new { cardId, balanceMinor = body.BalanceMinor }),
        IssueResult.Unchanged => Results.Ok(new { cardId, balanceMinor = body.BalanceMinor }),
        _ => Results.Conflict(new { error = "card_exists_with_different_balance" }),
    };
});

app.MapGet("/giftcards/flags", async (string? cardId, GiftCardService cards, CancellationToken ct) => Results.Ok(await cards.FlagsAsync(cardId, ct)));

app.MapGet("/giftcards/{cardId}", async (string cardId, GiftCardService cards, CancellationToken ct) =>
    await cards.GetAsync(cardId, ct) is { } card ? Results.Ok(card) : Results.NotFound());

app.MapPost("/giftcards/{cardId}/leases", async (string cardId, LeaseRequest body, GiftCardService cards, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.StoreId) || body.AmountMinor is null or <= 0 || body.TtlMinutes is <= 0)
        return Results.BadRequest(new { error = "invalid_lease_request" });

    var result = await cards.LeaseAsync(cardId, body.StoreId, body.AmountMinor.Value, TimeSpan.FromMinutes(body.TtlMinutes ?? 360), ct);
    return result.Outcome switch
    {
        LeaseOutcome.Granted => Results.Created($"/giftcards/{cardId}", result.Grant),
        LeaseOutcome.NothingAvailable => Results.Conflict(new { error = "nothing_available" }),
        _ => Results.NotFound(new { error = result.Outcome.ToString() }),
    };
});

app.MapPost("/giftcards/{cardId}/redeem", async (string cardId, RedeemRequest body, GiftCardService cards, CancellationToken ct) =>
{
    var r = await cards.RedeemOnlineAsync(body.EventId ?? "", body.StoreId ?? "", body.TerminalId ?? "", cardId, body.AmountMinor ?? 0, body.OccurredAt, ct);
    return r.Status switch
    {
        RedeemStatus.Approved or RedeemStatus.Replayed => Results.Ok(new { approved = true, replayed = r.Status == RedeemStatus.Replayed, balanceMinor = r.BalanceMinor, availableMinor = r.AvailableMinor }),
        RedeemStatus.Declined => Results.Conflict(new { approved = false, balanceMinor = r.BalanceMinor, availableMinor = r.AvailableMinor }),
        RedeemStatus.Invalid => Results.BadRequest(new { error = r.Reason }),
        RedeemStatus.Conflict => Results.Conflict(new { error = r.Reason }),
        _ => Results.NotFound(new { error = r.Status.ToString() }),
    };
});

app.MapGet("/loyalty/negative", async (LoyaltyService loyalty, CancellationToken ct) => Results.Ok(await loyalty.NegativeAsync(ct)));

app.MapGet("/loyalty/{accountId}", async (string accountId, LoyaltyService loyalty, CancellationToken ct) =>
    await loyalty.GetAsync(accountId, ct) is { } account ? Results.Ok(account) : Results.NotFound());

app.MapPost("/loyalty/{accountId}/burn", async (string accountId, BurnRequest body, LoyaltyService loyalty, CancellationToken ct) =>
{
    var r = await loyalty.BurnOnlineAsync(body.EventId ?? "", body.StoreId ?? "", body.TerminalId ?? "", accountId, body.Points ?? 0, body.OccurredAt, ct);
    return r.Status switch
    {
        BurnStatus.Approved or BurnStatus.Replayed => Results.Ok(new { approved = true, replayed = r.Status == BurnStatus.Replayed, balancePoints = r.BalancePoints }),
        BurnStatus.Declined => Results.Conflict(new { approved = false, balancePoints = r.BalancePoints }),
        BurnStatus.Invalid => Results.BadRequest(new { error = r.Reason }),
        BurnStatus.Conflict => Results.Conflict(new { error = r.Reason }),
        _ => Results.NotFound(new { error = r.Status.ToString() }),
    };
});

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

public sealed record CardRequest(long? BalanceMinor);

public sealed record BurnRequest(string? EventId, string? StoreId, string? TerminalId, long? Points, DateTimeOffset? OccurredAt);

public sealed record LeaseRequest(string? StoreId, long? AmountMinor, int? TtlMinutes);

public sealed record RedeemRequest(string? EventId, string? StoreId, string? TerminalId, long? AmountMinor, DateTimeOffset? OccurredAt);

public partial class Program;
