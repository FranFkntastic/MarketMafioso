using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace MarketMafioso.Windows.Main;

internal sealed class InventoryReporterTabPanel
{
    private const string InventoryModuleSummary = "Inventory Reporter exports character and retainer inventory snapshots as JSON.";

    private readonly HttpReporter reporter;
    private readonly AutoRetainerRefreshService autoRetainerRefresh;

    private bool showPreview;

    public InventoryReporterTabPanel(
        HttpReporter reporter,
        AutoRetainerRefreshService autoRetainerRefresh)
    {
        this.reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        this.autoRetainerRefresh = autoRetainerRefresh ?? throw new ArgumentNullException(nameof(autoRetainerRefresh));
    }

    public void Draw()
    {
        ImGui.Spacing();
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Inventory Reporter");
        ImGui.TextWrapped(InventoryModuleSummary);
        ImGui.Spacing();

        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Capture scope and automatic sending are configured under Settings / Inventory Reporter.");
        ImGui.Spacing();
        DrawActionsSection();

        if (showPreview)
        {
            ImGui.Separator();
            DrawJsonPreview();
        }
    }

    private void DrawActionsSection()
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Inventory Reporter Actions");
        ImGui.Separator();

        var third = (ImGui.GetContentRegionAvail().X - 2 * ImGui.GetStyle().ItemSpacing.X) / 3f;

        if (ImGui.Button("Send Report Now", new Vector2(third, 0)))
            _ = reporter.SendReportAsync();

        ImGui.SameLine();

        var canRefreshRetainers = autoRetainerRefresh.CanStartRefresh &&
                                  !autoRetainerRefresh.IsRefreshing &&
                                  !autoRetainerRefresh.IsStartQueued;
        if (!canRefreshRetainers)
            ImGui.BeginDisabled();

        if (ImGui.Button("Refresh Retainer Cache", new Vector2(third, 0)))
            autoRetainerRefresh.StartFullRefresh();

        if (!canRefreshRetainers)
            ImGui.EndDisabled();

        ImGui.SameLine();

        var previewLabel = showPreview ? "Hide JSON Preview" : "Show JSON Preview";
        if (ImGui.Button(previewLabel, new Vector2(third, 0)))
            showPreview = !showPreview;

        ImGui.Spacing();
        ImGui.TextColored(GetRefreshStatusColor(), autoRetainerRefresh.LastStatus);
    }

    private Vector4 GetRefreshStatusColor()
    {
        if (autoRetainerRefresh.IsRefreshing)
            return MarketMafiosoUiTheme.Header;

        if (autoRetainerRefresh.LastStatus.Contains("complete", StringComparison.OrdinalIgnoreCase))
            return MarketMafiosoUiTheme.Success;

        if (autoRetainerRefresh.LastStatus.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            autoRetainerRefresh.LastStatus.Contains("unable", StringComparison.OrdinalIgnoreCase) ||
            autoRetainerRefresh.LastStatus.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return MarketMafiosoUiTheme.Error;

        return MarketMafiosoUiTheme.Muted;
    }

    private void DrawJsonPreview()
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "JSON Preview (last payload)");
        ImGui.Separator();

        var json = reporter.LastPayload ?? "(No payload yet - press 'Send Report Now' first)";
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextMultiline(
            "##jsonPreview",
            ref json,
            Math.Max(json.Length + 1, 8192),
            new Vector2(-1, 240),
            ImGuiInputTextFlags.ReadOnly,
            (ImGui.ImGuiInputTextCallbackDelegate?)null);
    }

}
