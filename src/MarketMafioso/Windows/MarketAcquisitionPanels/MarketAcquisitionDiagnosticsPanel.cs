using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using MarketMafioso.Diagnostics;
using Franthropy.Dalamud.AgentBridge;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Windows.Main;
using MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

namespace MarketMafioso.Windows.MarketAcquisitionPanels;

internal sealed class MarketAcquisitionDiagnosticsPanel
{
    private readonly Func<MarketAcquisitionRouteEngineSnapshot> getRouteSnapshot;
    private readonly string diagnosticsDirectory;
    private readonly IPluginLog log;
    private readonly Action drawMarketAcquisitionDiagnostics;
    private readonly Action drawAutomationDiagnostics;
    private readonly Action drawExternalExactRouteDiagnostics;
    private readonly Func<bool> isMarketAcquisitionUnlocked;
    private readonly UiStateCaptureService uiStateCapture;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly Func<CraftAppraisalDiagnosticsSnapshot> getCraftAppraisalDiagnostics;
    private readonly Func<bool> areDryRunToolsEnabled;
    private readonly Func<bool> canStartPreparedRouteDryRun;
    private readonly Action startPreparedRouteDryRun;
    private readonly Func<ExactAcquisitionDryRunScenario> getExactAcquisitionDryRunScenario;
    private readonly Func<ExactAcquisitionDryRunScenario, bool> armExactAcquisitionDryRunScenario;
    private readonly DiagnosticsHierarchyState hierarchyState = new();
#if DEBUG
    private readonly Func<bool> canSeedExactAcquisitionDryRunSunkState;
    private readonly Func<string> seedExactAcquisitionDryRunSunkState;
#endif

    private string diagnosticsFolderStatus = "Route diagnostics folder opens in Explorer.";

    public MarketAcquisitionDiagnosticsPanel(
        Func<MarketAcquisitionRouteEngineSnapshot> getRouteSnapshot,
        string diagnosticsDirectory,
        IPluginLog log,
        Action drawMarketAcquisitionDiagnostics,
        Action drawAutomationDiagnostics,
        Action drawExternalExactRouteDiagnostics,
        Func<bool> isMarketAcquisitionUnlocked,
        UiStateCaptureService uiStateCapture,
        AgentBridgeUiReviewRegistry reviewRegistry,
        Func<CraftAppraisalDiagnosticsSnapshot> getCraftAppraisalDiagnostics,
        Func<bool> areDryRunToolsEnabled,
        Func<bool> canStartPreparedRouteDryRun,
        Action startPreparedRouteDryRun,
        Func<ExactAcquisitionDryRunScenario> getExactAcquisitionDryRunScenario,
        Func<ExactAcquisitionDryRunScenario, bool> armExactAcquisitionDryRunScenario
#if DEBUG
        , Func<bool> canSeedExactAcquisitionDryRunSunkState,
        Func<string> seedExactAcquisitionDryRunSunkState
#endif
        )
    {
        this.getRouteSnapshot = getRouteSnapshot ?? throw new ArgumentNullException(nameof(getRouteSnapshot));
        this.diagnosticsDirectory = diagnosticsDirectory ?? throw new ArgumentNullException(nameof(diagnosticsDirectory));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.drawMarketAcquisitionDiagnostics = drawMarketAcquisitionDiagnostics ?? throw new ArgumentNullException(nameof(drawMarketAcquisitionDiagnostics));
        this.drawAutomationDiagnostics = drawAutomationDiagnostics ?? throw new ArgumentNullException(nameof(drawAutomationDiagnostics));
        this.drawExternalExactRouteDiagnostics = drawExternalExactRouteDiagnostics ?? throw new ArgumentNullException(nameof(drawExternalExactRouteDiagnostics));
        this.isMarketAcquisitionUnlocked = isMarketAcquisitionUnlocked ?? throw new ArgumentNullException(nameof(isMarketAcquisitionUnlocked));
        this.uiStateCapture = uiStateCapture ?? throw new ArgumentNullException(nameof(uiStateCapture));
        this.reviewRegistry = reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry));
        this.getCraftAppraisalDiagnostics = getCraftAppraisalDiagnostics ?? throw new ArgumentNullException(nameof(getCraftAppraisalDiagnostics));
        this.areDryRunToolsEnabled = areDryRunToolsEnabled ?? throw new ArgumentNullException(nameof(areDryRunToolsEnabled));
        this.canStartPreparedRouteDryRun = canStartPreparedRouteDryRun ?? throw new ArgumentNullException(nameof(canStartPreparedRouteDryRun));
        this.startPreparedRouteDryRun = startPreparedRouteDryRun ?? throw new ArgumentNullException(nameof(startPreparedRouteDryRun));
        this.getExactAcquisitionDryRunScenario = getExactAcquisitionDryRunScenario ?? throw new ArgumentNullException(nameof(getExactAcquisitionDryRunScenario));
        this.armExactAcquisitionDryRunScenario = armExactAcquisitionDryRunScenario ?? throw new ArgumentNullException(nameof(armExactAcquisitionDryRunScenario));
#if DEBUG
        this.canSeedExactAcquisitionDryRunSunkState = canSeedExactAcquisitionDryRunSunkState ?? throw new ArgumentNullException(nameof(canSeedExactAcquisitionDryRunSunkState));
        this.seedExactAcquisitionDryRunSunkState = seedExactAcquisitionDryRunSunkState ?? throw new ArgumentNullException(nameof(seedExactAcquisitionDryRunSunkState));
#endif
    }

    public void Draw()
    {
        ImGui.Spacing();
        ImGuiUi.SectionHeader("Diagnostics", MarketMafiosoUiTheme.Header);

        var marketAcquisitionUnlocked = isMarketAcquisitionUnlocked();
        MarketAcquisitionRouteEngineSnapshot? snapshot = null;
        if (marketAcquisitionUnlocked)
        {
            snapshot = getRouteSnapshot();
            DrawReportOverview(snapshot);
        }

        var marketFlags = DiagnosticsHierarchyState.MarketAcquisitionDefaultOpen
            ? ImGuiTreeNodeFlags.DefaultOpen
            : ImGuiTreeNodeFlags.None;
        if (snapshot != null && ImGui.CollapsingHeader(BuildMarketAcquisitionHeader(snapshot), marketFlags))
            drawMarketAcquisitionDiagnostics();

        var automationFlags = DiagnosticsHierarchyState.AutomationDefaultOpen
            ? ImGuiTreeNodeFlags.DefaultOpen
            : ImGuiTreeNodeFlags.None;
        if (ImGui.CollapsingHeader("Automation diagnostics##DiagnosticsAutomation", automationFlags))
            drawAutomationDiagnostics();

        var externalRouteFlags = DiagnosticsHierarchyState.ExternalExactRouteDefaultOpen
            ? ImGuiTreeNodeFlags.DefaultOpen
            : ImGuiTreeNodeFlags.None;
        if (ImGui.CollapsingHeader("External plan route diagnostics##DiagnosticsExternalExactRoute", externalRouteFlags))
            drawExternalExactRouteDiagnostics();

        if (snapshot is { IsRouteActive: true, ExecutionMode: MarketAcquisitionExecutionMode.DryRun })
            ImGui.TextColored(MarketMafiosoUiTheme.Warning, $"Dry run active: {snapshot.VisibleAcquisitionStatus}");

        DrawTestTools(snapshot);
    }

    private void DrawReportOverview(MarketAcquisitionRouteEngineSnapshot snapshot)
    {
        if (ImGui.BeginTable("DiagnosticsReportOverview", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerH))
        {
            ImGui.TableSetupColumn("Summary", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(MarketMafiosoUiTheme.Header, "Latest diagnostics report");
            ImGui.SameLine();
            DrawWrappedColored(MarketMafiosoUiTheme.Muted, GetReportFileName(snapshot.LastDiagnosticFilePath));
            ImGui.TableNextColumn();
            if (ImGuiUi.Button("Open diagnostics folder", true))
                OpenDiagnosticsFolder(diagnosticsDirectory);
            RegisterLastControl(
                "diagnostics.open-folder",
                "Open diagnostics folder",
                true,
                () => OpenDiagnosticsFolder(diagnosticsDirectory));

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(MarketMafiosoUiTheme.Header, "Current run");
            ImGui.SameLine();
            DrawWrappedColored(MarketMafiosoUiTheme.Muted, BuildCurrentRunSummary(snapshot));
            ImGui.TableNextColumn();
            ImGui.EndTable();
        }

        if (diagnosticsFolderStatus.StartsWith("Unable", StringComparison.OrdinalIgnoreCase))
            DrawWrappedColored(MarketMafiosoUiTheme.Error, diagnosticsFolderStatus);

        foreach (var warning in GetCurrentWarnings(snapshot))
            DrawWrappedColored(MarketMafiosoUiTheme.Warning, $"Warning: {warning}");

        var routeException = GetActiveRouteException(snapshot);
        if (routeException != null)
            DrawWrappedColored(MarketMafiosoUiTheme.Error, $"Route exception: {routeException}");

        var reportLocationsFlags = DiagnosticsHierarchyState.ReportLocationsDefaultOpen
            ? ImGuiTreeNodeFlags.DefaultOpen
            : ImGuiTreeNodeFlags.None;
        if (ImGui.CollapsingHeader("Report locations##DiagnosticsReportLocations", reportLocationsFlags))
        {
            var craftAppraisal = getCraftAppraisalDiagnostics();
            DrawReportLocation("Diagnostics folder", diagnosticsDirectory);
            DrawReportLocation("Latest report", snapshot.LastDiagnosticFilePath);
            DrawReportLocation("Observed listings", snapshot.LastObservedListingsCsvPath);
            DrawReportLocation("Purchase records", snapshot.LastPurchaseRecordsCsvPath);
            DrawReportLocation("Craft quote printout", craftAppraisal.LastCraftQuoteDiagnosticFilePath);
            DrawReportLocation("UI-state capture", uiStateCapture.LastCapturePath);
        }
    }

    private void DrawTestTools(MarketAcquisitionRouteEngineSnapshot? snapshot)
    {
        ImGui.SetNextItemOpen(hierarchyState.TestToolsExpanded, ImGuiCond.Always);
        var expanded = ImGui.CollapsingHeader("Test tools##DiagnosticsTestTools");
        hierarchyState.SetTestToolsExpanded(expanded);
        reviewRegistry.RegisterLastItem(
            DiagnosticsHierarchyState.TestToolsControlId,
            hierarchyState.TestToolsActionLabel,
            AgentBridgeUiControlKind.Toggle,
            enabled: true,
            selected: hierarchyState.TestToolsExpanded,
            value: hierarchyState.TestToolsValue,
            invoke: hierarchyState.ToggleTestTools);

        if (!expanded)
            return;

        if (snapshot != null)
            DrawDryRunTools();
        DrawUiStateCapture();
    }

    private void DrawDryRunTools()
    {
        if (!areDryRunToolsEnabled())
            return;

        ImGui.Spacing();
        ImGuiUi.SectionHeader("Non-spending Route Dry Run", MarketMafiosoUiTheme.Header);
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Scenario");
        ImGui.SameLine();
        DrawDryRunScenario("Ordinary", ExactAcquisitionDryRunScenario.Ordinary);
        ImGui.SameLine();
        DrawDryRunScenario("Changed row", ExactAcquisitionDryRunScenario.ChangedListingRecovery);
        ImGui.SameLine();
        DrawDryRunScenario("No viable row", ExactAcquisitionDryRunScenario.NoViableRecovery);
        var enabled = canStartPreparedRouteDryRun();
        if (ImGuiUi.Button("Dry Run Prepared Route", enabled))
            startPreparedRouteDryRun();
        RegisterLastControl(
            "diagnostics.market-acquisition.dry-run-prepared",
            "Dry run the prepared Market Acquisition route without purchases",
            enabled,
            startPreparedRouteDryRun);
#if DEBUG
        var canSeed = canSeedExactAcquisitionDryRunSunkState();
        if (ImGuiUi.Button("Seed one persisted sunk purchase##Debug", canSeed))
            diagnosticsFolderStatus = seedExactAcquisitionDryRunSunkState();
        RegisterLastControl(
            "diagnostics.market-acquisition.debug-seed-sunk-purchase",
            "DEBUG: seed one exact persisted sunk purchase for restart dry-run proof",
            canSeed,
            () => diagnosticsFolderStatus = seedExactAcquisitionDryRunSunkState());
        if (!diagnosticsFolderStatus.StartsWith("Route diagnostics", StringComparison.Ordinal) &&
            !diagnosticsFolderStatus.StartsWith("Opened route diagnostics", StringComparison.Ordinal) &&
            !diagnosticsFolderStatus.StartsWith("Unable to open", StringComparison.Ordinal))
        {
            DrawWrappedColored(MarketMafiosoUiTheme.Muted, diagnosticsFolderStatus);
        }
#endif
    }

    private void DrawDryRunScenario(string label, ExactAcquisitionDryRunScenario scenario)
    {
        var selected = getExactAcquisitionDryRunScenario() == scenario;
        var enabled = !getRouteSnapshot().IsRouteActive;
        if (ImGui.RadioButton($"{label}##ExactAcquisitionDryRun{scenario}", selected) && enabled)
            armExactAcquisitionDryRunScenario(scenario);
        RegisterLastControl(
            $"diagnostics.market-acquisition.dry-run-scenario.{scenario.ToString().ToLowerInvariant()}",
            $"Use {label} External plan dry-run scenario",
            enabled,
            () => armExactAcquisitionDryRunScenario(scenario));
    }

    public void DrawLatestWorldCompletionSummary(MarketAcquisitionRouteEngineSnapshot snapshot)
    {
        var summary = snapshot.LatestWorldCompletionSummary;
        if (summary == null)
            return;

        ImGui.TextColored(
            MarketMafiosoUiTheme.Muted,
            $"Latest world: {summary.WorldName} ({FormatRouteDataCenter(summary.DataCenter)}) bought {summary.PurchasedQuantity:N0}, spent {FormatGil(summary.SpentGil)}; {summary.CompletedLineCount:N0} complete / {summary.SkippedLineCount:N0} skipped.");
    }

    public void DrawPostRunDiagnosticSummary(MarketAcquisitionRouteEngineSnapshot snapshot)
    {
        var runSummary = snapshot.LastRunSummary;
        if (runSummary != null)
        {
            var purchaseVerb = snapshot.ExecutionMode == MarketAcquisitionExecutionMode.DryRun
                ? "would purchase"
                : "purchased";
            var spendVerb = snapshot.ExecutionMode == MarketAcquisitionExecutionMode.DryRun
                ? "would spend"
                : "spent";
            ImGui.TextColored(
                runSummary.FailedWorldCount > 0 || runSummary.Warnings.Count > 0 ? MarketMafiosoUiTheme.Header : MarketMafiosoUiTheme.Success,
                $"Run rollup: {purchaseVerb} {runSummary.PurchasedQuantity:N0}, {spendVerb} {FormatGil(runSummary.SpentGil)}; {runSummary.CompletedWorldCount:N0} complete / {runSummary.PartialWorldCount:N0} partial / {runSummary.FailedWorldCount:N0} failed world(s).");

            if (runSummary.OpportunisticPurchasedQuantity > 0 || runSummary.PlannedPurchasedQuantity > 0)
            {
                ImGui.TextColored(
                    MarketMafiosoUiTheme.Muted,
                    $"Planned buys: {runSummary.PlannedPurchasedQuantity:N0} / {FormatGil(runSummary.PlannedSpentGil)}. Opportunistic buys: {runSummary.OpportunisticPurchasedQuantity:N0} / {FormatGil(runSummary.OpportunisticSpentGil)}.");
            }

            if (runSummary.TopItemsBySpentGil.Count > 0)
            {
                var topItems = string.Join(
                    "; ",
                    runSummary.TopItemsBySpentGil
                        .Take(3)
                        .Select(item => $"{item.ItemName} {item.PurchasedQuantity:N0} / {FormatGil(item.SpentGil)}"));
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"Top buys: {topItems}");
            }

            if (runSummary.Warnings.Count > 0)
            {
                ImGui.TextColored(
                    MarketMafiosoUiTheme.Error,
                    $"Post-run diagnostics: {runSummary.Warnings.Count:N0} warning(s). Open Diagnostics for details.");
            }

            return;
        }

        var summary = snapshot.LastRunDiagnosticSummary;
        if (summary.Warnings.Count > 0)
        {
            ImGui.TextColored(
                MarketMafiosoUiTheme.Error,
                $"Post-run diagnostics: {summary.Warnings.Count:N0} warning(s). Open Diagnostics for details.");
        }
    }

    private void DrawUiStateCapture()
    {
        ImGui.Spacing();
        ImGuiUi.SectionHeader("Catchall UI-State Recorder", MarketMafiosoUiTheme.Header);

        if (ImGuiUi.Button("Start UI Capture", !uiStateCapture.IsRecording))
            uiStateCapture.Start();
        RegisterLastControl("diagnostics.ui-capture.start", "Start catchall UI-state capture", !uiStateCapture.IsRecording, () => uiStateCapture.Start());

        ImGui.SameLine();
        if (ImGuiUi.Button("Add Marker", uiStateCapture.IsRecording))
            uiStateCapture.Mark("manual-marker");
        RegisterLastControl("diagnostics.ui-capture.marker", "Add a marker to the active UI-state capture", uiStateCapture.IsRecording, () => uiStateCapture.Mark("agent-marker"));

        ImGui.SameLine();
        if (ImGuiUi.Button("Finish UI Capture", uiStateCapture.IsRecording))
            uiStateCapture.Stop();
        RegisterLastControl("diagnostics.ui-capture.finish", "Finish and export catchall UI-state capture", uiStateCapture.IsRecording, () => uiStateCapture.Stop());

        ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"{uiStateCapture.Status} Events: {uiStateCapture.EventCount:N0}");
    }

    private static string BuildMarketAcquisitionHeader(MarketAcquisitionRouteEngineSnapshot snapshot)
    {
        var warningCount = GetCurrentWarnings(snapshot).Count;
        var status = snapshot.IsRouteActive
            ? snapshot.ExecutionMode == MarketAcquisitionExecutionMode.DryRun ? "dry run active" : "route active"
            : warningCount > 0 ? $"{warningCount:N0} warning(s)" : null;
        return status == null
            ? "Market Acquisition diagnostics##DiagnosticsMarketAcquisition"
            : $"Market Acquisition diagnostics - {status}##DiagnosticsMarketAcquisition";
    }

    private static string BuildCurrentRunSummary(MarketAcquisitionRouteEngineSnapshot snapshot)
    {
        if (snapshot.IsRouteActive)
        {
            var mode = snapshot.ExecutionMode == MarketAcquisitionExecutionMode.DryRun ? "Non-spending dry run" : "Route active";
            return $"{mode}; {snapshot.CompletedOrProbedStopCount:N0}/{snapshot.Stops.Count:N0} stops complete or probed.";
        }

        var summary = snapshot.LastRunSummary;
        if (summary == null)
            return string.IsNullOrWhiteSpace(snapshot.StatusMessage) ? "No route active." : snapshot.StatusMessage;

        var purchaseVerb = snapshot.ExecutionMode == MarketAcquisitionExecutionMode.DryRun ? "would purchase" : "purchased";
        var spendVerb = snapshot.ExecutionMode == MarketAcquisitionExecutionMode.DryRun ? "would spend" : "spent";
        return $"Last run {purchaseVerb} {summary.PurchasedQuantity:N0}, {spendVerb} {FormatGil(summary.SpentGil)}; {summary.CompletedWorldCount:N0} complete / {summary.PartialWorldCount:N0} partial / {summary.FailedWorldCount:N0} failed world(s).";
    }

    private static IReadOnlyList<string> GetCurrentWarnings(MarketAcquisitionRouteEngineSnapshot snapshot) =>
        (snapshot.LastRunSummary?.Warnings ?? snapshot.LastRunDiagnosticSummary.Warnings)
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string? GetActiveRouteException(MarketAcquisitionRouteEngineSnapshot snapshot)
    {
        if (!snapshot.IsRouteActive)
            return null;

        var operation = IsRouteException(snapshot.ActiveOperation)
            ? snapshot.ActiveOperation
            : IsRouteException(snapshot.LastOperation) ? snapshot.LastOperation : null;
        if (operation == null)
            return null;

        return string.IsNullOrWhiteSpace(operation.Message)
            ? $"{operation.Kind}: {operation.Disposition}"
            : operation.Message;
    }

    private static bool IsRouteException(MarketAcquisitionRouteOperationSnapshot? operation) =>
        operation is not null && operation.Disposition is not (
            MarketAcquisitionRouteOperationDisposition.Pending or
            MarketAcquisitionRouteOperationDisposition.Succeeded);

    private static string GetReportFileName(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "No report captured this session." : Path.GetFileName(path);

    private static void DrawReportLocation(string label, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        ImGui.TextColored(MarketMafiosoUiTheme.Muted, label);
        ImGui.SameLine();
        ImGui.TextWrapped(path);
    }

    private static void DrawWrappedColored(System.Numerics.Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    private void RegisterLastControl(string id, string label, bool enabled, Action invoke) =>
        reviewRegistry.RegisterLastButton(
            id,
            label,
            enabled,
            invoke,
            uiStateCapture.EventCount.ToString(CultureInfo.InvariantCulture));

    private void OpenDiagnosticsFolder(string folderPath)
    {
        try
        {
            Directory.CreateDirectory(folderPath);
            Process.Start(new ProcessStartInfo(folderPath)
            {
                UseShellExecute = true,
            });
            diagnosticsFolderStatus = "Opened route diagnostics folder.";
        }
        catch (Exception ex)
        {
            diagnosticsFolderStatus = $"Unable to open route diagnostics folder. {ex.Message}";
            log.Error(ex, "[MarketMafioso] Unable to open route diagnostics folder.");
        }
    }

    private static string FormatGil(uint gil) => $"{gil:N0} gil";

    private static string FormatRouteDataCenter(string dataCenter) =>
        string.IsNullOrWhiteSpace(dataCenter) ? "-" : dataCenter;

}
