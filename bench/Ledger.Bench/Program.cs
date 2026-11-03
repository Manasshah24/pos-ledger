// Rebuild benchmark: how long does rebuilding the read models take from the whole log versus from a snapshot?
//
//   set LEDGER_TEST_PG to a superuser connection string (the same one the integration tests use), then
//   dotnet run -c Release --project bench/Ledger.Bench -- [events=1000000] [tail=20000]
//
// It creates a throwaway database, bulk-loads <events> sales and returns straight into the log with SQL (ingesting millions of
// events through the API would take hours and is not what is being measured), builds the read models, takes a snapshot, ingests
// <tail> more events through the real ingest path, and then times both kinds of rebuild. Both must give identical hashes.

using System.Diagnostics;
using Ledger.Core;
using Ledger.Core.Events;
using Ledger.Data;
using Npgsql;

var rows = args.Length > 0 ? long.Parse(args[0]) : 1_000_000L;
var tailRows = args.Length > 1 ? int.Parse(args[1]) : 20_000;
var admin = Environment.GetEnvironmentVariable("LEDGER_TEST_PG")
    ?? throw new InvalidOperationException("Set LEDGER_TEST_PG, e.g. Host=localhost;Port=54329;Username=postgres;Password=postgres;Database=postgres");

var dbName = $"ledger_bench_{DateTime.UtcNow:yyyyMMddHHmmss}";
await using (var adminDb = NpgsqlDataSource.Create(admin))
await using (var create = adminDb.CreateCommand($"CREATE DATABASE {dbName}"))
    await create.ExecuteNonQueryAsync();

var cs = new NpgsqlConnectionStringBuilder(admin) { Database = dbName, CommandTimeout = 3600, Timeout = 60 }.ConnectionString;
await using var db = NpgsqlDataSource.Create(cs);
await Migrator.MigrateAsync(db);

var clock = new SystemClock();
var registry = new StoreRegistry(db);
for (var i = 1; i <= 300; i++) await registry.UpsertAsync($"S{i:D3}", "UTC", "USD");

static string Mb() => $"{Process.GetCurrentProcess().PeakWorkingSet64 / 1024 / 1024} MB";
var results = new List<(string Step, double Seconds, string Note)>();
async Task<T> Time<T>(string step, Func<Task<T>> work, Func<T, double, string>? note = null)
{
    var sw = Stopwatch.StartNew();
    var value = await work();
    sw.Stop();
    var line = note?.Invoke(value, sw.Elapsed.TotalSeconds) ?? "";
    results.Add((step, sw.Elapsed.TotalSeconds, line));
    Console.WriteLine($"{step,-52} {sw.Elapsed.TotalSeconds,9:N1} s   {line}");
    return value;
}

Console.WriteLine($"database {dbName}: {rows:N0} events + {tailRows:N0} tail events");

await Time("load events into the log (bulk SQL)", async () =>
{
    await using var cmd = db.CreateCommand("""
        INSERT INTO events (event_id, store_id, terminal_id, type, occurred_at, received_at, sale_id, amount_minor, business_date)
        SELECT 'evt-' || g,
               'S' || lpad((((CASE WHEN g % 20 = 0 THEN g - 1 ELSE g END) % 300) + 1)::text, 3, '0'),
               'T1',
               CASE WHEN g % 20 = 0 THEN 'return' ELSE 'sale' END,
               ts AT TIME ZONE 'UTC', now(),
               'sale-' || (CASE WHEN g % 20 = 0 THEN g - 1 ELSE g END),
               CASE WHEN g % 20 = 0 THEN 50 ELSE 100 + g % 5000 END,
               (ts - interval '2 hours')::date
        FROM (SELECT g, timestamp '2026-01-05' + ((g % 259200) * interval '1 second') AS ts FROM generate_series(1, @n) g) x
        """);
    cmd.Parameters.AddWithValue("n", rows);
    return await cmd.ExecuteNonQueryAsync();
}, (n, _) => $"{n:N0} rows");

var rebuilder = new Rebuilder(db, clock);
var full = await Time("rebuild read models from the whole log", () => rebuilder.RebuildAsync(),
    (r, _) => $"{r.Events:N0} events replayed, {r.StoreDays:N0} store-days, peak process memory {Mb()}");
var snapshot = await Time("take a snapshot", () => rebuilder.CreateSnapshotAsync(), (s, _) => $"at log_seq {s.LogSeq:N0}");

var ingestor = new EventIngestor(db, clock, new LedgerOptions());
await Time($"ingest {tailRows:N0} more events through the real ingest path", async () =>
{
    for (var start = 0; start < tailRows; start += 250)
    {
        var batch = Enumerable.Range(start, Math.Min(250, tailRows - start)).Select(i =>
        {
            var ev = new IncomingEvent($"tail-{i}", $"S{i % 300 + 1:D3}", "T1", EventType.Sale, "2026-01-09T12:00:00Z", $"tail-sale-{i}", 100 + i % 900);
            return new RawEvent(i - start, System.Text.Json.JsonSerializer.Serialize(ev), ev);
        }).ToList();
        await ingestor.IngestAsync(batch);
    }
    return tailRows;
}, (n, seconds) => $"{n / seconds:N0} events/s");

var live = await rebuilder.HashAsync();
var fromSnapshot = await Time("rebuild from the snapshot + tail", () => rebuilder.RebuildAsync(useSnapshot: true),
    (r, _) => $"{r.Events:N0} events replayed (snapshot {r.SnapshotId}), rejected={r.SnapshotRejected}");
var fullAgain = await Time("rebuild from the whole log again", () => rebuilder.RebuildAsync(),
    (r, _) => $"{r.Events:N0} events replayed");

var identical = live == fromSnapshot.After && live == fullAgain.After;
Console.WriteLine();
Console.WriteLine($"hashes identical across live, snapshot rebuild and full rebuild: {identical}");
Console.WriteLine($"speed-up: {results[^1].Seconds / results[^2].Seconds:N0}x faster from the snapshot");
Console.WriteLine($"peak process memory: {Mb()}");
Environment.ExitCode = identical ? 0 : 1;

await using (var adminDb = NpgsqlDataSource.Create(admin))
{
    NpgsqlConnection.ClearAllPools();
    await using var drop = adminDb.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
    await drop.ExecuteNonQueryAsync();
}
