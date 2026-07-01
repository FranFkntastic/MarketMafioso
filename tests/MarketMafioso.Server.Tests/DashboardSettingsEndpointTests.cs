using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace MarketMafioso.Server.Tests;

public sealed class DashboardSettingsEndpointTests
{
    [Fact]
    public async Task CharactersApi_ReturnsCharactersForLoggedInDashboardAccount()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();
        await LoginAsync(client);
        var report = await client.PostAsJsonAsync("/inventory", CreateReport("Wei Ning", "Siren", 5064));
        report.EnsureSuccessStatusCode();

        var characters = await client.GetFromJsonAsync<IReadOnlyList<DashboardCharacterOption>>("/api/inventory/characters");

        Assert.NotNull(characters);
        var character = Assert.Single(characters);
        Assert.Equal("Wei Ning", character.CharacterName);
        Assert.Equal("Siren", character.HomeWorld);
        Assert.Equal("Wei Ning @ Siren", character.DisplayName);
    }

    [Fact]
    public async Task DashboardSettings_CanSaveAndLoadDefaultCharacter()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();
        await LoginAsync(client);
        var report = await client.PostAsJsonAsync("/inventory", CreateReport("Wei Ning", "Siren", 5064));
        report.EnsureSuccessStatusCode();
        var characters = await client.GetFromJsonAsync<IReadOnlyList<DashboardCharacterOption>>("/api/inventory/characters");
        var characterId = Assert.Single(characters!).Id;

        var save = await client.PutAsJsonAsync("/api/settings/dashboard", new DashboardSettingsUpdate
        {
            DefaultCharacterId = characterId,
            DefaultRegion = "North America",
            DefaultWorldMode = "Recommended",
            DefaultPickupExpiresSeconds = 900,
        });
        save.EnsureSuccessStatusCode();
        var loaded = await client.GetFromJsonAsync<DashboardSettingsView>("/api/settings/dashboard");

        Assert.NotNull(loaded);
        Assert.Equal(characterId, loaded.DefaultCharacterId);
        Assert.Equal("Recommended", loaded.DefaultWorldMode);
        Assert.Equal(900, loaded.DefaultPickupExpiresSeconds);
    }

    [Fact]
    public async Task DashboardSettings_CanSaveNonNorthAmericaDefaultRegion()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();
        await LoginAsync(client);

        var save = await client.PutAsJsonAsync("/api/settings/dashboard", new DashboardSettingsUpdate
        {
            DefaultCharacterId = null,
            DefaultRegion = "Oceania",
            DefaultWorldMode = "AllWorldSweep",
            DefaultPickupExpiresSeconds = 900,
        });
        save.EnsureSuccessStatusCode();
        var loaded = await client.GetFromJsonAsync<DashboardSettingsView>("/api/settings/dashboard");

        Assert.NotNull(loaded);
        Assert.Equal("Oceania", loaded.DefaultRegion);
        Assert.Equal("AllWorldSweep", loaded.DefaultWorldMode);
    }

    [Fact]
    public async Task DashboardSettings_RejectsUnknownDefaultCharacter()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();
        await LoginAsync(client);

        var response = await client.PutAsJsonAsync("/api/settings/dashboard", new DashboardSettingsUpdate
        {
            DefaultCharacterId = 999_999,
            DefaultRegion = "North America",
            DefaultWorldMode = "Recommended",
            DefaultPickupExpiresSeconds = 300,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task LoginAsync(HttpClient client)
    {
        var login = await client.PostAsJsonAsync("/auth/login", new
        {
            username = "admin",
            password = "secret-password",
        });
        login.EnsureSuccessStatusCode();
    }

    private static WebApplicationFactory<Program> CreateApplication()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "MarketMafioso.Server.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        var databasePath = Path.Combine(contentRoot, "marketmafioso.db");

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(contentRoot);
                builder.ConfigureAppConfiguration(config =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["MarketMafioso:DatabasePath"] = databasePath,
                        ["MarketMafioso:RequireDashboardAuth"] = "true",
                        ["MarketMafioso:DashboardBootstrapUsername"] = "admin",
                        ["MarketMafioso:DashboardBootstrapPassword"] = "secret-password",
                    });
                });
            });
    }

    private static InventoryReport CreateReport(string characterName, string homeWorld, uint itemId) =>
        new()
        {
            CharacterName = characterName,
            HomeWorld = homeWorld,
            Timestamp = "2026-06-23T12:00:00.0000000Z",
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
                            Quantity = 1,
                            IsHQ = false,
                            Condition = 0,
                        },
                    ],
                },
            ],
        };
}
