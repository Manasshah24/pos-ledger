using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using Dapper;
using Ledger.Data;
using Ledger.Harness;
using Npgsql;
using static Ledger.Integration.Tests.IngestHelpers;

namespace Ledger.Integration.Tests;

/// <summary>
/// The whole system, as it will be deployed: two real API processes against one Postgres, the panel's full replay profile
/// (100,000 deliveries: 12% duplicates, 3% orphan returns, shuffled within 48 hours, 20 store outages), queries at about
/// 50 requests per second during ingest, and a replica killed with the equivalent of kill -9 in the middle of the run.
/// Terminals behave as real ones do: a batch that was not acknowledged is simply sent again, to whichever replica answers.
/// </summary>
public class ChaosTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;

    public ChaosTests(PostgresFixture pg) => _pg = pg;

    private sealed class Replica(string name, string dll, string connectionString, int port) : IDisposable
    {
        private Process? _process;
        public string Name { get; } = name;
        public Uri Address { get; } = new($"http://127.0.0.1:{port}");

        public async Task StartAsync()
        {
            var info = new ProcessStartInfo("dotnet", $"\"{dll}\" --urls {Address}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            info.Environment["ConnectionStrings__Ledger"] = connectionString;
            info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            _process = Process.Start(info)!;
            _process.OutputDataReceived += (_, _) => { };
            _process.ErrorDataReceived += (_, _) => { };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            using var http = new HttpClient { BaseAddress = Address, Timeout = TimeSpan.FromSeconds(2) };
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                try { if ((await http.GetAsync("/health")).IsSuccessStatusCode) return; }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                await Task.Delay(250);
            }

            throw new TimeoutException($"{Name} did not become healthy");
        }

        /// <summary>The equivalent of kill -9: the process is destroyed without any chance to clean up.</summary>
        public void Kill()
        {
            _process!.Kill(entireProcessTree: true);
            _process.WaitForExit();
        }

        public void Dispose()
        {
            try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { }
        }
    }

    private static string ApiDll()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PosLedger.slnx"))) dir = dir.Parent;
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        return Path.Combine(dir!.FullName, "src", "Ledger.Api", "bin", config, "net10.0", "Ledger.Api.dll");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task Full_replay_through_two_real_replicas_with_one_killed_midway_loses_nothing_and_ends_exactly_right()
    {
        var data = Generator.Generate(new GeneratorOptions { Seed = 2026 }); // 100,000 deliveries, the full profile
        var cs = await _pg.NewDatabaseAsync();
        using var a = new Replica("A", ApiDll(), cs, FreePort());
        using var b = new Replica("B", ApiDll(), cs, FreePort());
        await Task.WhenAll(a.StartAsync(), b.StartAsync());
        var replicas = new[] { a, b };
        var clients = replicas.Select(r => new HttpClient { BaseAddress = r.Address, Timeout = TimeSpan.FromSeconds(30) }).ToArray();
        foreach (var s in data.Stores) (await clients[1].PutAsJsonAsync($"/stores/{s.StoreId}", new { tz = s.TimeZoneId })).EnsureSuccessStatusCode();

        var batches = data.ToBatches(250).Select(x => ToBatchJson(x.Select(d => d.Event))).ToList();
        var acknowledged = 0;
        var failedAttempts = 0;
        var finished = false;

        // Queries at ~50 requests per second against the replica that stays up, and a snapshot-consistency check of the database
        // itself: the sales in the log must always equal the sales in the totals, whatever batch is half way through.
        var queryFailures = 0;
        var queries = 0;
        var queryLoop = Task.Run(async () =>
        {
            while (!Volatile.Read(ref finished))
            {
                try
                {
                    var response = await clients[1].GetAsync("/totals");
                    if (!response.IsSuccessStatusCode) Interlocked.Increment(ref queryFailures);
                    else await response.Content.ReadAsStringAsync();
                }
                catch (Exception) { Interlocked.Increment(ref queryFailures); }
                Interlocked.Increment(ref queries);
                await Task.Delay(20);
            }
        });
        var partialViews = 0;
        var consistencyLoop = Task.Run(async () =>
        {
            await using var db = NpgsqlDataSource.Create(cs);
            while (!Volatile.Read(ref finished))
            {
                await using var conn = await db.OpenConnectionAsync();
                await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
                var inLog = await conn.ExecuteScalarAsync<long>("SELECT coalesce(sum(amount_minor), 0) FROM events WHERE type = 'sale'", transaction: tx);
                var inTotals = await conn.ExecuteScalarAsync<long>("SELECT coalesce(sum(sales_minor + adj_sales_minor), 0) FROM store_day_totals", transaction: tx);
                if (inLog != inTotals) Interlocked.Increment(ref partialViews);
                await Task.Delay(50);
            }
        });

        // The killer: once a quarter of the batches are in, destroy replica A without warning, leave it down for a while, bring it back.
        var killer = Task.Run(async () =>
        {
            while (Volatile.Read(ref acknowledged) < batches.Count / 4) await Task.Delay(5);
            a.Kill();
            await Task.Delay(3_000);
            await a.StartAsync();
        });

        await Parallel.ForEachAsync(batches.Select((body, i) => (body, i)), new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (item, _) =>
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var response = await clients[(item.i + attempt) % 2].PostAsync("/events/batch", new StringContent(item.body, Encoding.UTF8, "application/json"));
                    if (response.IsSuccessStatusCode)
                    {
                        Interlocked.Increment(ref acknowledged);
                        return;
                    }
                }
                catch (Exception) { }

                Interlocked.Increment(ref failedAttempts);
                if (attempt > 200) throw new InvalidOperationException($"batch {item.i} could not be delivered");
                await Task.Delay(100);
            }
        });
        await killer;
        Volatile.Write(ref finished, true);
        await Task.WhenAll(queryLoop, consistencyLoop);

        Assert.True(failedAttempts > 0, "the kill never interrupted a request, so the test proved nothing");
        Assert.Equal(0, partialViews); // nobody ever saw a half-applied batch
        Assert.Equal(0, queryFailures); // the surviving replica answered every query
        Assert.True(queries > 100, $"only {queries} queries ran");

        await using var verify = NpgsqlDataSource.Create(cs);
        Assert.Equal(data.UniqueEvents.Count, await CountAsync(verify, "events")); // not one event lost, not one twice
        Assert.Equal(0, await CountAsync(verify, "rejected_events"));

        var served = new SortedDictionary<StoreDay, long>();
        foreach (var t in (await clients[1].GetFromJsonAsync<List<StoreDayTotal>>("/totals"))!) served[new StoreDay(t.StoreId, t.BusinessDate)] = t.NetMinor;
        Assert.Equal(Oracle.Hash(Oracle.NetTotals(data.UniqueEvents, data.Stores)), Oracle.Hash(served)); // every store-day matches the checksum
        Assert.Empty((await clients[1].GetFromJsonAsync<List<ParkedEvent>>("/parked"))!); // every orphan return found its sale

        var rebuilt = await (await clients[1].PostAsync("/admin/rebuild", null)).Content.ReadFromJsonAsync<RebuildOutcome>();
        Assert.True(rebuilt!.Identical); // and the read models still equal what the log says, after all of that
    }

    private sealed record RebuildOutcome(bool Identical);
}
