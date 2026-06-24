using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using MarketMafioso.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace MarketMafioso.Server.Tests;

public sealed class DashboardAccountAuthTests
{
    [Fact]
    public async Task DashboardRoutes_RequireBootstrapUserCredentials()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();

        var unauthenticated = await client.GetAsync("/");
        var health = await client.GetAsync("/health");
        using var authenticatedRequest = new HttpRequestMessage(HttpMethod.Get, "/");
        authenticatedRequest.Headers.Authorization = CreateBasicAuth("admin", "secret-password");
        var authenticated = await client.SendAsync(authenticatedRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal("Basic", unauthenticated.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
    }

    [Fact]
    public async Task DashboardRoutes_RejectInvalidBootstrapUserCredentials()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Authorization = CreateBasicAuth("admin", "wrong-password");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DashboardRoutes_RequireCredentialsForBasePathWithoutTrailingSlash()
    {
        await using var application = CreateApplication(
            new KeyValuePair<string, string?>("MarketMafioso:BasePath", "/api/marketmafioso"));
        using var client = application.CreateClient();

        var response = await client.GetAsync("/api/marketmafioso");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/inventory")]
    [InlineData("/diagnostics")]
    public async Task DashboardToolRoutes_RequireBootstrapUserCredentials(string path)
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();

        var unauthenticated = await client.GetAsync(path);
        using var authenticatedRequest = new HttpRequestMessage(HttpMethod.Get, path);
        authenticatedRequest.Headers.Authorization = CreateBasicAuth("admin", "secret-password");
        var authenticated = await client.SendAsync(authenticatedRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
    }

    [Fact]
    public async Task InventoryIngestRoute_UsesApiKeyInsteadOfDashboardBasicAuth()
    {
        await using var application = CreateApplication(
            new KeyValuePair<string, string?>("MarketMafioso:RequireApiKey", "true"),
            new KeyValuePair<string, string?>("MarketMafioso:IngestApiKey", "ingest-secret"));
        using var client = application.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/inventory")
        {
            Content = JsonContent.Create(new InventoryReport
            {
                CharacterName = "Ingest Character",
                HomeWorld = "Gilgamesh",
                Timestamp = "2026-06-24T10:45:00.0000000Z",
                PlayerInventory =
                [
                    new InventoryBag
                    {
                        BagName = "Inventory1",
                        Items =
                        [
                            new ItemSlot
                            {
                                ItemId = 2,
                                ItemName = "Fire Shard",
                                Quantity = 1,
                            },
                        ],
                    },
                ],
            }),
        };
        request.Headers.Add("X-Api-Key", "ingest-secret");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateApplication(params KeyValuePair<string, string?>[] extraConfiguration)
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "MarketMafioso.Server.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(contentRoot);
                builder.ConfigureAppConfiguration(config =>
                {
                    var values = new Dictionary<string, string?>
                    {
                        ["MarketMafioso:DatabasePath"] = Path.Combine(contentRoot, "marketmafioso.db"),
                        ["MarketMafioso:RequireDashboardAuth"] = "true",
                        ["MarketMafioso:DashboardBootstrapUsername"] = "admin",
                        ["MarketMafioso:DashboardBootstrapPassword"] = "secret-password",
                    };
                    foreach (var item in extraConfiguration)
                        values[item.Key] = item.Value;

                    config.AddInMemoryCollection(values);
                });
            });
    }

    private static AuthenticationHeaderValue CreateBasicAuth(string username, string password)
    {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        return new AuthenticationHeaderValue("Basic", credentials);
    }
}
