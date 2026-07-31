using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
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
    private string inventoryFilter = string.Empty;
    private bool showOnlySelected;
    private bool confirmClear;
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
                    "Selected",
                    selectedSummary,
                    selectedRows > 0 ? MainWindow.ColHeader : MainWindow.ColMuted),
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
        DrawInventoryTable(rows);
        ImGui.Spacing();
        DrawExecutionControls(inventory);
        DrawTimingControls();
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
                2,
                flags,
                new System.Numerics.Vector2(0, tableHeight)))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
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
        ImGui.TextColored(MainWindow.ColMuted, $"{rowCount:N0} row(s)");
    }

    private void DrawInventoryRow(TradeQueueInventoryRow row)
    {
        ImGui.PushID($"tradeQueueInventory{row.Key.ItemId}");
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextColored(
            row.SelectedQuantity > 0 ? MainWindow.ColSuccess : ImGui.GetStyle().Colors[(int)ImGuiCol.Text],
            row.ItemName);

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
            DrawReceiverControls();
            return;
        }

        var validation = TradeQueuePlanner.Validate(config.TradeQueueItems, inventory);
        var hasPartner = io.TryGetSelectedPartner(out var partner);
        var canResume = runner.CanResume;
        if (ImGuiUi.Button(
                hasPartner
                    ? canResume
                        ? $"Resume Trading with {partner.Name}"
                        : $"Start Trading with {partner.Name}"
                    : "Start Trading",
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
            "Action delay paces item and quantity inputs; command retry limits repeated /trade attempts.");

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
