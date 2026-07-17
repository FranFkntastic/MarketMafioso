using MarketMafioso.Contracts.Inventory;
using MarketMafioso.Dashboard.Components.Inventory;

namespace MarketMafioso.Server.Tests;

public sealed class InventoryTableProjectionTests
{
    [Fact]
    public void GroupedInventory_FiltersStacksThenRecomputesGroups()
    {
        var query = new InventoryTableQueryState();
        query.SetFilter("item", "darksteel");
        query.SetFilter("quantity", "20..99");
        query.SetFilter("owner", "scrongle");
        var rows = new[]
        {
            Stack(1, "Darksteel Nugget", 42, "Scrongle"),
            Stack(2, "Darksteel Ingot", 120, "Scrongle"),
            Stack(3, "Iron Nugget", 42, "Scrongle"),
            Stack(4, "Darksteel Plate", 42, "Player Inventory"),
        };

        var visible = InventoryTableProjection.GroupedInventory(rows, query);

        Assert.Equal("Darksteel Nugget", Assert.Single(visible).DisplayName);
    }

    [Fact]
    public void GroupedInventory_HeaderSortTogglesAscendingThenDescending()
    {
        var query = new InventoryTableQueryState();
        query.ToggleSort("quantity");
        var rows = new[] { Stack(1, "Many", 99, "Player"), Stack(2, "Few", 3, "Player") };

        Assert.Equal(["Few", "Many"], InventoryTableProjection.GroupedInventory(rows, query).Select(item => item.DisplayName));

        query.ToggleSort("quantity");

        Assert.Equal(["Many", "Few"], InventoryTableProjection.GroupedInventory(rows, query).Select(item => item.DisplayName));
    }

    [Fact]
    public void Listings_HandlesEvidenceAndMissingPricesWithoutConflatingThem()
    {
        var query = new InventoryTableQueryState();
        query.SetFilter("unit-price", ">=1000");
        var rows = new[]
        {
            new InventoryBrowserMarketListingView { DisplayName = "Known", OwnerName = "One", Quantity = 1, UnitPrice = 1_800, TotalPrice = 1_800, EvidenceAgeSeconds = 30 },
            new InventoryBrowserMarketListingView { DisplayName = "Cheap", OwnerName = "One", Quantity = 1, UnitPrice = 500, TotalPrice = 500, EvidenceAgeSeconds = 60 },
            new InventoryBrowserMarketListingView { DisplayName = "Unknown", OwnerName = "Two", Quantity = 1, UnitPrice = null, TotalPrice = null, EvidenceAgeSeconds = null },
        };

        var visible = InventoryTableProjection.Listings(rows, query);

        Assert.Equal("Known", Assert.Single(visible).DisplayName);
    }

    private static InventoryBrowserStackView Stack(uint itemId, string name, int quantity, string owner) => new()
    {
        ItemId = itemId,
        DisplayName = name,
        Quantity = quantity,
        OwnerName = owner,
        Location = owner,
        BagName = "Inventory1",
    };
}
