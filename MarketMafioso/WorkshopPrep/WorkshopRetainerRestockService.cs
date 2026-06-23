using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Utility;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace MarketMafioso.WorkshopPrep;

public enum WorkshopRetainerRestockState
{
    Idle,
    Planning,
    WaitingForRetainerList,
    OpeningRetainer,
    OpeningInventory,
    WithdrawingItems,
    ClosingRetainer,
    Complete,
    Failed,
}

public sealed class WorkshopRetainerRestockService
{
    private const string RetainerListAddon = "RetainerList";
    private const string SelectStringAddon = "SelectString";
    private const string ContextMenuAddon = "ContextMenu";
    private const string RetainerInventoryLargeAddon = "InventoryRetainerLarge";
    private const string RetainerInventorySmallAddon = "InventoryRetainer";
    private const uint RetrieveFromRetainerAddonRow = 98;
    private const uint RetrieveQuantityAddonRow = 773;

    private static readonly InventoryType[] RetainerPages =
    [
        InventoryType.RetainerPage1,
        InventoryType.RetainerPage2,
        InventoryType.RetainerPage3,
        InventoryType.RetainerPage4,
        InventoryType.RetainerPage5,
        InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    private readonly IPluginLog log;
    private bool isRunning;
    private string lastStatus = "Workshop material restock has not run.";

    public WorkshopRetainerRestockService(IPluginLog log)
    {
        this.log = log;
    }

    public bool IsRunning => isRunning;
    public string LastStatus => lastStatus;
    public WorkshopRetainerRestockState State { get; private set; } = WorkshopRetainerRestockState.Idle;

    public async Task StartAsync(IReadOnlyList<WorkshopMaterialAvailability> availability)
    {
        if (isRunning)
        {
            lastStatus = "Workshop material restock is already running.";
            return;
        }

        var shortages = availability.Where(x => x.Shortage > 0).ToList();
        if (shortages.Count == 0)
        {
            lastStatus = "No workshop material shortages to restock.";
            return;
        }

        isRunning = true;
        State = WorkshopRetainerRestockState.Planning;
        try
        {
            var remaining = shortages.ToDictionary(x => x.ItemId, x => x.Shortage);
            var plannedStacks = new HashSet<LiveRetainerStack>();
            var candidates = shortages.SelectMany(x => x.CandidateRetainers)
                .DistinctBy(x => x.RetainerId)
                .ToList();
            if (candidates.Count == 0)
                throw new InvalidOperationException("No cached retainer candidates are available for the workshop material shortages.");

            State = WorkshopRetainerRestockState.WaitingForRetainerList;
            await WaitForRetainerListAsync().ConfigureAwait(false);

            foreach (var candidate in candidates)
            {
                State = WorkshopRetainerRestockState.OpeningRetainer;
                await OpenRetainerAsync(candidate).ConfigureAwait(false);

                State = WorkshopRetainerRestockState.OpeningInventory;
                await OpenRetainerInventoryAsync().ConfigureAwait(false);

                State = WorkshopRetainerRestockState.WithdrawingItems;
                var remainingBefore = remaining.Values.Where(x => x > 0).Sum();
                await WithdrawFromOpenRetainerAsync(remaining, plannedStacks).ConfigureAwait(false);
                var remainingAfter = remaining.Values.Where(x => x > 0).Sum();
                if (remainingAfter >= remainingBefore)
                    throw new InvalidOperationException($"No matching live retainer stacks were found for candidate {candidate.RetainerName}.");

                State = WorkshopRetainerRestockState.ClosingRetainer;
                await CloseRetainerAsync().ConfigureAwait(false);

                if (remaining.Values.All(x => x <= 0))
                    break;
            }

            if (remaining.Values.Any(x => x > 0))
                throw new InvalidOperationException($"Workshop material restock still has remaining shortages: {string.Join(", ", remaining.Where(x => x.Value > 0).Select(x => $"{x.Key}:{x.Value}"))}.");

            State = WorkshopRetainerRestockState.Complete;
            lastStatus = "Workshop material restock complete.";
        }
        catch (Exception ex)
        {
            var failedState = State;
            State = WorkshopRetainerRestockState.Failed;
            lastStatus = $"Workshop material restock failed during {failedState}. {ex.Message}";
            log.Error(ex, "[MarketMafioso] Workshop material restock failed.");
        }
        finally
        {
            isRunning = false;
            if (State is not WorkshopRetainerRestockState.Complete and not WorkshopRetainerRestockState.Failed)
                State = WorkshopRetainerRestockState.Idle;
        }
    }

    public unsafe IReadOnlyList<LiveRetainerStack> ScanLiveRetainerStacks(IReadOnlySet<uint> itemIds)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return [];

        var stacks = new List<LiveRetainerStack>();
        foreach (var page in RetainerPages)
        {
            var container = inventoryManager->GetInventoryContainer(page);
            if (container == null || !container->IsLoaded)
                continue;

            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var slot = container->GetInventorySlot(slotIndex);
                if (slot == null || slot->ItemId == 0 || !itemIds.Contains(slot->ItemId))
                    continue;

                stacks.Add(new LiveRetainerStack(page, slotIndex, slot->ItemId, slot->Quantity));
            }
        }

        return stacks;
    }

    private static async Task WaitForRetainerListAsync()
    {
        var startError = await Plugin.Framework.RunOnTick(() =>
            RetainerUiAutomationText.GetAutomatedRestockStartError(IsRetainerListReady(), IsRetainerInventoryReady())).ConfigureAwait(false);
        if (startError != null)
            throw new InvalidOperationException(startError);
    }

    private async Task OpenRetainerAsync(RetainerMaterialCandidate candidate)
    {
        var selected = await Plugin.Framework.RunOnTick(() => SelectRetainerFromList(candidate.RetainerName)).ConfigureAwait(false);
        if (!selected.Success)
            throw new InvalidOperationException(selected.Message);

        await WaitForRetainerCommandMenuAsync(candidate.RetainerName).ConfigureAwait(false);
        log.Information($"[MarketMafioso] Selected candidate retainer {candidate.RetainerName} ({candidate.RetainerId}) for workshop material retrieval.");
    }

    private async Task OpenRetainerInventoryAsync()
    {
        var selected = await Plugin.Framework.RunOnTick(SelectEntrustOrWithdrawItems).ConfigureAwait(false);
        if (!selected.Success)
            throw new InvalidOperationException(selected.Message);

        await WaitForRetainerInventoryAsync().ConfigureAwait(false);
    }

    private static async Task WaitForRetainerCommandMenuAsync(string retainerName)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            if (await Plugin.Framework.RunOnTick(IsRetainerCommandMenuReady).ConfigureAwait(false))
                return;

            await Plugin.Framework.DelayTicks(1).ConfigureAwait(false);
        }

        var uiState = await Plugin.Framework.RunOnTick(DescribeRetainerUiState).ConfigureAwait(false);
        throw new InvalidOperationException($"Timed out waiting for the retainer command menu for {retainerName}. {uiState}");
    }

    private static async Task WaitForRetainerInventoryAsync()
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            if (await Plugin.Framework.RunOnTick(IsRetainerInventoryReady).ConfigureAwait(false))
                return;

            await Plugin.Framework.DelayTicks(1).ConfigureAwait(false);
        }

        var uiState = await Plugin.Framework.RunOnTick(DescribeRetainerUiState).ConfigureAwait(false);
        throw new InvalidOperationException($"Timed out waiting for retainer inventory to open. {uiState}");
    }

    private async Task WithdrawFromOpenRetainerAsync(
        Dictionary<uint, int> remaining,
        HashSet<LiveRetainerStack> plannedStacks)
    {
        var itemIds = remaining.Where(x => x.Value > 0).Select(x => x.Key).ToHashSet();
        var liveStacks = await Plugin.Framework.RunOnTick(() => ScanLiveRetainerStacks(itemIds)).ConfigureAwait(false);
        foreach (var stack in liveStacks)
        {
            if (plannedStacks.Contains(stack))
                continue;

            if (!remaining.TryGetValue(stack.ItemId, out var needed) || needed <= 0)
                continue;

            var quantity = Math.Min(needed, stack.Quantity);
            var result = await RetrieveFromLiveStackAsync(stack, quantity).ConfigureAwait(false);
            if (!result.Success)
                throw new InvalidOperationException(result.Message);

            remaining[stack.ItemId] -= result.Retrieved;
            plannedStacks.Add(stack);
            log.Information($"[MarketMafioso] Retrieved {result.Retrieved}x item {stack.ItemId} from {stack.Page}/{stack.SlotIndex}.");
        }
    }

    private async Task<RetainerRetrievalResult> RetrieveFromLiveStackAsync(LiveRetainerStack stack, int quantity)
    {
        var pending = await Plugin.Framework.RunOnTick(() => OpenRetainerStackContextMenu(stack, quantity)).ConfigureAwait(false);
        if (!pending.Success)
            return new(false, 0, pending.Message);

        var selected = await WaitForRetainerContextMenuEntryAsync(stack.ItemId, pending.ContextMenuEntryText).ConfigureAwait(false);
        if (!selected.Success)
            return new(false, 0, selected.Message);

        if (pending.NeedsQuantityInput)
        {
            var quantityInput = await WaitForRetrievalQuantityInputAsync(stack.ItemId, pending.Retrieved).ConfigureAwait(false);
            if (!quantityInput.Success)
                return quantityInput;
        }

        await Plugin.Framework.DelayTicks(3).ConfigureAwait(false);
        return await Plugin.Framework.RunOnTick(() => VerifyRetrievalCompleted(stack, pending.Retrieved)).ConfigureAwait(false);
    }

    private unsafe PendingRetainerRetrieval OpenRetainerStackContextMenu(LiveRetainerStack stack, int quantity)
    {
        if (quantity <= 0)
            return new(false, 0, false, string.Empty, $"Invalid retrieval quantity {quantity} for item {stack.ItemId}.");

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return new(false, 0, false, string.Empty, "Inventory manager is unavailable.");

        var container = inventoryManager->GetInventoryContainer(stack.Page);
        if (container == null || !container->IsLoaded)
            return new(false, 0, false, string.Empty, $"Retainer inventory page {stack.Page} is not loaded.");

        var slot = container->GetInventorySlot(stack.SlotIndex);
        if (slot == null || slot->ItemId != stack.ItemId || slot->Quantity != stack.Quantity)
            return new(false, 0, false, string.Empty, $"Expected {stack.Quantity}x item {stack.ItemId} was not found at {stack.Page}/{stack.SlotIndex}.");

        var retrieveQuantity = Math.Min(quantity, slot->Quantity);
        var agent = AgentInventoryContext.Instance();
        var retainerAgent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer);
        if (retainerAgent == null)
            return new(false, 0, false, string.Empty, "Retainer agent is unavailable.");

        agent->OpenForItemSlot(stack.Page, stack.SlotIndex, 0, retainerAgent->GetAddonId());

        var needsQuantityInput = retrieveQuantity < slot->Quantity;
        var targetText = GetAddonText(needsQuantityInput ? RetrieveQuantityAddonRow : RetrieveFromRetainerAddonRow);
        return new(true, retrieveQuantity, needsQuantityInput, targetText, $"Opened retainer context menu for item {stack.ItemId}.");
    }

    private static async Task<RetainerUiActionResult> WaitForRetainerContextMenuEntryAsync(uint itemId, string targetText)
    {
        RetainerUiActionResult lastResult = new(false, $"Retainer context menu entry not found for item {itemId}: {targetText}.");
        for (var attempt = 0; attempt < 30; attempt++)
        {
            lastResult = await Plugin.Framework.RunOnTick(() => SelectRetainerContextMenuEntry(targetText, itemId)).ConfigureAwait(false);
            if (lastResult.Success)
                return lastResult;

            await Plugin.Framework.DelayTicks(1).ConfigureAwait(false);
        }

        return lastResult;
    }

    private static async Task<RetainerRetrievalResult> WaitForRetrievalQuantityInputAsync(uint itemId, int retrieveQuantity)
    {
        RetainerRetrievalResult lastResult = new(false, 0, $"Numeric quantity popup did not open for item {itemId}.");
        for (var attempt = 0; attempt < 30; attempt++)
        {
            lastResult = await Plugin.Framework.RunOnTick(() => SubmitRetrievalQuantity(itemId, retrieveQuantity)).ConfigureAwait(false);
            if (lastResult.Success)
                return lastResult;

            await Plugin.Framework.DelayTicks(1).ConfigureAwait(false);
        }

        return lastResult;
    }

    private static unsafe RetainerRetrievalResult SubmitRetrievalQuantity(uint itemId, int retrieveQuantity)
    {
        var numeric = Plugin.GameGui.GetAddonByName<AtkUnitBase>("InputNumeric", 1);
        if (numeric == null || !numeric->IsReady || !numeric->IsVisible)
            return new(false, 0, $"Numeric quantity popup did not open for item {itemId}.");

        numeric->FireCallbackInt(retrieveQuantity);
        return new(true, retrieveQuantity, "Retrieve quantity submitted.");
    }

    private unsafe RetainerRetrievalResult VerifyRetrievalCompleted(LiveRetainerStack stack, int retrieved)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return new(false, 0, "Inventory manager is unavailable after retrieval.");

        var container = inventoryManager->GetInventoryContainer(stack.Page);
        if (container == null || !container->IsLoaded)
            return new(false, 0, $"Retainer inventory page {stack.Page} is not loaded after retrieval.");

        var slot = container->GetInventorySlot(stack.SlotIndex);
        if (slot == null)
            return new(false, 0, $"Retainer inventory slot {stack.Page}/{stack.SlotIndex} is unavailable after retrieval.");

        var expectedRemaining = stack.Quantity - retrieved;
        if (expectedRemaining <= 0)
        {
            if (slot->ItemId != stack.ItemId || slot->Quantity == 0)
                return new(true, retrieved, "Retrieved full stack.");

            return new(false, 0, $"Retainer slot {stack.Page}/{stack.SlotIndex} did not change after full-stack retrieval.");
        }

        if (slot->ItemId == stack.ItemId && slot->Quantity == expectedRemaining)
            return new(true, retrieved, "Retrieved partial stack.");

        return new(false, 0, $"Retainer slot {stack.Page}/{stack.SlotIndex} did not decrease after retrieval.");
    }

    private static unsafe RetainerUiActionResult SelectRetainerFromList(string retainerName)
    {
        var addon = Plugin.GameGui.GetAddonByName<AtkUnitBase>(RetainerListAddon, 1);
        if (addon == null || !addon->IsReady || !addon->IsVisible)
            return new(false, "Retainer list is not ready.");

        var entries = ReadRetainerListEntries(addon);
        var index = RetainerUiAutomationText.FindRetainerListIndex(entries, retainerName);
        if (index == null)
        {
            var visibleNames = entries.Count == 0
                ? "none"
                : string.Join(", ", entries.Select(x => $"{x.Name}{(x.IsActive ? string.Empty : " (inactive)")}"));
            return new(false, $"Retainer {retainerName} was not found in the visible retainer list. Visible retainers: {visibleNames}.");
        }

        FireRetainerListSelect(addon, (uint)index.Value);
        return new(true, $"Selected retainer {retainerName}.");
    }

    private static unsafe IReadOnlyList<RetainerListEntry> ReadRetainerListEntries(AtkUnitBase* addon)
    {
        const int firstRetainerValueIndex = 3;
        const int retainerValueStride = 10;
        const int maxRetainers = 10;
        const int activeOffset = 8;

        var entries = new List<RetainerListEntry>();
        for (var index = 0; index < maxRetainers; index++)
        {
            var baseIndex = firstRetainerValueIndex + index * retainerValueStride;
            if (baseIndex + activeOffset >= addon->AtkValuesCount)
                break;

            var name = ReadAtkValueString(addon->AtkValues + baseIndex);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var isActive = ReadAtkValueBool(addon->AtkValues + baseIndex + activeOffset);
            entries.Add(new RetainerListEntry(name, isActive));
        }

        return entries;
    }

    private static unsafe void FireRetainerListSelect(AtkUnitBase* addon, uint index)
    {
        var values = stackalloc AtkValue[4];
        values[0] = new AtkValue { Type = AtkValueType.Int, Int = 2 };
        values[1] = new AtkValue { Type = AtkValueType.UInt, UInt = index };
        values[2] = default;
        values[3] = default;

        addon->FireCallback(4, values, true);
    }

    private static unsafe RetainerUiActionResult SelectEntrustOrWithdrawItems()
    {
        var addon = Plugin.GameGui.GetAddonByName<AddonSelectString>(SelectStringAddon, 1);
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
            return new(false, "Retainer command menu is not ready.");

        var targetText = GetAddonText(2378);
        if (!TryGetSelectStringIndex(addon, targetText, out var index))
            return new(false, $"Retainer menu entry not found: {targetText}. {DescribeRetainerUiState()}");

        addon->AtkUnitBase.FireCallbackInt(index);
        return new(true, "Selected retainer inventory command.");
    }

    private static unsafe RetainerUiActionResult SelectQuitRetainer()
    {
        var addon = Plugin.GameGui.GetAddonByName<AddonSelectString>(SelectStringAddon, 1);
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
            return new(false, "Retainer command menu is not ready for quit.");

        var targetText = GetAddonText(2383);
        if (!TryGetSelectStringIndex(addon, targetText, out var index))
            return new(false, $"Retainer quit entry not found: {targetText}. {DescribeRetainerUiState()}");

        addon->AtkUnitBase.FireCallbackInt(index);
        return new(true, "Selected retainer quit command.");
    }

    private static unsafe bool IsRetainerCommandMenuReady()
    {
        var addon = Plugin.GameGui.GetAddonByName<AddonSelectString>(SelectStringAddon, 1);
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
            return false;

        return TryGetSelectStringIndex(addon, GetAddonText(2378), out _);
    }

    private static unsafe bool TryGetSelectStringIndex(AddonSelectString* addon, string targetText, out int index)
    {
        var popup = addon->PopupMenu.PopupMenu;
        for (var i = 0; i < popup.EntryCount; i++)
        {
            var entry = popup.EntryNames[i].ToString();
            if (!RetainerUiAutomationText.IsSelectStringEntryMatch(entry, targetText))
                continue;

            index = i;
            return true;
        }

        index = -1;
        return false;
    }

    private static string GetAddonText(uint rowId)
    {
        return Plugin.DataManager.GetExcelSheet<Addon>().GetRow(rowId).Text.ExtractText();
    }

    private static unsafe string DescribeRetainerUiState()
    {
        var trackedAddons = new[]
        {
            RetainerListAddon,
            SelectStringAddon,
            RetainerInventoryLargeAddon,
            RetainerInventorySmallAddon,
            "InputNumeric",
        };
        var activeAddons = new List<string>();
        foreach (var addonName in trackedAddons)
        {
            var addon = Plugin.GameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
            if (addon == null)
                continue;

            activeAddons.Add($"{addonName}({(addon->IsReady ? "ready" : "not ready")}, {(addon->IsVisible ? "visible" : "hidden")})");
        }

        var state = activeAddons.Count == 0
            ? "Retainer UI state: no tracked addons present"
            : $"Retainer UI state: {string.Join(", ", activeAddons)}";

        var selectStringEntries = DescribeSelectStringEntries();
        if (!string.IsNullOrWhiteSpace(selectStringEntries))
            state += $"; SelectString entries: {selectStringEntries}";

        return state;
    }

    private static unsafe string DescribeSelectStringEntries()
    {
        var addon = Plugin.GameGui.GetAddonByName<AddonSelectString>(SelectStringAddon, 1);
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
            return string.Empty;

        var popup = addon->PopupMenu.PopupMenu;
        if (popup.EntryCount <= 0)
            return string.Empty;

        var entries = new List<string>();
        for (var i = 0; i < popup.EntryCount; i++)
        {
            var entry = popup.EntryNames[i].ToString();
            if (!string.IsNullOrWhiteSpace(entry))
                entries.Add($"[{i}] {entry}");
        }

        return string.Join(" | ", entries);
    }

    private static unsafe string ReadAtkValueString(AtkValue* value)
    {
        if (value == null)
            return string.Empty;

        if (value->Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.WideString or AtkValueType.ConstString))
            return string.Empty;

        return value->GetValueAsString();
    }

    private static unsafe bool ReadAtkValueBool(AtkValue* value)
    {
        return value != null && value->Type == AtkValueType.Bool && value->Byte != 0;
    }

    private static unsafe RetainerUiActionResult SelectRetainerContextMenuEntry(string targetText, uint itemId)
    {
        var contextMenu = Plugin.GameGui.GetAddonByName<AtkUnitBase>(ContextMenuAddon, 1);
        if (contextMenu == null || !contextMenu->IsReady || !contextMenu->IsVisible)
            return new(false, $"Retainer item context menu did not open for item {itemId}.");

        var agent = AgentInventoryContext.Instance();
        var labels = ReadContextMenuLabels(agent);
        var index = RetainerUiAutomationText.FindContextMenuLabelIndex(labels, targetText);
        if (index is null)
            return new(false, $"Retainer context menu entry not found for item {itemId}: {targetText}. Available: {string.Join(", ", labels)}.");

        FireContextMenuSelect(contextMenu, index.Value);
        return new(true, $"Selected retainer context menu entry: {targetText}.");
    }

    private static unsafe IReadOnlyList<string> ReadContextMenuLabels(AgentInventoryContext* agent)
    {
        var labels = new List<string>();
        foreach (var parameter in agent->EventParams)
        {
            if (parameter.Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.WideString or AtkValueType.ConstString))
                continue;

            labels.Add(parameter.GetValueAsString());
        }

        return labels;
    }

    private static unsafe void FireContextMenuSelect(AtkUnitBase* contextMenu, int index)
    {
        var values = stackalloc AtkValue[5];
        values[0] = new AtkValue { Type = AtkValueType.Int, Int = 0 };
        values[1] = new AtkValue { Type = AtkValueType.Int, Int = index };
        values[2] = new AtkValue { Type = AtkValueType.Int, Int = 0 };
        values[3] = new AtkValue { Type = AtkValueType.Int, Int = 0 };
        values[4] = new AtkValue { Type = AtkValueType.Int, Int = 0 };

        contextMenu->FireCallback(5, values, true);
    }

    private static async Task CloseRetainerAsync()
    {
        await Plugin.Framework.RunOnTick(CloseOpenRetainerInventory).ConfigureAwait(false);
        await WaitForRetainerCommandMenuAsync("current retainer").ConfigureAwait(false);

        var selectedQuit = await Plugin.Framework.RunOnTick(SelectQuitRetainer).ConfigureAwait(false);
        if (!selectedQuit.Success)
            throw new InvalidOperationException(selectedQuit.Message);

        for (var attempt = 0; attempt < 120; attempt++)
        {
            if (await Plugin.Framework.RunOnTick(IsRetainerListReady).ConfigureAwait(false))
                return;

            await Plugin.Framework.DelayTicks(1).ConfigureAwait(false);
        }

        var uiState = await Plugin.Framework.RunOnTick(DescribeRetainerUiState).ConfigureAwait(false);
        throw new InvalidOperationException($"Timed out waiting for retainer list after closing retainer. {uiState}");
    }

    private static unsafe bool IsRetainerListReady()
    {
        return IsAddonReady(RetainerListAddon);
    }

    private static unsafe bool IsRetainerInventoryReady()
    {
        return IsAddonReady(RetainerInventoryLargeAddon) || IsAddonReady(RetainerInventorySmallAddon);
    }

    private static unsafe void CloseOpenRetainerInventory()
    {
        var addon = Plugin.GameGui.GetAddonByName<AtkUnitBase>(RetainerInventoryLargeAddon, 1);
        if (addon == null)
            addon = Plugin.GameGui.GetAddonByName<AtkUnitBase>(RetainerInventorySmallAddon, 1);

        if (addon == null || !addon->IsReady || !addon->IsVisible)
            return;

        addon->Close(true);
    }

    private static unsafe bool IsAddonReady(string addonName)
    {
        var addon = Plugin.GameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
        return addon != null && addon->IsReady && addon->IsVisible;
    }
}

public sealed record LiveRetainerStack(
    InventoryType Page,
    int SlotIndex,
    uint ItemId,
    int Quantity);

public sealed record RetainerRetrievalResult(
    bool Success,
    int Retrieved,
    string Message);

internal sealed record PendingRetainerRetrieval(
    bool Success,
    int Retrieved,
    bool NeedsQuantityInput,
    string ContextMenuEntryText,
    string Message);

internal sealed record RetainerUiActionResult(
    bool Success,
    string Message);
