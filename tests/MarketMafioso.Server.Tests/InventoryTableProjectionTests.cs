using MarketMafioso.Contracts.Inventory;
using MarketMafioso.Dashboard.Components.Inventory;

namespace MarketMafioso.Server.Tests;

public sealed class InventoryTableProjectionTests
{
    [Fact]
    public void Items_CombinesColumnFiltersAndNumericRanges()
    {
        var query = new InventoryTableQueryState();
        query.SetFilter("item", "darksteel");
        query.SetFilter("owned", "20..99");
        query.SetFilter("location", "scrongle");
        var rows = new[]
        {
            Item("Darksteel Nugget", 42, "Scrongle"),
            Item("Darksteel Ingot", 120, "Scrongle"),
            Item("Iron Nugget", 42, "Scrongle"),
            Item("Darksteel Plate", 42, "Player Inventory"),
        };

        var visible = InventoryTableProjection.Items(rows, query);

        Assert.Equal("Darksteel Nugget", Assert.Single(visible).DisplayName);
    }

    [Fact]
    public void Items_HeaderSortTogglesAscendingThenDescending()
    {
        var query = new InventoryTableQueryState();
        query.ToggleSort("owned");
        var rows = new[] { Item("Many", 99, "Player"), Item("Few", 3, "Player") };

        Assert.Equal(["Few", "Many"], InventoryTableProjection.Items(rows, query).Select(item => item.DisplayName));

        query.ToggleSort("owned");

        Assert.Equal(["Many", "Few"], InventoryTableProjection.Items(rows, query).Select(item => item.DisplayName));
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

    private static InventoryBrowserItemView Item(string name, int quantity, string owner) => new()
    {
        DisplayName = name,
        TotalQuantity = quantity,
        Locations =
        [
            new InventoryBrowserLocationView
            {
                OwnerName = owner,
                Location = owner,
                BagName = "Inventory1",
                Quantity = quantity,
            },
        ],
        OwnerCount = 1,
    };
}
