using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using MarketMafioso.Windows.Main;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Windows.WorkshopLogistics;

internal sealed class WorkshopMaterialPanel
{
    private readonly AutoRetainerRefreshService autoRetainerRefresh;
    private readonly WorkshopRetainerRestockService workshopRetainerRestock;
    private readonly Func<IReadOnlyList<WorkshopMaterialAvailability>> getAvailability;
    private string searchText = string.Empty;
    private bool shortagesOnly;

    public WorkshopMaterialPanel(
        AutoRetainerRefreshService autoRetainerRefresh,
        WorkshopRetainerRestockService workshopRetainerRestock,
        Func<IReadOnlyList<WorkshopMaterialAvailability>> getAvailability)
    {
        this.autoRetainerRefresh = autoRetainerRefresh ?? throw new ArgumentNullException(nameof(autoRetainerRefresh));
        this.workshopRetainerRestock = workshopRetainerRestock ?? throw new ArgumentNullException(nameof(workshopRetainerRestock));
        this.getAvailability = getAvailability ?? throw new ArgumentNullException(nameof(getAvailability));
    }

    public void Draw(IReadOnlyList<WorkshopMaterialAvailability>? availability = null)
    {
        ImGuiUi.SectionHeaderWithActions("Materials", MarketMafiosoUiTheme.Header, DrawHeaderActions, 332);

        availability ??= getAvailability();
        ImGui.SetNextItemWidth(Math.Max(220f, ImGui.GetContentRegionAvail().X - 260f));
        ImGui.InputTextWithHint("##workshopMaterialSearch", "Filter materials...", ref searchText, 128);
        ImGui.SameLine();
        ImGui.Checkbox("Shortages only", ref shortagesOnly);
        var filtered = availability
            .Where(item => (!shortagesOnly || item.Shortage > 0) &&
                           (string.IsNullOrWhiteSpace(searchText) || item.ItemName.Contains(searchText.Trim(), StringComparison.OrdinalIgnoreCase)))
            .ToList();
        ImGui.SameLine();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"{filtered.Count:N0} / {availability.Count:N0}");

        if (ImGui.BeginTable("WorkshopPrepMaterials", 7, ImGuiUi.InteractiveTableFlags))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Required", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Stock Differential", ImGuiTableColumnFlags.WidthFixed, 128);
            ImGui.TableSetupColumn("Inventory Missing", ImGuiTableColumnFlags.WidthFixed, 128);
            ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthFixed, 72);
            ImGui.TableSetupColumn("Retainers", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultHide, 88);
            ImGui.TableSetupColumn("Candidates", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultHide);
            ImGui.TableHeadersRow();

            if (filtered.Count == 0)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(
                    MarketMafiosoUiTheme.Muted,
                    availability.Count == 0
                        ? "No workshop materials yet. Add projects to the prep queue."
                        : "No materials match the current filter.");
                ImGui.TableNextColumn();
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, "-");
                ImGui.TableNextColumn();
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, "-");
                ImGui.TableNextColumn();
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, "-");
                ImGui.TableNextColumn();
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, "-");
                ImGui.TableNextColumn();
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, "-");
                ImGui.TableNextColumn();
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, "-");
            }

            foreach (var item in filtered)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.ItemName);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.Required.ToString());
                ImGui.TableNextColumn();
                ImGui.TextColored(item.StockDifferential < 0 ? MarketMafiosoUiTheme.Error : MarketMafiosoUiTheme.Success, FormatSignedQuantity(item.StockDifferential));
                ImGui.TableNextColumn();
                ImGui.TextColored(item.Shortage > 0 ? MarketMafiosoUiTheme.Error : MarketMafiosoUiTheme.Success, item.Shortage.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.PlayerInventory.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.RetainerCache.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.Join(", ", item.CandidateRetainers.Select(x => x.RetainerName)));
            }

            ImGui.EndTable();
        }
    }

    private void DrawHeaderActions()
    {
        var canRefreshRetainers = autoRetainerRefresh.CanStartRefresh &&
                                  !autoRetainerRefresh.IsRefreshing &&
                                  !autoRetainerRefresh.IsStartQueued;

        if (ImGuiUi.Button("Refresh Retainer Cache", canRefreshRetainers))
            autoRetainerRefresh.StartFullRefresh();

        ImGui.SameLine();
        if (ImGuiUi.Button("Restock From Retainers", !workshopRetainerRestock.IsRunning))
            _ = workshopRetainerRestock.StartAsync(getAvailability());

    }

    private static string FormatSignedQuantity(int value)
    {
        return value > 0
            ? $"+{value}"
            : value.ToString();
    }
}
