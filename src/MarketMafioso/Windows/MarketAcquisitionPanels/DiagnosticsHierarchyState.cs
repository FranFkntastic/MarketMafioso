namespace MarketMafioso.Windows.MarketAcquisitionPanels;

internal sealed class DiagnosticsHierarchyState
{
    public const string TestToolsControlId = "diagnostics.test-tools.expanded";
    public const bool MarketAcquisitionDefaultOpen = true;
    public const bool AutomationDefaultOpen = false;
    public const bool SquireRouteDefaultOpen = false;
    public const bool ReportLocationsDefaultOpen = false;
    public const bool TestToolsDefaultOpen = false;

    public bool TestToolsExpanded { get; private set; } = TestToolsDefaultOpen;

    public string TestToolsActionLabel => TestToolsExpanded ? "Collapse Test tools" : "Expand Test tools";

    public string TestToolsValue => TestToolsExpanded ? "Expanded" : "Collapsed";

    public void SetTestToolsExpanded(bool expanded) => TestToolsExpanded = expanded;

    public void ToggleTestTools() => TestToolsExpanded = !TestToolsExpanded;
}
