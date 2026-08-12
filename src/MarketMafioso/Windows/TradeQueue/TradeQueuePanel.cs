using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Tables;
using MarketMafioso.TradeQueue;
using MarketMafioso.Windows.Main;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Windows.TradeQueue;

internal sealed class TradeQueuePanel
{
    private static readonly AgentBridgeActionArgumentSchema ExactRecipientSchema = new(
    [
        new("recipientName", AgentBridgeActionArgumentKind.String),
        new("homeWorld", AgentBridgeActionArgumentKind.String),
    ]);
    private readonly Configuration config;
    private readonly MarketMafioso.TradeQueue.TradeQueueRunner runner;
    private readonly ITradeQueueIo io;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly TableSelectionModel<uint> inventorySelection = new();
    private readonly DalamudTableProjection<TradeQueueInventoryRow> inventoryTable;
    private string inventoryFilter = string.Empty;
    private bool showQueuedOnly;
    private bool confirmClear;
    private uint? editingQuantityItemId;
    private string receiverStatus = "No incoming trade action has been invoked.";

    public TradeQueuePanel(
        Configuration config,
        MarketMafioso.TradeQueue.TradeQueueRunner runner,
        ITradeQueueIo io,
        AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.config = config;
        this.runner = runner;
        this.io = io;
        this.reviewRegistry = reviewRegistry;
        inventoryTable = new DalamudTableProjection<TradeQueueInventoryRow>(
        [
            new(
                "Item",
                1f,
                row => row.ItemName,
                row => row.ItemName,
                ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoHide,
                Draw: DrawItemCell,
                Id: "item"),
            new(
                "Available",
                110f,
                row => row.AvailableQuantity.ToString("N0"),
                row => row.AvailableQuantity,
                ImGuiTableColumnFlags.WidthFixed,
                TextColor: row => row.AvailableQuantity > 0 ? null : MainWindow.ColWarning,
                Id: "available",
                HeaderTooltip: "Current quantity in tradeable player inventory."),
            new(
                "Queued",
                150f,
                row => row.SelectedQuantity.ToString("N0"),
                row => row.SelectedQuantity,
                ImGuiTableColumnFlags.WidthFixed,
                Draw: DrawQueuedQuantityCell,
                Id: "queued",
                HeaderTooltip: "Durable quantity currently queued for trade."),
            new(
                "State",
                140f,
                QueueStateLabel,
                QueueStateSortKey,
                ImGuiTableColumnFlags.WidthFixed,
                TextColor: QueueStateColor,
                Id: "state"),
        ],
            DalamudTableSelection<TradeQueueInventoryRow>.Multi(
                inventorySelection,
                row => row.Key.ItemId,
                IsSelectableInventoryRow));
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
            "Select quantities from current tradeable inventory, target the recipient, and trade exact five-slot batches.");

        var inventory = io.ScanTradeableInventory();
        var rows = TradeQueueInventoryProjection.Build(inventory, config.TradeQueueItems);
        inventorySelection.Retain(
            rows
                .Where(row =>
                    row.Key.ItemId != TradeQueuePlanner.GilItemId &&
                    row.AvailableQuantity > 0)
                .Select(row => row.Key.ItemId));
        var snapshot = runner.Snapshot;
        var hasPartner = io.TryGetSelectedPartner(out var partner);
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
        var displayedRecipient = snapshot.IsActive ||
                                 snapshot.State is TradeQueueExecutionState.Failed or TradeQueueExecutionState.Stopped
            ? snapshot.PartnerName
            : hasPartner
                ? partner.Name
                : null;
        UtilityWorkspaceUi.DrawStatusStrip(
            "##tradeQueueStatus",
            [
                new(
                    "Queued",
                    selectedSummary,
                    selectedRows > 0 ? MainWindow.ColHeader : MainWindow.ColMuted,
                    HasItems ? DrawQueueActions : null,
                    HasItems ? 28f : 0f),
                new(
                    "Recipient",
                    displayedRecipient ?? "No selected player",
                    displayedRecipient != null ? MainWindow.ColSuccess : MainWindow.ColWarning),
                new(
                    "Progress",
                    ProgressLabel(snapshot),
                    snapshot.State is TradeQueueExecutionState.Failed ? MainWindow.ColError :
                        snapshot.IsActive ? MainWindow.ColHeader : MainWindow.ColMuted),
            ]);
        ImGui.TextColored(StatusColor(snapshot.State), snapshot.Message);
        ImGui.Spacing();

        DrawInventoryHeader();
        DrawInventoryFilter(rows);
        DrawInventorySelectionActions(rows);
        DrawInventoryTable(rows);
        ImGui.Spacing();
        DrawExecutionControls(inventory);
        DrawTimingControls();
    }

    private void DrawQueueActions()
    {
        if (ImGuiUi.Button("...##TradeQueueActions", HasItems && !runner.IsActive))
            ImGui.OpenPopup("TradeQueueActions");
        if (!ImGui.BeginPopup("TradeQueueActions"))
        {
            confirmClear = false;
            return;
        }
        if (!confirmClear && ImGui.Selectable("Clear entire queue...", false, ImGuiSelectableFlags.DontClosePopups))
            confirmClear = true;
        if (confirmClear)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Remove every queued item and gil amount?");
            if (ImGuiUi.Button("Clear queue", HasItems && !runner.IsActive))
            {
                config.TradeQueueItems.Clear();
                config.Save();
                confirmClear = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                confirmClear = false;
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.EndPopup();
    }

    private static void DrawInventoryHeader()
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Inventory");
    }

    private void DrawInventoryFilter(IReadOnlyList<TradeQueueInventoryRow> rows)
    {
        ImGui.SetNextItemWidth(Math.Max(220, ImGui.GetContentRegionAvail().X - 250));
        ImGui.InputTextWithHint("##tradeQueueInventoryFilter", "Filter current inventory...", ref inventoryFilter, 120);
        ImGui.SameLine();
        ImGui.Checkbox("Queued only", ref showQueuedOnly);
        ImGui.SameLine();
        inventoryTable.DrawColumnMenuButton("trade-queue-inventory-columns");

        if (rows.Count == 0)
            ImGui.TextColored(MainWindow.ColWarning, "No tradeable inventory is currently observable.");
    }

    private void DrawInventorySelectionActions(IReadOnlyList<TradeQueueInventoryRow> rows)
    {
        var visibleSelectableRows = FilterRows(rows)
            .Where(IsSelectableInventoryRow)
            .ToArray();
        var visibleSelectedCount = visibleSelectableRows.Count(row => inventorySelection.IsSelected(row.Key.ItemId));
        var selectionCountLabel = inventorySelection.Count > 0
            ? $"{inventorySelection.Count:N0} row{(inventorySelection.Count == 1 ? string.Empty : "s")} selected"
            : "No rows selected";
        var selectionDetail = inventorySelection.Count == 0
            ? "Select rows, then apply one queue action to all of them."
            : inventorySelection.Count == visibleSelectedCount
                ? "Ready for one bulk queue action."
                : $"{visibleSelectedCount:N0} selected row{(visibleSelectedCount == 1 ? string.Empty : "s")} visible.";

        var style = ImGui.GetStyle();
        var barHeight = ImGui.GetFrameHeightWithSpacing() + 10f;
        var barBackground = inventorySelection.Count > 0
            ? new System.Numerics.Vector4(
                MainWindow.ColHeader.X * 0.20f,
                MainWindow.ColHeader.Y * 0.20f,
                MainWindow.ColHeader.Z * 0.20f,
                0.96f)
            : style.Colors[(int)ImGuiCol.ChildBg];
        var barBorder = inventorySelection.Count > 0
            ? new System.Numerics.Vector4(
                MainWindow.ColHeader.X * 0.65f,
                MainWindow.ColHeader.Y * 0.65f,
                MainWindow.ColHeader.Z * 0.65f,
                1f)
            : style.Colors[(int)ImGuiCol.Border];
        ImGui.PushStyleColor(ImGuiCol.ChildBg, barBackground);
        ImGui.PushStyleColor(ImGuiCol.Border, barBorder);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 2f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new System.Numerics.Vector2(8f, 5f));
        if (!ImGui.BeginChild(
                "TradeQueueInventorySelectionBar",
                new System.Numerics.Vector2(0, barHeight),
                true,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.EndChild();
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(2);
            return;
        }

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(
            inventorySelection.Count > 0 ? MainWindow.ColHeader : MainWindow.ColMuted,
            selectionCountLabel);
        var actionWidth = CalculateSelectionActionWidth();
        var availableForDetail = ImGui.GetContentRegionAvail().X - actionWidth - style.ItemSpacing.X;
        if (availableForDetail > 180f)
        {
            ImGui.SameLine();
            ImGui.TextColored(MainWindow.ColMuted, selectionDetail);
        }
        ImGuiUi.SameLineRight(actionWidth);

        var canSelectVisible = visibleSelectableRows.Length > 0 && !runner.IsActive;
        if (ImGuiUi.Button("Select visible", canSelectVisible))
        {
            foreach (var row in visibleSelectableRows)
                inventorySelection.SetSelected(row.Key.ItemId, true);
        }
        RegisterLastAction(
            "trade-queue.select-visible",
            "Select every visible inventory row",
            canSelectVisible,
            $"visible={visibleSelectableRows.Length:N0}; selected={inventorySelection.Count:N0}",
            arguments: null,
            _ =>
            {
                if (runner.IsActive)
                    return AgentBridgeUiActionResult.Fail("Trade Queue rows cannot be selected while trading is active.");
                foreach (var row in visibleSelectableRows)
                    inventorySelection.SetSelected(row.Key.ItemId, true);
                return AgentBridgeUiActionResult.Ok($"Selected {visibleSelectableRows.Length:N0} visible inventory row(s).");
            });

        ImGui.SameLine();
        var canBulkEdit = inventorySelection.Count > 0 && !runner.IsActive;
        if (ImGuiUi.PrimaryButton("Queue all available", canBulkEdit))
            ApplyBulkAction(rows, TradeQueueBulkAction.QueueAllAvailable);
        RegisterLastAction(
            "trade-queue.queue-selected-all",
            "Queue all available units for the selected rows",
            canBulkEdit,
            $"selected={inventorySelection.Count:N0}",
            arguments: null,
            _ =>
            {
                if (!ApplyBulkAction(rows, TradeQueueBulkAction.QueueAllAvailable))
                    return AgentBridgeUiActionResult.Fail("Trade Queue cannot be edited while trading is active.");
                return AgentBridgeUiActionResult.Ok($"Queued all available units for {inventorySelection.Count:N0} selected row(s).");
            });

        ImGui.SameLine();
        if (ImGuiUi.Button("Remove from queue", canBulkEdit))
            ApplyBulkAction(rows, TradeQueueBulkAction.RemoveFromQueue);
        RegisterLastAction(
            "trade-queue.remove-selected",
            "Remove the selected rows from the Trade Queue",
            canBulkEdit,
            $"selected={inventorySelection.Count:N0}",
            arguments: null,
            _ =>
            {
                if (!ApplyBulkAction(rows, TradeQueueBulkAction.RemoveFromQueue))
                    return AgentBridgeUiActionResult.Fail("Trade Queue cannot be edited while trading is active.");
                return AgentBridgeUiActionResult.Ok($"Removed {inventorySelection.Count:N0} selected row(s) from the Trade Queue.");
            });

        ImGui.SameLine();
        var canClearSelection = inventorySelection.Count > 0 && !runner.IsActive;
        if (ImGuiUi.Button("Clear row selection", canClearSelection))
            inventorySelection.Clear();
        RegisterLastAction(
            "trade-queue.clear-row-selection",
            "Clear transient Trade Queue row selection",
            canClearSelection,
            $"selected={inventorySelection.Count:N0}",
            arguments: null,
            _ =>
            {
                if (runner.IsActive)
                    return AgentBridgeUiActionResult.Fail("Trade Queue row selection cannot be changed while trading is active.");
                inventorySelection.Clear();
                return AgentBridgeUiActionResult.Ok("Cleared Trade Queue row selection.");
            });
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
    }

    private unsafe void DrawInventoryTable(IReadOnlyList<TradeQueueInventoryRow> rows)
    {
        var tableHeight = Math.Clamp(ImGui.GetContentRegionAvail().Y - 58, 180, 520);
        if (!inventoryTable.Begin("TradeQueueInventory", tableHeight))
            return;

        var visibleRows = inventoryTable.Apply(FilterRows(rows), ImGui.TableGetSortSpecs());

        if (visibleRows.Count == 0)
        {
            inventoryTable.DrawMessageRow(
                rows.Count == 0 ? "No inventory rows." : "No matching inventory rows.",
                textColor: MarketMafiosoUiTheme.Muted);
        }

        inventoryTable.DrawClippedRows(
            visibleRows,
            (row, index) => inventoryTable.DrawSaneRow(
                visibleRows,
                index,
                $"trade-queue-inventory-{row.Key.ItemId}",
                background: ResolveInventoryRowBackground(row),
                selectable: IsSelectableInventoryRow(row),
                enabled: !runner.IsActive));
        inventoryTable.End();
    }

    private IReadOnlyList<TradeQueueInventoryRow> FilterRows(IReadOnlyList<TradeQueueInventoryRow> rows) =>
        rows
            .Where(row =>
                (!showQueuedOnly || row.SelectedQuantity > 0) &&
                (string.IsNullOrWhiteSpace(inventoryFilter) ||
                 row.ItemName.Contains(inventoryFilter.Trim(), StringComparison.OrdinalIgnoreCase)))
            .ToArray();

    private static bool IsSelectableInventoryRow(TradeQueueInventoryRow row) =>
        row.Key.ItemId != TradeQueuePlanner.GilItemId && row.AvailableQuantity > 0;

    private void DrawItemCell(TradeQueueInventoryRow row)
    {
        ImGui.TextColored(
            row.SelectedQuantity > 0 ? MainWindow.ColSuccess : ImGui.GetStyle().Colors[(int)ImGuiCol.Text],
            row.ItemName);
        var canSelect = IsSelectableInventoryRow(row) && !runner.IsActive;
        RegisterLastAction(
            $"trade-queue.row-{row.Key.ItemId}",
            inventorySelection.IsSelected(row.Key.ItemId)
                ? $"Unselect {row.ItemName}"
                : $"Select {row.ItemName}",
            canSelect,
            inventorySelection.IsSelected(row.Key.ItemId) ? "selected" : "not selected",
            arguments: null,
            _ =>
            {
                if (runner.IsActive)
                    return AgentBridgeUiActionResult.Fail("Trade Queue rows cannot be selected while trading is active.");
                var selected = !inventorySelection.IsSelected(row.Key.ItemId);
                inventorySelection.SetSelected(row.Key.ItemId, selected);
                return AgentBridgeUiActionResult.Ok(
                    selected ? $"Selected {row.ItemName}." : $"Unselected {row.ItemName}.");
            });
    }

    private void DrawQueuedQuantityCell(TradeQueueInventoryRow row)
    {
        var isEditing = editingQuantityItemId == row.Key.ItemId;
        if (!isEditing)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(
                row.SelectedQuantity > 0 ? MainWindow.ColSuccess : MainWindow.ColMuted,
                row.SelectedQuantity.ToString("N0"));
            if (!runner.IsActive && ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                ImGui.SetTooltip("Click to edit the queued quantity.");
                if (ImGui.IsItemClicked())
                    editingQuantityItemId = row.Key.ItemId;
            }
            return;
        }

        var quantity = row.SelectedQuantity;
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt(
                $"##trade-queue-quantity-{row.Key.ItemId}",
                ref quantity,
                1,
                100,
                "%d",
                ImGuiInputTextFlags.EnterReturnsTrue))
        {
            SetSelectedQuantity(row, Math.Clamp(quantity, 0, row.AvailableQuantity));
            editingQuantityItemId = null;
        }
        else if (ImGui.IsItemDeactivatedAfterEdit())
        {
            SetSelectedQuantity(row, Math.Clamp(quantity, 0, row.AvailableQuantity));
            editingQuantityItemId = null;
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            editingQuantityItemId = null;
        }
    }

    private static System.Numerics.Vector4? ResolveInventoryRowBackground(TradeQueueInventoryRow row)
    {
        if (row.SelectedQuantity <= 0)
            return null;
        var color = row.AvailableQuantity > 0 ? MainWindow.ColSuccess : MainWindow.ColWarning;
        return new System.Numerics.Vector4(color.X, color.Y, color.Z, 0.11f);
    }

    private static float CalculateSelectionActionWidth()
    {
        var style = ImGui.GetStyle();
        var labels = new[] { "Select visible", "Queue all available", "Remove from queue", "Clear row selection" };
        return labels.Sum(label => ImGui.CalcTextSize(label).X + (style.FramePadding.X * 2f)) +
               ((labels.Length - 1) * style.ItemSpacing.X);
    }

    private static string QueueStateLabel(TradeQueueInventoryRow row)
    {
        if (row.Key.ItemId == TradeQueuePlanner.GilItemId)
            return row.SelectedQuantity > 0 ? "Manual amount" : "Not queued";
        if (row.SelectedQuantity <= 0)
            return "Not queued";
        if (row.AvailableQuantity <= 0)
            return "Unavailable";
        return row.SelectedQuantity >= row.AvailableQuantity ? "All available" : "Partial";
    }

    private static IComparable QueueStateSortKey(TradeQueueInventoryRow row) =>
        QueueStateLabel(row) switch
        {
            "All available" => 0,
            "Partial" => 1,
            "Manual amount" => 2,
            "Unavailable" => 3,
            _ => 4,
        };

    private static System.Numerics.Vector4? QueueStateColor(TradeQueueInventoryRow row) =>
        row.SelectedQuantity > 0
            ? row.AvailableQuantity > 0 ? MainWindow.ColSuccess : MainWindow.ColWarning
            : MainWindow.ColMuted;

    private bool ApplyBulkAction(
        IReadOnlyList<TradeQueueInventoryRow> rows,
        TradeQueueBulkAction action)
    {
        if (runner.IsActive)
            return false;
        var updated = TradeQueueBulkEdit.Apply(
            config.TradeQueueItems,
            rows,
            inventorySelection.SelectedKeys,
            action);
        config.TradeQueueItems.Clear();
        foreach (var item in updated)
            config.TradeQueueItems.Add(item);
        config.Save();
        return true;
    }

    private void DrawExecutionControls(IReadOnlyList<TradeQueueInventoryStack> inventory)
    {
        ImGui.TextColored(
            MainWindow.ColMuted,
            config.AutoAcceptIncomingTrades
                ? "Incoming trades are handled automatically."
                : "Incoming trades require manual confirmation.");
        if (runner.IsActive)
        {
            ImGuiUi.SameLineRight(ImGui.CalcTextSize("Stop Trading").X + ImGui.GetStyle().FramePadding.X * 2f);
            if (ImGui.Button("Stop Trading"))
                runner.Stop();
            RegisterLastAction(
                "trade-queue.stop",
                "Stop the active Trade Queue run",
                enabled: true,
                value: runner.Snapshot.RunId,
                arguments: null,
                _ =>
                {
                    runner.Stop();
                    return AgentBridgeUiActionResult.Ok(
                        "Trade Queue stopped at the last verified checkpoint.",
                        runner.Snapshot.RunId,
                        runner.Snapshot);
                });
            return;
        }

        if (io.IsTradeOpen)
        {
            ImGui.Spacing();
            DrawReceiverControls();
            return;
        }

        var validation = TradeQueuePlanner.Validate(config.TradeQueueItems, inventory);
        var hasPartner = io.TryGetSelectedPartner(out var partner);
        var canResume = runner.CanResume;
        var startLabel = hasPartner
            ? canResume
                ? $"Resume Trading with {partner.Name}"
                : $"Start Trading with {partner.Name}"
            : "Start Trading";
        ImGuiUi.SameLineRight(ImGui.CalcTextSize(startLabel).X + ImGui.GetStyle().FramePadding.X * 2f);
        if (ImGuiUi.Button(
                startLabel,
                validation.Success && hasPartner))
        {
            runner.Start();
        }
        var availablePartners = io.GetAvailablePartners();
        RegisterLastAction(
            "trade-queue.start-exact",
            "Start Trade Queue for an exact nearby recipient",
            validation.Success && availablePartners.Count > 0,
            $"queue={config.TradeQueueItems.Count:N0} lines/{config.TradeQueueItems.Sum(item => item.Quantity):N0} units; " +
            $"nearby={string.Join(", ", availablePartners.Select(candidate => $"{candidate.Name} @ {candidate.HomeWorldName}"))}",
            ExactRecipientSchema,
            arguments => StartExactRecipient(arguments));

        if (!validation.Success && validation.Code != TradeQueueValidationCode.Empty)
            ImGui.TextColored(MainWindow.ColWarning, validation.Message);
        else if (!hasPartner && HasItems)
            ImGui.TextColored(MainWindow.ColMuted, "Target or focus-target the receiving player to begin.");
    }

    private void DrawReceiverControls()
    {
        ImGui.TextColored(
            MainWindow.ColHeader,
            "Incoming trade controls");
        ImGui.TextColored(
            MainWindow.ColMuted,
            "These one-shot controls act only on the currently open, patch-approved trade window.");

        var canReady = io.CanClickReady;
        if (ImGuiUi.Button("Ready Incoming Trade", canReady))
            receiverStatus = InvokeReceiverReady().Message;
        RegisterLastAction(
            "trade-queue.receiver-ready",
            "Ready the current incoming trade",
            canReady,
            receiverStatus,
            arguments: null,
            _ => InvokeReceiverReady());

        ImGui.SameLine();
        var canConfirm = io.CanConfirmTrade;
        if (ImGuiUi.Button("Confirm Incoming Trade", canConfirm))
            receiverStatus = InvokeReceiverConfirm().Message;
        RegisterLastAction(
            "trade-queue.receiver-confirm",
            "Confirm the current incoming trade",
            canConfirm,
            receiverStatus,
            arguments: null,
            _ => InvokeReceiverConfirm());

        ImGui.SameLine();
        var canCancel = io.CanCancelTrade;
        if (ImGuiUi.Button("Cancel Incoming Trade", canCancel))
            receiverStatus = InvokeReceiverCancel().Message;
        RegisterLastAction(
            "trade-queue.receiver-cancel",
            "Cancel the current incoming trade",
            canCancel,
            receiverStatus,
            arguments: null,
            _ => InvokeReceiverCancel());

        ImGui.TextColored(MainWindow.ColMuted, receiverStatus);
    }

    private AgentBridgeUiActionResult StartExactRecipient(System.Text.Json.JsonElement? arguments)
    {
        var recipientName = arguments!.Value.GetProperty("recipientName").GetString()!;
        var homeWorld = arguments.Value.GetProperty("homeWorld").GetString()!;
        if (!io.TryGetPartner(recipientName, homeWorld, out var partner))
        {
            return AgentBridgeUiActionResult.Fail(
                $"{recipientName} @ {homeWorld} is not an exact, targetable nearby player.");
        }

        var result = runner.Start(partner);
        return result.Success
            ? AgentBridgeUiActionResult.Ok(result.Message, runner.Snapshot.RunId, runner.Snapshot)
            : AgentBridgeUiActionResult.Fail(result.Message, runner.Snapshot);
    }

    private AgentBridgeUiActionResult InvokeReceiverReady()
    {
        if (!io.TryClickReady(out var error))
        {
            return AgentBridgeUiActionResult.Fail(
                string.IsNullOrWhiteSpace(error)
                    ? "The incoming trade is not ready for recipient confirmation."
                    : error);
        }

        return AgentBridgeUiActionResult.Ok("Recipient marked the current trade ready.");
    }

    private AgentBridgeUiActionResult InvokeReceiverConfirm()
    {
        if (!io.TryConfirmTrade(out var error))
        {
            return AgentBridgeUiActionResult.Fail(
                string.IsNullOrWhiteSpace(error)
                    ? "The exact trade confirmation is not currently available."
                    : error);
        }

        return AgentBridgeUiActionResult.Ok("Recipient confirmed the current trade.");
    }

    private AgentBridgeUiActionResult InvokeReceiverCancel()
    {
        if (!io.TryCancelTrade(out var error))
        {
            return AgentBridgeUiActionResult.Fail(
                string.IsNullOrWhiteSpace(error)
                    ? "The exact Cancel control is not currently available."
                    : error);
        }

        return AgentBridgeUiActionResult.Ok("Recipient canceled the current trade.");
    }

    private void RegisterLastAction(
        string id,
        string label,
        bool enabled,
        string? value,
        AgentBridgeActionArgumentSchema? arguments,
        Func<System.Text.Json.JsonElement?, AgentBridgeUiActionResult> invoke) =>
        reviewRegistry.Register(
            id,
            label,
            AgentBridgeUiControlKind.Button,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            enabled,
            selected: false,
            value,
            arguments,
            surfaceId: "trade-queue",
            mutating: true,
            completionOperationKind: null,
            invoke);

    private void DrawTimingControls()
    {
        if (!ImGui.CollapsingHeader("Trade timing##tradeQueueTiming"))
            return;

        var actionDelay = Math.Clamp(
            config.TradeQueueTiming.ActionDelayMilliseconds,
            TradeQueueTimingOptions.MinimumActionDelayMilliseconds,
            TradeQueueTimingOptions.MaximumActionDelayMilliseconds);
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderInt(
                "Action delay (ms)##tradeQueueActionDelay",
                ref actionDelay,
                TradeQueueTimingOptions.MinimumActionDelayMilliseconds,
                TradeQueueTimingOptions.MaximumActionDelayMilliseconds))
        {
            config.TradeQueueTiming.ActionDelayMilliseconds = actionDelay;
            config.Save();
        }

        var tradeRetry = Math.Clamp(
            config.TradeQueueTiming.TradeRetryMilliseconds,
            TradeQueueTimingOptions.MinimumTradeRetryMilliseconds,
            TradeQueueTimingOptions.MaximumTradeRetryMilliseconds);
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderInt(
                "Trade command retry (ms)##tradeQueueTradeRetry",
                ref tradeRetry,
                TradeQueueTimingOptions.MinimumTradeRetryMilliseconds,
                TradeQueueTimingOptions.MaximumTradeRetryMilliseconds))
        {
            config.TradeQueueTiming.TradeRetryMilliseconds = tradeRetry;
            config.Save();
        }

        ImGui.TextColored(
            MainWindow.ColMuted,
            "Action delay paces item, quantity, and auto-accept inputs; command retry limits repeated /trade attempts.");

        if (ImGui.Button("Reset timing defaults"))
        {
            config.TradeQueueTiming.ActionDelayMilliseconds =
                TradeQueueTimingOptions.DefaultActionDelayMilliseconds;
            config.TradeQueueTiming.TradeRetryMilliseconds =
                TradeQueueTimingOptions.DefaultTradeRetryMilliseconds;
            config.Save();
        }
    }

    private void SetSelectedQuantity(TradeQueueInventoryRow row, int quantity)
    {
        for (var index = config.TradeQueueItems.Count - 1; index >= 0; index--)
        {
            var item = config.TradeQueueItems[index];
            if (item.ItemId == row.Key.ItemId)
                config.TradeQueueItems.RemoveAt(index);
        }

        if (quantity > 0)
        {
            config.TradeQueueItems.Add(new()
            {
                ItemId = row.Key.ItemId,
                ItemName = row.ItemName,
                Quantity = quantity,
            });
        }

        config.Save();
    }

    private static TradeQueueItem Clone(TradeQueueItem item) => new()
    {
        ItemId = item.ItemId,
        ItemName = item.ItemName,
        Quantity = item.Quantity,
    };

    private static System.Numerics.Vector4 StatusColor(TradeQueueExecutionState state) => state switch
    {
        TradeQueueExecutionState.Completed => MainWindow.ColSuccess,
        TradeQueueExecutionState.Failed => MainWindow.ColError,
        TradeQueueExecutionState.Stopped => MainWindow.ColWarning,
        _ => MainWindow.ColMuted,
    };

    private static string ProgressLabel(TradeQueueExecutionSnapshot snapshot)
    {
        if (snapshot.State == TradeQueueExecutionState.Idle || snapshot.InitialUnitCount <= 0)
            return "Not started";
        if (snapshot.State == TradeQueueExecutionState.Completed)
            return $"{snapshot.CompletedUnitCount:N0} units · {snapshot.CompletedBatchCount:N0} batch(es)";

        return $"{snapshot.CompletedUnitCount:N0} / {snapshot.InitialUnitCount:N0} units · batch {snapshot.BatchNumber:N0}";
    }
}
