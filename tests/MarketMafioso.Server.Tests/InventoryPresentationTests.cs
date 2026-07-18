using MarketMafioso.Dashboard.Components.Inventory;

namespace MarketMafioso.Server.Tests;

using Franthropy.Filtering.Completion;
using Franthropy.Filtering.Syntax;

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

    [Fact]
    public void PartialPredicate_WithAValueCompletion_RemainsAnActiveEdit()
    {
        var completions = new[]
        {
            new FilterCompletionItem(
                "hq",
                "hq",
                FilterCompletionKind.Value,
                new TextSpan(3, 1)),
        };

        Assert.True(InventoryFilterPresentation.HasValueCompletion(completions));
    }

    [Theory]
    [InlineData("quality:hq", InventoryBrowserMode.Listings, InventoryBrowserMode.Items)]
    [InlineData("condition<50", InventoryBrowserMode.Listings, InventoryBrowserMode.Items)]
    [InlineData("price<2000", InventoryBrowserMode.Items, InventoryBrowserMode.Listings)]
    [InlineData("age<10m", InventoryBrowserMode.Items, InventoryBrowserMode.Listings)]
    public void SuggestedMode_PreservesTheUsersFilterIntent(
        string filter,
        InventoryBrowserMode current,
        InventoryBrowserMode expected)
    {
        Assert.Equal(expected, InventoryFilterPresentation.SuggestedMode(filter, current));
    }

    [Theory]
    [InlineData("name:\"quality\"", InventoryBrowserMode.Listings, null)]
    [InlineData("priceless", InventoryBrowserMode.Items, null)]
    [InlineData("not (price<2000)", InventoryBrowserMode.Items, InventoryBrowserMode.Listings)]
    [InlineData("(location:inventory)", InventoryBrowserMode.Listings, InventoryBrowserMode.Items)]
    public void SuggestedMode_UsesParsedFieldReferences(
        string filter,
        InventoryBrowserMode current,
        InventoryBrowserMode? expected)
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
