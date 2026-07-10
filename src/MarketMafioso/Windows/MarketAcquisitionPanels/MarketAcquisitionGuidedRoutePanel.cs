using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Windows.Main;

namespace MarketMafioso.Windows.MarketAcquisitionPanels;

internal sealed class MarketAcquisitionGuidedRoutePanel
{
    private readonly Func<MarketAcquisitionRouteEngineSnapshot> getRouteSnapshot;
    private readonly Action<bool> startRoute;
    private readonly Action pauseRoute;
    private readonly Action resumeRoute;
    private readonly Action stopRoute;
    private readonly Action restartRoute;
    private readonly Action reprepareRoute;
    private readonly Action<MarketAcquisitionRouteEngineSnapshot> drawPostRunDiagnosticSummary;
    private readonly Action<MarketAcquisitionRouteEngineSnapshot> drawLatestWorldCompletionSummary;
    private readonly Action<MarketAcquisitionRouteEngineSnapshot> drawMarketBoardProbeStatus;
    private readonly HashSet<string> expandedStops = new(StringComparer.OrdinalIgnoreCase);

    public MarketAcquisitionGuidedRoutePanel(
        Func<MarketAcquisitionRouteEngineSnapshot> getRouteSnapshot,
        Action<bool> startRoute,
        Action pauseRoute,
        Action resumeRoute,
        Action stopRoute,
        Action restartRoute,
        Action reprepareRoute,
        Action<MarketAcquisitionRouteEngineSnapshot> drawPostRunDiagnosticSummary,
        Action<MarketAcquisitionRouteEngineSnapshot> drawLatestWorldCompletionSummary,
        Action<MarketAcquisitionRouteEngineSnapshot> drawMarketBoardProbeStatus)
    {
        this.getRouteSnapshot = getRouteSnapshot ?? throw new ArgumentNullException(nameof(getRouteSnapshot));
        this.startRoute = startRoute ?? throw new ArgumentNullException(nameof(startRoute));
        this.pauseRoute = pauseRoute ?? throw new ArgumentNullException(nameof(pauseRoute));
        this.resumeRoute = resumeRoute ?? throw new ArgumentNullException(nameof(resumeRoute));
        this.stopRoute = stopRoute ?? throw new ArgumentNullException(nameof(stopRoute));
        this.restartRoute = restartRoute ?? throw new ArgumentNullException(nameof(restartRoute));
        this.reprepareRoute = reprepareRoute ?? throw new ArgumentNullException(nameof(reprepareRoute));
        this.drawPostRunDiagnosticSummary = drawPostRunDiagnosticSummary ?? throw new ArgumentNullException(nameof(drawPostRunDiagnosticSummary));
        this.drawLatestWorldCompletionSummary = drawLatestWorldCompletionSummary ?? throw new ArgumentNullException(nameof(drawLatestWorldCompletionSummary));
        this.drawMarketBoardProbeStatus = drawMarketBoardProbeStatus ?? throw new ArgumentNullException(nameof(drawMarketBoardProbeStatus));
    }

    public void Draw(MarketAcquisitionPlan? plan, bool isPlanStale)
    {
        var snapshot = getRouteSnapshot();
        ImGuiUi.SectionHeader("Route", MarketMafiosoUiTheme.Header);

        var canStart = plan is { Status: "Ready" } &&
                       !isPlanStale &&
                       plan.WorldBatches.Count > 0 &&
                       !snapshot.IsRunning &&
                       !snapshot.IsPaused;
        var canReprepare = canStart &&
                            snapshot.CanRestart &&
                            snapshot.CompletedOrProbedStopCount > 0;
        DrawGuidedRouteActionRow(snapshot, canStart, canReprepare);

        ImGui.TextColored(GetGuidedRouteStatusColor(snapshot), snapshot.StatusMessage);
        if (isPlanStale)
            ImGui.TextColored(MarketMafiosoUiTheme.Error, "Request changed after this plan was prepared. Prepare a fresh plan before starting.");
        drawPostRunDiagnosticSummary(snapshot);
        drawLatestWorldCompletionSummary(snapshot);

        if (snapshot.Stops.Count == 0)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Start after preparing a plan. Routes travel, validate live listings, and purchase safe rows automatically.");
            return;
        }

        if (snapshot.LastDiagnosticFilePath != null)
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"Diagnostics: {snapshot.LastDiagnosticFilePath}");

        var activeStop = snapshot.ActiveStop;
        if (activeStop == null)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Success, "Route is not actively executing.");
        }
        else
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Header, $"Next stop: {activeStop.WorldName}");
            ImGui.SameLine();
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"Planned {activeStop.PlannedQuantity:N0} item(s), {FormatGil(activeStop.PlannedGil)}");
        }

        DrawGuidedRouteStops(snapshot.Stops);
        drawMarketBoardProbeStatus(snapshot);
    }

    private void DrawGuidedRouteActionRow(MarketAcquisitionRouteEngineSnapshot snapshot, bool canStart, bool canReprepare)
    {
        if (!ImGui.BeginTable("MarketAcquisitionGuidedRouteActions", 3, ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Run", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Control", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Reset", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Run");
        if (ImGuiUi.Button("Start##MarketAcquisitionStartRoute", canStart))
            startRoute(false);
        ImGui.SameLine();
        if (ImGuiUi.Button("Diagnostic Run##MarketAcquisitionStartDiagnostics", canStart))
            startRoute(true);

        ImGui.TableNextColumn();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Control");
        if (snapshot.IsPaused)
        {
            if (ImGuiUi.Button("Resume##MarketAcquisitionResumeRoute", true))
                resumeRoute();
        }
        else
        {
            if (ImGuiUi.Button("Pause##MarketAcquisitionPauseRoute", snapshot.IsRunning))
                pauseRoute();
        }
        ImGui.SameLine();
        if (ImGuiUi.Button("Stop##MarketAcquisitionStopRoute", snapshot.IsRunning || snapshot.IsPaused))
            stopRoute();

        ImGui.TableNextColumn();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Reset");
        if (ImGuiUi.Button("Restart##MarketAcquisitionRestartRoute", canStart && snapshot.CanRestart))
            restartRoute();
        ImGui.SameLine();
        if (ImGuiUi.Button("Refresh Plan##MarketAcquisitionReprepareRoute", canReprepare))
            reprepareRoute();

        ImGui.EndTable();
    }

    private void DrawGuidedRouteStops(IReadOnlyList<MarketAcquisitionGuidedRouteStop> stops)
    {
        var rows = MarketAcquisitionRouteTablePresenter.BuildRows(stops);
        if (ImGui.BeginTable("MarketAcquisitionGuidedRouteStops", 7, ImGuiUi.InteractiveTableFlags))
        {
            ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, 140);
            ImGui.TableSetupColumn("Data Center");
            ImGui.TableSetupColumn("Route Lines");
            ImGui.TableSetupColumn("State");
            ImGui.TableSetupColumn("Intent");
            ImGui.TableSetupColumn("Result");
            ImGui.TableSetupColumn("Notes");
            ImGui.TableHeadersRow();

            foreach (var row in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                DrawGuidedRouteStopExpander(row);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatRouteDataCenter(row.DataCenter));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.RouteLines);
                if (!string.IsNullOrWhiteSpace(row.LineMix) && !string.Equals(row.LineMix, "No route lines", StringComparison.Ordinal))
                {
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(row.LineMix);
                }

                ImGui.TableNextColumn();
                ImGui.TextColored(GetGuidedRouteStopColor(row.State), row.State);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Intent);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Result);
                ImGui.TableNextColumn();
                ImGui.TextColored(row.Aggregate.FailedLineCount > 0 ? MarketMafiosoUiTheme.Error : MarketMafiosoUiTheme.Muted, row.Notes);

                if (expandedStops.Contains(GetGuidedRouteStopKey(row)))
                    DrawGuidedRouteStopLineRows(row);
            }

            ImGui.EndTable();
        }
    }

    private void DrawGuidedRouteStopExpander(MarketAcquisitionRouteStopRow row)
    {
        var key = GetGuidedRouteStopKey(row);
        var expanded = expandedStops.Contains(key);
        var buttonLabel = expanded ? $"v##route-stop-{key}" : $">##route-stop-{key}";
        if (ImGui.SmallButton(buttonLabel))
        {
            if (expanded)
                expandedStops.Remove(key);
            else
                expandedStops.Add(key);
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(row.WorldName);
    }

    private static void DrawGuidedRouteStopLineRows(MarketAcquisitionRouteStopRow stop)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "  Item");
        ImGui.TableNextColumn();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Source");
        ImGui.TableNextColumn();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "State");
        ImGui.TableNextColumn();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Planned");
        ImGui.TableNextColumn();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Discovered");
        ImGui.TableNextColumn();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Bought");
        ImGui.TableNextColumn();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Notes");

        foreach (var line in stop.Lines)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"  {line.Item}");
            ImGui.TableNextColumn();
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, line.Source);
            ImGui.TableNextColumn();
            ImGui.TextColored(GetRouteLineStateColor(line.State), line.State);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.Planned);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.Discovered);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.Bought);
            ImGui.TableNextColumn();
            ImGui.TextColored(line.State.Equals("Blocked", StringComparison.OrdinalIgnoreCase) ? MarketMafiosoUiTheme.Error : MarketMafiosoUiTheme.Muted, line.Notes);
        }
    }

    private static System.Numerics.Vector4 GetGuidedRouteStatusColor(MarketAcquisitionRouteEngineSnapshot snapshot)
    {
        var status = snapshot.StatusMessage;
        if (status.StartsWith("Unable", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("Cannot", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("Failed", StringComparison.OrdinalIgnoreCase))
            return MarketMafiosoUiTheme.Error;

        if (status.Contains("Waiting", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("Approve", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("Purchasing", StringComparison.OrdinalIgnoreCase) ||
            snapshot.IsPaused)
            return MarketMafiosoUiTheme.Header;

        if (status.Contains("Arrived", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("Recorded", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("complete", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("started", StringComparison.OrdinalIgnoreCase))
            return MarketMafiosoUiTheme.Success;

        return MarketMafiosoUiTheme.Muted;
    }

    private static System.Numerics.Vector4 GetGuidedRouteStopColor(string state) =>
        state switch
        {
            "Complete" => MarketMafiosoUiTheme.Success,
            "Partial" or "Buying" or "Traveling" or "Arrived" => MarketMafiosoUiTheme.Header,
            "Blocked" or "Failed" => MarketMafiosoUiTheme.Error,
            _ => MarketMafiosoUiTheme.Muted,
        };

    private static System.Numerics.Vector4 GetRouteLineStateColor(string state) =>
        state switch
        {
            "Complete" or "Purchasing" or "Buying" => MarketMafiosoUiTheme.Success,
            "Pending" => MarketMafiosoUiTheme.Muted,
            _ when state.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase) => MarketMafiosoUiTheme.Muted,
            _ when state.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
                   state.Equals("Blocked", StringComparison.OrdinalIgnoreCase) => MarketMafiosoUiTheme.Error,
            _ => MarketMafiosoUiTheme.Header,
        };

    private static string GetGuidedRouteStopKey(MarketAcquisitionRouteStopRow row) =>
        $"{row.WorldName}|{row.DataCenter}";

    private static string FormatGil(uint gil) => $"{gil:N0} gil";

    private static string FormatRouteDataCenter(string dataCenter) =>
        string.IsNullOrWhiteSpace(dataCenter) ? "-" : dataCenter;
}
