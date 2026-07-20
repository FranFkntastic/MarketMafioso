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

        var refreshActive = autoRetainerRefresh.IsRefreshing || autoRetainerRefresh.IsStartQueued;
        var transferActive = workshopRetainerRestock.IsRunning;
        var canRefreshRetainers = !transferActive &&
                                  !refreshActive &&
                                  autoRetainerRefresh.CanStartRefresh;
        var canRun = !transferActive &&
                     !refreshActive &&
                     plan.Lines.Any(line => line.NeededQuantity > 0 && line.Candidates.Count > 0);
        var canDeposit = !transferActive && !refreshActive && depositPlan.CanRun;

        if (ImGuiUi.PrimaryButton("Quick deposit", canDeposit))
            _ = workshopRetainerRestock.StartElementalDepositAsync(depositPlan);

        ImGui.SameLine();
        if (ImGuiUi.PrimaryButton("Run withdrawal plan", canRun))
            _ = workshopRetainerRestock.StartRestockAsync(plan.Lines);

        if (canRefreshRetainers)
        {
            ImGui.SameLine();
            if (ImGuiUi.Button("Refresh retainers", true))
                autoRetainerRefresh.StartFullRefresh();
        }

        if (refreshActive)
            ImGui.TextColored(MarketMafiosoUiTheme.Header, $"Refresh activity: {autoRetainerRefresh.LastStatus}");
        if (transferActive)
            ImGui.TextColored(MarketMafiosoUiTheme.Header, $"Transfer activity: {workshopRetainerRestock.LastStatus}");
        if (!refreshActive && !transferActive && !string.IsNullOrWhiteSpace(workshopRetainerRestock.LastStatus))
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, workshopRetainerRestock.LastStatus);

        if (!ownerScope.IsAvailable)
            ImGui.TextColored(MarketMafiosoUiTheme.Error, "Current character and home world are unavailable; retainer transfers are unavailable.");
    }
}
