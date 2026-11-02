using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ledger.Integration.Tests;

public class HealthTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;

    public HealthTests(PostgresFixture pg) => _pg = pg;

    private static WebApplicationFactory<Program> Replica(string connectionString) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Ledger", connectionString));

    private sealed record Health(string Status, string Db);

    [Fact]
    public async Task Health_reports_ok_with_the_database_up()
    {
        await using var replica = Replica(_pg.ConnectionString);

        var response = await replica.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Health>(new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal(new Health("ok", "up"), body);
    }

    [Fact]
    public async Task Two_replicas_start_against_one_database_and_both_serve_health()
    {
        await using var a = Replica(_pg.ConnectionString);
        await using var b = Replica(_pg.ConnectionString);

        var responses = await Task.WhenAll(
            Task.Run(() => a.CreateClient().GetAsync("/health")),
            Task.Run(() => b.CreateClient().GetAsync("/health")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }

    [Fact]
    public void A_replica_that_cannot_reach_the_database_refuses_to_start()
    {
        const string unreachable = "Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x;Timeout=2";
        using var replica = Replica(unreachable);

        Assert.ThrowsAny<Exception>(() => replica.CreateClient());
    }
}
