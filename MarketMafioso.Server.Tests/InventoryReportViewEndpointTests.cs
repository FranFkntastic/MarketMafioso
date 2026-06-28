using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace MarketMafioso.Server.Tests;

public sealed class InventoryReportViewEndpointTests
{
    [Fact]
    public async Task GetReportView_ReturnsParsedInventorySnapshot()
    {
        await using var application = CreateApplication();
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
        await using var application = CreateApplication();
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
    public async Task InventoryBrowser_RendersLatestSnapshotItemAggregates()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();

        var createResponse = await client.PostAsJsonAsync("/inventory", new InventoryReport
        {
            CharacterName = "Browser Character",
            HomeWorld = "Gilgamesh",
            Timestamp = "2026-06-24T01:00:00.0000000Z",
            PlayerInventory =
            [
                new InventoryBag
                {
                    BagName = "Inventory1",
                    Items =
                    [
                        new ItemSlot
                        {
                            ItemId = 5057,
                            ItemName = "Darksteel Nugget",
                            Quantity = 12,
                            IsHQ = true,
                            Condition = 100,
                        },
                    ],
                },
            ],
            Retainers =
            [
                new RetainerReport
                {
                    RetainerName = "Scrongle",
                    RetainerId = 42,
                    LastUpdated = "2026-06-24T00:55:00.0000000Z",
                    Bags =
                    [
                        new InventoryBag
                        {
                            BagName = "Retainer Page 1",
                            Items =
                            [
                                new ItemSlot
                                {
                                    ItemId = 5057,
                                    ItemName = "Darksteel Nugget",
                                    Quantity = 99,
                                    IsHQ = false,
                                    Condition = 0,
                                },
                            ],
                        },
                    ],
                },
            ],
        });
        createResponse.EnsureSuccessStatusCode();

        var id = await ReadCreatedIdAsync(createResponse);
        var stored = await client.GetFromJsonAsync<StoredInventoryReport>($"/api/reports/{id}");
        var shell = await client.GetStringAsync("/inventory?search=darksteel");
        var view = InventoryBrowserViewBuilder.Build(stored, "darksteel");

        Assert.Contains("_framework/blazor", shell, StringComparison.Ordinal);
        Assert.NotNull(stored);
        Assert.Equal("Browser Character", view.CharacterName);
        Assert.Equal("Gilgamesh", view.HomeWorld);
        var item = Assert.Single(view.Items);
        Assert.Equal("Darksteel Nugget", item.DisplayName);
        Assert.Equal(111, item.TotalQuantity);
        Assert.Equal(12, item.HqQuantity);
        Assert.Equal(2, item.OwnerCount);
        Assert.Contains(item.Locations, x => x.OwnerName == "Player Inventory" && x.BagName == "Inventory1" && x.Quantity == 12);
        Assert.Contains(item.Locations, x => x.OwnerName == "Scrongle" && x.BagName == "Retainer Page 1" && x.Quantity == 99);
    }

    [Fact]
    public async Task InventoryBrowser_RendersScopesListingsTypesAndResizableSeparators()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();

        var createResponse = await client.PostAsJsonAsync("/inventory", new InventoryReport
        {
            CharacterName = "Semantic Browser",
            HomeWorld = "Siren",
            Timestamp = "2026-06-24T12:00:00.0000000Z",
            PlayerInventory =
            [
                new InventoryBag
                {
                    BagName = "Inventory1",
                    Items =
                    [
                        new ItemSlot
                        {
                            ItemId = 5057,
                            ItemName = "Darksteel Nugget",
                            ItemType = "Metal",
                            Quantity = 12,
                            IsHQ = true,
                            Condition = 100,
                        },
                    ],
                },
            ],
            Retainers =
            [
                new RetainerReport
                {
                    RetainerName = "Scrongle",
                    RetainerId = 42,
                    LastUpdated = "2026-06-24T11:53:00.0000000Z",
                    Gil = 1_242_888,
                    Bags =
                    [
                        new InventoryBag
                        {
                            BagName = "RetainerInventory",
                            Items =
                            [
                                new ItemSlot
                                {
                                    ItemId = 5057,
                                    ItemName = "Darksteel Nugget",
                                    ItemType = "Metal",
                                    Quantity = 99,
                                    IsHQ = false,
                                    Condition = 100,
                                },
                            ],
                        },
                    ],
                    MarketListings =
                    [
                        new RetainerMarketListing
                        {
                            ItemId = 5057,
                            ItemName = "Darksteel Nugget",
                            ItemType = "Metal",
                            Quantity = 20,
                            IsHQ = false,
                            Condition = 100,
                            UnitPrice = 1_800,
                            ListedAt = "2026-06-24T11:53:00.0000000Z",
                        },
                        new RetainerMarketListing
                        {
                            ItemId = 5057,
                            ItemName = "Darksteel Nugget",
                            ItemType = "Metal",
                            Quantity = 79,
                            IsHQ = false,
                            Condition = 100,
                            UnitPrice = 2_150,
                            ListedAt = "2026-06-24T11:53:00.0000000Z",
                        },
                    ],
                },
            ],
        });
        createResponse.EnsureSuccessStatusCode();

        var id = await ReadCreatedIdAsync(createResponse);
        var stored = await client.GetFromJsonAsync<StoredInventoryReport>($"/api/reports/{id}");
        var shell = await client.GetStringAsync("/inventory?search=darksteel");
        var view = InventoryBrowserViewBuilder.Build(stored, "darksteel");

        Assert.Contains("_framework/blazor", shell, StringComparison.Ordinal);
        Assert.NotNull(stored);
        Assert.Equal((ulong)1_242_888, view.RetainerGil);
        var scope = Assert.Single(view.Scopes, x => x.DisplayName == "Scrongle");
        Assert.Equal(1, scope.StackCount);
        Assert.Equal((ulong)1_242_888, scope.Gil);
        Assert.Equal(2, scope.MarketListingCount);
        var item = Assert.Single(view.Items);
        Assert.Equal("Metal", item.ItemType);
        Assert.Equal(111, item.TotalQuantity);
        Assert.Equal(12, item.HqQuantity);
        Assert.Equal(2, view.MarketListings.Count);
        Assert.Contains(view.MarketListings, x => x.OwnerName == "Scrongle" && x.UnitPrice == 1_800 && x.Quantity == 20);
        Assert.Contains(view.MarketListings, x => x.OwnerName == "Scrongle" && x.UnitPrice == 2_150 && x.Quantity == 79);
    }

    [Fact]
    public async Task GetReportView_ReturnsNotFoundForMissingSnapshot()
    {
        await using var application = CreateApplication();
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
        request.Headers.Add("X-Api-Key", "test-client-secret");

        var authenticated = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, authenticated.StatusCode);
    }

    [Fact]
    public async Task HostedMode_AcceptsPreviousClientKeyForInventoryPost()
    {
        await using var application = CreateHostedApplication(
            new KeyValuePair<string, string?>("MarketMafioso:PreviousClientApiKey", "previous-client-secret"));
        using var client = application.CreateClient();

        var response = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/inventory",
            "previous-client-secret",
            CreateReport("Previous Client Character"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task HostedMode_RejectsWrongClientKeyForInventoryPost()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();

        var response = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/inventory",
            "wrong-secret",
            CreateReport("Wrong Key Character"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("invalid_api_key", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostedMode_ReadApiAcceptsClientKey()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();

        var createResponse = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/inventory",
            "test-client-secret",
            CreateReport("Fails Closed Character"));
        createResponse.EnsureSuccessStatusCode();

        var unauthenticated = await client.GetAsync("/api/reports");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        var clientKeyResponse = await SendWithKeyAsync(client, HttpMethod.Get, "/api/reports", "test-client-secret");
        Assert.Equal(HttpStatusCode.OK, clientKeyResponse.StatusCode);
    }

    [Fact]
    public async Task HostedMode_ReadApiRejectsLegacyReadKeyWhenClientKeyIsConfigured()
    {
        await using var application = CreateHostedApplication(
            new KeyValuePair<string, string?>("MarketMafioso:ReadApiKey", "read-secret"));
        using var client = application.CreateClient();

        var createResponse = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/inventory",
            "test-client-secret",
            CreateReport("Reports Character"));
        createResponse.EnsureSuccessStatusCode();

        var unauthenticated = await client.GetAsync("/api/reports");

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        var clientKeyResponse = await SendWithKeyAsync(client, HttpMethod.Get, "/api/reports", "test-client-secret");
        Assert.Equal(HttpStatusCode.OK, clientKeyResponse.StatusCode);

        var readKeyResponse = await SendWithKeyAsync(client, HttpMethod.Get, "/api/reports", "read-secret");

        Assert.Equal(HttpStatusCode.Unauthorized, readKeyResponse.StatusCode);
    }

    [Fact]
    public async Task HostedMode_ReadApiAcceptsPreviousClientKey()
    {
        await using var application = CreateHostedApplication(
            new KeyValuePair<string, string?>("MarketMafioso:PreviousClientApiKey", "previous-client-secret"));
        using var client = application.CreateClient();

        var createResponse = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/inventory",
            "test-client-secret",
            CreateReport("Previous Read Character"));
        createResponse.EnsureSuccessStatusCode();

        var response = await SendWithKeyAsync(client, HttpMethod.Get, "/api/reports", "previous-client-secret");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HostedMode_DisablesApiDeleteRoutesForApiKeys()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();

        var createResponse = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/inventory",
            "test-client-secret",
            CreateReport("Delete Character"));
        createResponse.EnsureSuccessStatusCode();

        var deleteAllWithClientKey = await SendWithKeyAsync(client, HttpMethod.Delete, "/api/reports", "test-client-secret");
        var deleteAllWithWrongKey = await SendWithKeyAsync(client, HttpMethod.Delete, "/api/reports", "wrong-secret");

        Assert.Equal(HttpStatusCode.NotFound, deleteAllWithClientKey.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deleteAllWithWrongKey.StatusCode);
    }

    [Fact]
    public async Task HostedMode_AcceptsConfiguredBasePath()
    {
        await using var application = CreateHostedApplication(
            new KeyValuePair<string, string?>("MarketMafioso:BasePath", "/marketmafioso"));
        using var client = application.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/marketmafioso/api/inventory")
        {
            Content = JsonContent.Create(CreateReport("Base Path Character")),
        };
        request.Headers.Add("X-Api-Key", "test-client-secret");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.StartsWith("/marketmafioso/api/reports/", response.Headers.Location?.ToString(), StringComparison.Ordinal);

        var dashboard = await client.GetStringAsync("/marketmafioso/");

        Assert.Contains("<base href=\"/marketmafioso/\" />", dashboard, StringComparison.Ordinal);
        Assert.Contains("_framework/blazor", dashboard, StringComparison.Ordinal);
        Assert.Contains("_framework/blazor.webassembly.", dashboard, StringComparison.Ordinal);
        Assert.DoesNotContain("[.{fingerprint}]", dashboard, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/marketmafioso/api/reports", dashboard, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostedMode_ServesDashboardStaticAssets()
    {
        await using var application = CreateHostedApplication(
            new KeyValuePair<string, string?>("MarketMafioso:BasePath", "/marketmafioso"));
        using var client = application.CreateClient();

        var dashboard = await client.GetStringAsync("/marketmafioso/");
        var bootScript = Regex.Match(
            dashboard,
            "_framework/blazor\\.webassembly\\.[^\"]+\\.js").Value;

        Assert.False(string.IsNullOrWhiteSpace(bootScript));

        var appCss = await client.GetAsync("/marketmafioso/css/app.css");
        var dotnetJs = await client.GetAsync("/marketmafioso/_framework/dotnet.js");
        var bootJs = await client.GetAsync($"/marketmafioso/{bootScript}");

        Assert.Equal(HttpStatusCode.OK, appCss.StatusCode);
        Assert.Equal(HttpStatusCode.OK, dotnetJs.StatusCode);
        Assert.Equal(HttpStatusCode.OK, bootJs.StatusCode);
    }

    [Fact]
    public async Task HostedMode_CreateResponse_ReturnsDashboardUrlsFromPublicOriginAndBasePath()
    {
        await using var application = CreateHostedApplication(
            new KeyValuePair<string, string?>("MarketMafioso:BasePath", "/marketmafioso"),
            new KeyValuePair<string, string?>("MarketMafioso:PublicOrigin", "https://dev.xivcraftarchitect.com"));
        using var client = application.CreateClient();

        var createResponse = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/marketmafioso/api/inventory",
            "test-client-secret",
            CreateReport("Response Link Character"));
        createResponse.EnsureSuccessStatusCode();

        var createBody = await createResponse.Content.ReadAsStringAsync();
        using var createJson = JsonDocument.Parse(createBody);
        var id = createJson.RootElement.GetProperty("id").GetString();

        Assert.Equal(
            "https://dev.xivcraftarchitect.com/marketmafioso/",
            createJson.RootElement.GetProperty("dashboardUrl").GetString());
        Assert.Equal(
            $"https://dev.xivcraftarchitect.com/marketmafioso/reports/{id}",
            createJson.RootElement.GetProperty("reportUrl").GetString());
        Assert.Equal(
            $"https://dev.xivcraftarchitect.com/marketmafioso/api/reports/{id}",
            createJson.RootElement.GetProperty("apiReportUrl").GetString());
    }

    [Fact]
    public async Task DashboardJsonRoutes_ReturnReportPayloads()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();

        var createResponse = await client.PostAsJsonAsync("/inventory", CreateReport("Json Route Character"));
        createResponse.EnsureSuccessStatusCode();

        var createBody = await createResponse.Content.ReadAsStringAsync();
        using var createJson = JsonDocument.Parse(createBody);
        var id = createJson.RootElement.GetProperty("id").GetString();

        var byId = await client.GetAsync($"/reports/{id}/json");
        var latest = await client.GetAsync("/reports/latest/json");

        Assert.Equal(HttpStatusCode.OK, byId.StatusCode);
        Assert.Equal(HttpStatusCode.OK, latest.StatusCode);

        var byIdBody = await byId.Content.ReadAsStringAsync();
        Assert.Contains("Json Route Character", byIdBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostedMode_DashboardHidesFilesystemPath()
    {
        string? contentRoot = null;
        await using var application = CreateHostedApplication(
            values =>
            {
                contentRoot = values.ContentRoot;
                values.Configuration["MarketMafioso:BasePath"] = "/marketmafioso";
                values.Configuration["MarketMafioso:StorageLabel"] = "dev receiver storage";
            });
        using var client = application.CreateClient();

        var dashboard = await client.GetStringAsync("/marketmafioso/");

        Assert.Contains("<base href=\"/marketmafioso/\" />", dashboard, StringComparison.Ordinal);
        Assert.DoesNotContain("[.{fingerprint}]", dashboard, StringComparison.Ordinal);
        Assert.DoesNotContain(contentRoot!, dashboard, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DashboardListing_SkipsUnreadableSnapshotFiles()
    {
        string? contentRoot = null;
        await using var application = CreateHostedApplication(
            values =>
            {
                contentRoot = values.ContentRoot;
                values.Configuration["MarketMafioso:BasePath"] = "/marketmafioso";
            });
        using var client = application.CreateClient();

        var reportDirectory = Path.Combine(contentRoot!, "data", "reports");
        Directory.CreateDirectory(reportDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(reportDirectory, "20260623121600439-10aa438b.json"),
            string.Empty);

        var createResponse = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/marketmafioso/api/inventory",
            "test-client-secret",
            CreateReport("Corrupt File Character"));
        createResponse.EnsureSuccessStatusCode();

        var dashboard = await client.GetAsync("/marketmafioso/");
        var latestJson = await client.GetAsync("/marketmafioso/reports/latest/json");

        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        Assert.Equal(HttpStatusCode.OK, latestJson.StatusCode);
        Assert.Contains("_framework/blazor", await dashboard.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains(
            "Corrupt File Character",
            await latestJson.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostedMode_DashboardDeleteDoesNotRequireCsrf()
    {
        await using var application = CreateHostedApplication(
            new KeyValuePair<string, string?>("MarketMafioso:PublicOrigin", "https://dev.xivcraftarchitect.com"));
        using var client = application.CreateClient();

        var createResponse = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/inventory",
            "test-client-secret",
            CreateReport("Delete Without Csrf Character"));
        createResponse.EnsureSuccessStatusCode();

        var id = await ReadCreatedIdAsync(createResponse);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/reports/{id}/delete");
        request.Headers.Add("Origin", "https://dev.xivcraftarchitect.com");
        request.Content = new FormUrlEncodedContent([]);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HostedMode_ReportDetailsDoesNotRenderCsrf()
    {
        await using var application = CreateHostedApplication(
            new KeyValuePair<string, string?>("MarketMafioso:PublicOrigin", "https://dev.xivcraftarchitect.com"));
        using var client = application.CreateClient();

        var createResponse = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/inventory",
            "test-client-secret",
            CreateReport("No Csrf Render Character"));
        createResponse.EnsureSuccessStatusCode();

        var id = await ReadCreatedIdAsync(createResponse);
        var detail = await client.GetStringAsync($"/reports/{id}");
        Assert.DoesNotContain("name=\"csrf\"", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("mmf_csrf", detail, StringComparison.Ordinal);
    }

    private sealed class HostedApplicationValues
    {
        public required string ContentRoot { get; init; }
        public required Dictionary<string, string?> Configuration { get; init; }
    }

    private static WebApplicationFactory<Program> CreateHostedApplication(params KeyValuePair<string, string?>[] extraConfiguration) =>
        CreateHostedApplication(values =>
        {
            foreach (var item in extraConfiguration)
                values.Configuration[item.Key] = item.Value;
        });

    private static WebApplicationFactory<Program> CreateApplication()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "MarketMafioso.Server.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(contentRoot);
                builder.ConfigureAppConfiguration(config =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["MarketMafioso:DatabasePath"] = Path.Combine(contentRoot, "marketmafioso.db"),
                    });
                });
            });
    }

    private static WebApplicationFactory<Program> CreateHostedApplication(Action<HostedApplicationValues> configure)
    {
        var values = new Dictionary<string, string?>
        {
            ["MarketMafioso:RequireApiKey"] = "true",
            ["MarketMafioso:ClientApiKey"] = "test-client-secret",
        };

        var contentRoot = Path.Combine(Path.GetTempPath(), "MarketMafioso.Server.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        configure(new HostedApplicationValues
        {
            ContentRoot = contentRoot,
            Configuration = values,
        });

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

    private static Task<HttpResponseMessage> SendWithKeyAsync(
        HttpClient client,
        HttpMethod method,
        string requestUri,
        string apiKey,
        InventoryReport? report = null)
    {
        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Add("X-Api-Key", apiKey);
        if (report != null)
            request.Content = JsonContent.Create(report);

        return client.SendAsync(request);
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

    private static async Task<string> ReadCreatedIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("id").GetString()!;
    }
}

