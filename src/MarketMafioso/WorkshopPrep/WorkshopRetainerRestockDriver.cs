using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.Automation.Inventory;
using Franthropy.Dalamud.Automation.Retainers;
using MarketMafioso.Automation.Retainers;
using Lumina.Excel.Sheets;
using MarketMafioso.Automation.Safety;

namespace MarketMafioso.WorkshopPrep;

internal interface IWorkshopRetainerRestockDriver
{
    IReadOnlyList<DalamudInventoryStack> ScanLiveRetainerStacks(IReadOnlySet<uint> itemIds);

    Task WaitForRetainerListAsync();

    Task OpenRetainerAsync(RetainerMaterialCandidate candidate);

    Task OpenRetainerInventoryAsync();

    Task<IReadOnlyList<DalamudInventoryStack>> ScanLiveRetainerStacksAsync(IReadOnlySet<uint> itemIds);

    Task<RetainerRetrievalResult> RetrieveFromLiveStackAsync(DalamudInventoryStack stack, int quantity);

    Task<IReadOnlyList<DalamudInventoryStack>> ScanLivePlayerCrystalStacksAsync(IReadOnlySet<uint> itemIds);

    Task<RetainerCrystalTransferResult> DepositCrystalStackAsync(DalamudInventoryStack stack, int quantity);

    Task CloseRetainerAsync();
}

internal sealed class WorkshopRetainerRestockDriver : IWorkshopRetainerRestockDriver
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
    private readonly DalamudSummoningBellInteractor summoningBellInteractor;
    private readonly DalamudRetainerCrystalTransfer crystalTransfer;
    private RetainerMaterialCandidate? activeCandidate;

    public WorkshopRetainerRestockDriver(IPluginLog log)
    {
        this.log = log;
        summoningBellInteractor = new(
            Plugin.ObjectTable,
            Plugin.TargetManager,
            Plugin.DataManager);
        crystalTransfer = new(
            Plugin.SigScanner,
            Plugin.GameGui,
            Plugin.Framework,
            log);
    }

    public IReadOnlyList<DalamudInventoryStack> ScanLiveRetainerStacks(IReadOnlySet<uint> itemIds)
    {
        return DalamudRetainerInventory.ScanLoadedStacks(itemIds);
    }

    public async Task WaitForRetainerListAsync()
    {
        var initialState = await Plugin.Framework.RunOnTick(() => new
        {
            RetainerListReady = IsRetainerListReady(),
            RetainerInventoryReady = IsRetainerInventoryReady(),
        }).ConfigureAwait(false);
        var startError = WorkshopRetainerRestockService.GetAutomatedRestockStartError(initialState.RetainerInventoryReady);
        if (startError != null)
            throw new InvalidOperationException(startError);
        if (initialState.RetainerListReady)
            return;

        SummoningBellInteractionResult? interaction = null;
        for (var attempt = 0; attempt < 120; attempt++)
        {
            if (await Plugin.Framework.RunOnTick(IsRetainerListReady).ConfigureAwait(false))
                return;

            if (interaction?.Submitted != true)
            {
                interaction = await Plugin.Framework.RunOnTick(summoningBellInteractor.TryInteract).ConfigureAwait(false);
                if (interaction.State == SummoningBellInteractionState.Unavailable)
                    throw new InvalidOperationException(interaction.Message);
                if (interaction.Submitted)
                    log.Information($"[MarketMafioso] {interaction.Message}");
            }

            await Plugin.Framework.DelayTicks(1).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"Timed out waiting for the retainer list after summoning-bell interaction. {interaction?.Message ?? "No interaction was submitted."}");
    }

    public async Task OpenRetainerAsync(RetainerMaterialCandidate candidate)
    {
        activeCandidate = null;
        var selected = await Plugin.Framework.RunOnTick(() => SelectRetainerFromList(candidate.RetainerName)).ConfigureAwait(false);
        if (!selected.Success)
            throw new InvalidOperationException(selected.Message);

        await WaitForRetainerCommandMenuAsync(candidate.RetainerName).ConfigureAwait(false);
        var identity = await Plugin.Framework.RunOnTick(() => VerifyActiveRetainerId(candidate.RetainerId)).ConfigureAwait(false);
        if (!identity.Success)
            throw new InvalidOperationException(identity.Message);

        activeCandidate = candidate;
        log.Information($"[MarketMafioso] Selected candidate retainer {candidate.RetainerName} ({candidate.RetainerId}) for retainer transfer.");
    }

    public async Task OpenRetainerInventoryAsync()
    {
        var identity = await Plugin.Framework.RunOnTick(VerifyExpectedActiveRetainer).ConfigureAwait(false);
        if (!identity.Success)
            throw new InvalidOperationException(identity.Message);

        var selected = await Plugin.Framework.RunOnTick(SelectEntrustOrWithdrawItems).ConfigureAwait(false);
        if (!selected.IsSuccess)
            throw new InvalidOperationException(selected.Message);

        await WaitForRetainerInventoryAsync().ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DalamudInventoryStack>> ScanLiveRetainerStacksAsync(IReadOnlySet<uint> itemIds)
    {
        return await Plugin.Framework.RunOnTick(() => ScanLiveRetainerStacks(itemIds)).ConfigureAwait(false);
    }

    public async Task<RetainerRetrievalResult> RetrieveFromLiveStackAsync(DalamudInventoryStack stack, int quantity)
    {
        var identity = await Plugin.Framework.RunOnTick(VerifyExpectedActiveRetainer).ConfigureAwait(false);
        if (!identity.Success)
            return new(false, 0, identity.Message);

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

    public async Task<IReadOnlyList<DalamudInventoryStack>> ScanLivePlayerCrystalStacksAsync(IReadOnlySet<uint> itemIds)
    {
        return await Plugin.Framework.RunOnTick(() =>
            DalamudInventoryStackScanner.ScanLoadedStacks([InventoryType.Crystals], itemIds)).ConfigureAwait(false);
    }

    public async Task<RetainerCrystalTransferResult> DepositCrystalStackAsync(DalamudInventoryStack stack, int quantity)
    {
        var identity = await Plugin.Framework.RunOnTick(VerifyExpectedActiveRetainer).ConfigureAwait(false);
        if (!identity.Success)
            return new(false, 0, "RetainerIdentityMismatch", identity.Message);

        return await crystalTransfer.DepositAsync(stack, quantity).ConfigureAwait(false);
    }

    public async Task CloseRetainerAsync()
    {
        await Plugin.Framework.RunOnTick(CloseOpenRetainerInventory).ConfigureAwait(false);
        await WaitForRetainerCommandMenuAsync("current retainer").ConfigureAwait(false);

        var selectedQuit = await Plugin.Framework.RunOnTick(SelectQuitRetainer).ConfigureAwait(false);
        if (!selectedQuit.IsSuccess)
            throw new InvalidOperationException(selectedQuit.Message);

        for (var attempt = 0; attempt < 120; attempt++)
        {
            if (await Plugin.Framework.RunOnTick(IsRetainerListReady).ConfigureAwait(false))
            {
                activeCandidate = null;
                return;
            }

            await Plugin.Framework.DelayTicks(1).ConfigureAwait(false);
        }

        var uiState = await Plugin.Framework.RunOnTick(DescribeRetainerUiState).ConfigureAwait(false);
        throw new InvalidOperationException($"Timed out waiting for retainer list after closing retainer. {uiState}");
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

    private unsafe RetainerUiActionResult VerifyExpectedActiveRetainer()
    {
        if (activeCandidate == null)
            return new(false, "No stable retainer candidate is active for this transfer.");

        return VerifyActiveRetainerId(activeCandidate.RetainerId);
    }

    private static unsafe RetainerUiActionResult VerifyActiveRetainerId(ulong expectedRetainerId)
    {
        var manager = RetainerManager.Instance();
        if (manager == null)
            return new(false, "Retainer manager is unavailable; cannot verify the selected retainer identity.");

        var activeRetainer = manager->GetActiveRetainer();
        if (activeRetainer == null || activeRetainer->RetainerId == 0)
            return new(false, "No active retainer is available to verify for this transfer.");

        return VerifyCandidateRetainerIdentity(expectedRetainerId, activeRetainer->RetainerId);
    }

    internal static RetainerUiActionResult VerifyCandidateRetainerIdentity(ulong expectedRetainerId, ulong activeRetainerId)
    {
        if (expectedRetainerId == 0)
            return new(false, "The selected retainer has no stable retainer ID; transfer was not started.");
        if (activeRetainerId == expectedRetainerId)
            return new(true, $"Verified active retainer identity {activeRetainerId}.");

        return new(false,
            $"Retainer identity mismatch: expected stable ID {expectedRetainerId}, but active retainer ID is {activeRetainerId}. Transfer was not started.");
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

    private unsafe PendingRetainerRetrieval OpenRetainerStackContextMenu(DalamudInventoryStack stack, int quantity)
    {
        if (quantity <= 0)
            return new(false, 0, false, string.Empty, 0, $"Invalid retrieval quantity {quantity} for item {stack.ItemId}.");

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return new(false, 0, false, string.Empty, 0, "Inventory manager is unavailable.");

        var container = inventoryManager->GetInventoryContainer(stack.Container);
        if (container == null || !container->IsLoaded)
            return new(false, 0, false, string.Empty, 0, $"Retainer inventory page {stack.Container} is not loaded.");

        var slot = container->GetInventorySlot(stack.SlotIndex);
        if (slot == null || slot->ItemId != stack.ItemId || slot->Quantity != stack.Quantity)
            return new(false, 0, false, string.Empty, 0, $"Expected {stack.Quantity}x item {stack.ItemId} was not found at {stack.Container}/{stack.SlotIndex}.");

        var retrieveQuantity = Math.Min(quantity, slot->Quantity);
        var agent = AgentInventoryContext.Instance();
        var retainerAgent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer);
        if (retainerAgent == null)
            return new(false, 0, false, string.Empty, 0, "Retainer agent is unavailable.");

        agent->OpenForItemSlot(stack.Container, stack.SlotIndex, 0, retainerAgent->GetAddonId());

        var needsQuantityInput = retrieveQuantity < slot->Quantity;
        var targetText = GetAddonText(needsQuantityInput ? RetrieveQuantityAddonRow : RetrieveFromRetainerAddonRow);
        var playerQuantityBefore = CountPlayerItem(stack.ItemId);
        log.Verbose(
            $"[MarketMafioso] Opening retainer context menu for item {stack.ItemId}: " +
            $"retainerSlot={stack.Container}/{stack.SlotIndex}, slotQuantity={slot->Quantity}, requested={quantity}, retrieving={retrieveQuantity}, " +
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
        DalamudInventoryStack stack,
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
        DalamudInventoryStack stack,
        int retrieved,
        int playerQuantityBefore)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return new(false, 0, "Inventory manager is unavailable after retrieval.");

        var container = inventoryManager->GetInventoryContainer(stack.Container);
        if (container == null || !container->IsLoaded)
            return new(false, 0, $"Retainer inventory page {stack.Container} is not loaded after retrieval.");

        var slot = container->GetInventorySlot(stack.SlotIndex);
        if (slot == null)
            return new(false, 0, $"Retainer inventory slot {stack.Container}/{stack.SlotIndex} is unavailable after retrieval.");

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
        DalamudInventoryStack stack,
        int retrieved,
        int expectedRemaining,
        uint actualItemId,
        int actualQuantity,
        int playerQuantityBefore,
        int playerQuantityAfter)
    {
        return
            $"Retainer retrieval did not change the expected slot for item {stack.ItemId}: " +
            $"retainerSlot={stack.Container}/{stack.SlotIndex}, originalRetainerQuantity={stack.Quantity}, requestedRetrieved={retrieved}, " +
            $"expectedRemaining={Math.Max(expectedRemaining, 0)}, actualSlotItem={actualItemId}, actualSlotQuantity={actualQuantity}, " +
            $"playerQuantity={playerQuantityBefore}->{playerQuantityAfter}. {DescribeRetainerUiState()}";
    }
}

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
