using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MarketMafioso.Server.Tests;

public sealed class DashboardCharacterFilterTests
{
    [Fact]
    public async Task ReportsApi_ReturnsLatestAndListsAllCharacters()
    {
        await using var application = new WebApplicationFactory<Program>();
        using var client = application.CreateClient();

        var first = await client.PostAsJsonAsync("/inventory", CreateReport("First Character", 2));
        first.EnsureSuccessStatusCode();
        await Task.Delay(10);
        var second = await client.PostAsJsonAsync("/inventory", CreateReport("Second Character", 4));
        second.EnsureSuccessStatusCode();

        var latest = await client.GetFromJsonAsync<StoredInventoryReport>("/api/reports/latest");
        var allReports = await client.GetFromJsonAsync<IReadOnlyList<ReportSummary>>("/api/reports");

        Assert.NotNull(latest);
        Assert.Equal("Second Character", latest.Report.CharacterName);
        Assert.NotNull(allReports);
        Assert.Contains(allReports, x => string.Equals(x.CharacterName, "Second Character", StringComparison.Ordinal));
        Assert.Contains(allReports, x => string.Equals(x.CharacterName, "First Character", StringComparison.Ordinal));
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
