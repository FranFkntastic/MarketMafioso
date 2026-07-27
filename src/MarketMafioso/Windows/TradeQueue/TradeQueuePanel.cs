using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.UI.Items;
using Lumina.Excel.Sheets;
using MarketMafioso.TradeQueue;
using MarketMafioso.Windows.Main;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Windows.TradeQueue;

internal sealed class TradeQueuePanel
{
    private readonly Configuration config;
    private readonly MarketMafioso.TradeQueue.TradeQueueRunner runner;
    private readonly ITradeQueueIo io;
    private readonly IReadOnlyList<DalamudItemOption> itemOptions;
    private readonly HashSet<uint> highQualityItems;
    private readonly DalamudItemAutocompleteState addItemState = new();
    private int addQuantity = 1;
    private bool addHighQuality;
    private bool confirmClear;

    public TradeQueuePanel(
        Configuration config,
        MarketMafioso.TradeQueue.TradeQueueRunner runner,
        ITradeQueueIo io,
        IDataManager dataManager)
    {
        this.config = config;
        this.runner = runner;
        this.io = io;
        var items = dataManager.GetExcelSheet<Item>()
            .Where(item => item.RowId > 0 && !item.IsUntradable && !string.IsNullOrWhiteSpace(item.Name.ToString()))
            .ToList();
        itemOptions = items
            .Select(item => new DalamudItemOption(item.RowId, item.Name.ToString()))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ItemId)
            .ToList();
        highQualityItems = items
            .Where(item => item.CanBeHq)
            .Select(item => item.RowId)
            .ToHashSet();
    }

    public bool HasItems => config.TradeQueueItems.Count > 0;

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
            "Build a named item queue, focus-target its recipient, and trade exact five-slot batches.");

        var inventory = io.ScanTradeableInventory();
        var inventoryCounts = TradeQueuePlanner.CountInventory(inventory);
        var inventoryItems = inventory
            .Where(stack => stack.Quantity > 0)
            .GroupBy(stack => stack.ItemId)
            .Select(group => new DalamudItemOption(group.Key, group.First().ItemName))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ItemId)
            .ToList();
        var snapshot = runner.Snapshot;
        var hasPartner = io.TryGetFocusPartner(out var partner);
        UtilityWorkspaceUi.DrawStatusStrip(
            "##tradeQueueStatus",
            [
                new(
                    "Queue",
                    $"{config.TradeQueueItems.Count:N0} item(s); {config.TradeQueueItems.Sum(item => item.Quantity):N0} units",
                    config.TradeQueueItems.Count > 0 ? MainWindow.ColHeader : MainWindow.ColMuted),
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

        DrawQueueHeader();
        DrawQueueTable(inventoryCounts, inventoryItems);
        ImGui.Spacing();
        DrawAddItem(inventoryCounts);
        ImGui.Spacing();
        DrawExecutionControls(inventory);
    }

    private void DrawQueueHeader()
    {
        ImGuiUi.SectionHeaderWithActions(
            "Items",
            MarketMafiosoUiTheme.Header,
            () =>
            {
                if (ImGuiUi.Button("Clear Queue", HasItems && !runner.IsActive))
                    confirmClear = true;
            },
            100);

        if (!confirmClear)
            return;

        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Clear every item from Trade Queue?");
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

    private void DrawQueueTable(
        IReadOnlyDictionary<TradeQueueItemKey, int> inventoryCounts,
        IReadOnlyList<DalamudItemOption> inventoryItems)
    {
        if (!ImGui.BeginTable("TradeQueueItems", 5, ImGuiUi.InteractiveTableFlags))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthFixed, 72);
        ImGui.TableSetupColumn("Quantity", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("Available", ImGuiTableColumnFlags.WidthFixed, 88);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 76);
        ImGui.TableHeadersRow();

        if (config.TradeQueueItems.Count == 0)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "No items queued.");
            for (var column = 1; column < 5; column++)
            {
                ImGui.TableNextColumn();
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, "-");
            }
        }

        for (var index = 0; index < config.TradeQueueItems.Count; index++)
        {
            var item = config.TradeQueueItems[index];
            var available = inventoryCounts.GetValueOrDefault(new(item.ItemId, item.IsHighQuality));
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (runner.IsActive)
                ImGui.BeginDisabled();
            DrawItemEditor(index, item, inventoryItems, inventoryCounts);
            ImGui.TableNextColumn();
            DrawQualityEditor(index, item, inventoryCounts);
            ImGui.TableNextColumn();
            var maximum = TradeQueuePlanner.GetMaximumQueueQuantity(
                config.TradeQueueItems,
                inventoryCounts,
                new(item.ItemId, item.IsHighQuality),
                index);
            var minimum = maximum > 0 ? 1 : 0;
            var quantity = item.Quantity;
            ImGui.SetNextItemWidth(92);
            if (ImGui.InputInt($"##tradeQueueQuantity{index}", ref quantity))
            {
                item.Quantity = Math.Clamp(quantity, minimum, maximum);
                config.Save();
            }
            else if (!runner.IsActive && item.Quantity > maximum)
            {
                item.Quantity = maximum;
                config.Save();
            }
            if (runner.IsActive)
                ImGui.EndDisabled();

            ImGui.TableNextColumn();
            ImGui.TextColored(
                available >= item.Quantity ? MainWindow.ColSuccess : MainWindow.ColWarning,
                available.ToString("N0"));
            ImGui.TableNextColumn();
            if (ImGuiUi.Button($"Remove##tradeQueueRemove{index}", !runner.IsActive))
            {
                config.TradeQueueItems.RemoveAt(index);
                config.Save();
                index--;
            }
        }

        ImGui.EndTable();
    }

    private void DrawAddItem(IReadOnlyDictionary<TradeQueueItemKey, int> inventoryCounts)
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Add item");
        DalamudItemAutocompleteRenderer.DrawInline(
            "tradeQueueAdd",
            itemOptions,
            addItemState,
            MainWindow.ColMuted,
            MainWindow.ColSuccess,
            MainWindow.ColError);

        var selected = addItemState.SelectedItem;
        var supportsHighQuality = selected is not null && highQualityItems.Contains(selected.ItemId);
        if (!supportsHighQuality)
            addHighQuality = false;

        var remaining = selected is null
            ? 0
            : TradeQueuePlanner.GetMaximumQueueQuantity(
                config.TradeQueueItems,
                inventoryCounts,
                new(selected.ItemId, addHighQuality));
        if (remaining > 0)
            addQuantity = Math.Clamp(addQuantity, 1, remaining);

        ImGui.SetNextItemWidth(110);
        ImGui.InputInt("Quantity##tradeQueueAddQuantity", ref addQuantity);
        addQuantity = remaining > 0
            ? Math.Clamp(addQuantity, 1, remaining)
            : Math.Max(1, addQuantity);
        ImGui.SameLine();
        if (!supportsHighQuality)
            ImGui.BeginDisabled();
        ImGui.Checkbox("HQ##tradeQueueAddHq", ref addHighQuality);
        if (!supportsHighQuality)
            ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGuiUi.Button("Add to Queue", selected is not null && remaining > 0 && !runner.IsActive))
            AddSelectedItem(selected!, remaining);
        if (selected is not null)
        {
            ImGui.SameLine();
            ImGui.TextColored(
                remaining > 0 ? MainWindow.ColMuted : MainWindow.ColWarning,
                $"{remaining:N0} available to queue");
        }
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

    private void AddSelectedItem(DalamudItemOption selected, int remaining)
    {
        var quantity = Math.Clamp(addQuantity, 1, remaining);
        var existing = config.TradeQueueItems.FirstOrDefault(item =>
            item.ItemId == selected.ItemId &&
            item.IsHighQuality == addHighQuality);
        if (existing is null)
        {
            config.TradeQueueItems.Add(new()
            {
                ItemId = selected.ItemId,
                ItemName = selected.Name,
                IsHighQuality = addHighQuality,
                Quantity = quantity,
            });
        }
        else
        {
            existing.Quantity = checked(existing.Quantity + quantity);
        }

        config.Save();
        addItemState.SearchBuffer = string.Empty;
        addItemState.SelectedItem = null;
        addQuantity = 1;
        addHighQuality = false;
    }

    private void DrawItemEditor(
        int rowIndex,
        TradeQueueItem item,
        IReadOnlyList<DalamudItemOption> inventoryItems,
        IReadOnlyDictionary<TradeQueueItemKey, int> inventoryCounts)
    {
        var preview = DalamudItemAutocompletePresenter.FormatDisplayName(
            itemOptions,
            itemOptions.FirstOrDefault(option => option.ItemId == item.ItemId) ??
            new(item.ItemId, item.ItemName));
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo($"##tradeQueueItem{rowIndex}", preview))
            return;

        foreach (var option in inventoryItems)
        {
            var selected = option.ItemId == item.ItemId;
            var label = DalamudItemAutocompletePresenter.FormatDisplayName(itemOptions, option);
            if (!ImGui.Selectable($"{label}##tradeQueueItem{rowIndex}-{option.ItemId}", selected))
                continue;

            item.ItemId = option.ItemId;
            item.ItemName = option.Name;
            if (!highQualityItems.Contains(option.ItemId))
                item.IsHighQuality = false;
            else if (inventoryCounts.GetValueOrDefault(new(option.ItemId, item.IsHighQuality)) == 0)
                item.IsHighQuality = inventoryCounts.GetValueOrDefault(new(option.ItemId, true)) > 0;
            ClampRowQuantity(rowIndex, item, inventoryCounts);
            config.Save();
        }

        ImGui.EndCombo();
    }

    private void DrawQualityEditor(
        int rowIndex,
        TradeQueueItem item,
        IReadOnlyDictionary<TradeQueueItemKey, int> inventoryCounts)
    {
        if (!ImGui.BeginCombo($"##tradeQueueQuality{rowIndex}", item.IsHighQuality ? "HQ" : "NQ"))
            return;

        DrawQualityOption(false);
        if (highQualityItems.Contains(item.ItemId))
            DrawQualityOption(true);
        ImGui.EndCombo();
        return;

        void DrawQualityOption(bool highQuality)
        {
            var available = inventoryCounts.GetValueOrDefault(new(item.ItemId, highQuality));
            if (available <= 0 && item.IsHighQuality != highQuality)
                return;

            var label = $"{(highQuality ? "HQ" : "NQ")} ({available:N0})";
            if (!ImGui.Selectable($"{label}##tradeQueueQuality{rowIndex}-{highQuality}", item.IsHighQuality == highQuality))
                return;

            item.IsHighQuality = highQuality;
            ClampRowQuantity(rowIndex, item, inventoryCounts);
            config.Save();
        }
    }

    private void ClampRowQuantity(
        int rowIndex,
        TradeQueueItem item,
        IReadOnlyDictionary<TradeQueueItemKey, int> inventoryCounts)
    {
        var maximum = TradeQueuePlanner.GetMaximumQueueQuantity(
            config.TradeQueueItems,
            inventoryCounts,
            new(item.ItemId, item.IsHighQuality),
            rowIndex);
        item.Quantity = maximum > 0 ? Math.Clamp(item.Quantity, 1, maximum) : 0;
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
