using MarketMafioso.RetainerRestock;
using MarketMafioso.Windows.RetainerRestock;

namespace MarketMafioso.Tests.Windows.RetainerRestock;

public sealed class RetainerRestockBrowserStateTests
{
    [Fact]
    public void CanSaveStagedItem_RequiresSelectedStockRowAndPositiveDesiredQuantity()
    {
        var state = new RetainerRestockBrowserState();

        Assert.False(state.CanSaveStagedItem);
        Assert.Equal("Select an item and enter a desired quantity.", state.StagedValidationMessage);

        state.Stage(Row(100, "Darksteel Ore"));

        Assert.False(state.CanSaveStagedItem);
        Assert.Equal("Enter a desired quantity.", state.StagedValidationMessage);

        state.StagedDesiredQuantityText = "0";

        Assert.False(state.CanSaveStagedItem);
        Assert.Equal("Desired quantity must be greater than zero.", state.StagedValidationMessage);

        state.StagedDesiredQuantityText = "100";

        Assert.True(state.CanSaveStagedItem);
        Assert.Equal(100, state.StagedDesiredQuantity);
        Assert.Equal("Ready to add Darksteel Ore.", state.StagedValidationMessage);
    }

    [Fact]
    public void ApplyStagedItem_AddsNewPlanRowWithExplicitQuantity()
    {
        var state = new RetainerRestockBrowserState();
        var planItems = new List<RetainerRestockPlanItem>();

        state.Stage(Row(100, "Darksteel Ore"));
        state.StagedDesiredQuantityText = "100";

        var applied = state.ApplyStagedItem(planItems);

        Assert.True(applied);
        var planItem = Assert.Single(planItems);
        Assert.Equal(100U, planItem.ItemId);
        Assert.Equal("Darksteel Ore", planItem.ItemName);
        Assert.Equal(100, planItem.DesiredPlayerQuantity);
        Assert.True(planItem.Enabled);
        Assert.Null(state.SelectedStockRow);
        Assert.Equal(string.Empty, state.StagedDesiredQuantityText);
    }

    [Fact]
    public void ApplyStagedItem_UpdatesExistingPlanRowOnlyAfterExplicitQuantity()
    {
        var state = new RetainerRestockBrowserState();
        var planItems = new List<RetainerRestockPlanItem>
        {
            new()
            {
                ItemId = 100,
                ItemName = "Old Darksteel Ore",
                DesiredPlayerQuantity = 80,
                Enabled = false,
            },
        };

        state.Stage(Row(100, "Darksteel Ore"));
        state.StagedDesiredQuantityText = "240";

        var applied = state.ApplyStagedItem(planItems);

        Assert.True(applied);
        var planItem = Assert.Single(planItems);
        Assert.Equal(100U, planItem.ItemId);
        Assert.Equal("Darksteel Ore", planItem.ItemName);
        Assert.Equal(240, planItem.DesiredPlayerQuantity);
        Assert.True(planItem.Enabled);
    }

    [Fact]
    public void ApplyStagedItem_WhenInvalid_DoesNotMutatePlan()
    {
        var state = new RetainerRestockBrowserState();
        var planItems = new List<RetainerRestockPlanItem>();

        state.Stage(Row(100, "Darksteel Ore"));

        var applied = state.ApplyStagedItem(planItems);

        Assert.False(applied);
        Assert.Empty(planItems);
    }

    [Fact]
    public void FilterRows_RespectsSourceTogglesAndSearch()
    {
        var state = new RetainerRestockBrowserState();
        var rows = new[]
        {
            Row(200, "Cobalt Ingot", playerQuantity: 12, retainerQuantity: 0),
            Row(100, "Darksteel Ore", playerQuantity: 0, retainerQuantity: 24),
            Row(300, "Fire Shard", playerQuantity: 8, retainerQuantity: 16),
        };

        Assert.Collection(
            state.FilterRows(rows),
            row => Assert.Equal("Cobalt Ingot", row.ItemName),
            row => Assert.Equal("Darksteel Ore", row.ItemName),
            row => Assert.Equal("Fire Shard", row.ItemName));

        state.ShowPlayerStock = false;

        Assert.Collection(
            state.FilterRows(rows),
            row => Assert.Equal("Darksteel Ore", row.ItemName),
            row => Assert.Equal("Fire Shard", row.ItemName));

        state.ShowPlayerStock = true;
        state.ShowRetainerStock = false;

        Assert.Collection(
            state.FilterRows(rows),
            row => Assert.Equal("Cobalt Ingot", row.ItemName),
            row => Assert.Equal("Fire Shard", row.ItemName));

        state.ShowRetainerStock = true;
        state.SearchText = "shard";

        var result = Assert.Single(state.FilterRows(rows));
        Assert.Equal("Fire Shard", result.ItemName);
    }

    [Fact]
    public void FilterRows_UsesConfigurableVisibleRowLimitWithSmallerDefault()
    {
        var state = new RetainerRestockBrowserState();
        var rows = Enumerable
            .Range(1, 80)
            .Select(index => Row((uint)index, $"Item {index:000}"))
            .ToList();

        Assert.Equal(25, state.FilterRows(rows).Count);

        state.VisibleRowLimit = 50;

        Assert.Equal(50, state.FilterRows(rows).Count);
    }

    private static RetainerRestockStockRow Row(
        uint itemId,
        string itemName,
        int playerQuantity = 0,
        int retainerQuantity = 1) =>
        new(
            itemId,
            itemName,
            playerQuantity + retainerQuantity,
            playerQuantity,
            retainerQuantity,
            Sources: [],
            RetainerSources: [],
            OldestRetainerCacheAge: null,
            NewestRetainerCacheAge: null);
}
