using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Windows.Main;

namespace MarketMafioso.Windows.MarketAcquisitionPanels;

internal sealed class MarketAcquisitionDiagnosticsPanel
{
    private readonly MarketAcquisitionRouteRunner routeRunner;
    private readonly string diagnosticsDirectory;
    private readonly IPluginLog log;
    private readonly Action openMarketAcquisitionDiagnostics;
    private readonly Action openAutomationDiagnostics;
    private readonly Action captureMarketBoardInputState;
    private readonly Action finalizeMarketBoardInputCaptureLog;

    private string diagnosticsFolderStatus = "Route diagnostics folder opens in Explorer.";

    public MarketAcquisitionDiagnosticsPanel(
        MarketAcquisitionRouteRunner routeRunner,
        string diagnosticsDirectory,
        IPluginLog log,
        Action openMarketAcquisitionDiagnostics,
        Action openAutomationDiagnostics,
        Action captureMarketBoardInputState,
        Action finalizeMarketBoardInputCaptureLog)
    {
        this.routeRunner = routeRunner ?? throw new ArgumentNullException(nameof(routeRunner));
        this.diagnosticsDirectory = diagnosticsDirectory ?? throw new ArgumentNullException(nameof(diagnosticsDirectory));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.openMarketAcquisitionDiagnostics = openMarketAcquisitionDiagnostics ?? throw new ArgumentNullException(nameof(openMarketAcquisitionDiagnostics));
        this.openAutomationDiagnostics = openAutomationDiagnostics ?? throw new ArgumentNullException(nameof(openAutomationDiagnostics));
        this.captureMarketBoardInputState = captureMarketBoardInputState ?? throw new ArgumentNullException(nameof(captureMarketBoardInputState));
        this.finalizeMarketBoardInputCaptureLog = finalizeMarketBoardInputCaptureLog ?? throw new ArgumentNullException(nameof(finalizeMarketBoardInputCaptureLog));
    }

    public void Draw()
    {
        ImGui.Spacing();
        ImGuiUi.SectionHeader("Diagnostics", MarketMafiosoUiTheme.Header);

        if (ImGuiUi.Button("Open Route Diagnostics Folder", true))
            OpenDiagnosticsFolder(diagnosticsDirectory);

        ImGui.SameLine();
        if (ImGuiUi.Button("Market Acquisition Diagnostics", true))
            openMarketAcquisitionDiagnostics();

        ImGui.SameLine();
        if (ImGuiUi.Button("Automation Diagnostics", true))
            openAutomationDiagnostics();

        ImGui.TextColored(GetDiagnosticsFolderStatusColor(), diagnosticsFolderStatus);
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, diagnosticsDirectory);

        if (routeRunner.LastDiagnosticFilePath != null)
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"Latest report: {routeRunner.LastDiagnosticFilePath}");

        DrawPostRunDiagnosticSummary();
        DrawMarketBoardInputCapture();
    }

    public void DrawLatestWorldCompletionSummary()
    {
        var summary = routeRunner.LatestWorldCompletionSummary;
        if (summary == null)
            return;

        ImGui.TextColored(
            MarketMafiosoUiTheme.Muted,
            $"Latest world: {summary.WorldName} ({FormatRouteDataCenter(summary.DataCenter)}) bought {summary.PurchasedQuantity:N0}, spent {FormatGil(summary.SpentGil)}; {summary.CompletedLineCount:N0} complete / {summary.SkippedLineCount:N0} skipped.");
    }

    public void DrawPostRunDiagnosticSummary()
    {
        var runSummary = routeRunner.LastRunSummary;
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

        var summary = routeRunner.LastRunDiagnosticSummary;
        if (summary.Warnings.Count > 0)
        {
            ImGui.TextColored(
                MarketMafiosoUiTheme.Error,
                $"Post-run diagnostics: {summary.Warnings.Count:N0} warning(s). Open Diagnostics for details.");
        }
    }

    private void DrawMarketBoardInputCapture()
    {
        ImGui.Spacing();
        ImGuiUi.SectionHeader("Input Capture", MarketMafiosoUiTheme.Header);
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Capture current market-board UI/input state before and after manual purchase clicks or pagination attempts.");

        if (ImGuiUi.Button("Capture Input State", true))
            captureMarketBoardInputState();

        ImGui.SameLine();
        if (ImGuiUi.Button("Finish Capture Log", routeRunner.CanFinalizeInputCaptureLog))
            finalizeMarketBoardInputCaptureLog();

        if (routeRunner.LastDiagnosticFilePath != null)
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"Capture log: {routeRunner.LastDiagnosticFilePath}");
    }

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
