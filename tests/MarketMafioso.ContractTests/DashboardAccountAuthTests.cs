using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MarketMafioso.Contracts.Inventory;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace MarketMafioso.Server.ContractTests;

public sealed class DashboardAccountAuthTests
{
    [Fact]
    public async Task DashboardSession_LoginCreatesCookieAndSession()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();

        var anonymous = await client.GetAsync("/auth/session");
        var login = await client.PostAsJsonAsync("/auth/login", new
        {
            username = "admin",
            password = "secret-password",
        });
        var session = await client.GetAsync("/auth/session");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True(login.Headers.TryGetValues("Set-Cookie", out var cookies));
        Assert.Contains(cookies, cookie => cookie.Contains("mmf_dashboard_session=", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
    }

    [Fact]
    public async Task DashboardSession_WhenAuthenticationIsDisabled_ProvidesLocalSession()
    {
        await using var application = CreateApplication(
            new KeyValuePair<string, string?>("MarketMafioso:RequireDashboardAuth", "false"));
        using var client = application.CreateClient();

        var session = await client.GetAsync("/auth/session");
        var inventory = await client.GetAsync("/api/inventory/characters");

        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.Equal(HttpStatusCode.OK, inventory.StatusCode);
    }

    [Fact]
    public async Task InventoryCharacters_UsesConfiguredAccountNumberAndCompleteDisplayName()
    {
        await using var application = CreateApplication(
            new KeyValuePair<string, string?>("MarketMafioso:RequireDashboardAuth", "false"));
        using var client = application.CreateClient();
        var report = new InventoryReport
        {
            Metadata = new InventoryReportMetadata { SchemaVersion = 5, SourcePlugin = "MarketMafioso" },
            CharacterName = "Eriana Ning",
            HomeWorld = "Siren",
            ServiceAccountNumber = 2,
            PlayerStorage = new StorageSourceEvidence
            {
                RequestedSources = ["Inventory1"],
                ObservedSources = ["Inventory1"],
            },
        };

        var ingest = await client.PostAsJsonAsync("/inventory", report);
        ingest.EnsureSuccessStatusCode();
        var characters = await client.GetFromJsonAsync<DashboardCharacterOption[]>("/api/inventory/characters");

        var character = Assert.Single(characters!);
        Assert.Equal("Eriana Ning @ Siren", character.DisplayName);
        Assert.Equal(2, character.ServiceAccountNumber);
        Assert.Equal("Service Account 2", character.ServiceAccountGroup);
    }

    [Fact]
    public async Task InventoryBrowser_AllKnownAggregatesNewestPerCharacterWhileExplicitLinksRemainExact()
    {
        await using var application = CreateApplication(
            new KeyValuePair<string, string?>("MarketMafioso:RequireDashboardAuth", "false"));
        using var client = application.CreateClient();

        var olderResponse = await client.PostAsJsonAsync("/inventory", BrowserReport("Eriana Ning", "Siren", 999, 1));
        olderResponse.EnsureSuccessStatusCode();
        var olderId = (await JsonDocument.ParseAsync(await olderResponse.Content.ReadAsStreamAsync())).RootElement.GetProperty("id").GetString()!;
        (await client.PostAsJsonAsync("/inventory", BrowserReport("Eriana Ning", "Siren", 42, 2))).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/inventory", BrowserReport("Wei Ning", "Siren", 42, 3))).EnsureSuccessStatusCode();

        var characters = await client.GetFromJsonAsync<DashboardCharacterOption[]>("/api/inventory/characters");
        var erianaId = Assert.Single(characters!, character => character.CharacterName == "Eriana Ning").Id;
        var allKnown = await client.GetFromJsonAsync<InventoryBrowserView>("/api/inventory/browser?filter=Item%2042");
        var eriana = await client.GetFromJsonAsync<InventoryBrowserView>($"/api/inventory/browser?characterId={erianaId}&filter=Item%2042");
        var historical = await client.GetFromJsonAsync<InventoryBrowserView>($"/api/inventory/browser?snapshotId={Uri.EscapeDataString(olderId)}");

        Assert.NotNull(allKnown);
        Assert.Null(allKnown.SnapshotId);
        Assert.Null(allKnown.CharacterName);
        Assert.False(string.IsNullOrWhiteSpace(allKnown.RevisionToken));
        Assert.Equal(5, Assert.Single(allKnown.Items).TotalQuantity);
        Assert.Equal(2, allKnown.Scopes.Count);
        Assert.NotNull(eriana);
        Assert.Equal("Eriana Ning", eriana.CharacterName);
        Assert.Equal(2, Assert.Single(eriana.Items).TotalQuantity);
        Assert.NotNull(historical);
        Assert.Equal(olderId, historical.SnapshotId);
        Assert.Equal((uint)999, Assert.Single(historical.Items).ItemId);
    }

    [Fact]
    public async Task InventoryEventStream_EmitsTheAuthorizedAccountRevision()
    {
        await using var application = CreateApplication(
            new KeyValuePair<string, string?>("MarketMafioso:RequireDashboardAuth", "false"));
        using var client = application.CreateClient();
        (await client.PostAsJsonAsync("/inventory", BrowserReport("Eriana Ning", "Siren", 42, 2))).EnsureSuccessStatusCode();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/inventory/events/stream"),
            HttpCompletionOption.ResponseHeadersRead,
            cancellation.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation.Token);
        using var reader = new StreamReader(stream);

        Assert.Equal("event: inventory", await reader.ReadLineAsync(cancellation.Token));
        var data = await reader.ReadLineAsync(cancellation.Token);
        Assert.NotNull(data);
        Assert.StartsWith("data: ", data);
        var revision = JsonSerializer.Deserialize<InventoryRevisionView>(data![6..], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.False(string.IsNullOrWhiteSpace(revision?.Token));
    }

    [Theory]
    [InlineData("/inventory")]
    [InlineData("/api/inventory")]
    [InlineData("/inventory/delta")]
    [InlineData("/api/inventory/delta")]
    public async Task InventoryIngest_WhenApiKeysAreOptionalDoesNotRequireDashboardSession(string path)
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();

        var response = await client.PostAsJsonAsync(path, new
        {
            characterName = "Wei Ning",
            homeWorld = "Maduin",
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
            playerInventory = Array.Empty<object>(),
            retainers = Array.Empty<object>(),
        });

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DashboardSession_RejectsInvalidCredentials()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/login", new
        {
            username = "admin",
            password = "wrong-password",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReportReaderRequiresCookieSession()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();

        var anonymous = await client.GetAsync("/api/reports");
        var login = await client.PostAsJsonAsync("/auth/login", new
        {
            username = "admin",
            password = "secret-password",
        });
        var authenticated = await client.GetAsync("/api/reports");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, authenticated.StatusCode);
    }

    [Fact]
    public async Task DashboardSession_StopsWorkingWhenUserIsDisabled()
    {
        var values = CreateApplicationValues();
        await using var application = values.Application;
        using var client = application.CreateClient();

        var login = await client.PostAsJsonAsync("/auth/login", new
        {
            username = "admin",
            password = "secret-password",
        });
        login.EnsureSuccessStatusCode();

        await using (var connection = new SqliteConnection($"Data Source={values.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE dashboard_users
                SET disabled_at_utc = $disabledAt
                WHERE username = 'admin'
                """;
            command.Parameters.AddWithValue("$disabledAt", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        var session = await client.GetAsync("/auth/session");

        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateApplication(params KeyValuePair<string, string?>[] extraConfiguration) =>
        CreateApplicationValues(extraConfiguration).Application;

    private static ApplicationValues CreateApplicationValues(params KeyValuePair<string, string?>[] extraConfiguration)
    {
        var host = ServerTestHost.CreateConfiguration();
        host.Configuration["MarketMafioso:RequireDashboardAuth"] = "true";
        host.Configuration["MarketMafioso:DashboardBootstrapUsername"] = "admin";
        host.Configuration["MarketMafioso:DashboardBootstrapPassword"] = "secret-password";
        foreach (var item in extraConfiguration)
            host.Configuration[item.Key] = item.Value;

        return new ApplicationValues(ServerTestHost.Create(host), host.DatabasePath);
    }

    private static InventoryReport BrowserReport(
        string characterName,
        string homeWorld,
        uint itemId,
        uint quantity) => new()
    {
        Metadata = new InventoryReportMetadata { SchemaVersion = 5, SourcePlugin = "MarketMafioso" },
        CharacterName = characterName,
        HomeWorld = homeWorld,
        Timestamp = DateTimeOffset.UtcNow.ToString("O"),
        PlayerStorage = new StorageSourceEvidence
        {
            RequestedSources = ["Inventory1"],
            ObservedSources = ["Inventory1"],
        },
        PlayerInventory =
        [
            new InventoryBag
            {
                BagName = "Inventory1",
                Items =
                [
                    new ItemSlot
                    {
                        ItemId = itemId,
                        ItemName = $"Item {itemId}",
                        Quantity = quantity,
                        SlotIndex = 0,
                        ConditionPercent = 100,
                    },
                ],
            },
        ],
    };

    private sealed record ApplicationValues(WebApplicationFactory<Program> Application, string DatabasePath);
}
