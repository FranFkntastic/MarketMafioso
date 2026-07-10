using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace MarketMafioso.Windows.Main;

internal sealed class InventoryReporterTabPanel
{
    private const string InventoryModuleSummary = "Inventory Reporter exports character and retainer inventory snapshots as JSON.";

    private readonly Configuration config;
    private readonly HttpReporter reporter;
    private readonly AutoRetainerRefreshService autoRetainerRefresh;
    private readonly Action restartTimer;
    private readonly Action saveConfig;

    private bool showPreview;

    public InventoryReporterTabPanel(
        Configuration config,
        HttpReporter reporter,
        AutoRetainerRefreshService autoRetainerRefresh,
        Action restartTimer,
        Action saveConfig)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        this.autoRetainerRefresh = autoRetainerRefresh ?? throw new ArgumentNullException(nameof(autoRetainerRefresh));
        this.restartTimer = restartTimer ?? throw new ArgumentNullException(nameof(restartTimer));
        this.saveConfig = saveConfig ?? throw new ArgumentNullException(nameof(saveConfig));
    }

    public void Draw()
    {
        ImGui.Spacing();
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Inventory Reporter");
        ImGui.TextWrapped(InventoryModuleSummary);
        ImGui.Spacing();

        DrawInventoryOptionsSection();
        ImGui.Spacing();
        DrawBehaviourSection();
        ImGui.Spacing();
        DrawActionsSection();

        if (showPreview)
        {
            ImGui.Separator();
            DrawJsonPreview();
        }
    }

    private void DrawInventoryOptionsSection()
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Included Data");
        ImGui.Separator();

        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Player inventory (4 bags) is always included.");
        ImGui.Spacing();

        DrawCheckbox("Armoury Chest", v => config.IncludeArmoury = v, config.IncludeArmoury);
        DrawCheckbox("Crystal bag", v => config.IncludeCrystals = v, config.IncludeCrystals);
        DrawCheckbox("Equipped gear", v => config.IncludeEquipped = v, config.IncludeEquipped);
        DrawCheckbox("Saddlebag (if subscribed)", v => config.IncludeSaddlebag = v, config.IncludeSaddlebag);
        ImGui.Spacing();
        DrawCheckbox("Resolve item names via Lumina", v => config.IncludeItemNames = v, config.IncludeItemNames);
        DrawCheckbox("Include character name & world", v => config.IncludeCharacterInfo = v, config.IncludeCharacterInfo);
    }

    private void DrawBehaviourSection()
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Automation");
        ImGui.Separator();

        DrawCheckbox("Auto-send on retainer window close", v => config.AutoSendOnRetainerClose = v, config.AutoSendOnRetainerClose);
        ImGui.TextColored(MarketMafiosoUiTheme.Muted,
            "  Retainer data is cached each time you close a retainer window.\n" +
            "  Visit each retainer once per session to populate the cache.");

        ImGui.Spacing();

        DrawCheckbox("Enable automatic periodic sending", v =>
        {
            config.EnableAutoSendTimer = v;
            restartTimer();
        }, config.EnableAutoSendTimer);

        if (config.EnableAutoSendTimer)
        {
            var interval = config.AutoSendIntervalMinutes;
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputInt("Send Interval (minutes)##interval", ref interval, 1, 5))
            {
                if (interval < 1) interval = 1;
                if (interval != config.AutoSendIntervalMinutes)
                {
                    config.AutoSendIntervalMinutes = interval;
                    saveConfig();
                    restartTimer();
                }
            }
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

    private void DrawCheckbox(string label, Action<bool> setter, bool currentValue)
    {
        var v = currentValue;
        if (ImGui.Checkbox(label, ref v))
        {
            setter(v);
            saveConfig();
        }
    }
}
