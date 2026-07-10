using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace MarketMafioso.Windows.Main;

internal sealed class StatusTabPanel
{
    private readonly Configuration config;
    private readonly HttpReporter reporter;
    private readonly RetainerCacheFileStore? retainerCacheStore;
    private readonly IPluginLog log;

    public StatusTabPanel(
        Configuration config,
        HttpReporter reporter,
        RetainerCacheFileStore? retainerCacheStore,
        IPluginLog log)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        this.retainerCacheStore = retainerCacheStore;
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public void Draw()
    {
        ImGui.Spacing();
        DrawStatusSection();
        ImGui.Spacing();
        DrawRetainerCacheSection();
    }

    private void DrawRetainerCacheSection()
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Retainer Cache");
        ImGui.Separator();

        if (config.RetainerCache.Count == 0)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "No retainers cached. Open a retainer inventory to populate.");
        }
        else
        {
            foreach (var (_, cached) in config.RetainerCache)
            {
                var total = cached.Bags.Sum(b => b.Items.Count);
                ImGui.BulletText(
                    $"{cached.RetainerName}  -  {total} items  (last seen {cached.LastUpdated:HH:mm:ss UTC})");
            }

            ImGui.Spacing();
            if (ImGui.Button("Clear Retainer Cache"))
            {
                config.RetainerCache.Clear();
                try
                {
                    retainerCacheStore?.Save(config.RetainerCache);
                }
                catch (Exception ex)
                {
                    log.Error(ex, "[MarketMafioso] Error saving cleared retainer inventory cache");
                }
            }
        }
    }

    private void DrawStatusSection()
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Module Status");
        ImGui.Separator();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"Build: {PluginBuildInfo.DisplayVersion}");
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
    }
}
