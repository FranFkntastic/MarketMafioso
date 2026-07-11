using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Observation;

public sealed class DalamudSquireActionGameAdapter : ISquireActionGameAdapter
{
    private readonly ICharacterEquipmentSnapshotSource snapshotSource;
    private readonly IPlayerState playerState;
    private readonly ICondition condition;
    private readonly Func<bool> hasExternalConflict;

    public DalamudSquireActionGameAdapter(
        ICharacterEquipmentSnapshotSource snapshotSource,
        IPlayerState playerState,
        ICondition condition,
        Func<bool>? hasExternalConflict = null)
    {
        this.snapshotSource = snapshotSource;
        this.playerState = playerState;
        this.condition = condition;
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

    public Task<SquireActionResult> ExecuteAsync(
        EquipmentInstanceFingerprint fingerprint,
        SquireDisposition disposition,
        CancellationToken cancellationToken) =>
        Task.FromResult(SquireActionResult.Fail("AdapterNotEnabled", "Destructive action adapters are not enabled."));

    public void ReleaseOwnedState()
    {
    }
}

