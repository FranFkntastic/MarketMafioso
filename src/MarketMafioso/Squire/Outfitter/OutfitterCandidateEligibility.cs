using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Outfitter;

/// <summary>
/// Final source-independent candidate boundary for the general loadout planner. Technical
/// equipability is necessary but not sufficient: an offer must contribute a stat the target
/// discipline can actually use.
/// </summary>
public static class OutfitterCandidateEligibility
{
    public static bool IsRecommendable(CharacterJobSnapshot job, EquipmentLoadoutOffer offer)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(offer);
        if (!offer.Definition.EligibleClassJobIds.Contains(job.ClassJobId))
            return false;
        var profile = offer.ResolveStatProfile();
        return profile is { IsComplete: true } && HasRelevantStats(
            job,
            offer.Definition.Slot,
            profile.PhysicalDamage,
            profile.MagicalDamage,
            profile.Parameters);
    }

    internal static bool HasRelevantStats(
        CharacterJobSnapshot job,
        EquipmentSlot slot,
        int physicalDamage,
        int magicalDamage,
        IReadOnlyList<EquipmentStatValue> parameters)
    {
        var supplied = parameters.Where(value => value.Value > 0).Select(value => value.Semantic).ToHashSet();
        return job.Discipline switch
        {
            EquipmentDiscipline.Combat =>
                (slot is EquipmentSlot.MainHand or EquipmentSlot.OffHand && (physicalDamage > 0 || magicalDamage > 0)) ||
                (job.PrimaryStat is { } primary && primary != EquipmentStatSemantic.Unknown && supplied.Contains(primary)),
            EquipmentDiscipline.Crafter => supplied.Overlaps([
                EquipmentStatSemantic.Craftsmanship,
                EquipmentStatSemantic.Control,
                EquipmentStatSemantic.CraftingPoints]),
            EquipmentDiscipline.Gatherer => supplied.Overlaps([
                EquipmentStatSemantic.Gathering,
                EquipmentStatSemantic.Perception,
                EquipmentStatSemantic.GatheringPoints]),
            _ => false,
        };
    }
}
