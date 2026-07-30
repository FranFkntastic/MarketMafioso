using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using MarketMafioso.TradeQueue;
using MarketMafioso.Windows.Main;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Windows.TradeQueue;

internal sealed class TradeQueuePanel
{
    private readonly Configuration config;
    private readonly MarketMafioso.TradeQueue.TradeQueueRunner runner;
    private readonly ITradeQueueIo io;
    private string inventoryFilter = string.Empty;
    private bool showOnlySelected;
    private bool confirmClear;

    public TradeQueuePanel(
        Configuration config,
        MarketMafioso.TradeQueue.TradeQueueRunner runner,
        ITradeQueueIo io)
    {
        this.config = config;
        this.runner = runner;
        this.io = io;
    }

    public bool HasItems => config.TradeQueueItems.Any(item => item.Quantity > 0);

    public string ReplaceWithWorkshopMaterials(IReadOnlyList<WorkshopMaterialAvailability> availability)
    {
        if (runner.IsActive)
            return "Stop the active trade before replacing Trade Queue.";

        var handoff = WorkshopTradeQueueHandoffService.Build(availability);
        if (!handoff.Success)
            return handoff.Message;

        config.TradeQueueItems.Clear();
        foreach (var item in handoff.Items)
            config.TradeQueueItems.Add(Clone(item));
        config.Save();
        confirmClear = false;
        return handoff.Message;
    }

    public void Draw()
    {
        UtilityWorkspaceUi.DrawModuleHeader(
            "Trade Queue",
            "Select quantities from current tradeable inventory, focus-target the recipient, and trade exact five-slot batches.");

        var inventory = io.ScanTradeableInventory();
        var rows = TradeQueueInventoryProjection.Build(inventory, config.TradeQueueItems);
        var snapshot = runner.Snapshot;
        var hasPartner = io.TryGetFocusPartner(out var partner);
        var selectedRows = rows.Count(row => row.SelectedQuantity > 0);
        var selectedItemUnits = rows
            .Where(row => row.Key.ItemId != TradeQueuePlanner.GilItemId)
            .Sum(row => Math.Max(0, row.SelectedQuantity));
        var selectedGil = rows
            .Where(row => row.Key.ItemId == TradeQueuePlanner.GilItemId)
            .Sum(row => Math.Max(0, row.SelectedQuantity));
        var selectedSummary = $"{selectedRows:N0} row(s); {selectedItemUnits:N0} items";
        if (selectedGil > 0)
            selectedSummary += $"; {selectedGil:N0} gil";
        UtilityWorkspaceUi.DrawStatusStrip(
            "##tradeQueueStatus",
            [
                new(
                    "Selected",
                    selectedSummary,
                    selectedRows > 0 ? MainWindow.ColHeader : MainWindow.ColMuted),
                new(
                    "Recipient",
                    hasPartner ? partner.Name : "No focused player",
                    hasPartner ? MainWindow.ColSuccess : MainWindow.ColWarning),
                new(
                    "Execution",
                    snapshot.State.ToString(),
                    snapshot.State is TradeQueueExecutionState.Failed ? MainWindow.ColError :
                        snapshot.IsActive ? MainWindow.ColHeader : MainWindow.ColMuted),
            ]);
        ImGui.TextColored(StatusColor(snapshot.State), snapshot.Message);
        ImGui.Spacing();

        DrawInventoryHeader();
        DrawInventoryFilter(rows);
        DrawInventoryTable(rows);
        ImGui.Spacing();
        DrawExecutionControls(inventory);
    }

    private void DrawInventoryHeader()
    {
        ImGuiUi.SectionHeaderWithActions(
            "Inventory",
            MarketMafiosoUiTheme.Header,
            () =>
            {
                if (ImGuiUi.Button("Clear Selection", HasItems && !runner.IsActive))
                    confirmClear = true;
            },
            112);

        if (!confirmClear)
            return;

        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Set every selected quantity to zero?");
        if (ImGuiUi.Button("Confirm Clear", HasItems && !runner.IsActive))
        {
            config.TradeQueueItems.Clear();
            config.Save();
            confirmClear = false;
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel Clear"))
            confirmClear = false;
    }

    private void DrawInventoryFilter(IReadOnlyList<TradeQueueInventoryRow> rows)
    {
        ImGui.SetNextItemWidth(Math.Max(220, ImGui.GetContentRegionAvail().X - 210));
        ImGui.InputTextWithHint("##tradeQueueInventoryFilter", "Filter current inventory...", ref inventoryFilter, 120);
        ImGui.SameLine();
        ImGui.Checkbox("Show selected only", ref showOnlySelected);

        if (rows.Count == 0)
            ImGui.TextColored(MainWindow.ColWarning, "No tradeable inventory is currently observable.");
    }

    private void DrawInventoryTable(IReadOnlyList<TradeQueueInventoryRow> rows)
    {
        var tableHeight = Math.Clamp(ImGui.GetContentRegionAvail().Y - 58, 180, 520);
        var flags = ImGuiUi.InteractiveTableFlags | ImGuiTableFlags.ScrollY;
        if (!ImGui.BeginTable(
                "TradeQueueInventory",
                3,
                flags,
                new System.Numerics.Vector2(0, tableHeight)))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthFixed, 72);
        ImGui.TableSetupColumn("Quantity", ImGuiTableColumnFlags.WidthFixed, 210);
        ImGui.TableHeadersRow();

        var visibleRows = rows
            .Where(row =>
                (!showOnlySelected || row.SelectedQuantity > 0) &&
                (string.IsNullOrWhiteSpace(inventoryFilter) ||
                 row.ItemName.Contains(inventoryFilter.Trim(), StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (visibleRows.Count == 0)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, rows.Count == 0 ? "No inventory rows." : "No matching inventory rows.");
            ImGui.TableNextColumn();
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "-");
            ImGui.TableNextColumn();
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "-");
        }

        var queuedRows = visibleRows
            .Where(row => row.SelectedQuantity > 0)
            .ToList();
        var availableRows = visibleRows
            .Where(row => row.SelectedQuantity <= 0)
            .ToList();

        if (queuedRows.Count > 0)
        {
            DrawInventoryGroupHeader("Queued to trade", queuedRows.Count, MainWindow.ColSuccess);
            foreach (var row in queuedRows)
                DrawInventoryRow(row);
        }

        if (availableRows.Count > 0)
        {
            if (queuedRows.Count > 0)
                DrawInventoryGroupHeader("Available inventory", availableRows.Count, MainWindow.ColMuted);
            foreach (var row in availableRows)
                DrawInventoryRow(row);
        }

        ImGui.EndTable();
    }

    private static void DrawInventoryGroupHeader(
        string label,
        int rowCount,
        System.Numerics.Vector4 color)
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableNextColumn();
        ImGui.TextColored(color, label);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(string.Empty);
        ImGui.TableNextColumn();
        ImGui.TextColored(MainWindow.ColMuted, $"{rowCount:N0} row(s)");
    }

    private void DrawInventoryRow(TradeQueueInventoryRow row)
    {
        ImGui.PushID($"tradeQueueInventory{row.Key.ItemId}-{row.Key.IsHighQuality}");
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextColored(
            row.SelectedQuantity > 0 ? MainWindow.ColSuccess : ImGui.GetStyle().Colors[(int)ImGuiCol.Text],
            row.ItemName);

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(
            row.Key.ItemId == TradeQueuePlanner.GilItemId
                ? "-"
                : row.Key.IsHighQuality ? "HQ" : "NQ");

        ImGui.TableNextColumn();
        if (runner.IsActive)
            ImGui.BeginDisabled();
        var quantity = row.SelectedQuantity;
        ImGui.SetNextItemWidth(110);
        if (ImGui.InputInt("##quantity", ref quantity))
            SetSelectedQuantity(row, Math.Clamp(quantity, 0, row.AvailableQuantity));
        if (runner.IsActive)
            ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextColored(
            row.SelectedQuantity <= row.AvailableQuantity ? MainWindow.ColMuted : MainWindow.ColWarning,
            $"/ {row.AvailableQuantity:N0}");
        ImGui.PopID();
    }

    private void DrawExecutionControls(IReadOnlyList<TradeQueueInventoryStack> inventory)
    {
        if (runner.IsActive)
        {
            if (ImGui.Button("Stop Trading"))
                runner.Stop();
            return;
        }

        var validation = TradeQueuePlanner.Validate(config.TradeQueueItems, inventory);
        var hasPartner = io.TryGetFocusPartner(out var partner);
        if (ImGuiUi.Button(
                hasPartner ? $"Start Trading with {partner.Name}" : "Start Trading",
                validation.Success && hasPartner))
        {
            runner.Start();
        }

        if (!validation.Success && validation.Code != TradeQueueValidationCode.Empty)
            ImGui.TextColored(MainWindow.ColWarning, validation.Message);
        else if (!hasPartner && HasItems)
            ImGui.TextColored(MainWindow.ColMuted, "Focus-target the receiving player to begin.");
    }

    private void SetSelectedQuantity(TradeQueueInventoryRow row, int quantity)
    {
        for (var index = config.TradeQueueItems.Count - 1; index >= 0; index--)
        {
            var item = config.TradeQueueItems[index];
            if (item.ItemId == row.Key.ItemId && item.IsHighQuality == row.Key.IsHighQuality)
                config.TradeQueueItems.RemoveAt(index);
        }

        if (quantity > 0)
        {
            config.TradeQueueItems.Add(new()
            {
                ItemId = row.Key.ItemId,
                ItemName = row.ItemName,
                IsHighQuality = row.Key.IsHighQuality,
                Quantity = quantity,
            });
        }

        config.Save();
    }

    private static TradeQueueItem Clone(TradeQueueItem item) => new()
    {
        ItemId = item.ItemId,
        ItemName = item.ItemName,
        IsHighQuality = item.IsHighQuality,
        Quantity = item.Quantity,
    };

    private static System.Numerics.Vector4 StatusColor(TradeQueueExecutionState state) => state switch
    {
        TradeQueueExecutionState.Completed => MainWindow.ColSuccess,
        TradeQueueExecutionState.Failed => MainWindow.ColError,
        TradeQueueExecutionState.Stopped => MainWindow.ColWarning,
        _ => MainWindow.ColMuted,
    };
}
