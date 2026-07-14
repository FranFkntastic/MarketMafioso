using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using MarketMafioso.RetainerRestock;
using MarketMafioso.Windows.Main;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Windows.RetainerRestock;

public sealed class RetainerRestockControlsPanel
{
    private readonly AutoRetainerRefreshService autoRetainerRefresh;
    private readonly WorkshopRetainerRestockService workshopRetainerRestock;

    public RetainerRestockControlsPanel(
        Configuration config,
        AutoRetainerRefreshService autoRetainerRefresh,
        WorkshopRetainerRestockService workshopRetainerRestock)
    {
        ArgumentNullException.ThrowIfNull(config);
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

        if (ImGuiUi.Button("Refresh retainer cache", canRefreshRetainers))
            autoRetainerRefresh.StartFullRefresh();

        ImGui.SameLine();
        if (ImGuiUi.PrimaryButton("Run restock plan", canRun))
            _ = workshopRetainerRestock.StartRestockAsync(plan.Lines);

        ImGui.SameLine();
        ImGui.TextColored(
            workshopRetainerRestock.IsRunning ? MarketMafiosoUiTheme.Header : MarketMafiosoUiTheme.Muted,
            workshopRetainerRestock.LastStatus);

        if (!ownerScope.IsAvailable)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Error, "Current character and home world are unavailable; retainer restock cannot use cached retainers.");
            return;
        }
        if (!canRun && !workshopRetainerRestock.IsRunning)
            ImGui.TextColored(
                MarketMafiosoUiTheme.Muted,
                plan.Lines.Any(line => line.NeededQuantity > 0)
                    ? "The plan has no retrievable cached stock yet. Refresh the cache or adjust the plan."
                    : "Add a plan row whose desired quantity exceeds current player stock.");
    }
}
