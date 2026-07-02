using MarketMafioso.Automation.Retainers;

namespace MarketMafioso.Tests.Automation.Retainers;

public sealed class RetainerUiAutomationTextTests
{
    [Theory]
    [InlineData("Entrust or withdraw items.", "Entrust or withdraw items", true)]
    [InlineData("Entrust or withdraw items. (22)", "Entrust or withdraw items", true)]
    [InlineData("\uE03CEntrust or withdraw items.", "Entrust or withdraw items", true)]
    [InlineData("Assign venture.", "Entrust or withdraw items", false)]
    public void IsSelectStringEntryMatch_NormalizesDecoratedLocalizedEntries(
        string entry,
        string targetText,
        bool expected)
    {
        Assert.Equal(expected, RetainerUiAutomationText.IsSelectStringEntryMatch(entry, targetText));
    }

    [Fact]
    public void FindRetainerListIndex_MatchesByNameAndRequiresActiveRow()
    {
        var rows = new[]
        {
            new RetainerListEntry("Alpha", IsActive: true),
            new RetainerListEntry("Beta", IsActive: false),
            new RetainerListEntry("Gamma", IsActive: true),
        };

        Assert.Equal(2, RetainerUiAutomationText.FindRetainerListIndex(rows, "gamma"));
        Assert.Null(RetainerUiAutomationText.FindRetainerListIndex(rows, "Beta"));
        Assert.Null(RetainerUiAutomationText.FindRetainerListIndex(rows, "Delta"));
    }

    [Fact]
    public void FindContextMenuLabelIndex_MatchesLocalizedContextMenuText()
    {
        var labels = new[]
        {
            "Retrieve from Retainer",
            "Retrieve Quantity",
            "Have Retainer Sell Items",
        };

        Assert.Equal(1, RetainerUiAutomationText.FindContextMenuLabelIndex(labels, "Retrieve Quantity"));
        Assert.Null(RetainerUiAutomationText.FindContextMenuLabelIndex(labels, "Retrieve HQ"));
    }

    [Theory]
    [InlineData(true, false, null)]
    [InlineData(true, true, "Close the current retainer inventory before starting automated workshop material restock.")]
    [InlineData(false, true, "Close the current retainer inventory and open the retainer list before starting automated workshop material restock.")]
    [InlineData(false, false, "Open the retainer list before starting automated workshop material restock.")]
    public void GetAutomatedRestockStartError_RequiresRetainerList(
        bool isRetainerListReady,
        bool isRetainerInventoryReady,
        string? expected)
    {
        Assert.Equal(expected, RetainerUiAutomationText.GetAutomatedRestockStartError(isRetainerListReady, isRetainerInventoryReady));
    }
}
