using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using MarketMafioso.RetainerRestock;
using MarketMafioso.Windows.Main;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Windows.RetainerRestock;

public sealed class RetainerRestockControlsPanel
{
    private readonly Configuration config;
    private readonly AutoRetainerRefreshService autoRetainerRefresh;
    private readonly WorkshopRetainerRestockService workshopRetainerRestock;

    public RetainerRestockControlsPanel(
        Configuration config,
        AutoRetainerRefreshService autoRetainerRefresh,
        WorkshopRetainerRestockService workshopRetainerRestock)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.autoRetainerRefresh = autoRetainerRefresh ?? throw new ArgumentNullException(nameof(autoRetainerRefresh));
        this.workshopRetainerRestock = workshopRetainerRestock ?? throw new ArgumentNullException(nameof(workshopRetainerRestock));
    }

    public void Draw(RetainerRestockPlan plan, RetainerOwnerScope ownerScope)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var canRefreshRetainers = autoRetainerRefresh.CanStartRefresh &&
                                  !autoRetainerRefresh.IsRefreshing &&
                                  !autoRetainerRefresh.IsStartQueued;
        var canRun = !workshopRetainerRestock.IsRunning &&
                     plan.Lines.Any(line => line.NeededQuantity > 0 && line.Candidates.Count > 0);

        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Run");
        ImGui.Separator();
        if (ImGuiUi.Button("Refresh Retainer Cache", canRefreshRetainers))
            autoRetainerRefresh.StartFullRefresh();

        ImGui.SameLine();
        if (ImGuiUi.Button("Restock From Retainers", canRun))
            _ = workshopRetainerRestock.StartRestockAsync(plan.Lines);

        ImGui.Spacing();
        ImGui.TextColored(workshopRetainerRestock.IsRunning ? MarketMafiosoUiTheme.Header : MarketMafiosoUiTheme.Muted, workshopRetainerRestock.LastStatus);

        if (!ownerScope.IsAvailable)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Error, "Current character and home world are unavailable; retainer restock cannot use cached retainers.");
            return;
        }

        var scopedRetainers = config.RetainerCache.Values
            .Where(retainer => ownerScope.Matches(retainer.OwnerCharacterName, retainer.OwnerHomeWorld))
            .ToList();

        if (scopedRetainers.Count == 0)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"No retainers cached for {ownerScope.CharacterName} @ {ownerScope.HomeWorld} yet.");
            return;
        }

        var newest = scopedRetainers.Max(x => x.LastUpdated);
        var oldest = scopedRetainers.Min(x => x.LastUpdated);
        ImGui.TextColored(
            MarketMafiosoUiTheme.Muted,
            $"Cached retainers for {ownerScope.CharacterName} @ {ownerScope.HomeWorld}: {scopedRetainers.Count}; newest {newest:HH:mm:ss UTC}; oldest {oldest:HH:mm:ss UTC}.");
    }
}
