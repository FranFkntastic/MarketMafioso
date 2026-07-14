using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.UI.Settings;

namespace MarketMafioso.Windows.Main.Settings;

internal sealed class InventoryReporterActionsSettingsPage
{
    private readonly HttpReporter reporter;
    private readonly AutoRetainerRefreshService autoRetainerRefresh;
    private bool showPreview;

    public InventoryReporterActionsSettingsPage(
        HttpReporter reporter,
        AutoRetainerRefreshService autoRetainerRefresh)
    {
        this.reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        this.autoRetainerRefresh = autoRetainerRefresh ?? throw new ArgumentNullException(nameof(autoRetainerRefresh));
    }

    public SettingsPageDescriptor Descriptor => new(
        "inventory.actions",
        "Inventory Reporter / Actions and Status",
        Draw,
        9,
        searchTerms: ["send report", "refresh retainer cache", "last payload", "JSON preview", "report status"]);

    private void Draw(SettingsPageContext context)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, MarketMafiosoUiTheme.Muted);
        ImGui.TextWrapped("Send an inventory snapshot, refresh cached retainers, or inspect the last payload. Capture scope and scheduling have their own pages.");
        ImGui.PopStyleColor();
        ImGui.Spacing();

        DrawStatusSummary();
        ImGui.Spacing();
        DrawActions();

        if (!showPreview)
            return;

        ImGui.Spacing();
        ImGuiUi.SectionHeader("Last payload", MarketMafiosoUiTheme.Header);
        var json = reporter.LastPayload ?? "(No payload yet. Send a report to create one.)";
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextMultiline(
            "##inventoryReporterJsonPreview",
            ref json,
            Math.Max(json.Length + 1, 8192),
            new Vector2(-1, Math.Max(180, ImGui.GetContentRegionAvail().Y)),
            ImGuiInputTextFlags.ReadOnly,
            (ImGui.ImGuiInputTextCallbackDelegate?)null);
    }

    private void DrawStatusSummary()
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##inventoryReporterStatus", 3, flags))
            return;

        ImGui.TableSetupColumn("Last report", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn("Receiver status", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Retainer cache", ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableHeadersRow();
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(reporter.LastSentAt is { } sentAt ? sentAt.ToString("g") : "Never sent");

        ImGui.TableNextColumn();
        ImGui.TextColored(GetReporterStatusColor(), reporter.LastStatus);

        ImGui.TableNextColumn();
        ImGui.TextColored(GetRefreshStatusColor(), autoRetainerRefresh.LastStatus);

        ImGui.EndTable();
    }

    private void DrawActions()
    {
        if (ImGuiUi.PrimaryButton("Send report now", true))
            _ = reporter.SendReportAsync();

        ImGui.SameLine();
        var canRefreshRetainers = autoRetainerRefresh.CanStartRefresh &&
                                  !autoRetainerRefresh.IsRefreshing &&
                                  !autoRetainerRefresh.IsStartQueued;
        if (ImGuiUi.Button("Refresh retainer cache", canRefreshRetainers))
            autoRetainerRefresh.StartFullRefresh();

        ImGui.SameLine();
        var previewLabel = showPreview ? "Hide payload" : "Preview payload";
        if (ImGui.Button(previewLabel))
            showPreview = !showPreview;
    }

    private Vector4 GetReporterStatusColor()
    {
        if (reporter.LastStatus.StartsWith("2", StringComparison.Ordinal))
            return MarketMafiosoUiTheme.Success;
        if (reporter.LastStatus.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            reporter.LastStatus.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
            reporter.LastStatus.Contains("required", StringComparison.OrdinalIgnoreCase))
            return MarketMafiosoUiTheme.Error;
        return MarketMafiosoUiTheme.Muted;
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
}
