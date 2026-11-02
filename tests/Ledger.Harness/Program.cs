using Ledger.Harness;

// Usage: dotnet run --project tests/Ledger.Harness -- [seed]
var seed = args.Length > 0 ? ulong.Parse(args[0]) : 1UL;
var data = Generator.Generate(new GeneratorOptions { Seed = seed });
var totals = Oracle.NetTotals(data.UniqueEvents, data.Stores);

Console.WriteLine($"seed={seed}");
Console.WriteLine($"deliveries={data.Deliveries.Count} unique={data.UniqueEvents.Count} duplicates={data.Deliveries.Count(d => d.IsDuplicate)}");
Console.WriteLine($"orphan_returns={data.OrphanReturnEventIds.Count} outages={data.Outages.Count}");
Console.WriteLine($"store_days={totals.Count} oracle_sha256={Oracle.Hash(totals)}");
