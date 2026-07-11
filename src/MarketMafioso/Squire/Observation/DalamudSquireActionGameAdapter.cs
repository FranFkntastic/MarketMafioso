using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using ECommons.Automation.UIInput;
using FFXIVClientStructs.FFXIV.Client.UI;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using Franthropy.Dalamud.Automation.Inventory;

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
    private readonly DalamudDesynthesisUiTransaction desynthesisUi;

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
        desynthesisUi = new DalamudDesynthesisUiTransaction(gameGui);
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

        var supported = new SquireDispositionEligibilityEvaluator()
            .Evaluate(definition, capabilitySource.Capture())
            .SupportedDispositions.Contains(disposition);
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
                desynthesisUi.Complete();
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
        var result = desynthesisUi.Begin(fingerprint);
        return new SquireActionResult(result.Success, result.Code, result.Message);
    }

    private unsafe SquireActionResult TryConfirmDesynthesis(EquipmentInstanceFingerprint fingerprint)
    {
        var result = desynthesisUi.AdvanceToConfirmation(fingerprint);
        if (!result.Success)
            return result.Code == "Pending"
                ? SquireActionResult.Fail("ConfirmationPending", result.Message)
                : SquireActionResult.Fail(result.Code, result.Message);
        var validation = Revalidate(fingerprint, SquireDisposition.Desynthesize);
        if (!validation.Success)
            return SquireActionResult.Fail(validation.Code, validation.Message);
        var addon = gameGui.GetAddonByName<AddonSalvageDialog>("SalvageDialog", 1);
        if (addon == null || !addon->AtkUnitBase.IsVisible || addon->DesynthesizeButton == null || !addon->DesynthesizeButton->IsEnabled)
            return SquireActionResult.Fail("ConfirmationUnavailable", "The verified desynthesis confirmation button is unavailable.");
        addon->DesynthesizeButton->ClickAddonButton(&addon->AtkUnitBase);
        return new SquireActionResult(true, "ConfirmationSubmitted", "Clicked the owned desynthesis dialog's normal confirmation button.");
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
        var result = desynthesisUi.OpenExactSlotContextMenu(fingerprint);
        return new SquireActionResult(result.Success, result.Code, result.Message);
    }

    public unsafe string DescribeContextMenuProbe()
    {
        return desynthesisUi.DescribeContextMenu();
    }

    public unsafe string CloseContextMenuProbe()
    {
        desynthesisUi.CloseVisibleUi();
        return "Closed visible Squire diagnostic item UI through its addon controls.";
    }

    public void ReleaseOwnedState()
    {
        _ = framework.RunOnTick(desynthesisUi.CloseOwnedUi);
    }
}
