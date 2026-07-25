using MarketMafioso.Windows;

namespace MarketMafioso.Tests.AgentBridge;

public sealed class MainWindowAgentSelectionTests
{
    [Fact]
    public void NoPendingSelection_DoesNotSelectTabsWithoutLegacyAliases()
    {
        Assert.False(MainWindow.ShouldSelectAgentWorkspaceTab(null, "Inbox"));
        Assert.False(MainWindow.ShouldSelectAgentWorkspaceTab(null, "Route"));
    }

    [Theory]
    [InlineData("Retainers")]
    [InlineData("Retainers/Overview")]
    [InlineData("Retainers/Browse stock")]
    [InlineData("Restock")]
    [InlineData("Restock/Plan")]
    [InlineData("Plan")]
    public void BridgeTabRouting_RejectsRemovedRetainerViewsAndAliases(string requestedTab)
    {
        Assert.False(MainWindow.TryNormalizeAgentBridgeTab(requestedTab, out _, out _));
    }

    [Theory]
    [InlineData("Workbench", "Workbench", "Compose", "Working Set")]
    [InlineData("Compose", "Workbench", "Compose", "Working Set")]
    [InlineData("Working Set", "Workbench", "Compose", "Working Set")]
    [InlineData("Plan", "Workbench", "Plan", "Request")]
    public void PendingSelection_SelectsCurrentAndLegacyWorkspaceNames(
        string requestedView,
        string viewName,
        string firstLegacyViewName,
        string secondLegacyViewName)
    {
        Assert.True(MainWindow.ShouldSelectAgentWorkspaceTab(requestedView, viewName, firstLegacyViewName, secondLegacyViewName));
    }

    [Fact]
    public void CountedWorkspaceTabLabel_KeepsIdentityIndependentOfCount()
    {
        var empty = MainWindow.BuildCountedWorkspaceTabLabel("Inbox", 0, "MarketAcquisitionInbox");
        var populated = MainWindow.BuildCountedWorkspaceTabLabel("Inbox", 12, "MarketAcquisitionInbox");

        Assert.Equal("Inbox (0)###MarketAcquisitionInbox", empty);
        Assert.Equal("Inbox (12)###MarketAcquisitionInbox", populated);
        Assert.Equal(empty[(empty.IndexOf("###", StringComparison.Ordinal) + 3)..],
            populated[(populated.IndexOf("###", StringComparison.Ordinal) + 3)..]);
    }
}
