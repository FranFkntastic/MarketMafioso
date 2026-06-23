using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
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

            var taken = Math.Min(needed, stack.Quantity);
            remaining[stack.ItemId] -= taken;
            plannedStacks.Add(stack);
            log.Information($"[MarketMafioso] Planned retrieval of {taken}x item {stack.ItemId} from {stack.Page}/{stack.SlotIndex}.");
        }
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
