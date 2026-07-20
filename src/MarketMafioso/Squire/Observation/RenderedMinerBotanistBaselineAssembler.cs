using System;
using System.Collections.Generic;
using System.Linq;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Squire.Observation;

public enum RenderedMinerBotanistBaselineStatus
{
    Complete,
    Incomplete,
    Inconsistent,
}

public sealed record RenderedMinerBotanistBaseline(
    RenderedMinerBotanistBaselineStatus Status,
    uint? ClassJobId,
    int? Level,
    MinerBotanistUtilityStats? TotalStats,
    MinerBotanistUtilityStats? FixedStats,
    IReadOnlyList<RenderedEquipmentSlotObservation> EquippedSlots,
    string Diagnostic);

/// <summary>
/// Reconciles the Character stat pane with separately rendered item and materia bonuses. The
/// remainder is the non-offer contribution supplied to the exact loadout solver.
/// </summary>
public static class RenderedMinerBotanistBaselineAssembler
{
    public static RenderedMinerBotanistBaseline Assemble(
        RenderedAdvisorStatsObservation stats,
        RenderedEquipmentScanProgress equipment,
        IAdvisorStatFamily family)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(equipment);
        ArgumentNullException.ThrowIfNull(family);
        var classJobId = AdvisorStatFamilies.ClassJobIdForRenderedJob(stats.JobName) ?? 0u;
        if (stats.Status != RenderedCharacterObservationStatus.Complete || classJobId == 0 ||
            stats.Level is null || stats.Stats is null)
            return Incomplete("A complete rendered level, supported advisor job, and stat tuple is required.");
        if (equipment.Status != RenderedEquipmentScanStatus.Complete ||
            equipment.CompletedSlots != equipment.TotalSlots || equipment.TotalSlots != 12 ||
            equipment.Observations.Any(value => value.Item?.Status != RenderedItemDetailStatus.Complete))
            return Incomplete("All twelve stat-bearing equipment slots require complete rendered tooltip evidence.");

        var total = stats.Stats;
        var equippedFirst = 0L;
        var equippedSecond = 0L;
        var equippedThird = 0L;
        foreach (var item in equipment.Observations.Select(value => value.Item!))
        {
            var contribution = family.TripleFromRendered(item.Stats, item.MateriaStats);
            equippedFirst += contribution.First;
            equippedSecond += contribution.Second;
            equippedThird += contribution.Third;
        }

        var fixedFirst = total.First - equippedFirst;
        var fixedSecond = total.Second - equippedSecond;
        var fixedThird = total.Third - equippedThird;
        if (fixedFirst < 0 || fixedSecond < 0 || fixedThird < 0)
            return new(
                RenderedMinerBotanistBaselineStatus.Inconsistent,
                classJobId,
                stats.Level,
                ToUtilityStats(total),
                null,
                equipment.Observations,
                "Rendered item and materia bonuses exceed the rendered Character totals; the evidence generations cannot be reconciled.");

        return new(
            RenderedMinerBotanistBaselineStatus.Complete,
            classJobId,
            stats.Level,
            ToUtilityStats(total),
            new(checked((int)fixedFirst), checked((int)fixedSecond), checked((int)fixedThird)),
            equipment.Observations,
            $"Rendered {family.CoverageJobLabel} baseline and non-offer stat remainder are complete.");
    }

    private static MinerBotanistUtilityStats ToUtilityStats(AdvisorStatTriple triple) =>
        new(checked((int)triple.First), checked((int)triple.Second), checked((int)triple.Third));

    private static RenderedMinerBotanistBaseline Incomplete(string diagnostic) =>
        new(RenderedMinerBotanistBaselineStatus.Incomplete, null, null, null, null, [], diagnostic);
}
