using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Settings;

namespace MarketMafioso.Windows.Main.Settings;

internal sealed class InventoryReporterActionsSettingsPage
{
    private readonly HttpReporter reporter;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private bool showPreview;

    public InventoryReporterActionsSettingsPage(
        HttpReporter reporter,
        AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        this.reviewRegistry = reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry));
    }

    public SettingsPageDescriptor Descriptor => new(
        "inventory.actions",
        "Inventory Reporter / Actions and Status",
        Draw,
        9,
        searchTerms: ["send report", "Quartermaster", "last payload", "JSON preview", "report status"]);

    private void Draw(SettingsPageContext context)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, MarketMafiosoUiTheme.Muted);
        ImGui.TextWrapped("Send an inventory snapshot or inspect the last payload. Retainer inventory comes from Quartermaster when available.");
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
        ImGui.TableSetupColumn("Retainer source", ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableHeadersRow();
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(reporter.LastSentAt is { } sentAt ? sentAt.ToString("g") : "Never sent");

        ImGui.TableNextColumn();
        ImGui.TextColored(GetReporterStatusColor(), reporter.LastStatus);

        ImGui.TableNextColumn();
        ImGui.TextColored(GetRetainerSourceStatusColor(), reporter.LastRetainerSourceStatus);

        ImGui.EndTable();
    }

    private void DrawActions()
    {
        if (ImGuiUi.PrimaryButton("Send report now", true))
            _ = reporter.SendReportAsync();
        reviewRegistry.RegisterLastButton(
            "inventory.report.send",
            "Send an inventory report now",
            true,
            () => _ = reporter.SendReportAsync(),
            reporter.LastStatus);

        ImGui.SameLine();
        var previewLabel = showPreview ? "Hide payload" : "Preview payload";
        if (ImGui.Button(previewLabel))
            showPreview = !showPreview;
        reviewRegistry.RegisterLastButton(
            "inventory.report.toggle-preview",
            previewLabel,
            true,
            () => showPreview = !showPreview,
            showPreview ? "Visible" : "Hidden");
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

    private Vector4 GetRetainerSourceStatusColor()
    {
        if (reporter.LastRetainerSourceStatus.Contains("revision", StringComparison.OrdinalIgnoreCase))
            return MarketMafiosoUiTheme.Success;
        if (reporter.LastRetainerSourceStatus.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
            reporter.LastRetainerSourceStatus.Contains("omitted", StringComparison.OrdinalIgnoreCase))
            return MarketMafiosoUiTheme.Error;
        return MarketMafiosoUiTheme.Muted;
    }
}
