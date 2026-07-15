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

    public void Draw(RetainerRestockPlan plan, ElementalDepositPlan depositPlan, RetainerOwnerScope ownerScope)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var canRefreshRetainers = autoRetainerRefresh.CanStartRefresh &&
                                  !autoRetainerRefresh.IsRefreshing &&
                                  !autoRetainerRefresh.IsStartQueued;
        var canRun = !workshopRetainerRestock.IsRunning &&
                     plan.Lines.Any(line => line.NeededQuantity > 0 && line.Candidates.Count > 0);
        var canDeposit = !workshopRetainerRestock.IsRunning && depositPlan.CanRun;

        if (ImGuiUi.Button("Refresh retainer cache", canRefreshRetainers))
            autoRetainerRefresh.StartFullRefresh();

        ImGui.SameLine();
        if (ImGuiUi.PrimaryButton("Quick deposit", canDeposit))
            _ = workshopRetainerRestock.StartElementalDepositAsync(depositPlan);

        ImGui.SameLine();
        if (ImGuiUi.Button("Run withdrawal plan", canRun))
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
    }
}
