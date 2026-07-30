using System;
using Dalamud.Bindings.ImGui;

namespace MarketMafioso.Windows.Main;

internal sealed class StatusTabPanel
{
    private readonly Configuration config;
    private readonly HttpReporter reporter;

    public StatusTabPanel(Configuration config, HttpReporter reporter)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
    }

    public void Draw()
    {
        ImGui.Spacing();
        DrawStatusSection();
    }

    private void DrawStatusSection()
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Module Status");
        ImGui.Separator();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"Build: {PluginBuildInfo.DisplayVersion}");
        ImGui.TextColored(
            reporter.LastRetainerSourceStatus.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
                ? MarketMafiosoUiTheme.Error
                : MarketMafiosoUiTheme.Muted,
            $"Retainer source: {reporter.LastRetainerSourceStatus}");
        ImGui.Spacing();

        if (reporter.LastSentAt.HasValue)
        {
            var statusOk = reporter.LastStatus.StartsWith("2");
            ImGui.TextColored(
                statusOk ? MarketMafiosoUiTheme.Success : MarketMafiosoUiTheme.Error,
                $"Last sent: {reporter.LastSentAt:HH:mm:ss}  -  Status: {reporter.LastStatus}");
        }
        else
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"Status: {reporter.LastStatus}");
        }

        if (!MarketAcquisition.MarketAcquisitionUnlock.IsUnlocked(config) ||
            !config.EnableRetainerListingRefresh)
        {
            return;
        }

        ImGui.Spacing();
        var refresh = config.RetainerListingRefresh;
        ImGui.TextColored(
            refresh.NeedsAttention ? MarketMafiosoUiTheme.Error : MarketMafiosoUiTheme.Muted,
            $"Retainer listing refresh: {refresh.StatusMessage}");
    }
}
