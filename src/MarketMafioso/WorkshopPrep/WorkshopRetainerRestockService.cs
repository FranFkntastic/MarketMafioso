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
using MarketMafioso.Automation.Retainers;
using MarketMafioso.Automation.Safety;

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
    private const string RetainerListAddon = RetainerInventoryAddonNames.RetainerList;
    private const string SelectStringAddon = RetainerInventoryAddonNames.SelectString;
    private const string RetainerInventoryLargeAddon = RetainerInventoryAddonNames.InventoryLarge;
    private const string RetainerInventorySmallAddon = RetainerInventoryAddonNames.InventorySmall;
    private const uint RetrieveFromRetainerAddonRow = 98;
    private const uint RetrieveQuantityAddonRow = 773;

    private static readonly string[] RetainerUiStateAddons =
    [
        RetainerListAddon,
        SelectStringAddon,
        RetainerInventoryLargeAddon,
        RetainerInventorySmallAddon,
        RetainerInventoryAddonNames.InputNumeric,
    ];

    private static readonly InventoryType[] PlayerInventoryPages =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
        InventoryType.Crystals,
    ];

    private readonly IPluginLog log;
    private readonly RetainerLiveInventoryScanner liveInventoryScanner;
    private bool isRunning;
    private string lastStatus = "Workshop material restock has not run.";

    public WorkshopRetainerRestockService(IPluginLog log)
    {
        this.log = log;
        liveInventoryScanner = new RetainerLiveInventoryScanner();
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
            var candidates = shortages.SelectMany(x => x.CandidateRetainers)
                .DistinctBy(x => x.RetainerId)
                .ToList();
            if (candidates.Count == 0)
                throw new InvalidOperationException("No cached retainer candidates are available for the workshop material shortages.");

            var totalRetrieved = 0;
            State = WorkshopRetainerRestockState.WaitingForRetainerList;
            await WaitForRetainerListAsync().ConfigureAwait(false);

            foreach (var candidate in candidates)
            {
                State = WorkshopRetainerRestockState.OpeningRetainer;
                await OpenRetainerAsync(candidate).ConfigureAwait(false);

                State = WorkshopRetainerRestockState.OpeningInventory;
                await OpenRetainerInventoryAsync().ConfigureAwait(false);

                State = WorkshopRetainerRestockState.WithdrawingItems;
                var retrievedFromCandidate = await WithdrawFromOpenRetainerAsync(remaining).ConfigureAwait(false);
                totalRetrieved += retrievedFromCandidate;
                if (retrievedFromCandidate == 0)
                    log.Information($"[MarketMafioso] No matching live retainer stacks were found for candidate {candidate.RetainerName}.");

                State = WorkshopRetainerRestockState.ClosingRetainer;
                await CloseRetainerAsync().ConfigureAwait(false);

                if (remaining.Values.All(x => x <= 0))
                    break;
            }

            var summary = BuildCompletionSummary(remaining, totalRetrieved);
            if (!summary.IsSuccess)
            {
                State = WorkshopRetainerRestockState.WithdrawingItems;
                throw new InvalidOperationException(summary.Message);
            }

            State = WorkshopRetainerRestockState.Complete;
            lastStatus = summary.Message;
            log.Information($"[MarketMafioso] {summary.Message}");
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
        return liveInventoryScanner.ScanLiveRetainerStacks(itemIds);
    }

    private static async Task WaitForRetainerListAsync()
    {
        var startError = await Plugin.Framework.RunOnTick(() =>
            GetAutomatedRestockStartError(IsRetainerListReady(), IsRetainerInventoryReady())).ConfigureAwait(false);
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
        if (!selected.IsSuccess)
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

    public static WorkshopRetainerRestockCompletionSummary BuildCompletionSummary(
        IReadOnlyDictionary<uint, int> remaining,
        int totalRetrieved)
    {
        var remainingText = FormatRemainingShortages(remaining);
        if (string.IsNullOrEmpty(remainingText))
            return new(true, false, $"Workshop material restock complete. Retrieved {totalRetrieved} item(s).");

        if (totalRetrieved > 0)
            return new(true, true, $"Workshop material restock partially complete. Retrieved {totalRetrieved} item(s); remaining shortages: {remainingText}.");

        return new(false, false, $"No matching live retainer stacks were found for the workshop material shortages: {remainingText}.");
    }

    internal static string? GetAutomatedRestockStartError(bool isRetainerListReady, bool isRetainerInventoryReady)
    {
        if (isRetainerInventoryReady && isRetainerListReady)
            return "Close the current retainer inventory before starting automated workshop material restock.";

        if (isRetainerInventoryReady)
            return "Close the current retainer inventory and open the retainer list before starting automated workshop material restock.";

        if (!isRetainerListReady)
            return "Open the retainer list before starting automated workshop material restock.";

        return null;
    }

    private async Task<int> WithdrawFromOpenRetainerAsync(Dictionary<uint, int> remaining)
    {
        var retrievedTotal = 0;
        var plannedStacks = new HashSet<LiveRetainerStack>();
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
            retrievedTotal += result.Retrieved;
            plannedStacks.Add(stack);
            log.Information($"[MarketMafioso] Retrieved {result.Retrieved}x item {stack.ItemId} from {stack.Page}/{stack.SlotIndex}.");
        }

        return retrievedTotal;
    }

    private async Task<RetainerRetrievalResult> RetrieveFromLiveStackAsync(LiveRetainerStack stack, int quantity)
    {
        var pending = await Plugin.Framework.RunOnTick(() => OpenRetainerStackContextMenu(stack, quantity)).ConfigureAwait(false);
        if (!pending.Success)
            return new(false, 0, pending.Message);

        var selected = await WaitForRetainerContextMenuEntryAsync(stack.ItemId, pending.ContextMenuEntryText).ConfigureAwait(false);
        if (!selected.IsSuccess)
            return new(false, 0, selected.Message);

        if (pending.NeedsQuantityInput)
        {
            var quantityInput = await WaitForRetrievalQuantityInputAsync(stack.ItemId, pending.Retrieved).ConfigureAwait(false);
            if (!quantityInput.Success)
                return quantityInput;

            log.Information($"[MarketMafioso] {quantityInput.Message}");
        }

        log.Information($"[MarketMafioso] {selected.Message}");
        return await WaitForRetrievalCompletionAsync(stack, pending.Retrieved, pending.PlayerQuantityBefore).ConfigureAwait(false);
    }

    private unsafe PendingRetainerRetrieval OpenRetainerStackContextMenu(LiveRetainerStack stack, int quantity)
    {
        if (quantity <= 0)
            return new(false, 0, false, string.Empty, 0, $"Invalid retrieval quantity {quantity} for item {stack.ItemId}.");

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return new(false, 0, false, string.Empty, 0, "Inventory manager is unavailable.");

        var container = inventoryManager->GetInventoryContainer(stack.Page);
        if (container == null || !container->IsLoaded)
            return new(false, 0, false, string.Empty, 0, $"Retainer inventory page {stack.Page} is not loaded.");

        var slot = container->GetInventorySlot(stack.SlotIndex);
        if (slot == null || slot->ItemId != stack.ItemId || slot->Quantity != stack.Quantity)
            return new(false, 0, false, string.Empty, 0, $"Expected {stack.Quantity}x item {stack.ItemId} was not found at {stack.Page}/{stack.SlotIndex}.");

        var retrieveQuantity = Math.Min(quantity, slot->Quantity);
        var agent = AgentInventoryContext.Instance();
        var retainerAgent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer);
        if (retainerAgent == null)
            return new(false, 0, false, string.Empty, 0, "Retainer agent is unavailable.");

        agent->OpenForItemSlot(stack.Page, stack.SlotIndex, 0, retainerAgent->GetAddonId());

        var needsQuantityInput = retrieveQuantity < slot->Quantity;
        var targetText = GetAddonText(needsQuantityInput ? RetrieveQuantityAddonRow : RetrieveFromRetainerAddonRow);
        var playerQuantityBefore = CountPlayerItem(stack.ItemId);
        log.Verbose(
            $"[MarketMafioso] Opening retainer context menu for item {stack.ItemId}: " +
            $"retainerSlot={stack.Page}/{stack.SlotIndex}, slotQuantity={slot->Quantity}, requested={quantity}, retrieving={retrieveQuantity}, " +
            $"playerBefore={playerQuantityBefore}, action=\"{targetText}\", " +
            $"agentTarget={agent->TargetInventoryId}/{agent->TargetInventorySlotId}, ownerAddon={agent->OwnerAddonId}, retainerAddon={retainerAgent->GetAddonId()}.");
        return new(true, retrieveQuantity, needsQuantityInput, targetText, playerQuantityBefore, $"Opened retainer context menu for item {stack.ItemId}.");
    }

    private static async Task<AutomationOperationResult> WaitForRetainerContextMenuEntryAsync(uint itemId, string targetText)
    {
        AutomationOperationResult lastResult = AutomationOperationResult.Fail(
            AutomationFailureKind.MissingAddon,
            $"Retainer context menu entry not found for item {itemId}: {targetText}.");
        for (var attempt = 0; attempt < 30; attempt++)
        {
            lastResult = await Plugin.Framework.RunOnTick(() => SelectRetainerContextMenuEntry(targetText, itemId)).ConfigureAwait(false);
            if (lastResult.IsSuccess)
                return lastResult;

            await Plugin.Framework.DelayTicks(1).ConfigureAwait(false);
        }

        return lastResult;
    }

    private static async Task<RetainerRetrievalResult> WaitForRetrievalCompletionAsync(
        LiveRetainerStack stack,
        int retrieved,
        int playerQuantityBefore)
    {
        RetainerRetrievalResult lastResult = new(false, 0, $"Retrieval did not complete for item {stack.ItemId}.");
        for (var attempt = 0; attempt < 60; attempt++)
        {
            lastResult = await Plugin.Framework.RunOnTick(() => VerifyRetrievalCompleted(stack, retrieved, playerQuantityBefore)).ConfigureAwait(false);
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

    private static unsafe RetainerRetrievalResult VerifyRetrievalCompleted(
        LiveRetainerStack stack,
        int retrieved,
        int playerQuantityBefore)
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

        var playerQuantityAfter = CountPlayerItem(stack.ItemId);
        var expectedRemaining = stack.Quantity - retrieved;
        if (expectedRemaining <= 0)
        {
            if (slot->ItemId != stack.ItemId || slot->Quantity == 0)
                return new(true, retrieved, $"Retrieved full stack; player item count {playerQuantityBefore}->{playerQuantityAfter}.");

            return new(false, 0, BuildRetrievalFailureMessage(stack, retrieved, expectedRemaining, slot->ItemId, slot->Quantity, playerQuantityBefore, playerQuantityAfter));
        }

        if (slot->ItemId == stack.ItemId && slot->Quantity == expectedRemaining)
            return new(true, retrieved, $"Retrieved partial stack; player item count {playerQuantityBefore}->{playerQuantityAfter}.");

        return new(false, 0, BuildRetrievalFailureMessage(stack, retrieved, expectedRemaining, slot->ItemId, slot->Quantity, playerQuantityBefore, playerQuantityAfter));
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

    private static AutomationOperationResult SelectEntrustOrWithdrawItems()
    {
        return CreateCommandMenuDriver().SelectEntrustOrWithdrawItems();
    }

    private static AutomationOperationResult SelectQuitRetainer()
    {
        return CreateCommandMenuDriver().SelectQuitRetainer();
    }

    private static bool IsRetainerCommandMenuReady()
    {
        return CreateCommandMenuDriver().IsCommandMenuReady();
    }

    private static string GetAddonText(uint rowId)
    {
        return Plugin.DataManager.GetExcelSheet<Addon>().GetRow(rowId).Text.ExtractText();
    }

    private static unsafe string DescribeRetainerUiState()
    {
        return new RetainerUiStateReader(Plugin.GameGui).DescribeRetainerUiState(RetainerUiStateAddons);
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

    private static AutomationOperationResult SelectRetainerContextMenuEntry(string targetText, uint itemId)
    {
        return new RetainerContextMenuDriver(Plugin.GameGui).SelectContextMenuEntry(targetText, itemId);
    }

    private static async Task CloseRetainerAsync()
    {
        await Plugin.Framework.RunOnTick(CloseOpenRetainerInventory).ConfigureAwait(false);
        await WaitForRetainerCommandMenuAsync("current retainer").ConfigureAwait(false);

        var selectedQuit = await Plugin.Framework.RunOnTick(SelectQuitRetainer).ConfigureAwait(false);
        if (!selectedQuit.IsSuccess)
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

    private static RetainerCommandMenuDriver CreateCommandMenuDriver()
    {
        return new RetainerCommandMenuDriver(Plugin.GameGui, GetAddonText, DescribeRetainerUiState);
    }

    private static unsafe int CountPlayerItem(uint itemId)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return 0;

        var quantity = 0;
        foreach (var page in PlayerInventoryPages)
        {
            var container = inventoryManager->GetInventoryContainer(page);
            if (container == null || !container->IsLoaded)
                continue;

            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var slot = container->GetInventorySlot(slotIndex);
                if (slot == null || slot->ItemId != itemId)
                    continue;

                quantity += (int)slot->Quantity;
            }
        }

        return quantity;
    }

    private static string BuildRetrievalFailureMessage(
        LiveRetainerStack stack,
        int retrieved,
        int expectedRemaining,
        uint actualItemId,
        int actualQuantity,
        int playerQuantityBefore,
        int playerQuantityAfter)
    {
        return
            $"Retainer retrieval did not change the expected slot for item {stack.ItemId}: " +
            $"retainerSlot={stack.Page}/{stack.SlotIndex}, originalRetainerQuantity={stack.Quantity}, requestedRetrieved={retrieved}, " +
            $"expectedRemaining={Math.Max(expectedRemaining, 0)}, actualSlotItem={actualItemId}, actualSlotQuantity={actualQuantity}, " +
            $"playerQuantity={playerQuantityBefore}->{playerQuantityAfter}. {DescribeRetainerUiState()}";
    }

    private static string FormatRemainingShortages(IReadOnlyDictionary<uint, int> remaining)
    {
        return string.Join(
            ", ",
            remaining
                .Where(x => x.Value > 0)
                .OrderBy(x => x.Key)
                .Select(x => $"{x.Key}:{x.Value}"));
    }
}

public sealed record RetainerRetrievalResult(
    bool Success,
    int Retrieved,
    string Message);

internal sealed record PendingRetainerRetrieval(
    bool Success,
    int Retrieved,
    bool NeedsQuantityInput,
    string ContextMenuEntryText,
    int PlayerQuantityBefore,
    string Message);

internal sealed record RetainerUiActionResult(
    bool Success,
    string Message);

public sealed record WorkshopRetainerRestockCompletionSummary(
    bool IsSuccess,
    bool IsPartial,
    string Message);
