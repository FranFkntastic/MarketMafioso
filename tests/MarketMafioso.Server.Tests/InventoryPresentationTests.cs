using MarketMafioso.Dashboard.Components.Inventory;

namespace MarketMafioso.Server.Tests;

public sealed class InventoryPresentationTests
{
    [Theory]
    [InlineData("retainer:")]
    [InlineData("quality=")]
    [InlineData("name:darksteel and")]
    [InlineData("name:\"darksteel")]
    [InlineData("(quality:hq or quality:nq")]
    public void IncompleteFilter_DoesNotPresentAsCommittedFailure(string filter)
    {
        Assert.True(InventoryFilterPresentation.IsIncomplete(filter));
    }

    [Theory]
    [InlineData("retainer:Scrongle")]
    [InlineData("quality:hq")]
    [InlineData("(quality:hq)")]
    public void CompleteFilter_CanPresentDiagnostics(string filter)
    {
        Assert.False(InventoryFilterPresentation.IsIncomplete(filter));
    }

    [Theory]
    [InlineData("quality:hq", InventoryBrowserMode.Items, InventoryBrowserMode.Stacks)]
    [InlineData("condition<50", InventoryBrowserMode.Items, InventoryBrowserMode.Stacks)]
    [InlineData("price<2000", InventoryBrowserMode.Items, InventoryBrowserMode.Listings)]
    [InlineData("age<10m", InventoryBrowserMode.Stacks, InventoryBrowserMode.Listings)]
    public void SuggestedMode_PreservesTheUsersFilterIntent(
        string filter,
        InventoryBrowserMode current,
        InventoryBrowserMode expected)
    {
        Assert.Equal(expected, InventoryFilterPresentation.SuggestedMode(filter, current));
    }

    [Theory]
    [InlineData("Inventory1", "Inventory · bag 1")]
    [InlineData("RetainerInventory", "Retainer inventory")]
    [InlineData("RetainerPage3", "Retainer inventory · bag 3")]
    [InlineData("ArmoryMainHand", "Armoury · Main Hand")]
    [InlineData("EquippedItems", "Equipped gear")]
    public void StorageNames_AreTranslatedIntoDomainLanguage(string bagName, string expected)
    {
        Assert.Equal(expected, InventoryDisplayFormatter.FormatStorage(null, bagName));
    }
}
