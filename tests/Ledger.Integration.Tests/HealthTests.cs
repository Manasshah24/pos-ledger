using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ledger.Integration.Tests;

public class HealthTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;

    public HealthTests(PostgresFixture pg) => _pg = pg;

    private static WebApplicationFactory<Program> Replica(string connectionString) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Ledger", connectionString));

    [Fact]
    public async Task The_ops_dashboard_is_served_at_the_root()
    {
        await using var app = Replica(_pg.ConnectionString);

        var response = await app.CreateClient().GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("POS Ledger", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Two_replicas_start_against_one_database_and_both_report_healthy()
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
        using var replica = Replica("Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x;Timeout=2");

        Assert.ThrowsAny<Exception>(() => replica.CreateClient());
    }
}
