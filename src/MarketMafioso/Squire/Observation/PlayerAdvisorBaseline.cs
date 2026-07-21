using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Squire.Observation;

public enum PlayerAdvisorBaselineStatus
{
    Complete,
    Unavailable,
    Incomplete,
    Inconsistent,
    Unsupported,
}

public sealed record PlayerAdvisorEquippedPosition(
    int EquippedIndex,
    EquipmentLoadoutPosition Position,
    string PositionKey);

public static class PlayerAdvisorEquippedSlotMap
{
    public static IReadOnlyList<PlayerAdvisorEquippedPosition> All { get; } =
    [
        new(0, EquipmentLoadoutPosition.MainHand, "main-hand"),
        new(1, EquipmentLoadoutPosition.OffHand, "off-hand"),
        new(2, EquipmentLoadoutPosition.Head, "head"),
        new(3, EquipmentLoadoutPosition.Body, "body"),
        new(4, EquipmentLoadoutPosition.Hands, "hands"),
        new(6, EquipmentLoadoutPosition.Legs, "legs"),
        new(7, EquipmentLoadoutPosition.Feet, "feet"),
        new(8, EquipmentLoadoutPosition.Ears, "ears"),
        new(9, EquipmentLoadoutPosition.Neck, "neck"),
        new(10, EquipmentLoadoutPosition.Wrists, "wrists"),
        new(11, EquipmentLoadoutPosition.RightRing, "ring-right"),
        new(12, EquipmentLoadoutPosition.LeftRing, "ring-left"),
    ];

    public static PlayerAdvisorEquippedPosition? Find(int equippedIndex) =>
        All.FirstOrDefault(value => value.EquippedIndex == equippedIndex);
}

public sealed record PlayerAdvisorEquippedSlot(
    EquipmentLoadoutPosition Position,
    string PositionKey,
    EquipmentInstanceSnapshot? Instance,
    EquipmentItemDefinition? Definition,
    EquipmentQuality? Quality,
    EquipmentSolverUtilityVector Utility,
    IReadOnlyList<uint> MateriaIds,
    IReadOnlyList<byte> MateriaGrades);

public sealed record PlayerAdvisorBaseline(
    PlayerAdvisorBaselineStatus Status,
    CharacterScope? Character,
    uint? ClassJobId,
    short? Level,
    short? EffectiveLevel,
    bool? IsLevelSynced,
    IReadOnlyDictionary<EquipmentStatSemantic, int> TotalStats,
    IReadOnlyDictionary<EquipmentStatSemantic, int> FixedStats,
    IReadOnlyList<PlayerAdvisorEquippedSlot> EquippedSlots,
    CharacterEquipmentSnapshot? EquipmentSnapshot,
    string Diagnostic);

public interface IPlayerAdvisorBaselineSource
{
    PlayerAdvisorBaseline Capture();
}

internal sealed record PlayerAdvisorCaptureHeader(
    CharacterScope Character,
    uint CurrentWorldId,
    uint ClassJobId,
    short Level,
    short EffectiveLevel,
    bool IsLevelSynced);

internal sealed record PlayerAdvisorEquippedItemCapture(
    int EquippedIndex,
    uint ItemId,
    EquipmentQuality Quality,
    IReadOnlyDictionary<EquipmentStatSemantic, int> Contributions,
    IReadOnlyList<uint> MateriaIds,
    IReadOnlyList<byte> MateriaGrades);

internal static class PlayerAdvisorBaselineAssembler
{
    private const string EquippedContainer = "EquippedItems";

    public static PlayerAdvisorBaseline Assemble(
        CharacterEquipmentSnapshot snapshot,
        PlayerAdvisorCaptureHeader header,
        IAdvisorStatFamily? family,
        IReadOnlyDictionary<EquipmentStatSemantic, int> totalStats,
        IReadOnlyList<PlayerAdvisorEquippedItemCapture> equipped)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(totalStats);
        ArgumentNullException.ThrowIfNull(equipped);

        if (family is null)
            return Result(PlayerAdvisorBaselineStatus.Unsupported, snapshot, header, totalStats, EmptyStats(), [],
                AdvisorStatFamilies.UnsupportedDiagnostic(header.ClassJobId));
        if (header.Level is < 1 or > 100 || header.EffectiveLevel is < 1 or > 100 || header.EffectiveLevel > header.Level)
            return Result(PlayerAdvisorBaselineStatus.Incomplete, snapshot, header, totalStats, EmptyStats(), [],
                "A valid level and effective level are required for the advisor baseline.");
        if (header.IsLevelSynced)
            return Result(PlayerAdvisorBaselineStatus.Unsupported, snapshot, header, totalStats, EmptyStats(), [],
                "The advisor abstains while level sync is active because synced item contributions are not yet modeled.");
        if (snapshot.Identity.Status != SnapshotComponentStatus.Complete || snapshot.Identity.Scope is null ||
            snapshot.Identity.Scope != header.Character || snapshot.Identity.ActiveClassJobId != header.ClassJobId)
            return Result(PlayerAdvisorBaselineStatus.Inconsistent, snapshot, header, totalStats, EmptyStats(), [],
                "The character equipment snapshot identity does not match the advisor capture header.");
        if (!ComponentIsComplete(snapshot, "equipped"))
            return Result(PlayerAdvisorBaselineStatus.Incomplete, snapshot, header, totalStats, EmptyStats(), [],
                "The equipped-items component is incomplete or unavailable.");
        if (family.RelevantSemantics.Any(semantic => !totalStats.ContainsKey(semantic)))
            return Result(PlayerAdvisorBaselineStatus.Unavailable, snapshot, header, totalStats, EmptyStats(), [],
                "One or more family-relevant player attributes are unavailable.");

        var capturesByIndex = equipped
            .GroupBy(value => value.EquippedIndex)
            .ToDictionary(group => group.Key, group => group.ToArray());
        if (PlayerAdvisorEquippedSlotMap.All.Any(position =>
                !capturesByIndex.TryGetValue(position.EquippedIndex, out var captures) || captures.Length != 1))
            return Result(PlayerAdvisorBaselineStatus.Incomplete, snapshot, header, totalStats, EmptyStats(), [],
                "All twelve explicit equipped indices require one current item capture.");

        var slots = new List<PlayerAdvisorEquippedSlot>(PlayerAdvisorEquippedSlotMap.All.Count);
        var equippedTotals = family.RelevantSemantics.ToDictionary(semantic => semantic, _ => 0);
        foreach (var position in PlayerAdvisorEquippedSlotMap.All)
        {
            var captured = capturesByIndex[position.EquippedIndex][0];
            var instances = snapshot.Instances.Where(value =>
                    value.IsEquipped &&
                    string.Equals(value.Fingerprint.Container, EquippedContainer, StringComparison.Ordinal) &&
                    value.Fingerprint.SlotIndex == position.EquippedIndex)
                .ToArray();
            if (captured.ItemId == 0)
            {
                if (instances.Length != 0 || captured.MateriaIds.Count != 0 || captured.MateriaGrades.Count != 0 ||
                    family.RelevantSemantics.Any(semantic => captured.Contributions.GetValueOrDefault(semantic) != 0))
                    return Result(PlayerAdvisorBaselineStatus.Inconsistent, snapshot, header, totalStats, EmptyStats(), [],
                        $"Empty equipped index {position.EquippedIndex} has contradictory item or stat evidence.");
                slots.Add(new(
                    position.Position,
                    position.PositionKey,
                    null,
                    null,
                    null,
                    family.VectorFromSemantics(captured.Contributions),
                    [],
                    []));
                continue;
            }
            if (instances.Length != 1)
                return Result(PlayerAdvisorBaselineStatus.Incomplete, snapshot, header, totalStats, EmptyStats(), [],
                    $"Equipped index {position.EquippedIndex} is missing or duplicated in the character equipment snapshot.");
            var instance = instances[0];
            if (instance.Fingerprint.ItemId != captured.ItemId ||
                instance.Fingerprint.IsHighQuality != (captured.Quality == EquipmentQuality.High) ||
                !instance.Fingerprint.MateriaIds.SequenceEqual(captured.MateriaIds))
                return Result(PlayerAdvisorBaselineStatus.Inconsistent, snapshot, header, totalStats, EmptyStats(), [],
                    $"Equipped index {position.EquippedIndex} changed while the advisor baseline was captured.");
            if (!snapshot.Definitions.TryGetValue(captured.ItemId, out var definition))
                return Result(PlayerAdvisorBaselineStatus.Incomplete, snapshot, header, totalStats, EmptyStats(), [],
                    $"Equipped index {position.EquippedIndex} item {captured.ItemId} has no static equipment definition.");
            if (definition.ItemId != captured.ItemId || !DefinitionMatchesPosition(definition, position.Position))
                return Result(PlayerAdvisorBaselineStatus.Inconsistent, snapshot, header, totalStats, EmptyStats(), [],
                    $"Equipped index {position.EquippedIndex} definition does not match {position.PositionKey}.");
            if (!definition.EligibleClassJobIds.Contains(header.ClassJobId))
                return Result(PlayerAdvisorBaselineStatus.Inconsistent, snapshot, header, totalStats, EmptyStats(), [],
                    $"Equipped index {position.EquippedIndex} item {captured.ItemId} is not eligible for class/job {header.ClassJobId}.");
            if (definition.EquipLevel > header.Level)
                return Result(PlayerAdvisorBaselineStatus.Inconsistent, snapshot, header, totalStats, EmptyStats(), [],
                    $"Equipped index {position.EquippedIndex} item {captured.ItemId} requires level {definition.EquipLevel}, above the actual level {header.Level}.");
            if (family.RelevantSemantics.Any(semantic => !captured.Contributions.ContainsKey(semantic)))
                return Result(PlayerAdvisorBaselineStatus.Unavailable, snapshot, header, totalStats, EmptyStats(), [],
                    $"Equipped index {position.EquippedIndex} is missing a family-relevant exact stat contribution.");

            foreach (var semantic in family.RelevantSemantics)
                equippedTotals[semantic] = checked(equippedTotals[semantic] + captured.Contributions[semantic]);
            slots.Add(new(
                position.Position,
                position.PositionKey,
                instance,
                definition,
                captured.Quality,
                family.VectorFromSemantics(captured.Contributions),
                captured.MateriaIds,
                captured.MateriaGrades));
        }

        var fixedStats = new Dictionary<EquipmentStatSemantic, int>();
        foreach (var semantic in family.RelevantSemantics)
        {
            var remainder = checked(totalStats[semantic] - equippedTotals[semantic]);
            if (remainder < 0)
                return Result(PlayerAdvisorBaselineStatus.Inconsistent, snapshot, header, totalStats, EmptyStats(), slots,
                    $"Exact equipped {semantic} contributions exceed the player total.");
            fixedStats[semantic] = remainder;
        }

        return Result(
            PlayerAdvisorBaselineStatus.Complete,
            snapshot,
            header,
            totalStats,
            fixedStats,
            slots,
            $"Windowless {family.CoverageJobLabel} player baseline is complete.");
    }

    public static PlayerAdvisorBaseline Failure(
        PlayerAdvisorBaselineStatus status,
        string diagnostic,
        CharacterEquipmentSnapshot? snapshot = null,
        PlayerAdvisorCaptureHeader? header = null) =>
        new(
            status,
            header?.Character ?? snapshot?.Identity.Scope,
            header?.ClassJobId ?? snapshot?.Identity.ActiveClassJobId,
            header?.Level,
            header?.EffectiveLevel,
            header?.IsLevelSynced,
            new Dictionary<EquipmentStatSemantic, int>(),
            new Dictionary<EquipmentStatSemantic, int>(),
            [],
            snapshot,
            diagnostic);

    private static bool DefinitionMatchesPosition(
        EquipmentItemDefinition definition,
        EquipmentLoadoutPosition position) => position switch
    {
        EquipmentLoadoutPosition.MainHand => definition.Slot == EquipmentSlot.MainHand,
        EquipmentLoadoutPosition.OffHand => definition.Slot == EquipmentSlot.OffHand,
        EquipmentLoadoutPosition.Head => definition.Slot == EquipmentSlot.Head,
        EquipmentLoadoutPosition.Body => definition.Slot == EquipmentSlot.Body,
        EquipmentLoadoutPosition.Hands => definition.Slot == EquipmentSlot.Hands,
        EquipmentLoadoutPosition.Legs => definition.Slot == EquipmentSlot.Legs,
        EquipmentLoadoutPosition.Feet => definition.Slot == EquipmentSlot.Feet,
        EquipmentLoadoutPosition.Ears => definition.Slot == EquipmentSlot.Ears,
        EquipmentLoadoutPosition.Neck => definition.Slot == EquipmentSlot.Neck,
        EquipmentLoadoutPosition.Wrists => definition.Slot == EquipmentSlot.Wrists,
        EquipmentLoadoutPosition.LeftRing or EquipmentLoadoutPosition.RightRing => definition.Slot == EquipmentSlot.Ring,
        _ => false,
    };

    private static IReadOnlyDictionary<EquipmentStatSemantic, int> EmptyStats() =>
        new Dictionary<EquipmentStatSemantic, int>();

    private static bool ComponentIsComplete(CharacterEquipmentSnapshot snapshot, string component) =>
        snapshot.Diagnostics.Components.Any(value =>
            string.Equals(value.Component, component, StringComparison.Ordinal) &&
            value.Status == SnapshotComponentStatus.Complete);

    private static PlayerAdvisorBaseline Result(
        PlayerAdvisorBaselineStatus status,
        CharacterEquipmentSnapshot snapshot,
        PlayerAdvisorCaptureHeader header,
        IReadOnlyDictionary<EquipmentStatSemantic, int> totalStats,
        IReadOnlyDictionary<EquipmentStatSemantic, int> fixedStats,
        IReadOnlyList<PlayerAdvisorEquippedSlot> slots,
        string diagnostic) =>
        new(
            status,
            header.Character,
            header.ClassJobId,
            header.Level,
            header.EffectiveLevel,
            header.IsLevelSynced,
            totalStats,
            fixedStats,
            slots,
            snapshot,
            diagnostic);
}
