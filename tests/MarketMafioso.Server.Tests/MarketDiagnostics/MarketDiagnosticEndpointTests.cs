using System.Net;
using System.Net.Http.Json;
using MarketMafioso.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace MarketMafioso.Server.Tests.MarketDiagnostics;

public sealed class MarketDiagnosticEndpointTests
{
    [Fact]
    public async Task SaleEvidencePost_RequiresPluginKeyAndDeduplicates()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var evidence = new RetainerSaleEvidenceCreateRequest
        {
            EvidenceId = "endpoint-sale",
            ItemId = 4745,
            ItemName = "Orange Juice",
            TotalGil = 95,
            EventAtUtc = DateTimeOffset.Parse("2026-07-24T12:05:00Z"),
            HomeWorld = "Cactuar",
            RawMessage = "Orange Juice has sold for 95 gil (after fees).",
        };

        using var unauthorized = await client.PostAsJsonAsync("/api/market-diagnostics/sales", evidence);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        client.DefaultRequestHeaders.Add("X-Api-Key", "market-diagnostic-test-key");
        using var created = await client.PostAsJsonAsync("/api/market-diagnostics/sales", evidence);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var first = await created.Content.ReadFromJsonAsync<RetainerSaleEvidenceCreateResponse>();
        Assert.NotNull(first);
        Assert.False(first.Duplicate);

        using var duplicate = await client.PostAsJsonAsync("/api/market-diagnostics/sales", evidence);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        var second = await duplicate.Content.ReadFromJsonAsync<RetainerSaleEvidenceCreateResponse>();
        Assert.NotNull(second);
        Assert.True(second.Duplicate);
        Assert.Equal(first.Id, second.Id);
    }

    private static WebApplicationFactory<Program> CreateApplication()
    {
        var contentRoot = Path.Combine(
            Path.GetTempPath(),
            "MarketMafioso.Server.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        var databasePath = Path.Combine(contentRoot, "marketmafioso.db");
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(contentRoot);
                builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["MarketMafioso:DatabasePath"] = databasePath,
                        ["MarketMafioso:RequireApiKey"] = "true",
                        ["MarketMafioso:ClientApiKey"] = "market-diagnostic-test-key",
                        ["MarketMafioso:RequireDashboardAuth"] = "true",
                        ["MarketMafioso:DashboardBootstrapUsername"] = "admin",
                        ["MarketMafioso:DashboardBootstrapPassword"] = "secret-password",
                    }));
            });
    }
}
