using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MarketMafioso.Server.Tests;

public sealed class DashboardCharacterFilterTests
{
    [Fact]
    public async Task Dashboard_DefaultsToLatestCharacterAndCanShowAllCharacters()
    {
        await using var application = new WebApplicationFactory<Program>();
        using var client = application.CreateClient();

        var first = await client.PostAsJsonAsync("/inventory", CreateReport("First Character", 2));
        first.EnsureSuccessStatusCode();
        await Task.Delay(10);
        var second = await client.PostAsJsonAsync("/inventory", CreateReport("Second Character", 4));
        second.EnsureSuccessStatusCode();

        var defaultDashboard = await client.GetStringAsync("/");
        var allDashboard = await client.GetStringAsync("/?allCharacters=true");

        Assert.Contains("Second Character", defaultDashboard, StringComparison.Ordinal);
        Assert.DoesNotContain("<td>First Character</td>", defaultDashboard, StringComparison.Ordinal);
        Assert.Contains("Second Character", allDashboard, StringComparison.Ordinal);
        Assert.Contains("<td>First Character</td>", allDashboard, StringComparison.Ordinal);
        Assert.Contains("All Characters", allDashboard, StringComparison.Ordinal);
    }

    private static InventoryReport CreateReport(string characterName, uint itemId) =>
        new()
        {
            CharacterName = characterName,
            HomeWorld = "Gilgamesh",
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
