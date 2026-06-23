using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace MarketMafioso.Server.Tests;

public sealed class InventoryReportViewEndpointTests
{
    [Fact]
    public async Task GetReportView_ReturnsParsedInventorySnapshot()
    {
        await using var application = new WebApplicationFactory<Program>();
        using var client = application.CreateClient();

        var createResponse = await client.PostAsJsonAsync("/inventory", new InventoryReport
        {
            Metadata = new InventoryReportMetadata
            {
                SchemaVersion = 1,
                SourcePlugin = "MarketMafioso",
                PluginVersion = "1.0.0.0",
                GeneratedAtUtc = "2026-06-22T11:59:59.0000000Z",
            },
            CharacterName = "Endpoint Character",
            HomeWorld = "Gilgamesh",
            Timestamp = "2026-06-22T12:00:00.0000000Z",
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
                            Quantity = 120,
                            IsHQ = false,
                            Condition = 0,
                        },
                    ],
                },
            ],
            Retainers =
            [
                new RetainerReport
                {
                    RetainerName = "Endpoint Retainer",
                    RetainerId = 42,
                    LastUpdated = "2026-06-22T11:55:00.0000000Z",
                    Bags =
                    [
                        new InventoryBag
                        {
                            BagName = "RetainerPage1",
                            Items =
                            [
                                new ItemSlot
                                {
                                    ItemId = 4,
                                    ItemName = "Lightning Shard",
                                    Quantity = 99,
                                    IsHQ = true,
                                    Condition = 100,
                                },
                            ],
                        },
                    ],
                },
            ],
        });
        createResponse.EnsureSuccessStatusCode();

        var createBody = await createResponse.Content.ReadAsStringAsync();
        using var createJson = JsonDocument.Parse(createBody);
        var id = createJson.RootElement.GetProperty("id").GetString();

        var view = await client.GetFromJsonAsync<InventorySnapshotView>($"/api/reports/{id}/view");

        Assert.NotNull(view);
        Assert.Equal("MarketMafioso", view.Metadata.SourcePlugin);
        Assert.Equal("1.0.0.0", view.Metadata.PluginVersion);
        Assert.Equal("Endpoint Character", view.CharacterName);
        Assert.Equal("Gilgamesh", view.HomeWorld);
        Assert.Equal("Inventory1", view.PlayerInventory.Bags[0].Name);
        Assert.Equal("Endpoint Retainer", view.Retainers[0].Name);
        Assert.Equal(219, view.Totals.Quantity);
    }

    [Fact]
    public async Task GetReportDetails_RendersGroupedInventoryViewer()
    {
        await using var application = new WebApplicationFactory<Program>();
        using var client = application.CreateClient();

        var createResponse = await client.PostAsJsonAsync("/inventory", new InventoryReport
        {
            CharacterName = "Html Character",
            HomeWorld = "Leviathan",
            Timestamp = "2026-06-22T12:00:00.0000000Z",
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
                            Quantity = 120,
                            IsHQ = false,
                            Condition = 0,
                        },
                    ],
                },
            ],
            Retainers =
            [
                new RetainerReport
                {
                    RetainerName = "Html Retainer",
                    RetainerId = 84,
                    LastUpdated = "2026-06-22T11:55:00.0000000Z",
                    Bags =
                    [
                        new InventoryBag
                        {
                            BagName = "RetainerPage1",
                            Items =
                            [
                                new ItemSlot
                                {
                                    ItemId = 4,
                                    ItemName = "Lightning Shard",
                                    Quantity = 99,
                                    IsHQ = true,
                                    Condition = 100,
                                },
                            ],
                        },
                    ],
                },
            ],
        });
        createResponse.EnsureSuccessStatusCode();

        var createBody = await createResponse.Content.ReadAsStringAsync();
        using var createJson = JsonDocument.Parse(createBody);
        var id = createJson.RootElement.GetProperty("id").GetString();

        var html = await client.GetStringAsync($"/reports/{id}");

        Assert.Contains("inventory-table", html, StringComparison.Ordinal);
        Assert.Contains("Player Inventory", html, StringComparison.Ordinal);
        Assert.Contains("Html Retainer", html, StringComparison.Ordinal);
        Assert.Contains("Fire Shard", html, StringComparison.Ordinal);
        Assert.Contains("Lightning Shard", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetReportView_ReturnsNotFoundForMissingSnapshot()
    {
        await using var application = new WebApplicationFactory<Program>();
        using var client = application.CreateClient();

        var response = await client.GetAsync("/api/reports/missing/view");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task HostedMode_RequiresApiKeyForInventoryPost()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();

        var unauthenticated = await client.PostAsJsonAsync("/inventory", CreateReport("Hosted Character"));

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/inventory")
        {
            Content = JsonContent.Create(CreateReport("Hosted Character")),
        };
        request.Headers.Add("X-Api-Key", "test-secret");

        var authenticated = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, authenticated.StatusCode);
    }

    [Fact]
    public async Task HostedMode_RequiresApiKeyForReportsApi()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/inventory")
        {
            Content = JsonContent.Create(CreateReport("Reports Character")),
        };
        createRequest.Headers.Add("X-Api-Key", "test-secret");
        var createResponse = await client.SendAsync(createRequest);
        createResponse.EnsureSuccessStatusCode();

        var unauthenticated = await client.GetAsync("/api/reports");

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        using var listRequest = new HttpRequestMessage(HttpMethod.Get, "/api/reports");
        listRequest.Headers.Add("X-Api-Key", "test-secret");
        var authenticated = await client.SendAsync(listRequest);

        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
    }

    [Fact]
    public async Task HostedMode_AcceptsConfiguredBasePath()
    {
        await using var application = CreateHostedApplication(
            new KeyValuePair<string, string?>("MarketMafioso:BasePath", "/api/marketmafioso"));
        using var client = application.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/marketmafioso/inventory")
        {
            Content = JsonContent.Create(CreateReport("Base Path Character")),
        };
        request.Headers.Add("X-Api-Key", "test-secret");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var dashboard = await client.GetStringAsync("/api/marketmafioso/");

        Assert.Contains("href=\"/api/marketmafioso/reports/", dashboard, StringComparison.Ordinal);
    }

    private static WebApplicationFactory<Program> CreateHostedApplication(
        params KeyValuePair<string, string?>[] extraConfiguration)
    {
        var values = new Dictionary<string, string?>
        {
            ["MarketMafioso:RequireApiKey"] = "true",
            ["MarketMafioso:ApiKey"] = "test-secret",
        };

        foreach (var item in extraConfiguration)
            values[item.Key] = item.Value;

        var contentRoot = Path.Combine(Path.GetTempPath(), "MarketMafioso.Server.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(contentRoot);
                builder.ConfigureAppConfiguration(config =>
                {
                    config.AddInMemoryCollection(values);
                });
            });
    }

    private static InventoryReport CreateReport(string characterName) =>
        new()
        {
            CharacterName = characterName,
            HomeWorld = "Gilgamesh",
            Timestamp = "2026-06-22T12:00:00.0000000Z",
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
                            Quantity = 120,
                            IsHQ = false,
                            Condition = 0,
                        },
                    ],
                },
            ],
        };
}
