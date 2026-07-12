using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using Franthropy.Dalamud.Automation.Inventory;
using Lumina.Excel.Sheets;

namespace MarketMafioso.Squire.Observation;

public sealed class DalamudSquireActionGameAdapter : ISquireActionGameAdapter
{
    private readonly ICharacterEquipmentSnapshotSource snapshotSource;
    private readonly IPlayerState playerState;
    private readonly ICondition condition;
    private readonly IFramework framework;
    private readonly ISquireDispositionCapabilitySource capabilitySource;
    private readonly Func<bool> hasExternalConflict;
    private readonly DalamudDesynthesisUiTransaction desynthesisUi;
    private readonly DalamudExpertDeliveryUiTransaction expertDeliveryUi;
    private readonly DalamudExpertDeliveryPreparation expertDeliveryPreparation;

    public DalamudSquireActionGameAdapter(
        ICharacterEquipmentSnapshotSource snapshotSource,
        IPlayerState playerState,
        ICondition condition,
        IGameGui gameGui,
        IFramework framework,
        IDataManager dataManager,
        ISquireDispositionCapabilitySource capabilitySource,
        ICommandManager commandManager,
        IObjectTable objectTable,
        ITargetManager targetManager,
        IDalamudPluginInterface pluginInterface,
        IPluginLog log,
        Func<bool>? hasExternalConflict = null)
    {
        this.snapshotSource = snapshotSource;
        this.playerState = playerState;
        this.condition = condition;
        this.framework = framework;
        this.capabilitySource = capabilitySource;
        this.hasExternalConflict = hasExternalConflict ?? (() => false);
        desynthesisUi = new DalamudDesynthesisUiTransaction(gameGui);
        var hqPrompt = dataManager.GetExcelSheet<Addon>().GetRow(102434).Text.ExtractText().Trim();
        expertDeliveryUi = new DalamudExpertDeliveryUiTransaction(
            gameGui,
            observed => string.Equals(observed, hqPrompt, StringComparison.Ordinal));
        expertDeliveryPreparation = new DalamudExpertDeliveryPreparation(
            commandManager,
            objectTable,
            targetManager,
            gameGui,
            framework,
            dataManager,
            pluginInterface,
            log);
    }

    public CharacterScope? GetActiveCharacter() =>
        playerState.IsLoaded && playerState.ContentId != 0
            ? new CharacterScope(playerState.ContentId, playerState.CharacterName.ToString(), playerState.HomeWorld.RowId)
            : null;

    public bool HasConflictingAutomation(SquireDisposition disposition) =>
        hasExternalConflict() ||
        condition[ConditionFlag.BetweenAreas] ||
        condition[ConditionFlag.BetweenAreas51] ||
        condition[ConditionFlag.WatchingCutscene] ||
        condition[ConditionFlag.WatchingCutscene78] ||
        (disposition != SquireDisposition.ExpertDelivery && condition[ConditionFlag.OccupiedInQuestEvent]) ||
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

    public SquireRevalidationResult RevalidateEvidence(SquireReviewedSelection selection)
    {
        if (selection.Witnesses is not { Count: > 0 })
            return SquireRevalidationResult.Valid();
        var snapshot = snapshotSource.Capture();
        if (!snapshot.Diagnostics.IsComplete)
            return SquireRevalidationResult.Fail("EvidenceSnapshotIncomplete", "The fresh witness snapshot is incomplete.");
        var target = snapshot.Instances.FirstOrDefault(instance =>
            instance.Fingerprint.Container == selection.Fingerprint.Container && instance.Fingerprint.SlotIndex == selection.Fingerprint.SlotIndex);
        if (target is null || !SquireFingerprintMatcher.ExactMatch(selection.Fingerprint, target.Fingerprint) ||
            !snapshot.Definitions.TryGetValue(target.Fingerprint.ItemId, out var targetDefinition))
            return SquireRevalidationResult.Fail("EvidenceTargetChanged", "The target changed before witness revalidation.");
        var targetStats = EquipmentInstanceStats.Resolve(target, targetDefinition);
        if (targetStats is not { IsComplete: true })
            return SquireRevalidationResult.Fail("EvidenceTargetStatsIncomplete", "The target's quality-adjusted stat profile is incomplete.");

        foreach (var proof in selection.Witnesses)
        {
            var job = snapshot.Jobs.FirstOrDefault(value => value.ClassJobId == proof.ClassJobId && value.IsUnlocked == true);
            if (job is null)
                return SquireRevalidationResult.Fail("EvidenceJobChanged", $"The obtained-state observation for {proof.JobAbbreviation} changed.");
            if (proof.Fingerprints.Count != (proof.Slot == EquipmentSlot.Ring ? 2 : 1))
                return SquireRevalidationResult.Fail("EvidenceCapacityInvalid", $"The retained witness count for {proof.JobAbbreviation} is invalid.");
            var observedWitnesses = new List<(EquipmentInstanceSnapshot Instance, EquipmentItemDefinition Definition)>();
            foreach (var fingerprint in proof.Fingerprints)
            {
                var observed = snapshot.Instances.FirstOrDefault(instance =>
                    instance.Fingerprint.Container == fingerprint.Container && instance.Fingerprint.SlotIndex == fingerprint.SlotIndex);
                if (observed is null || !SquireFingerprintMatcher.ExactMatch(fingerprint, observed.Fingerprint) ||
                    !snapshot.Definitions.TryGetValue(fingerprint.ItemId, out var definition))
                    return SquireRevalidationResult.Fail("EvidenceWitnessChanged", $"A retained {proof.JobAbbreviation} witness changed or disappeared.");
                if (observed.Fingerprint.MateriaIds.Count > 0 || definition.Slot != proof.Slot || definition.EquipLevel > job.Level ||
                    !definition.EligibleClassJobIds.Contains(job.ClassJobId))
                    return SquireRevalidationResult.Fail("EvidenceWitnessIneligible", $"A retained witness is no longer safely usable by {proof.JobAbbreviation}.");
                if (proof.Slot == EquipmentSlot.MainHand &&
                    (targetDefinition.MainHandOccupancy != definition.MainHandOccupancy ||
                     targetDefinition.OffHandOccupancy != definition.OffHandOccupancy))
                    return SquireRevalidationResult.Fail("EvidenceWeaponConfigurationChanged", $"A retained {proof.JobAbbreviation} weapon no longer matches the target's hand occupancy.");
                if (proof.Slot == EquipmentSlot.OffHand && definition.OffHandOccupancy != 1)
                    return SquireRevalidationResult.Fail("EvidenceOffHandConfigurationChanged", $"A retained {proof.JobAbbreviation} off-hand is no longer a proven off-hand configuration.");
                if (proof.Slot == EquipmentSlot.Ring && (!definition.FitsLeftRing || !definition.FitsRightRing))
                    return SquireRevalidationResult.Fail("EvidenceRingCompatibilityChanged", $"A retained {proof.JobAbbreviation} ring no longer has proven two-slot compatibility.");
                var stats = EquipmentInstanceStats.Resolve(observed, definition);
                if (stats is not { IsComplete: true } || !EquipmentUseAnalyzer.Dominates(stats, targetStats, EquipmentUseAnalyzer.RelevantStats(job)))
                    return SquireRevalidationResult.Fail("EvidenceDominanceChanged", $"A retained witness no longer directly dominates the target for {proof.JobAbbreviation}.");
                observedWitnesses.Add((observed, definition));
            }
            if (proof.Slot == EquipmentSlot.Ring && observedWitnesses[0].Definition.ItemId == observedWitnesses[1].Definition.ItemId &&
                observedWitnesses[0].Definition.IsUnique)
                return SquireRevalidationResult.Fail("EvidenceRingPairInvalid", $"The retained ring pair for {proof.JobAbbreviation} is not jointly equippable.");
        }
        return SquireRevalidationResult.Valid();
    }

    public async Task<SquireActionResult> ExecuteAsync(
        EquipmentInstanceFingerprint fingerprint,
        SquireDisposition disposition,
        CancellationToken cancellationToken)
    {
        if (disposition == SquireDisposition.ExpertDelivery)
            return await ExecuteExpertDeliveryAsync(fingerprint, cancellationToken).ConfigureAwait(false);
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
                await WaitForDesynthesisUiSettledAsync(cancellationToken).ConfigureAwait(false);
                return transition;
            }
            if (transition.Code != "TransitionPending")
                return transition;
            await framework.DelayTicks(1).ConfigureAwait(false);
        }

        return SquireActionResult.Fail("TransitionTimeout", "The exact slot did not transition after desynthesis confirmation.");
    }

    private async Task WaitForDesynthesisUiSettledAsync(CancellationToken cancellationToken)
    {
        var stableFrames = 0;
        for (var attempt = 0; attempt < 180; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var settled = await framework.RunOnTick(desynthesisUi.IsUiSettled).ConfigureAwait(false);
            stableFrames = settled ? stableFrames + 1 : 0;
            if (stableFrames >= 12)
                return;
            await framework.DelayTicks(1).ConfigureAwait(false);
        }
        throw new InvalidOperationException("Desynthesis UI did not settle after the completed inventory transition.");
    }

    private async Task<SquireActionResult> ExecuteExpertDeliveryAsync(
        EquipmentInstanceFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        var preparation = await expertDeliveryPreparation.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        if (!preparation.Success)
            return preparation;

        SquireActionResult? started = null;
        for (var attempt = 0; attempt < 90; attempt++)
        {
            started = await framework.RunOnTick(() => BeginExpertDelivery(fingerprint)).ConfigureAwait(false);
            if (started.Success)
                break;
            if (started.Code != "ExpertDeliveryListUnavailable")
                return started;
            await framework.DelayTicks(1).ConfigureAwait(false);
        }
        if (started is not { Success: true })
            return SquireActionResult.Fail("ExpertDeliveryListTimeout", "The visible Expert Delivery list did not become ready.");
        for (var attempt = 0; attempt < 360; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transition = await framework.RunOnTick(() => ObserveSlotTransition(fingerprint)).ConfigureAwait(false);
            if (transition.Success)
            {
                expertDeliveryUi.Complete();
                return transition;
            }
            if (transition.Code != "TransitionPending")
                return transition;
            var advanced = await framework.RunOnTick(expertDeliveryUi.Advance).ConfigureAwait(false);
            if (!advanced.Success && advanced.Code != "Pending")
                return SquireActionResult.Fail(advanced.Code, advanced.Message);
            await framework.DelayTicks(1).ConfigureAwait(false);
        }
        return SquireActionResult.Fail("TransitionTimeout", "The exact slot did not transition after Expert Delivery confirmation.");
    }

    private unsafe SquireActionResult BeginExpertDelivery(EquipmentInstanceFingerprint fingerprint)
    {
        var validation = Revalidate(fingerprint, SquireDisposition.ExpertDelivery);
        if (!validation.Success)
            return SquireActionResult.Fail(validation.Code, validation.Message);
        var gc = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance()->GrandCompany;
        if (gc == 0)
            return SquireActionResult.Fail("GrandCompanyUnavailable", "The active character is not employed by a Grand Company.");
        var inventory = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        var result = expertDeliveryUi.Begin(fingerprint, inventory->GetCompanySeals(gc), inventory->GetMaxCompanySeals(gc));
        return new SquireActionResult(result.Success, result.Code, result.Message);
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
        // Once the context-menu command is submitted, the client reserves the item and the
        // inventory slot is no longer a valid identity oracle. Revalidate immediately before
        // that transition; thereafter the owned UI transaction and final slot transition are
        // the authoritative lifecycle signals.
        if (!desynthesisUi.MenuSelectionSubmitted)
        {
            var validation = Revalidate(fingerprint, SquireDisposition.Desynthesize);
            if (!validation.Success)
                return SquireActionResult.Fail(validation.Code, validation.Message);
        }

        var result = desynthesisUi.AdvanceToConfirmation(fingerprint);
        if (!result.Success)
            return result.Code == "Pending"
                ? SquireActionResult.Fail("ConfirmationPending", result.Message)
                : SquireActionResult.Fail(result.Code, result.Message);
        return new SquireActionResult(true, result.Code, result.Message);
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
        _ = framework.RunOnTick(expertDeliveryUi.CloseOwnedUi);
    }
}
