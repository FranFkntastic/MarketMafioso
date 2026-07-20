using Franthropy.FFXIV.Filtering;
using Franthropy.Filtering.Evaluation;
using MarketMafioso.RetainerRestock;
using MarketMafioso.Windows.RetainerRestock;

namespace MarketMafioso.Tests.Windows.RetainerRestock;

public sealed class RetainerRestockBrowserStateTests
{
    [Fact]
    public void CanSaveStagedItem_RequiresWithdrawableItemAndPositiveDesiredQuantity()
    {
        var state = new RetainerRestockBrowserState();

        Assert.False(state.CanSaveStagedItem);
        Assert.Equal("Select an item with observed retainer stock and enter a desired quantity.", state.StagedValidationMessage);

        state.Stage(ItemGroup(100, "Darksteel Ore", playerQuantity: 12, retainerQuantity: 0));

        Assert.False(state.CanSaveStagedItem);
        Assert.Equal("This item is only on the character and cannot be withdrawn from a retainer.", state.StagedValidationMessage);

        state.Stage(ItemGroup(100, "Darksteel Ore", playerQuantity: 0, retainerQuantity: 24));
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

        state.Stage(ItemGroup(100, "Darksteel Ore", retainerQuantity: 24));
        state.StagedDesiredQuantityText = "100";

        var applied = state.ApplyStagedItem(planItems);

        Assert.True(applied);
        var planItem = Assert.Single(planItems);
        Assert.Equal(100U, planItem.ItemId);
        Assert.Equal("Darksteel Ore", planItem.ItemName);
        Assert.Equal(100, planItem.DesiredPlayerQuantity);
        Assert.True(planItem.Enabled);
        Assert.Null(state.SelectedItemGroup);
        Assert.Equal(string.Empty, state.StagedDesiredQuantityText);
    }

    [Fact]
    public void ApplyStagedItem_UpdatesExistingPlanRow()
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

        state.Stage(ItemGroup(100, "Darksteel Ore", retainerQuantity: 24));
        state.StagedDesiredQuantityText = "240";

        Assert.True(state.ApplyStagedItem(planItems));
        var planItem = Assert.Single(planItems);
        Assert.Equal("Darksteel Ore", planItem.ItemName);
        Assert.Equal(240, planItem.DesiredPlayerQuantity);
        Assert.True(planItem.Enabled);
    }

    [Fact]
    public void ApplyStagedItem_WhenInvalid_DoesNotMutatePlan()
    {
        var state = new RetainerRestockBrowserState();
        var planItems = new List<RetainerRestockPlanItem>();

        state.Stage(ItemGroup(100, "Darksteel Ore", retainerQuantity: 24));

        Assert.False(state.ApplyStagedItem(planItems));
        Assert.Empty(planItems);
    }

    [Fact]
    public void SelectMode_ListingsClearsWithdrawalStaging()
    {
        var state = new RetainerRestockBrowserState();
        state.Stage(ItemGroup(100, "Darksteel Ore", retainerQuantity: 24));
        state.StagedDesiredQuantityText = "12";

        state.SelectMode(RetainerBrowseQueryMode.Listings);

        Assert.Equal(RetainerBrowseQueryMode.Listings, state.Mode);
        Assert.Null(state.SelectedItemGroup);
        Assert.Equal(string.Empty, state.StagedDesiredQuantityText);
    }

    [Fact]
    public void EnsureScope_FallsBackWhenSelectedRetainerDisappears()
    {
        var state = new RetainerRestockBrowserState
        {
            SelectedScopeKey = RetainerBrowseScopeOption.RetainerKey(99),
        };
        var projection = RetainerBrowseProjectionBuilder.Build([], new Configuration(), new RetainerOwnerScope("Wei Ning", "Siren"));

        state.EnsureScope(projection);

        Assert.Equal(RetainerBrowseScopeOption.AllKey, state.SelectedScopeKey);
    }

    [Fact]
    public void RebindSelectedItem_ClearsStagingWhenObservedRetainerStockDisappears()
    {
        var state = new RetainerRestockBrowserState();
        state.Stage(ItemGroup(100, "Darksteel Ore", retainerQuantity: 24));
        state.StagedDesiredQuantityText = "12";

        state.RebindSelectedItem([ItemGroup(100, "Darksteel Ore", playerQuantity: 2)]);

        Assert.Null(state.SelectedItemGroup);
        Assert.False(state.CanSaveStagedItem);
        Assert.Equal(string.Empty, state.StagedDesiredQuantityText);
    }

    [Fact]
    public void ExpansionState_IsPrunedToVisibleItems()
    {
        var state = new RetainerRestockBrowserState();
        state.ToggleExpanded(100);
        state.ToggleExpanded(200);

        state.RetainAvailableExpansions([ItemGroup(100, "Darksteel Ore", retainerQuantity: 1)]);

        Assert.True(state.IsExpanded(100));
        Assert.False(state.IsExpanded(200));
    }

    private static RetainerBrowseItemGroup ItemGroup(
        uint itemId,
        string itemName,
        int playerQuantity = 0,
        int retainerQuantity = 0)
    {
        var stacks = new List<RetainerBrowseStockStack>();
        if (playerQuantity > 0)
        {
            stacks.Add(new RetainerBrowseStockStack(
                RetainerBrowseScopeOption.PlayerKey,
                RetainerBrowseScopeKind.Player,
                null,
                "Player",
                "Inventory1",
                0,
                itemId,
                itemName,
                "Material",
                playerQuantity,
                FfxivItemQuality.NQ,
                Evidence.Unknown<decimal>("Not recorded")));
        }

        if (retainerQuantity > 0)
        {
            stacks.Add(new RetainerBrowseStockStack(
                RetainerBrowseScopeOption.RetainerKey(1),
                RetainerBrowseScopeKind.Retainer,
                1,
                "Little Piggy",
                "RetainerPage1",
                0,
                itemId,
                itemName,
                "Material",
                retainerQuantity,
                FfxivItemQuality.NQ,
                Evidence.Unknown<decimal>("Not recorded")));
        }

        return new RetainerBrowseItemGroup(itemId, itemName, "Material", stacks);
    }
}
