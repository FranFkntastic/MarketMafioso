using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using MarketMafioso.Diagnostics;
using Franthropy.Dalamud.AgentBridge;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Windows.Main;

namespace MarketMafioso.Windows.MarketAcquisitionPanels;

internal sealed class MarketAcquisitionDiagnosticsPanel
{
    private readonly Func<MarketAcquisitionRouteEngineSnapshot> getRouteSnapshot;
    private readonly string diagnosticsDirectory;
    private readonly IPluginLog log;
    private readonly Action drawMarketAcquisitionDiagnostics;
    private readonly Action drawAutomationDiagnostics;
    private readonly Action drawSquireDiagnostics;
    private readonly Func<bool> isMarketAcquisitionUnlocked;
    private readonly UiStateCaptureService uiStateCapture;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;

    private string diagnosticsFolderStatus = "Route diagnostics folder opens in Explorer.";

    public MarketAcquisitionDiagnosticsPanel(
        Func<MarketAcquisitionRouteEngineSnapshot> getRouteSnapshot,
        string diagnosticsDirectory,
        IPluginLog log,
        Action drawMarketAcquisitionDiagnostics,
        Action drawAutomationDiagnostics,
        Action drawSquireDiagnostics,
        Func<bool> isMarketAcquisitionUnlocked,
        UiStateCaptureService uiStateCapture,
        AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.getRouteSnapshot = getRouteSnapshot ?? throw new ArgumentNullException(nameof(getRouteSnapshot));
        this.diagnosticsDirectory = diagnosticsDirectory ?? throw new ArgumentNullException(nameof(diagnosticsDirectory));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.drawMarketAcquisitionDiagnostics = drawMarketAcquisitionDiagnostics ?? throw new ArgumentNullException(nameof(drawMarketAcquisitionDiagnostics));
        this.drawAutomationDiagnostics = drawAutomationDiagnostics ?? throw new ArgumentNullException(nameof(drawAutomationDiagnostics));
        this.drawSquireDiagnostics = drawSquireDiagnostics ?? throw new ArgumentNullException(nameof(drawSquireDiagnostics));
        this.isMarketAcquisitionUnlocked = isMarketAcquisitionUnlocked ?? throw new ArgumentNullException(nameof(isMarketAcquisitionUnlocked));
        this.uiStateCapture = uiStateCapture ?? throw new ArgumentNullException(nameof(uiStateCapture));
        this.reviewRegistry = reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry));
    }

    public void Draw()
    {
        ImGui.Spacing();
        ImGuiUi.SectionHeader("Diagnostics", MarketMafiosoUiTheme.Header);

        if (isMarketAcquisitionUnlocked())
        {
            var snapshot = getRouteSnapshot();
            if (ImGuiUi.Button("Open Route Diagnostics Folder", true))
                OpenDiagnosticsFolder(diagnosticsDirectory);

            ImGui.TextColored(GetDiagnosticsFolderStatusColor(), diagnosticsFolderStatus);
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, diagnosticsDirectory);

            if (snapshot.LastDiagnosticFilePath != null)
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"Latest report: {snapshot.LastDiagnosticFilePath}");

            DrawPostRunDiagnosticSummary(snapshot);
        }
        DrawUiStateCapture();

        if (isMarketAcquisitionUnlocked() && ImGui.CollapsingHeader("Market Acquisition Diagnostics", ImGuiTreeNodeFlags.DefaultOpen))
            drawMarketAcquisitionDiagnostics();
        if (ImGui.CollapsingHeader("Automation Diagnostics", ImGuiTreeNodeFlags.DefaultOpen))
            drawAutomationDiagnostics();
        if (ImGui.CollapsingHeader("Squire Route Diagnostics", ImGuiTreeNodeFlags.DefaultOpen))
            drawSquireDiagnostics();
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
            ImGui.TextColored(
                runSummary.FailedWorldCount > 0 || runSummary.Warnings.Count > 0 ? MarketMafiosoUiTheme.Header : MarketMafiosoUiTheme.Success,
                $"Run rollup: purchased {runSummary.PurchasedQuantity:N0}, spent {FormatGil(runSummary.SpentGil)}; {runSummary.CompletedWorldCount:N0} complete / {runSummary.PartialWorldCount:N0} partial / {runSummary.FailedWorldCount:N0} failed world(s).");

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
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Event-driven capture of addon lifecycle, receive events, focus, active agents, conditions, animation lock, and state diffs. Text payloads are redacted.");

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
        if (uiStateCapture.LastCapturePath != null)
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, uiStateCapture.LastCapturePath);
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

    private static System.Numerics.Vector4 GetDiagnosticsFolderStatusColor(string status) =>
        status.StartsWith("Unable", StringComparison.OrdinalIgnoreCase)
            ? MarketMafiosoUiTheme.Error
            : MarketMafiosoUiTheme.Muted;

    private System.Numerics.Vector4 GetDiagnosticsFolderStatusColor() =>
        GetDiagnosticsFolderStatusColor(diagnosticsFolderStatus);
}
