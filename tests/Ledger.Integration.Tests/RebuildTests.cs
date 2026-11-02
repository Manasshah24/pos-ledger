using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Ledger.Core.Events;
using Ledger.Data;
using Ledger.Harness;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using static Ledger.Integration.Tests.IngestHelpers;

namespace Ledger.Integration.Tests;

public class RebuildTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;

    public RebuildTests(PostgresFixture pg) => _pg = pg;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static WebApplicationFactory<Program> Replica(string cs) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Ledger", cs));

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    /// <summary>Replays the log with the pure projector, reading the log and closes with plain SQL (no Rebuilder code involved).</summary>
    private static async Task<ProjectionHashes> ReplayHashAsync(string cs)
    {
        await using var db = NpgsqlDataSource.Create(cs);
        await using var conn = await db.OpenConnectionAsync();
        var events = (await conn.QueryAsync<(long Seq, string Id, string Store, string Type, DateOnly Day, string Sale, long Amount)>(
            "SELECT log_seq, event_id, store_id, type, business_date, sale_id, amount_minor FROM events"))
            .Select(e => new LogEvent(e.Seq, e.Id, e.Store, e.Type, e.Day, e.Sale, e.Amount));
        var closures = (await conn.QueryAsync<(string Store, long Mark, DateOnly Through)>(
            "SELECT store_id, log_seq_mark, through_date FROM day_closures ORDER BY id"))
            .Select(c => new ClosureMark(c.Store, c.Mark, c.Through));
        return ProjectionHash.Of(Projector.Replay(events, closures));
    }

    private sealed record RebuildResponse(ProjectionHashes Before, ProjectionHashes After, bool Identical, int Events, int StoreDays, int Adjustments, int Parked);

    [Fact]
    public async Task Rebuilding_from_the_log_reproduces_the_live_read_models_exactly_every_time_even_after_they_are_wiped()
    {
        var data = Generator.Generate(new GeneratorOptions { Seed = 91, TotalDeliveries = 10_000, Stores = 50 });
        var cs = await _pg.NewDatabaseAsync();
        await using var a = Replica(cs);
        await using var b = Replica(cs);
        var clients = new[] { a.CreateClient(), b.CreateClient() };
        foreach (var s in data.Stores) await clients[0].PutAsJsonAsync($"/stores/{s.StoreId}", new { tz = s.TimeZoneId });

        // Orphan returns, duplicates, outages, and a mid-run close so that late events become adjustments.
        var batches = data.ToBatches(250).Select(x => ToBatchJson(x.Select(d => d.Event))).ToList();
        var split = batches.Count * 6 / 10;
        async Task Send(IEnumerable<string> bodies) => await Parallel.ForEachAsync(bodies.Select((body, i) => (body, i)),
            new ParallelOptions { MaxDegreeOfParallelism = 6 },
            async (x, _) => (await clients[x.i % 2].PostAsync("/events/batch", Json(x.body))).EnsureSuccessStatusCode());
        await Send(batches.Take(split));
        foreach (var s in data.Stores) (await clients[0].PostAsync($"/admin/stores/{s.StoreId}/close?through=2026-01-06", null)).EnsureSuccessStatusCode();
        await Send(batches.Skip(split));

        var live = (await clients[0].GetFromJsonAsync<ProjectionHashes>("/admin/hash", Web))!;
        var totalsBefore = await clients[0].GetStringAsync("/totals");
        var replayed = await ReplayHashAsync(cs);

        // Two independent implementations (incremental SQL on ingest, pure replay of the log) agree on every table.
        Assert.Equal(live, replayed);

        // Rebuild three times: identical to live each time, and the API answers do not change by a byte.
        for (var i = 0; i < 3; i++)
        {
            var result = (await (await clients[i % 2].PostAsync("/admin/rebuild", null)).Content.ReadFromJsonAsync<RebuildResponse>(Web))!;
            Assert.True(result.Identical);
            Assert.Equal(live, result.After);
            Assert.True(result.Adjustments > 0 && result.StoreDays > 0);
        }
        Assert.Equal(totalsBefore, await clients[1].GetStringAsync("/totals"));

        // Wipe the read models completely, then rebuild: same fingerprint, same answers.
        await using (var db = NpgsqlDataSource.Create(cs))
        await using (var conn = await db.OpenConnectionAsync())
            await conn.ExecuteAsync("TRUNCATE store_day_totals, adjustments, parked_events");
        Assert.Equal(0, (await clients[0].GetFromJsonAsync<List<StoreDayTotal>>("/totals", Web))!.Count(t => t.NetMinor != 0));

        var rebuilt = (await (await clients[0].PostAsync("/admin/rebuild", null)).Content.ReadFromJsonAsync<RebuildResponse>(Web))!;

        Assert.False(rebuilt.Identical); // it really was different (empty) before
        Assert.Equal(live, rebuilt.After);
        Assert.Equal(totalsBefore, await clients[0].GetStringAsync("/totals"));
    }

    [Fact]
    public async Task Rebuilds_running_while_two_replicas_ingest_neither_lose_events_nor_corrupt_the_read_models()
    {
        var cs = await _pg.NewDatabaseAsync();
        await using var a = Replica(cs);
        await using var b = Replica(cs);
        var clients = new[] { a.CreateClient(), b.CreateClient() };
        await clients[0].PutAsJsonAsync("/stores/S1", new { tz = "UTC" });
        await clients[0].PutAsJsonAsync("/stores/S2", new { tz = "Asia/Kolkata" });
        var batches = Enumerable.Range(0, 120).Select(n => JsonSerializer.Serialize(new
        {
            events = Enumerable.Range(0, 10).Select(i => Incoming($"b{n:D3}-{i}", store: i % 2 == 0 ? "S1" : "S2", amount: 100,
                at: $"2026-01-{5 + n % 3:D2}T12:00:00Z")),
        }, Web)).ToList();
        var done = false;
        var results = new List<RebuildResponse>();

        var rebuilds = Task.Run(async () =>
        {
            for (var i = 0; !Volatile.Read(ref done); i++)
            {
                results.Add((await (await clients[i % 2].PostAsync("/admin/rebuild", null)).Content.ReadFromJsonAsync<RebuildResponse>(Web))!);
                await Task.Delay(20);
            }
        });
        await Parallel.ForEachAsync(batches.Select((x, i) => (x, i)), new ParallelOptions { MaxDegreeOfParallelism = 6 },
            async (x, _) => (await clients[x.i % 2].PostAsync("/events/batch", Json(x.x))).EnsureSuccessStatusCode());
        Volatile.Write(ref done, true);
        await rebuilds;

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.True(r.Identical)); // at every moment, live state == what the log says it should be
        var live = (await clients[0].GetFromJsonAsync<ProjectionHashes>("/admin/hash", Web))!;
        Assert.Equal(live, await ReplayHashAsync(cs));
        Assert.Equal(1_200, (await clients[0].GetFromJsonAsync<List<StoreDayTotal>>("/totals", Web))!.Sum(t => t.SalesMinor) / 100);
    }
}
