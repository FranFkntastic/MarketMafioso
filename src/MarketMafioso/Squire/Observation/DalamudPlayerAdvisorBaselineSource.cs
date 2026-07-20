using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Player;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Squire.Observation;

/// <summary>
/// Captures one windowless player-advisor baseline. Callers must invoke <see cref="Capture"/>
/// synchronously on the framework thread; no native pointer escapes the call.
/// </summary>
public sealed unsafe class DalamudPlayerAdvisorBaselineSource : IPlayerAdvisorBaselineSource
{
    private readonly ICharacterEquipmentSnapshotSource snapshotSource;
    private readonly IPlayerState playerState;

    public DalamudPlayerAdvisorBaselineSource(
        ICharacterEquipmentSnapshotSource snapshotSource,
        IPlayerState playerState)
    {
        this.snapshotSource = snapshotSource;
        this.playerState = playerState;
    }

    public PlayerAdvisorBaseline Capture()
    {
        CharacterEquipmentSnapshot? snapshot = null;
        PlayerAdvisorCaptureHeader? before = null;
        try
        {
            if (!TryCaptureHeader(out before, out var diagnostic))
                return PlayerAdvisorBaselineAssembler.Failure(PlayerAdvisorBaselineStatus.Unavailable, diagnostic);

            snapshot = snapshotSource.Capture();
            if (!TryCaptureHeader(out var afterSnapshot, out diagnostic))
                return PlayerAdvisorBaselineAssembler.Failure(PlayerAdvisorBaselineStatus.Unavailable, diagnostic, snapshot, before);
            if (afterSnapshot != before)
                return PlayerAdvisorBaselineAssembler.Failure(
                    PlayerAdvisorBaselineStatus.Inconsistent,
                    "The player identity, job, world, or level changed while the equipment snapshot was captured.",
                    snapshot,
                    before);

            var family = AdvisorStatFamilies.Resolve(before!.ClassJobId);
            if (family is null)
                return PlayerAdvisorBaselineAssembler.Assemble(
                    snapshot,
                    before,
                    null,
                    new Dictionary<EquipmentStatSemantic, int>(),
                    []);

            var attributes = new Dictionary<EquipmentStatSemantic, PlayerAttribute>();
            var totals = new Dictionary<EquipmentStatSemantic, int>();
            foreach (var semantic in family.RelevantSemantics)
            {
                if (!TryMapPlayerAttribute(semantic, out var attribute))
                    return PlayerAdvisorBaselineAssembler.Failure(
                        PlayerAdvisorBaselineStatus.Unavailable,
                        $"Advisor semantic {semantic} has no PlayerState attribute mapping.",
                        snapshot,
                        before);
                var value = playerState.GetAttribute(attribute);
                if (value < 0)
                    return PlayerAdvisorBaselineAssembler.Failure(
                        PlayerAdvisorBaselineStatus.Unavailable,
                        $"PlayerState returned an invalid negative {semantic} total.",
                        snapshot,
                        before);
                attributes.Add(semantic, attribute);
                totals.Add(semantic, value);
            }

            var manager = InventoryManager.Instance();
            if (manager == null)
                return PlayerAdvisorBaselineAssembler.Failure(
                    PlayerAdvisorBaselineStatus.Unavailable,
                    "InventoryManager is unavailable.",
                    snapshot,
                    before);
            var container = manager->GetInventoryContainer(InventoryType.EquippedItems);
            if (container == null || !container->IsLoaded || container->Size <= 12)
                return PlayerAdvisorBaselineAssembler.Failure(
                    PlayerAdvisorBaselineStatus.Unavailable,
                    "The EquippedItems container is unavailable or has an unsupported layout.",
                    snapshot,
                    before);

            var equipped = new List<PlayerAdvisorEquippedItemCapture>(PlayerAdvisorEquippedSlotMap.All.Count);
            foreach (var position in PlayerAdvisorEquippedSlotMap.All)
            {
                var item = container->GetInventorySlot(position.EquippedIndex);
                if (item == null)
                    return PlayerAdvisorBaselineAssembler.Failure(
                        PlayerAdvisorBaselineStatus.Unavailable,
                        $"Equipped index {position.EquippedIndex} ({position.PositionKey}) could not be read.",
                        snapshot,
                        before);
                if (item->ItemId == 0)
                {
                    equipped.Add(new(
                        position.EquippedIndex,
                        0,
                        EquipmentQuality.Normal,
                        family.RelevantSemantics.ToDictionary(semantic => semantic, _ => 0),
                        [],
                        []));
                    continue;
                }

                var itemId = item->GetBaseItemId();
                if (itemId == 0)
                    return PlayerAdvisorBaselineAssembler.Failure(
                        PlayerAdvisorBaselineStatus.Incomplete,
                        $"Equipped index {position.EquippedIndex} ({position.PositionKey}) has no base item identity.",
                        snapshot,
                        before);
                var contributions = new Dictionary<EquipmentStatSemantic, int>();
                foreach (var semantic in family.RelevantSemantics)
                {
                    var value = InventoryItem.GetParameterValue(
                        (uint)attributes[semantic],
                        item,
                        includeMateria: true,
                        checkHQ: true,
                        checkPvPCharacterFlag: false,
                        checkPvPItemFlag: false);
                    contributions.Add(semantic, checked((int)value));
                }

                var materiaIds = new List<uint>(5);
                var materiaGrades = new List<byte>(5);
                for (byte materiaIndex = 0; materiaIndex < 5; materiaIndex++)
                {
                    var materiaId = item->GetMateriaId(materiaIndex);
                    if (materiaId == 0)
                        continue;
                    materiaIds.Add(materiaId);
                    materiaGrades.Add(item->GetMateriaGrade(materiaIndex));
                }
                equipped.Add(new(
                    position.EquippedIndex,
                    itemId,
                    item->IsHighQuality() ? EquipmentQuality.High : EquipmentQuality.Normal,
                    contributions,
                    materiaIds,
                    materiaGrades));
            }

            if (!TryCaptureHeader(out var after, out diagnostic))
                return PlayerAdvisorBaselineAssembler.Failure(PlayerAdvisorBaselineStatus.Unavailable, diagnostic, snapshot, before);
            if (after != before)
                return PlayerAdvisorBaselineAssembler.Failure(
                    PlayerAdvisorBaselineStatus.Inconsistent,
                    "The player identity, job, world, or level changed while the advisor baseline was captured.",
                    snapshot,
                    before);

            return PlayerAdvisorBaselineAssembler.Assemble(snapshot, before, family, totals, equipped);
        }
        catch (Exception ex)
        {
            return PlayerAdvisorBaselineAssembler.Failure(
                PlayerAdvisorBaselineStatus.Unavailable,
                $"Windowless player baseline capture failed safely: {ex.Message}",
                snapshot,
                before);
        }
    }

    internal static bool TryMapPlayerAttribute(EquipmentStatSemantic semantic, out PlayerAttribute attribute)
    {
        attribute = semantic switch
        {
            EquipmentStatSemantic.Strength => PlayerAttribute.Strength,
            EquipmentStatSemantic.Dexterity => PlayerAttribute.Dexterity,
            EquipmentStatSemantic.Vitality => PlayerAttribute.Vitality,
            EquipmentStatSemantic.Intelligence => PlayerAttribute.Intelligence,
            EquipmentStatSemantic.Mind => PlayerAttribute.Mind,
            EquipmentStatSemantic.CriticalHit => PlayerAttribute.CriticalHit,
            EquipmentStatSemantic.Determination => PlayerAttribute.Determination,
            EquipmentStatSemantic.DirectHit => PlayerAttribute.DirectHitRate,
            EquipmentStatSemantic.SkillSpeed => PlayerAttribute.SkillSpeed,
            EquipmentStatSemantic.SpellSpeed => PlayerAttribute.SpellSpeed,
            EquipmentStatSemantic.Tenacity => PlayerAttribute.Tenacity,
            EquipmentStatSemantic.Piety => PlayerAttribute.Piety,
            EquipmentStatSemantic.Craftsmanship => PlayerAttribute.Craftsmanship,
            EquipmentStatSemantic.Control => PlayerAttribute.Control,
            EquipmentStatSemantic.CraftingPoints => PlayerAttribute.CraftingPoints,
            EquipmentStatSemantic.Gathering => PlayerAttribute.Gathering,
            EquipmentStatSemantic.Perception => PlayerAttribute.Perception,
            EquipmentStatSemantic.GatheringPoints => PlayerAttribute.GatheringPoints,
            EquipmentStatSemantic.PhysicalDamage => PlayerAttribute.PhysicalDamage,
            EquipmentStatSemantic.MagicalDamage => PlayerAttribute.MagicDamage,
            EquipmentStatSemantic.PhysicalDefense => PlayerAttribute.Defense,
            EquipmentStatSemantic.MagicalDefense => PlayerAttribute.MagicDefense,
            EquipmentStatSemantic.PiercingResistance => PlayerAttribute.PiercingResistance,
            _ => default,
        };
        return semantic is
            EquipmentStatSemantic.Strength or EquipmentStatSemantic.Dexterity or EquipmentStatSemantic.Vitality or
            EquipmentStatSemantic.Intelligence or EquipmentStatSemantic.Mind or EquipmentStatSemantic.CriticalHit or
            EquipmentStatSemantic.Determination or EquipmentStatSemantic.DirectHit or EquipmentStatSemantic.SkillSpeed or
            EquipmentStatSemantic.SpellSpeed or EquipmentStatSemantic.Tenacity or EquipmentStatSemantic.Piety or
            EquipmentStatSemantic.Craftsmanship or EquipmentStatSemantic.Control or EquipmentStatSemantic.CraftingPoints or
            EquipmentStatSemantic.Gathering or EquipmentStatSemantic.Perception or EquipmentStatSemantic.GatheringPoints or
            EquipmentStatSemantic.PhysicalDamage or EquipmentStatSemantic.MagicalDamage or EquipmentStatSemantic.PhysicalDefense or
            EquipmentStatSemantic.MagicalDefense or EquipmentStatSemantic.PiercingResistance;
    }

    private bool TryCaptureHeader(out PlayerAdvisorCaptureHeader? header, out string diagnostic)
    {
        header = null;
        if (!playerState.IsLoaded || playerState.ContentId == 0)
        {
            diagnostic = "No active player character is loaded.";
            return false;
        }
        var classJobId = playerState.ClassJob.RowId;
        var homeWorldId = playerState.HomeWorld.RowId;
        var currentWorldId = playerState.CurrentWorld.RowId;
        var name = playerState.CharacterName.ToString();
        if (classJobId == 0 || homeWorldId == 0 || currentWorldId == 0 || string.IsNullOrWhiteSpace(name))
        {
            diagnostic = "The active player header is incomplete.";
            return false;
        }
        header = new(
            new CharacterScope(playerState.ContentId, name, homeWorldId),
            currentWorldId,
            classJobId,
            playerState.Level,
            playerState.EffectiveLevel,
            playerState.IsLevelSynced);
        diagnostic = string.Empty;
        return true;
    }
}
