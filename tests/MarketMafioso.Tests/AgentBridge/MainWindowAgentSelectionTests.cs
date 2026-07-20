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
    [InlineData("Retainers", "Retainers", null)]
    [InlineData("Retainers/Overview", "Retainers", "Overview")]
    [InlineData("Retainers/Browse stock", "Retainers", "Browse stock")]
    [InlineData("Retainers/Browse listings", "Retainers", "Browse listings")]
    [InlineData("Retainers/Quick deposit", "Retainers", "Quick deposit")]
    [InlineData("Retainers/Withdrawal plan", "Retainers", "Withdrawal plan")]
    [InlineData("Restock", "Retainers", null)]
    [InlineData("Restock/Plan", "Retainers", "Withdrawal plan")]
    [InlineData("Retainers/Plan and run", "Retainers", "Withdrawal plan")]
    [InlineData("Plan", "Retainers", "Withdrawal plan")]
    public void BridgeTabRouting_NormalizesRetainerViewsAndAliases(
        string requestedTab,
        string expectedMainTab,
        string? expectedWorkspaceView)
    {
        Assert.True(MainWindow.TryNormalizeAgentBridgeTab(requestedTab, out var mainTab, out var workspaceView));
        Assert.Equal(expectedMainTab, mainTab);
        Assert.Equal(expectedWorkspaceView, workspaceView);
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
}
