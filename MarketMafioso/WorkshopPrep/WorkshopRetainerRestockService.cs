using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

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
    private const string RetainerInventoryLargeAddon = "InventoryRetainerLarge";
    private const string RetainerInventorySmallAddon = "InventoryRetainer";

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
            lastStatus = "Workshop material restock planning complete.";
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
        var isReady = await Plugin.Framework.RunOnTick(IsRetainerListOrInventoryReady).ConfigureAwait(false);
        if (!isReady)
            throw new InvalidOperationException("Open the retainer list or a retainer inventory before starting workshop material restock.");
    }

    private static async Task OpenRetainerAsync(RetainerMaterialCandidate candidate)
    {
        var hasInventory = await Plugin.Framework.RunOnTick(IsRetainerInventoryReady).ConfigureAwait(false);
        if (!hasInventory)
            throw new InvalidOperationException($"Open {candidate.RetainerName}'s retainer inventory before starting retrieval. Automated retainer selection is not enabled yet.");

        await Plugin.Framework.RunOnTick(() =>
        {
            Plugin.Log.Information($"[MarketMafioso] Selected candidate retainer {candidate.RetainerName} ({candidate.RetainerId}) for workshop material retrieval.");
        }).ConfigureAwait(false);
    }

    private static async Task OpenRetainerInventoryAsync()
    {
        var isReady = await Plugin.Framework.RunOnTick(IsRetainerInventoryReady).ConfigureAwait(false);
        if (!isReady)
            throw new InvalidOperationException("Retainer inventory is not open.");
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
        var pending = await Plugin.Framework.RunOnTick(() => BeginRetrieveFromLiveStack(stack, quantity)).ConfigureAwait(false);
        if (!pending.Success)
            return new(false, 0, pending.Message);

        if (pending.NeedsQuantityInput)
        {
            await Plugin.Framework.DelayTicks(1).ConfigureAwait(false);
            var quantityInput = await Plugin.Framework.RunOnTick(() => SubmitRetrievalQuantity(stack.ItemId, pending.Retrieved)).ConfigureAwait(false);
            if (!quantityInput.Success)
                return quantityInput;
        }

        await Plugin.Framework.DelayTicks(3).ConfigureAwait(false);
        return await Plugin.Framework.RunOnTick(() => VerifyRetrievalCompleted(stack, pending.Retrieved)).ConfigureAwait(false);
    }

    private unsafe PendingRetainerRetrieval BeginRetrieveFromLiveStack(LiveRetainerStack stack, int quantity)
    {
        if (quantity <= 0)
            return new(false, 0, false, $"Invalid retrieval quantity {quantity} for item {stack.ItemId}.");

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return new(false, 0, false, "Inventory manager is unavailable.");

        var container = inventoryManager->GetInventoryContainer(stack.Page);
        if (container == null || !container->IsLoaded)
            return new(false, 0, false, $"Retainer inventory page {stack.Page} is not loaded.");

        var slot = container->GetInventorySlot(stack.SlotIndex);
        if (slot == null || slot->ItemId != stack.ItemId || slot->Quantity != stack.Quantity)
            return new(false, 0, false, $"Expected {stack.Quantity}x item {stack.ItemId} was not found at {stack.Page}/{stack.SlotIndex}.");

        var retrieveQuantity = Math.Min(quantity, slot->Quantity);
        var agent = AgentInventoryContext.Instance();
        var retainerAgent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer);
        if (retainerAgent == null)
            return new(false, 0, false, "Retainer agent is unavailable.");

        agent->OpenForItemSlot(stack.Page, stack.SlotIndex, 0, retainerAgent->GetAddonId());

        if (retrieveQuantity >= slot->Quantity)
        {
            var retrieveAll = FindRetainerCallback(agent, 0);
            if (retrieveAll == null)
                return new(false, 0, false, $"Retrieve-all action was not available for item {stack.ItemId}.");

            retrieveAll->Handler->HandleCallback((uint)stack.SlotIndex, stack.Page, agent->TargetInventoryFlags, retrieveAll->CallbackParam);
            return new(true, retrieveQuantity, false, "Retrieve full-stack callback submitted.");
        }

        var retrievePartial = FindRetainerCallback(agent, 3);
        if (retrievePartial == null)
            return new(false, 0, false, $"Retrieve-quantity action was not available for item {stack.ItemId}.");

        retrievePartial->Handler->HandleCallback((uint)stack.SlotIndex, stack.Page, agent->TargetInventoryFlags, retrievePartial->CallbackParam);
        return new(true, retrieveQuantity, true, "Retrieve quantity callback submitted.");
    }

    private unsafe RetainerRetrievalResult SubmitRetrievalQuantity(uint itemId, int retrieveQuantity)
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

        if (slot->ItemId == stack.ItemId && slot->Quantity <= expectedRemaining)
            return new(true, retrieved, "Retrieved partial stack.");

        return new(false, 0, $"Retainer slot {stack.Page}/{stack.SlotIndex} did not decrease after retrieval.");
    }

    private static unsafe AgentInventoryContext.ContextCallbackInfo* FindRetainerCallback(
        AgentInventoryContext* agent,
        ulong callbackParam)
    {
        if (agent->ContextCallbackInfos == null)
            return null;

        for (var index = 0; index < agent->ContextItemCount; index++)
        {
            if (agent->IsContextItemDisabled(index))
                continue;

            var info = agent->ContextCallbackInfos + index;
            if (info->Handler != null && info->CallbackParam == callbackParam)
                return info;
        }

        return null;
    }

    private static async Task CloseRetainerAsync()
    {
        await Plugin.Framework.RunOnTick(() =>
        {
            Plugin.Log.Information("[MarketMafioso] Retainer close step reached; actual close is reserved for the retrieval callback slice.");
        }).ConfigureAwait(false);
    }

    private static unsafe bool IsRetainerListOrInventoryReady()
    {
        return IsAddonReady(RetainerListAddon) || IsRetainerInventoryReady();
    }

    private static unsafe bool IsRetainerInventoryReady()
    {
        return IsAddonReady(RetainerInventoryLargeAddon) || IsAddonReady(RetainerInventorySmallAddon);
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
    string Message);
