using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Observation;

public enum RenderedEquipmentResolutionStatus
{
    Complete,
    Unresolved,
    Ambiguous,
}

public sealed record ResolvedRenderedEquipmentSlot(
    string PositionKey,
    EquipmentLoadoutPosition Position,
    EquipmentItemDefinition Definition,
    EquipmentQuality Quality,
    EquipmentLoadoutOffer BaselineOffer);

public sealed record RenderedEquipmentResolution(
    RenderedEquipmentResolutionStatus Status,
    IReadOnlyList<ResolvedRenderedEquipmentSlot> Slots,
    string Diagnostic);

/// <summary>
/// Resolves name-first rendered observations against version-matched static definitions. Static
/// data enriches a complete UI observation; it never supplies missing identity or quality evidence.
/// </summary>
public static class RenderedEquipmentDefinitionResolver
{
    public static RenderedEquipmentResolution Resolve(
        IReadOnlyList<RenderedEquipmentSlotObservation> observations,
        uint classJobId,
        Func<string, IReadOnlyList<EquipmentItemDefinition>> findByExactName)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(findByExactName);
        var resolved = new List<ResolvedRenderedEquipmentSlot>();
        foreach (var observation in observations)
        {
            if (observation.Item is not { Status: RenderedItemDetailStatus.Complete, Name: { } name,
                    Quality: { } renderedQuality, ItemLevel: { } itemLevel, EquipLevel: { } equipLevel })
                return Unresolved($"{observation.PositionKey} has no complete rendered item identity tuple.");
            var quality = renderedQuality == RenderedItemQuality.High ? EquipmentQuality.High : EquipmentQuality.Normal;
            var namedCandidates = findByExactName(name);
            var candidates = namedCandidates
                .Where(value => string.Equals(value.Name, name, StringComparison.Ordinal))
                .Where(value => value.ItemLevel == itemLevel && value.EquipLevel == equipLevel)
                .Where(value => value.Slot == observation.Slot)
                .Where(value => value.EligibleClassJobIds.Contains(classJobId))
                .Where(value => value.ResolveStatProfile(quality) is { IsComplete: true } profile &&
                    RelevantRenderedStatsMatch(observation.Item.Stats, profile))
                .ToArray();
            if (candidates.Length == 0)
                return Unresolved(
                    $"Rendered {observation.PositionKey} item '{name}' did not match one complete static definition for the observed quality, levels, slot, and job. " +
                    DescribeCandidates(namedCandidates, observation, classJobId, quality));
            if (candidates.Length > 1)
                return new(RenderedEquipmentResolutionStatus.Ambiguous, [],
                    $"Rendered {observation.PositionKey} item '{name}' matched {candidates.Length} static definitions; raw IDs will not be used to guess.");

            var position = ToPosition(observation.PositionKey);
            if (position is null)
                return Unresolved($"Rendered equipment position '{observation.PositionKey}' is unsupported.");
            var definition = candidates[0];
            var offer = new EquipmentLoadoutOffer(
                definition,
                EquipmentAcquisitionSourceKind.Owned,
                "Currently equipped · rendered UI",
                UnitPriceGil: 0,
                Quality: quality,
                SourceCatalogKey: $"rendered-current:{observation.PositionKey}");
            resolved.Add(new(observation.PositionKey, position.Value, definition, quality, offer));
        }
        return new(RenderedEquipmentResolutionStatus.Complete, resolved, "All rendered equipment identities resolved uniquely against static definitions.");
    }

    private static bool RelevantRenderedStatsMatch(
        IReadOnlyDictionary<string, int> rendered,
        EquipmentStatProfile profile)
    {
        int Rendered(string name) => rendered.GetValueOrDefault(name);
        int Defined(EquipmentStatSemantic semantic) => profile.Parameters
            .Where(value => value.Semantic == semantic)
            .Sum(value => value.Value);
        return Rendered("Gathering") == Defined(EquipmentStatSemantic.Gathering) &&
            Rendered("Perception") == Defined(EquipmentStatSemantic.Perception) &&
            Rendered("GP") == Defined(EquipmentStatSemantic.GatheringPoints);
    }

    private static string DescribeCandidates(
        IReadOnlyList<EquipmentItemDefinition> candidates,
        RenderedEquipmentSlotObservation observation,
        uint classJobId,
        EquipmentQuality quality)
    {
        if (candidates.Count == 0)
            return "No exact-name static candidates exist in the current game data.";
        var rendered = observation.Item!;
        return "Diagnostic candidates: " + string.Join("; ", candidates.Take(6).Select(candidate =>
        {
            var profile = candidate.ResolveStatProfile(quality);
            var defined = profile is null
                ? "profile=missing"
                : $"profile={(profile.IsComplete ? "complete" : "incomplete")},stats={FormatRelevantStats(profile)}";
            return $"id={candidate.ItemId},ilvl={candidate.ItemLevel},equip={candidate.EquipLevel},slot={candidate.Slot},job={candidate.EligibleClassJobIds.Contains(classJobId)},quality={quality},{defined},rendered={FormatRelevantStats(rendered.Stats)}";
        }));
    }

    private static string FormatRelevantStats(EquipmentStatProfile profile) =>
        $"G{Defined(profile, EquipmentStatSemantic.Gathering)}/P{Defined(profile, EquipmentStatSemantic.Perception)}/GP{Defined(profile, EquipmentStatSemantic.GatheringPoints)}";

    private static string FormatRelevantStats(IReadOnlyDictionary<string, int> rendered) =>
        $"G{rendered.GetValueOrDefault("Gathering")}/P{rendered.GetValueOrDefault("Perception")}/GP{rendered.GetValueOrDefault("GP")}";

    private static int Defined(EquipmentStatProfile profile, EquipmentStatSemantic semantic) =>
        profile.Parameters.Where(value => value.Semantic == semantic).Sum(value => value.Value);

    private static EquipmentLoadoutPosition? ToPosition(string key) => key switch
    {
        "main-hand" => EquipmentLoadoutPosition.MainHand,
        "off-hand" => EquipmentLoadoutPosition.OffHand,
        "head" => EquipmentLoadoutPosition.Head,
        "body" => EquipmentLoadoutPosition.Body,
        "hands" => EquipmentLoadoutPosition.Hands,
        "legs" => EquipmentLoadoutPosition.Legs,
        "feet" => EquipmentLoadoutPosition.Feet,
        "ears" => EquipmentLoadoutPosition.Ears,
        "neck" => EquipmentLoadoutPosition.Neck,
        "wrists" => EquipmentLoadoutPosition.Wrists,
        "ring-left" => EquipmentLoadoutPosition.LeftRing,
        "ring-right" => EquipmentLoadoutPosition.RightRing,
        _ => null,
    };

    private static RenderedEquipmentResolution Unresolved(string diagnostic) =>
        new(RenderedEquipmentResolutionStatus.Unresolved, [], diagnostic);
}
