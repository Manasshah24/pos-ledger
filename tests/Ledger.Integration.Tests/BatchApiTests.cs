using System.Net;
using System.Net.Http.Json;
using System.Text;
using Dapper;
using Ledger.Core.Events;
using Ledger.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using static Ledger.Integration.Tests.IngestHelpers;

namespace Ledger.Integration.Tests;

public class BatchApiTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;

    public BatchApiTests(PostgresFixture pg) => _pg = pg;

    private sealed record BatchResponse(int Inserted, int Duplicates, List<RejectedEvent> Rejected);

    private static WebApplicationFactory<Program> Replica(string connectionString) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Ledger", connectionString));

    private static async Task<HttpResponseMessage> PutStore(HttpClient c, string id, string tz) =>
        await c.PutAsJsonAsync($"/stores/{id}", new { tz });

    private static async Task<BatchResponse> PostBatch(HttpClient c, string body)
    {
        var response = await c.PostAsync("/events/batch", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BatchResponse>())!;
    }

    private const string Sale1 =
        """{"eventId":"e1","storeId":"S1","terminalId":"T1","type":"sale","occurredAt":"2026-01-05T10:00:00Z","saleId":"sale-1","amountMinor":500}""";

    [Fact]
    public async Task Store_registration_is_idempotent_and_refuses_a_time_zone_change()
    {
        await using var app = Replica(await _pg.NewDatabaseAsync());
        var client = app.CreateClient();

        Assert.Equal(HttpStatusCode.Created, (await PutStore(client, "S1", "Asia/Kolkata")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutStore(client, "S1", "Asia/Kolkata")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PutStore(client, "S1", "UTC")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutStore(client, "S2", "Mars/Olympus")).StatusCode);
    }

    [Fact]
    public async Task Batch_is_accepted_replaying_it_changes_nothing_and_rejects_are_reported_by_position()
    {
        await using var app = Replica(await _pg.NewDatabaseAsync());
        var client = app.CreateClient();
        await PutStore(client, "S1", "UTC");
        var body = $$"""{"events":[{{Sale1}}, {"eventId":"e2","amountMinor":12.5}]}""";

        var first = await PostBatch(client, body);
        var second = await PostBatch(client, body);

        Assert.Equal((1, 0), (first.Inserted, first.Duplicates));
        Assert.Equal((0, 1), (second.Inserted, second.Duplicates));
        Assert.Equal([new RejectedEvent(1, null, RejectReason.MalformedEvent)], first.Rejected);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    public async Task A_body_that_is_not_a_batch_is_a_400(string body)
    {
        await using var app = Replica(await _pg.NewDatabaseAsync());

        var response = await app.CreateClient().PostAsync("/events/batch", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Two_replicas_receiving_the_same_batch_at_the_same_time_insert_it_once()
    {
        var cs = await _pg.NewDatabaseAsync();
        await using var a = Replica(cs);
        await using var b = Replica(cs);
        var ca = a.CreateClient();
        var cb = b.CreateClient();
        await PutStore(ca, "S1", "UTC");
        var body = System.Text.Json.JsonSerializer.Serialize(
            new { events = Enumerable.Range(0, 1000).Select(i => Incoming($"e{i:D4}")) },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(() => PostBatch(i % 2 == 0 ? ca : cb, body))));

        Assert.Equal(1000, results.Sum(r => r.Inserted));
        Assert.Equal(9000, results.Sum(r => r.Duplicates));
    }

    [Fact]
    public async Task Harness_stream_through_two_replicas_leaves_exactly_the_unique_events_and_the_oracles_totals()
    {
        // A scaled-down version of the panel's replay: duplicates, orphans, outages, scrambled and concurrent batches.
        var data = Generator.Generate(new GeneratorOptions { Seed = 2026, TotalDeliveries = 20_000, Stores = 100 });
        var cs = await _pg.NewDatabaseAsync();
        await using var a = Replica(cs);
        await using var b = Replica(cs);
        var clients = new[] { a.CreateClient(), b.CreateClient() };
        foreach (var s in data.Stores) Assert.True((await PutStore(clients[0], s.StoreId, s.TimeZoneId)).IsSuccessStatusCode);

        var batches = data.ToBatches(500).Select(batch => ToBatchJson(batch.Select(d => d.Event))).ToList();
        new SplitMix64(5).Shuffle(batches);
        var inserted = 0;
        var rejected = 0;
        await Parallel.ForEachAsync(batches.Select((body, i) => (body, i)), new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (item, _) =>
        {
            var r = await PostBatch(clients[item.i % 2], item.body);
            Interlocked.Add(ref inserted, r.Inserted);
            Interlocked.Add(ref rejected, r.Rejected.Count);
        });

        Assert.Equal(0, rejected);
        Assert.Equal(data.UniqueEvents.Count, inserted);

        // The log, aggregated by SQL, equals the independent oracle for every store-day.
        await using var db = NpgsqlDataSource.Create(cs);
        await using var conn = await db.OpenConnectionAsync();
        var fromLog = new SortedDictionary<StoreDay, long>();
        foreach (var (store, day, net) in await conn.QueryAsync<(string, DateOnly, long)>("""
            SELECT store_id, business_date, sum(CASE type WHEN 'sale' THEN amount_minor ELSE -amount_minor END)
            FROM events GROUP BY store_id, business_date
            """))
            fromLog[new StoreDay(store, day)] = net;

        Assert.Equal(Oracle.Hash(Oracle.NetTotals(data.UniqueEvents, data.Stores)), Oracle.Hash(fromLog));
    }
}
