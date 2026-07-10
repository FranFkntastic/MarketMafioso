using System;
using Dalamud.Bindings.ImGui;

namespace MarketMafioso.Windows.Main;

internal sealed class OverviewTabPanel
{
    private const string InventoryModuleSummary = "Inventory Reporter exports character and retainer inventory snapshots as JSON.";
    private const string WorkshopLogisticsModuleSummary = "Workshop Logistics tracks company workshop jobs, materials, retainer restock, handoff, and assembly.";
    private const string MarketAcquisitionModuleSummary = "Build, sync, and monitor acquisition requests from one persistent board.";

    private readonly Func<bool> isMarketAcquisitionUnlocked;

    public OverviewTabPanel(Func<bool> isMarketAcquisitionUnlocked)
    {
        this.isMarketAcquisitionUnlocked = isMarketAcquisitionUnlocked ?? throw new ArgumentNullException(nameof(isMarketAcquisitionUnlocked));
    }

    public void Draw()
    {
        ImGui.Spacing();
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Modules");
        ImGui.Separator();

        DrawModuleSummary("Inventory Reporter", "Enabled", InventoryModuleSummary);
        DrawModuleSummary("Workshop Logistics", "Enabled", WorkshopLogisticsModuleSummary);
        if (isMarketAcquisitionUnlocked())
            DrawModuleSummary("Market Acquisition", "Internal", MarketAcquisitionModuleSummary);
        DrawModuleSummary("General Improvements", "Planned", "Small quality-of-life tools that are useful, but too narrow for their own plugin.");
    }

    private static void DrawModuleSummary(string name, string state, string description)
    {
        ImGui.BulletText(name);
        ImGui.SameLine();
        ImGui.TextColored(state == "Enabled" ? MarketMafiosoUiTheme.Success : MarketMafiosoUiTheme.Muted, $"({state})");
        ImGui.TextWrapped(description);
        ImGui.Spacing();
    }
}
