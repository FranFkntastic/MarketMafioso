using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using MarketMafioso.Quartermaster;
using MarketMafioso.Windows.Main;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Windows.WorkshopLogistics;

internal sealed class WorkshopMaterialPanel
{
    private readonly QuartermasterIpcClient quartermaster;
    private readonly WorkshopQuartermasterRequestService requestService;
    private readonly Func<IReadOnlyList<WorkshopMaterialAvailability>> getAvailability;
    private readonly Func<QuartermasterOwnerScope> getOwnerScope;
    private string searchText = string.Empty;
    private bool shortagesOnly;

    public WorkshopMaterialPanel(
        QuartermasterIpcClient quartermaster,
        WorkshopQuartermasterRequestService requestService,
        Func<IReadOnlyList<WorkshopMaterialAvailability>> getAvailability,
        Func<QuartermasterOwnerScope> getOwnerScope)
    {
        this.quartermaster = quartermaster ?? throw new ArgumentNullException(nameof(quartermaster));
        this.requestService = requestService ?? throw new ArgumentNullException(nameof(requestService));
        this.getAvailability = getAvailability ?? throw new ArgumentNullException(nameof(getAvailability));
        this.getOwnerScope = getOwnerScope ?? throw new ArgumentNullException(nameof(getOwnerScope));
    }

    public void Draw(IReadOnlyList<WorkshopMaterialAvailability>? availability = null)
    {
        availability ??= getAvailability();
        ImGuiUi.SectionHeaderWithActions(
            "Materials",
            MarketMafiosoUiTheme.Header,
            () => DrawHeaderActions(availability),
            210);
        ImGui.TextColored(GetQuartermasterStatusColor(), quartermaster.LastStatus);
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, requestService.LastStatus);
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
            ImGui.TableSetupColumn("Quartermaster", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultHide, 104);
            ImGui.TableSetupColumn("Sources", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultHide);
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
                ImGui.TextUnformatted(item.QuartermasterStock.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.Join(", ", item.QuartermasterRetainers.Select(x => x.RetainerName)));
            }

            ImGui.EndTable();
        }
    }

    private void DrawHeaderActions(IReadOnlyList<WorkshopMaterialAvailability> availability)
    {
        var canRequest = quartermaster.HasCachedSnapshot &&
                         availability.Any(item => item.Shortage > 0) &&
                         getOwnerScope().IsAvailable;
        if (ImGuiUi.PrimaryButton("Request From Quartermaster", canRequest))
            requestService.Submit(getOwnerScope(), availability);
    }

    private System.Numerics.Vector4 GetQuartermasterStatusColor() =>
        quartermaster.LastStatus.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
        quartermaster.LastStatus.Contains("not loaded", StringComparison.OrdinalIgnoreCase) ||
        quartermaster.LastStatus.Contains("does not expose", StringComparison.OrdinalIgnoreCase) ||
        quartermaster.LastStatus.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
        quartermaster.LastStatus.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
        quartermaster.LastStatus.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
        quartermaster.LastStatus.Contains("malformed", StringComparison.OrdinalIgnoreCase) ||
        quartermaster.LastStatus.Contains("omitted", StringComparison.OrdinalIgnoreCase)
            ? MarketMafiosoUiTheme.Error
            : MarketMafiosoUiTheme.Muted;

    private static string FormatSignedQuantity(int value)
    {
        return value > 0
            ? $"+{value}"
            : value.ToString();
    }
}
