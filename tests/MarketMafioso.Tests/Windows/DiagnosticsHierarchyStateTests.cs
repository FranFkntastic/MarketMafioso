using MarketMafioso.Windows.MarketAcquisitionPanels;

namespace MarketMafioso.Tests.Windows;

public sealed class DiagnosticsHierarchyStateTests
{
    [Fact]
    public void Defaults_expand_only_market_acquisition_diagnostics()
    {
        var state = new DiagnosticsHierarchyState();

        Assert.True(DiagnosticsHierarchyState.MarketAcquisitionDefaultOpen);
        Assert.False(DiagnosticsHierarchyState.AutomationDefaultOpen);
        Assert.False(DiagnosticsHierarchyState.SquireRouteDefaultOpen);
        Assert.False(DiagnosticsHierarchyState.ReportLocationsDefaultOpen);
        Assert.False(DiagnosticsHierarchyState.TestToolsDefaultOpen);
        Assert.False(state.TestToolsExpanded);
        Assert.Equal("Expand Test tools", state.TestToolsActionLabel);
        Assert.Equal("Collapsed", state.TestToolsValue);
    }

    [Fact]
    public void Test_tools_expansion_tracks_rendered_and_semantic_changes()
    {
        var state = new DiagnosticsHierarchyState();

        state.SetTestToolsExpanded(true);
        Assert.True(state.TestToolsExpanded);
        Assert.Equal("Collapse Test tools", state.TestToolsActionLabel);

        state.ToggleTestTools();
        Assert.False(state.TestToolsExpanded);
    }
}
