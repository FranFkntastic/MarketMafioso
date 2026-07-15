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
    [InlineData("Compose", "Compose", "Request")]
    [InlineData("Request", "Compose", "Request")]
    [InlineData("Working Set", "Working Set", "Plan")]
    [InlineData("Plan", "Working Set", "Plan")]
    public void PendingSelection_SelectsCurrentAndLegacyWorkspaceNames(
        string requestedView,
        string viewName,
        string legacyViewName)
    {
        Assert.True(MainWindow.ShouldSelectAgentWorkspaceTab(requestedView, viewName, legacyViewName));
    }
}
