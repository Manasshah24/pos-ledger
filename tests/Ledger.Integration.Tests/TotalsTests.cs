using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Ledger.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using static Ledger.Integration.Tests.IngestHelpers;

namespace Ledger.Integration.Tests;

public class TotalsTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;

    public TotalsTests(PostgresFixture pg) => _pg = pg;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static WebApplicationFactory<Program> Replica(string cs) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Ledger", cs));

    private static Task<HttpResponseMessage> Post(HttpClient c, params Ledger.Core.Events.IncomingEvent[] events) =>
        c.PostAsync("/events/batch", new StringContent(JsonSerializer.Serialize(new { events }, Web), Encoding.UTF8, "application/json"));

    [Fact]
    public async Task Totals_follow_sale_return_void_per_business_day_and_a_replayed_batch_changes_nothing()
    {
        await using var app = Replica(await _pg.NewDatabaseAsync());
        var client = app.CreateClient();
        await client.PutAsJsonAsync("/stores/S1", new { tz = "UTC" });
        var batch = new[]
        {
            Incoming("e1", type: "sale", amount: 1000, sale: "s1", at: "2026-01-05T10:00:00Z"),
            Incoming("e2", type: "return", amount: 300, sale: "s1", at: "2026-01-05T11:00:00Z"),
            Incoming("e3", type: "sale", amount: 500, sale: "s2", at: "2026-01-05T12:00:00Z"),
            Incoming("e4", type: "void", amount: 500, sale: "s2", at: "2026-01-05T12:05:00Z"),
            Incoming("e5", type: "sale", amount: 70, sale: "s3", at: "2026-01-06T01:59:59Z"), // still the 5th
            Incoming("e6", type: "sale", amount: 40, sale: "s4", at: "2026-01-06T02:00:00Z"), // the 6th
        };

        await Post(client, batch);
        await Post(client, batch);
        await Post(client, batch[..3]);

        var all = await client.GetFromJsonAsync<List<StoreDayTotal>>("/totals", Web);
        Assert.Equal(
        [
            new StoreDayTotal("S1", new DateOnly(2026, 1, 5), 1570, 300, 500, 770),
            new StoreDayTotal("S1", new DateOnly(2026, 1, 6), 40, 0, 0, 40),
        ], all);

        Assert.Equal(40, (await client.GetFromJsonAsync<StoreDayTotal>("/stores/S1/days/2026-01-06", Web))!.NetMinor);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/stores/S1/days/2030-01-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/stores/S1/days/tomorrow")).StatusCode);
    }

    [Fact]
    public async Task A_reader_never_sees_a_partly_applied_batch_while_two_replicas_ingest()
    {
        var cs = await _pg.NewDatabaseAsync();
        await using var a = Replica(cs);
        await using var b = Replica(cs);
        var writers = new[] { a.CreateClient(), b.CreateClient() };
        await writers[0].PutAsJsonAsync("/stores/S1", new { tz = "UTC" });

        // Every batch adds exactly 1000 across five different days, so any consistent view totals a multiple of 1000.
        Ledger.Core.Events.IncomingEvent[] BatchOf(int n) => Enumerable.Range(0, 10)
            .Select(i => Incoming($"b{n:D3}-{i}", amount: 100, at: $"2026-01-{10 + i % 5:D2}T12:00:00Z")).ToArray();

        var done = false;
        var apiSums = new List<long>();
        var apiReader = Task.Run(async () =>
        {
            var reader = writers[1]; // read through the other replica too
            while (!Volatile.Read(ref done))
            {
                var rows = await reader.GetFromJsonAsync<List<StoreDayTotal>>("/totals", Web);
                lock (apiSums) apiSums.Add(rows!.Sum(r => r.NetMinor));
                await Task.Delay(20); // ~50 requests per second
            }
        });

        var dbViolations = 0;
        var dbReader = Task.Run(async () =>
        {
            await using var db = NpgsqlDataSource.Create(cs);
            while (!Volatile.Read(ref done))
            {
                // One snapshot over both tables: the log and the totals must always agree.
                await using var conn = await db.OpenConnectionAsync();
                await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
                var fromLog = await conn.ExecuteScalarAsync<long>("SELECT coalesce(sum(amount_minor), 0) FROM events", transaction: tx);
                var fromTotals = await conn.ExecuteScalarAsync<long>("SELECT coalesce(sum(net_minor), 0) FROM store_day_totals", transaction: tx);
                if (fromLog != fromTotals) Interlocked.Increment(ref dbViolations);
            }
        });

        await Parallel.ForEachAsync(Enumerable.Range(0, 60), new ParallelOptions { MaxDegreeOfParallelism = 6 }, async (n, _) =>
            Assert.Equal(HttpStatusCode.OK, (await Post(writers[n % 2], BatchOf(n))).StatusCode));
        Volatile.Write(ref done, true);
        await Task.WhenAll(apiReader, dbReader);

        Assert.NotEmpty(apiSums);
        Assert.All(apiSums, s => Assert.Equal(0, s % 1000)); // never a half-applied batch
        Assert.Equal(0, dbViolations);
        Assert.Equal(60_000, (await writers[0].GetFromJsonAsync<List<StoreDayTotal>>("/totals", Web))!.Sum(r => r.NetMinor));
    }
}
