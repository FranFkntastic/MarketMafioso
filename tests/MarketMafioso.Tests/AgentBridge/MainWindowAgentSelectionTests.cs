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
