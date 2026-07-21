using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Player;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using Lumina.Excel.Sheets;
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
    private readonly IDataManager dataManager;

    public DalamudPlayerAdvisorBaselineSource(
        ICharacterEquipmentSnapshotSource snapshotSource,
        IPlayerState playerState,
        IDataManager dataManager)
    {
        this.snapshotSource = snapshotSource;
        this.playerState = playerState;
        this.dataManager = dataManager;
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
                totals.Add(semantic, value);
            }

            var baseParamSheet = dataManager.GetExcelSheet<BaseParam>();
            if (baseParamSheet is null || !TryResolveBaseParamIds(
                    baseParamSheet.Select(value => (value.RowId, (string?)value.Name.ToString())),
                    family.RelevantSemantics,
                    out var baseParamIds,
                    out diagnostic))
                return PlayerAdvisorBaselineAssembler.Failure(
                    PlayerAdvisorBaselineStatus.Unavailable,
                    baseParamSheet is null ? "The BaseParam sheet is unavailable." : diagnostic,
                    snapshot,
                    before);

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
                        baseParamIds[semantic],
                        item,
                        includeMateria: true,
                        checkHQ: true,
                        checkPvPCharacterFlag: false,
                        checkPvPItemFlag: false);
                    if (value > int.MaxValue)
                        return PlayerAdvisorBaselineAssembler.Failure(
                            PlayerAdvisorBaselineStatus.Unavailable,
                            $"Equipped index {position.EquippedIndex} ({position.PositionKey}) returned an invalid {semantic} contribution.",
                            snapshot,
                            before);
                    contributions.Add(semantic, (int)value);
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

            var trustedCapture = PlayerAdvisorTrustedCapture.Complete(Guid.NewGuid(), DateTimeOffset.UtcNow);
            return PlayerAdvisorBaselineAssembler.Assemble(snapshot, before, family, totals, equipped, trustedCapture);
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

    internal static bool TryResolveBaseParamIds(
        IEnumerable<(uint RowId, string? Name)> rows,
        IEnumerable<EquipmentStatSemantic> requiredSemantics,
        out IReadOnlyDictionary<EquipmentStatSemantic, uint> ids,
        out string diagnostic)
    {
        var bySemantic = rows
            .Where(value => value.RowId != 0)
            .Select(value => (value.RowId, Semantic: DalamudCharacterEquipmentSnapshotSource.MapStatSemantic(value.RowId, value.Name)))
            .Where(value => value.Semantic != EquipmentStatSemantic.Unknown)
            .GroupBy(value => value.Semantic)
            .ToDictionary(group => group.Key, group => group.Select(value => value.RowId).Distinct().ToArray());
        var resolved = new Dictionary<EquipmentStatSemantic, uint>();
        foreach (var semantic in requiredSemantics.Distinct())
        {
            if (!bySemantic.TryGetValue(semantic, out var matches) || matches.Length != 1)
            {
                ids = new Dictionary<EquipmentStatSemantic, uint>();
                diagnostic = matches is { Length: > 1 }
                    ? $"BaseParam has multiple rows for advisor semantic {semantic}."
                    : $"BaseParam has no row for advisor semantic {semantic}.";
                return false;
            }
            resolved.Add(semantic, matches[0]);
        }
        ids = resolved;
        diagnostic = string.Empty;
        return true;
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
