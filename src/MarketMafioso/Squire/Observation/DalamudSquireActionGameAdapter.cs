using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons.Automation.UIInput;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Observation;

public sealed class DalamudSquireActionGameAdapter : ISquireActionGameAdapter
{
    private readonly ICharacterEquipmentSnapshotSource snapshotSource;
    private readonly IPlayerState playerState;
    private readonly ICondition condition;
    private readonly IGameGui gameGui;
    private readonly IFramework framework;
    private readonly Func<bool> hasExternalConflict;
    private bool ownsDesynthesisUi;
    private bool selectorCategoryRequested;
    private bool selectorItemActivated;

    public DalamudSquireActionGameAdapter(
        ICharacterEquipmentSnapshotSource snapshotSource,
        IPlayerState playerState,
        ICondition condition,
        IGameGui gameGui,
        IFramework framework,
        Func<bool>? hasExternalConflict = null)
    {
        this.snapshotSource = snapshotSource;
        this.playerState = playerState;
        this.condition = condition;
        this.gameGui = gameGui;
        this.framework = framework;
        this.hasExternalConflict = hasExternalConflict ?? (() => false);
    }

    public CharacterScope? GetActiveCharacter() =>
        playerState.IsLoaded && playerState.ContentId != 0
            ? new CharacterScope(playerState.ContentId, playerState.CharacterName.ToString(), playerState.HomeWorld.RowId)
            : null;

    public bool HasConflictingAutomation() =>
        hasExternalConflict() ||
        condition[ConditionFlag.BetweenAreas] ||
        condition[ConditionFlag.BetweenAreas51] ||
        condition[ConditionFlag.WatchingCutscene] ||
        condition[ConditionFlag.WatchingCutscene78] ||
        condition[ConditionFlag.OccupiedInQuestEvent] ||
        condition[ConditionFlag.BeingMoved];

    public SquireRevalidationResult Revalidate(EquipmentInstanceFingerprint fingerprint, SquireDisposition disposition)
    {
        var snapshot = snapshotSource.Capture();
        if (!snapshot.Diagnostics.IsComplete)
            return SquireRevalidationResult.Fail("PartialSnapshot", "Fresh revalidation snapshot is incomplete.");
        if (snapshot.Identity.Scope != fingerprint.Character)
            return SquireRevalidationResult.Fail("CharacterScopeChanged", "The active character changed.");

        var observed = snapshot.Instances.FirstOrDefault(instance =>
            instance.Fingerprint.Container == fingerprint.Container && instance.Fingerprint.SlotIndex == fingerprint.SlotIndex);
        if (observed is null || !SquireFingerprintMatcher.ExactMatch(fingerprint, observed.Fingerprint))
            return SquireRevalidationResult.Fail("ExactSlotMismatch", "The approved exact slot identity changed.");
        if (observed.IsEquipped)
            return SquireRevalidationResult.Fail("CurrentlyEquipped", "The item is currently equipped.");
        if (GearsetProtectionIndex.Create(snapshot.Gearsets).IsProtected(fingerprint.ItemId))
            return SquireRevalidationResult.Fail("ReferencedByGearset", "A valid gearset now references this item ID.");
        if (!snapshot.Definitions.TryGetValue(fingerprint.ItemId, out var definition))
            return SquireRevalidationResult.Fail("ItemDefinitionMissing", "The item definition is unavailable.");

        var supported = disposition switch
        {
            SquireDisposition.Desynthesize => definition.IsDesynthesizable == true,
            SquireDisposition.VendorSell => definition.IsVendorSellable == true && definition.VendorSellPrice is > 0,
            SquireDisposition.Discard => definition.IsDiscardable == true,
            _ => false,
        };
        return supported
            ? SquireRevalidationResult.Valid()
            : SquireRevalidationResult.Fail("DispositionUnavailable", "The approved disposition is no longer supported.");
    }

    public async Task<SquireActionResult> ExecuteAsync(
        EquipmentInstanceFingerprint fingerprint,
        SquireDisposition disposition,
        CancellationToken cancellationToken)
    {
        if (disposition != SquireDisposition.Desynthesize)
            return SquireActionResult.Fail("AdapterNotEnabled", $"The {disposition} UI adapter is not enabled.");

        var started = await framework.RunOnTick(() => BeginDesynthesis(fingerprint)).ConfigureAwait(false);
        if (!started.Success)
            return started;

        for (var attempt = 0; attempt < 90; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var confirmation = await framework.RunOnTick(() => TryConfirmDesynthesis(fingerprint)).ConfigureAwait(false);
            if (confirmation.Code == "ConfirmationSubmitted")
                break;
            if (confirmation.Code != "ConfirmationPending")
                return confirmation;
            await framework.DelayTicks(1).ConfigureAwait(false);
            if (attempt == 89)
                return SquireActionResult.Fail("ConfirmationTimeout", "The owned desynthesis dialog did not become ready.");
        }

        for (var attempt = 0; attempt < 360; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transition = await framework.RunOnTick(() => ObserveSlotTransition(fingerprint)).ConfigureAwait(false);
            if (transition.Success)
            {
                ownsDesynthesisUi = false;
                selectorCategoryRequested = false;
                selectorItemActivated = false;
                return transition;
            }
            if (transition.Code != "TransitionPending")
                return transition;
            await framework.DelayTicks(1).ConfigureAwait(false);
        }

        return SquireActionResult.Fail("TransitionTimeout", "The exact slot did not transition after desynthesis confirmation.");
    }

    private unsafe SquireActionResult BeginDesynthesis(EquipmentInstanceFingerprint fingerprint)
    {
        var validation = Revalidate(fingerprint, SquireDisposition.Desynthesize);
        if (!validation.Success)
            return SquireActionResult.Fail(validation.Code, validation.Message);
        var salvage = AgentSalvage.Instance();
        if (salvage == null || !salvage->IsActivatable())
            return SquireActionResult.Fail("DesynthesisUnavailable", "The game's Desynthesis UI is not currently available.");
        var existingDialog = gameGui.GetAddonByName<AddonSalvageDialog>("SalvageDialog", 1);
        var existingSelector = gameGui.GetAddonByName<AddonSalvageItemSelector>("SalvageItemSelector", 1);
        if ((existingDialog != null && existingDialog->AtkUnitBase.IsVisible) ||
            (existingSelector != null && existingSelector->AtkUnitBase.IsVisible))
            return SquireActionResult.Fail("ConflictingUi", "A Desynthesis UI was already open and is not owned by Squire.");

        salvage->Show();
        ownsDesynthesisUi = true;
        selectorCategoryRequested = false;
        selectorItemActivated = false;
        return new SquireActionResult(true, "DesynthesisUiRequested", "Opened the game's normal Desynthesis selector.");
    }

    private unsafe SquireActionResult TryConfirmDesynthesis(EquipmentInstanceFingerprint fingerprint)
    {
        if (!ownsDesynthesisUi)
            return SquireActionResult.Fail("PromptOwnershipLost", "Squire no longer owns the Desynthesis UI.");
        var addon = gameGui.GetAddonByName<AddonSalvageDialog>("SalvageDialog", 1);
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
            return selectorItemActivated
                ? SquireActionResult.Fail("ConfirmationPending", "Waiting for the owned desynthesis dialog.")
                : ActivateSelectorItem(fingerprint);

        var salvage = AgentSalvage.Instance();
        if (salvage == null || salvage->DesynthItemId != fingerprint.ItemId || salvage->DesynthItemSlot.ItemId != fingerprint.ItemId)
            return SquireActionResult.Fail("UnexpectedConfirmation", "The desynthesis dialog does not identify the approved item.");
        var validation = Revalidate(fingerprint, SquireDisposition.Desynthesize);
        if (!validation.Success)
            return SquireActionResult.Fail(validation.Code, validation.Message);
        if (addon->DesynthesizeButton == null || !addon->DesynthesizeButton->IsEnabled)
            return SquireActionResult.Fail("ConfirmationUnavailable", "The owned desynthesis confirmation button is unavailable.");

        addon->DesynthesizeButton->ClickAddonButton(&addon->AtkUnitBase);
        return new SquireActionResult(true, "ConfirmationSubmitted", "Clicked the owned desynthesis dialog's normal confirmation button.");
    }

    private unsafe SquireActionResult ActivateSelectorItem(EquipmentInstanceFingerprint fingerprint)
    {
        var selector = gameGui.GetAddonByName<AddonSalvageItemSelector>("SalvageItemSelector", 1);
        if (selector == null || !selector->AtkUnitBase.IsReady || !selector->AtkUnitBase.IsVisible)
            return SquireActionResult.Fail("ConfirmationPending", "Waiting for the owned Desynthesis selector.");
        if (!Enum.TryParse<InventoryType>(fingerprint.Container, out var inventoryType))
            return SquireActionResult.Fail("UnsupportedContainer", $"Inventory container {fingerprint.Container} is not recognized.");

        var itemIndex = -1;
        for (var index = 0; index < selector->ItemCount; index++)
        {
            var item = selector->Items[index];
            if (item.Inventory == inventoryType && item.Slot == fingerprint.SlotIndex)
            {
                itemIndex = index;
                break;
            }
        }

        if (itemIndex < 0 && !selectorCategoryRequested && inventoryType.ToString().StartsWith("Armory", StringComparison.Ordinal))
        {
            for (uint componentId = 1; componentId <= 100; componentId++)
            {
                var button = selector->AtkUnitBase.GetComponentButtonById(componentId);
                var text = button == null || button->ButtonTextNode == null
                    ? string.Empty
                    : button->ButtonTextNode->NodeText.ExtractText();
                if (!text.Contains("Main Hand", StringComparison.OrdinalIgnoreCase) || !text.Contains("Off Hand", StringComparison.OrdinalIgnoreCase))
                    continue;
                button->ClickAddonButton(&selector->AtkUnitBase);
                selectorCategoryRequested = true;
                return SquireActionResult.Fail("ConfirmationPending", "Selected the normal Main Hand/Off Hand category in the Desynthesis UI.");
            }
        }

        if (itemIndex < 0)
            return SquireActionResult.Fail("DesynthesisItemUnavailable", "The visible Desynthesis selector does not contain the approved exact slot.");
        var validation = Revalidate(fingerprint, SquireDisposition.Desynthesize);
        if (!validation.Success)
            return SquireActionResult.Fail(validation.Code, validation.Message);

        AtkComponentList* list = null;
        for (uint componentId = 1; componentId <= 100; componentId++)
        {
            var candidate = selector->AtkUnitBase.GetComponentListById(componentId);
            if (candidate == null || candidate->GetItemCount() <= itemIndex || (!candidate->IsItemInteractionEnabled && !candidate->IsItemClickEnabled))
                continue;
            if (list == null || candidate->GetItemCount() > list->GetItemCount())
                list = candidate;
        }
        if (list == null)
            return SquireActionResult.Fail("DesynthesisListUnavailable", "The visible Desynthesis item list is not interactive.");

        list->ScrollToItem((short)itemIndex);
        list->SelectItem(itemIndex, true);
        list->DispatchItemEvent(itemIndex, AtkEventType.ListItemClick);
        selectorItemActivated = true;
        return SquireActionResult.Fail("ConfirmationPending", "Clicked the approved exact slot in the visible Desynthesis list.");
    }

    private SquireActionResult ObserveSlotTransition(EquipmentInstanceFingerprint fingerprint)
    {
        var snapshot = snapshotSource.Capture();
        if (snapshot.Identity.Scope != fingerprint.Character)
            return SquireActionResult.Fail("CharacterScopeChanged", "The active character changed while waiting for the item transition.");
        var observed = snapshot.Instances.FirstOrDefault(instance =>
            instance.Fingerprint.Container == fingerprint.Container && instance.Fingerprint.SlotIndex == fingerprint.SlotIndex);
        return observed is null || !SquireFingerprintMatcher.ExactMatch(fingerprint, observed.Fingerprint)
            ? SquireActionResult.Completed()
            : SquireActionResult.Fail("TransitionPending", "The exact item remains in its approved slot.");
    }

    public unsafe SquireActionResult OpenContextMenuProbe(EquipmentInstanceFingerprint fingerprint)
    {
        var snapshot = snapshotSource.Capture();
        var observed = snapshot.Instances.FirstOrDefault(instance =>
            instance.Fingerprint.Container == fingerprint.Container && instance.Fingerprint.SlotIndex == fingerprint.SlotIndex);
        if (!snapshot.Diagnostics.IsComplete || observed is null || !SquireFingerprintMatcher.ExactMatch(fingerprint, observed.Fingerprint))
            return SquireActionResult.Fail("ExactSlotMismatch", "The approved exact slot identity changed before the UI probe.");
        if (!Enum.TryParse<InventoryType>(fingerprint.Container, out var inventoryType))
            return SquireActionResult.Fail("UnsupportedContainer", $"Inventory container {fingerprint.Container} is not recognized.");

        var agent = AgentInventoryContext.Instance();
        if (agent == null)
            return SquireActionResult.Fail("InventoryContextUnavailable", "The inventory context agent is unavailable.");

        var ownerId = inventoryType.ToString().StartsWith("Armory", StringComparison.Ordinal)
            ? AgentId.ArmouryBoard
            : AgentId.Inventory;
        var owner = AgentModule.Instance()->GetAgentByInternalId(ownerId);
        if (owner == null)
            return SquireActionResult.Fail("InventoryOwnerUnavailable", $"The normal {ownerId} UI agent is unavailable.");
        if (!owner->IsAgentActive())
            owner->Show();
        var ownerAddonId = owner->GetAddonId();
        if (ownerAddonId == 0)
            return SquireActionResult.Fail("InventoryOwnerPending", $"Opened the normal {ownerId} UI; retry after it is ready.");

        agent->OpenForItemSlot(inventoryType, fingerprint.SlotIndex, 0, ownerAddonId);
        return new SquireActionResult(true, "ContextMenuRequested", $"Requested the normal item context menu for {fingerprint.Container}:{fingerprint.SlotIndex}.");
    }

    public unsafe string DescribeContextMenuProbe()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("ContextMenu", 1);
        var agent = AgentInventoryContext.Instance();
        if (addon == null || !addon->IsReady || !addon->IsVisible || agent == null)
            return "ContextMenu is not ready or visible.";

        var labelValues = new List<string>();
        foreach (var value in agent->EventParams)
        {
            if (value.Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.WideString or AtkValueType.ConstString)
                labelValues.Add(value.GetValueAsString());
        }
        var labels = labelValues.ToArray();
        var entries = Enumerable.Range(0, Math.Min(agent->ContextItemCount, labels.Length))
            .Select(index =>
            {
                var info = agent->ContextCallbackInfos == null ? null : agent->ContextCallbackInfos + index;
                return info == null
                    ? $"[{index}] {labels[index]}"
                    : $"[{index}] {labels[index]} (labelId={info->LabelId}, disabled={agent->IsContextItemDisabled(index)})";
            });
        return $"target={agent->TargetInventoryId}:{agent->TargetInventorySlotId}; {string.Join(" | ", entries)}";
    }

    public void ReleaseOwnedState()
    {
        if (!ownsDesynthesisUi)
            return;
        ownsDesynthesisUi = false;
        selectorCategoryRequested = false;
        selectorItemActivated = false;
        _ = framework.RunOnTick(CloseOwnedDesynthesisDialog);
    }

    private unsafe void CloseOwnedDesynthesisDialog()
    {
        var addon = gameGui.GetAddonByName<AddonSalvageDialog>("SalvageDialog", 1);
        if (addon != null && addon->AtkUnitBase.IsReady && addon->AtkUnitBase.IsVisible && addon->CancelButtonNode != null)
            addon->CancelButtonNode->ClickAddonButton(&addon->AtkUnitBase);
        var salvage = AgentSalvage.Instance();
        if (salvage != null && salvage->IsAgentActive())
            salvage->Hide();
    }
}
