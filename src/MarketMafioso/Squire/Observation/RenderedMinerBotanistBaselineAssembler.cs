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
        RenderedGatheringStatsObservation stats,
        RenderedEquipmentScanProgress equipment)
    {
        var classJobId = stats.JobName switch
        {
            "Miner" => MinerBotanistUtilityProfile.MinerClassJobId,
            "Botanist" => MinerBotanistUtilityProfile.BotanistClassJobId,
            _ => 0u,
        };
        if (stats.Status != RenderedCharacterObservationStatus.Complete || classJobId == 0 ||
            stats.Level is null || stats.Gathering is null || stats.Perception is null || stats.GatheringPoints is null)
            return Incomplete("A complete rendered level, MIN/BTN job, Gathering, Perception, and GP tuple is required.");
        if (equipment.Status != RenderedEquipmentScanStatus.Complete ||
            equipment.CompletedSlots != equipment.TotalSlots || equipment.TotalSlots != 12 ||
            equipment.Observations.Any(value => value.Item?.Status != RenderedItemDetailStatus.Complete))
            return Incomplete("All twelve stat-bearing equipment slots require complete rendered tooltip evidence.");

        var total = new MinerBotanistUtilityStats(stats.Gathering.Value, stats.Perception.Value, stats.GatheringPoints.Value);
        var equippedGathering = 0;
        var equippedPerception = 0;
        var equippedGp = 0;
        foreach (var item in equipment.Observations.Select(value => value.Item!))
        {
            equippedGathering += Read(item.Stats, "Gathering") + Read(item.MateriaStats, "Gathering");
            equippedPerception += Read(item.Stats, "Perception") + Read(item.MateriaStats, "Perception");
            equippedGp += Read(item.Stats, "GP") + Read(item.MateriaStats, "GP");
        }

        var fixedStats = new MinerBotanistUtilityStats(
            total.Gathering - equippedGathering,
            total.Perception - equippedPerception,
            total.GatheringPoints - equippedGp);
        if (fixedStats.Gathering < 0 || fixedStats.Perception < 0 || fixedStats.GatheringPoints < 0)
            return new(
                RenderedMinerBotanistBaselineStatus.Inconsistent,
                classJobId,
                stats.Level,
                total,
                null,
                equipment.Observations,
                "Rendered item and materia bonuses exceed the rendered Character totals; the evidence generations cannot be reconciled.");

        return new(
            RenderedMinerBotanistBaselineStatus.Complete,
            classJobId,
            stats.Level,
            total,
            fixedStats,
            equipment.Observations,
            "Rendered MIN/BTN baseline and non-offer stat remainder are complete.");
    }

    private static int Read(IReadOnlyDictionary<string, int> stats, string key) =>
        stats.TryGetValue(key, out var value) ? value : 0;

    private static RenderedMinerBotanistBaseline Incomplete(string diagnostic) =>
        new(RenderedMinerBotanistBaselineStatus.Incomplete, null, null, null, null, [], diagnostic);
}
