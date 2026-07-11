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
    private readonly ISquireDispositionCapabilitySource capabilitySource;
    private readonly Func<bool> hasExternalConflict;
    private bool ownsDesynthesisUi;
    private bool contextMenuSelectionSubmitted;

    public DalamudSquireActionGameAdapter(
        ICharacterEquipmentSnapshotSource snapshotSource,
        IPlayerState playerState,
        ICondition condition,
        IGameGui gameGui,
        IFramework framework,
        ISquireDispositionCapabilitySource capabilitySource,
        Func<bool>? hasExternalConflict = null)
    {
        this.snapshotSource = snapshotSource;
        this.playerState = playerState;
        this.condition = condition;
        this.gameGui = gameGui;
        this.framework = framework;
        this.capabilitySource = capabilitySource;
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
        if (disposition == SquireDisposition.Desynthesize && capabilitySource.Capture().DesynthesisUnlocked != true)
            return SquireRevalidationResult.Fail("DesynthesisNotUnlocked", "Desynthesis is unavailable until Gone to Pieces is complete.");

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
                contextMenuSelectionSubmitted = false;
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
        var existingDialog = gameGui.GetAddonByName<AddonSalvageDialog>("SalvageDialog", 1);
        var existingContextMenu = gameGui.GetAddonByName<AtkUnitBase>("ContextMenu", 1);
        if ((existingDialog != null && existingDialog->AtkUnitBase.IsVisible) ||
            (existingContextMenu != null && existingContextMenu->IsVisible))
            return SquireActionResult.Fail("ConflictingUi", "A Desynthesis UI was already open and is not owned by Squire.");

        var opened = OpenExactSlotContextMenu(fingerprint);
        if (!opened.Success)
            return opened;
        ownsDesynthesisUi = true;
        contextMenuSelectionSubmitted = false;
        return new SquireActionResult(true, "DesynthesisContextMenuRequested", "Opened the approved exact slot's normal item context menu.");
    }

    private unsafe SquireActionResult TryConfirmDesynthesis(EquipmentInstanceFingerprint fingerprint)
    {
        if (!ownsDesynthesisUi)
            return SquireActionResult.Fail("PromptOwnershipLost", "Squire no longer owns the Desynthesis UI.");
        var addon = gameGui.GetAddonByName<AddonSalvageDialog>("SalvageDialog", 1);
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
            return contextMenuSelectionSubmitted
                ? SquireActionResult.Fail("ConfirmationPending", "Waiting for the owned desynthesis dialog.")
                : SelectDesynthesizeContextMenuEntry(fingerprint);

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

    private unsafe SquireActionResult SelectDesynthesizeContextMenuEntry(EquipmentInstanceFingerprint fingerprint)
    {
        var contextMenu = gameGui.GetAddonByName<AtkUnitBase>("ContextMenu", 1);
        if (contextMenu == null || !contextMenu->IsReady || !contextMenu->IsVisible)
            return SquireActionResult.Fail("ConfirmationPending", "Waiting for the approved exact slot's item context menu.");
        if (!Enum.TryParse<InventoryType>(fingerprint.Container, out var inventoryType))
            return SquireActionResult.Fail("UnsupportedContainer", $"Inventory container {fingerprint.Container} is not recognized.");

        var agent = AgentInventoryContext.Instance();
        if (agent == null || agent->TargetInventoryId != inventoryType || agent->TargetInventorySlotId != fingerprint.SlotIndex)
            return SquireActionResult.Fail("UnexpectedContextMenu", "The visible item context menu does not target the approved exact slot.");
        var validation = Revalidate(fingerprint, SquireDisposition.Desynthesize);
        if (!validation.Success)
            return SquireActionResult.Fail(validation.Code, validation.Message);

        var itemIndex = FindDesynthesizeEntry(ReadContextMenuLabels(agent));
        if (itemIndex < 0 || itemIndex >= agent->ContextItemCount)
            return SquireActionResult.Fail("DesynthesisEntryUnavailable", "The approved exact slot's context menu does not offer Desynthesize.");
        if (agent->IsContextItemDisabled(itemIndex))
            return SquireActionResult.Fail("DesynthesisEntryDisabled", "The approved exact slot's Desynthesize context-menu entry is disabled.");

        var values = stackalloc AtkValue[5];
        values[0] = new AtkValue { Type = AtkValueType.Int, Int = 0 };
        values[1] = new AtkValue { Type = AtkValueType.Int, Int = itemIndex };
        values[2] = new AtkValue { Type = AtkValueType.Int, Int = 0 };
        values[3] = new AtkValue { Type = AtkValueType.Int, Int = 0 };
        values[4] = new AtkValue { Type = AtkValueType.Int, Int = 0 };
        if (!contextMenu->FireCallback(5, values, true))
            return SquireActionResult.Fail("DesynthesisSelectionRejected", "The normal item context menu rejected the Desynthesize selection.");

        contextMenuSelectionSubmitted = true;
        return SquireActionResult.Fail("ConfirmationPending", "Selected Desynthesize from the approved exact slot's normal item context menu.");
    }

    internal static int FindDesynthesizeEntry(IReadOnlyList<string> labels)
    {
        for (var index = 0; index < labels.Count; index++)
        {
            if (labels[index].Equals("Desynthesize", StringComparison.OrdinalIgnoreCase))
                return index;
        }
        return -1;
    }

    private static unsafe IReadOnlyList<string> ReadContextMenuLabels(AgentInventoryContext* agent)
    {
        var labels = new List<string>();
        foreach (var parameter in agent->EventParams)
        {
            if (parameter.Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.WideString or AtkValueType.ConstString)
                labels.Add(parameter.GetValueAsString());
        }
        return labels;
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
        return OpenExactSlotContextMenu(fingerprint);
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
        contextMenuSelectionSubmitted = false;
        _ = framework.RunOnTick(CloseOwnedDesynthesisDialog);
    }

    private unsafe void CloseOwnedDesynthesisDialog()
    {
        var addon = gameGui.GetAddonByName<AddonSalvageDialog>("SalvageDialog", 1);
        if (addon != null && addon->AtkUnitBase.IsReady && addon->AtkUnitBase.IsVisible && addon->CancelButtonNode != null)
            addon->CancelButtonNode->ClickAddonButton(&addon->AtkUnitBase);
    }

    private unsafe SquireActionResult OpenExactSlotContextMenu(EquipmentInstanceFingerprint fingerprint)
    {
        if (!Enum.TryParse<InventoryType>(fingerprint.Container, out var inventoryType))
            return SquireActionResult.Fail("UnsupportedContainer", $"Inventory container {fingerprint.Container} is not recognized.");
        var agent = AgentInventoryContext.Instance();
        if (agent == null)
            return SquireActionResult.Fail("InventoryContextUnavailable", "The inventory context UI is unavailable.");
        var ownerId = inventoryType.ToString().StartsWith("Armory", StringComparison.Ordinal) ? AgentId.ArmouryBoard : AgentId.Inventory;
        var owner = AgentModule.Instance()->GetAgentByInternalId(ownerId);
        if (owner == null)
            return SquireActionResult.Fail("InventoryOwnerUnavailable", $"The normal {ownerId} UI is unavailable.");
        if (!owner->IsAgentActive())
            owner->Show();
        var ownerAddonId = owner->GetAddonId();
        if (ownerAddonId == 0)
            return SquireActionResult.Fail("InventoryOwnerPending", $"Opened the normal {ownerId} UI; retry after it is ready.");
        agent->OpenForItemSlot(inventoryType, fingerprint.SlotIndex, 0, ownerAddonId);
        return new SquireActionResult(true, "ContextMenuRequested", $"Requested the normal item context menu for {fingerprint.Container}:{fingerprint.SlotIndex}.");
    }
}
