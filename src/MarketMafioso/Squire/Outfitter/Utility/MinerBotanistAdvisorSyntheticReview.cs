#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Outfitter.Utility;

/// <summary>
/// Privacy-minimized debug replay derived from the frozen model-gearset challenge family.
/// It contains no source identities, URLs, or expected answers and can never enter release builds.
/// Costs are deliberately illustrative so the real cost/utility presentation can be reviewed.
/// </summary>
internal static class MinerBotanistAdvisorSyntheticReview
{
    private sealed record Benchmark(
        string Id,
        string Label,
        ulong IllustrativeCostGil,
        int BurdenRank,
        MinerBotanistUtilityStats Stats,
        string[] Assumptions,
        bool UsesFood = false,
        bool IsDerivedAdversarial = false);

    private static readonly EquipmentLoadoutPosition[] Positions =
    [
        EquipmentLoadoutPosition.MainHand,
        EquipmentLoadoutPosition.OffHand,
        EquipmentLoadoutPosition.Head,
        EquipmentLoadoutPosition.Body,
        EquipmentLoadoutPosition.Hands,
        EquipmentLoadoutPosition.Legs,
        EquipmentLoadoutPosition.Feet,
        EquipmentLoadoutPosition.Ears,
        EquipmentLoadoutPosition.Neck,
        EquipmentLoadoutPosition.Wrists,
        EquipmentLoadoutPosition.LeftRing,
        EquipmentLoadoutPosition.RightRing,
    ];

    private static readonly Benchmark[] Benchmarks =
    [
        new("published-budget-raw", "Budget stopping point", 0, 0,
            new(4_879, 5_444, 884), ["No food", "Synthetic equipped baseline"]),
        new("published-budget-cloudsail", "Budget set with food", 18_000, 1,
            new(4_970, 5_620, 884), ["Recurring consumable", "Food remains outside JobUtilityScore"], UsesFood: true),
        new("published-mid-raw", "Mid-tier meld set", 220_000, 2,
            new(5_510, 5_470, 904), ["No food", "Mixed NQ/HQ acquisition example"]),
        new("published-high-raw", "High-tier meld set", 750_000, 3,
            new(5_700, 5_504, 995), ["No food", "HQ-heavy acquisition example"]),
        new("derived-high-regression", "Weaker, dearer adversarial set", 900_000, 4,
            new(5_690, 5_494, 985), ["Synthetic dominated witness"], IsDerivedAdversarial: true),
        new("derived-high-cost-only", "Identical-stat dearer adversarial set", 1_050_000, 4,
            new(5_700, 5_504, 995), ["Synthetic cost-only domination witness"], IsDerivedAdversarial: true),
    ];

    public static MinerBotanistReadOnlyAdvice Build(MinerBotanistUtilityContextKind context)
    {
        var baseline = Benchmarks[0];
        var profile = new MinerBotanistUtilityProfile(
            context,
            baseline.Stats,
            MinerBotanistUtilityProfile.MinerClassJobId);
        var offers = new Dictionary<EquipmentOfferAllocationKey, EquipmentExactSolverOffer>();
        var solutions = Benchmarks.Select((benchmark, index) =>
            BuildSolution(benchmark, index, profile, offers)).ToArray();
        var pareto = new EquipmentParetoFrontierBuilder().Build(solutions);
        var authority = pareto.Frontier.ToDictionary(
            solution => solution.Candidate.SolutionId,
            solution => profile.AssessAuthority(
                solution.Utility,
                solution.AcquisitionCostGil,
                hasUnmodeledRelevantEffect: Benchmarks.First(value => value.Id == solution.Candidate.SolutionId).UsesFood ||
                    context == MinerBotanistUtilityContextKind.CollectableEfficiency &&
                    solution.Candidate.SolutionId == "published-high-raw"),
            StringComparer.Ordinal);
        var nomination = pareto.Frontier
            .Where(solution => authority[solution.Candidate.SolutionId].AdvisorMayConsider)
            .OrderBy(solution => solution.AcquisitionCostGil)
            .ThenByDescending(solution => solution.Utility.UtilityScore)
            .FirstOrDefault();
        var exact = new EquipmentExactFrontierResult(
            pareto,
            new(
                ExpandedStateCount: 0,
                InfeasibleTransitionCount: 0,
                DominatedStateCount: pareto.Dominated.Count,
                CompactedEquivalentStateCount: pareto.EquivalenceGroups.Count,
                PeakRetainedStateCount: pareto.Frontier.Count,
                CompleteSolutionCount: solutions.Length,
                ExactCompleteVariantCount: solutions.Length,
                EquivalentRepresentativeLimit: 16,
                BaselineSolutionId: baseline.Id,
                Elapsed: TimeSpan.Zero),
            []);
        return new(
            MinerBotanistAdvisorStatus.Complete,
            MinerBotanistReadOnlyAdvisor.AdvisoryRule,
            exact,
            nomination,
            authority,
            offers,
            nomination is null
                ? "Synthetic replay is complete; the advisor abstains under the displayed rule."
                : $"Synthetic replay nominates {nomination.VariantLabels[0]} under the displayed rule.");
    }

    private static EquipmentDecisionSolution BuildSolution(
        Benchmark benchmark,
        int benchmarkIndex,
        MinerBotanistUtilityProfile profile,
        IDictionary<EquipmentOfferAllocationKey, EquipmentExactSolverOffer> offers)
    {
        var selections = new List<EquipmentLoadoutSelection>(Positions.Length);
        var baseSlotCost = benchmark.IllustrativeCostGil / (ulong)Positions.Length;
        var remainder = benchmark.IllustrativeCostGil % (ulong)Positions.Length;
        for (var positionIndex = 0; positionIndex < Positions.Length; positionIndex++)
        {
            var position = Positions[positionIndex];
            var quality = Quality(benchmark.Id, positionIndex);
            var sourceKind = benchmark.IllustrativeCostGil == 0
                ? EquipmentAcquisitionSourceKind.Owned
                : EquipmentAcquisitionSourceKind.MarketBoard;
            var itemId = checked(8_900_000u + (uint)(benchmarkIndex * 100) + (uint)positionIndex);
            var definition = new EquipmentItemDefinition(
                ItemId: itemId,
                Name: $"Synthetic {benchmark.Label} — {PositionLabel(position)}",
                EquipLevel: 100,
                ItemLevel: 750,
                Slot: Slot(position),
                EligibleClassJobIds: new HashSet<uint> { MinerBotanistUtilityProfile.MinerClassJobId, MinerBotanistUtilityProfile.BotanistClassJobId },
                Rarity: 3,
                IsEquipment: true,
                IsSoulCrystal: false,
                IsDesynthesizable: null,
                IsVendorSellable: null,
                VendorSellPrice: null,
                IsDiscardable: null,
                IsArmoireEligible: null,
                IsRecoverable: null,
                IsExplicitlyProtectedFamily: false);
            var sourceLabel = sourceKind == EquipmentAcquisitionSourceKind.Owned
                ? "Synthetic equipped baseline"
                : "Synthetic market evidence · illustrative price";
            var offer = new EquipmentLoadoutOffer(
                definition,
                sourceKind,
                sourceLabel,
                UnitPriceGil: checked((uint)(baseSlotCost + (positionIndex == 0 ? remainder : 0))),
                Quality: quality,
                SourceCatalogKey: $"synthetic-review:{benchmark.Id}:{position}");
            var exact = new EquipmentExactSolverOffer(
                offer,
                ObservationId: $"synthetic-review:{benchmark.Id}:{position}",
                Positions: new HashSet<EquipmentLoadoutPosition> { position },
                AvailableQuantity: 1,
                Utility: EquipmentSolverUtilityVector.Empty,
                AcquisitionCostGil: baseSlotCost + (positionIndex == 0 ? remainder : 0),
                WorldVisitKey: sourceKind == EquipmentAcquisitionSourceKind.MarketBoard ? "synthetic-world" : null,
                VendorStopKey: null,
                PurchaseTransactions: sourceKind == EquipmentAcquisitionSourceKind.MarketBoard ? 1 : 0,
                EvidenceRisk: new(0, 0, 0),
                VariantLabels: [quality == EquipmentQuality.High ? "HQ" : "NQ", "Synthetic replay"]);
            offers.Add(exact.AllocationKey, exact);
            selections.Add(new(position, offer.Key, ObservationId: exact.ObservationId));
        }

        var labels = new List<string> { benchmark.Label, "Synthetic replay", $"Stats {benchmark.Stats.Gathering}/{benchmark.Stats.Perception}/{benchmark.Stats.GatheringPoints}" };
        labels.AddRange(benchmark.Assumptions);
        if (benchmark.IsDerivedAdversarial)
            labels.Add("Adversarial witness");
        return new(
            new(benchmark.Id, selections),
            profile.Evaluate(benchmark.Stats),
            benchmark.IllustrativeCostGil,
            new(
                WorldVisits: benchmark.IllustrativeCostGil == 0 ? 0 : 1,
                VendorStops: 0,
                PurchaseTransactions: benchmark.IllustrativeCostGil == 0 ? 0 : benchmark.BurdenRank + 1),
            new(0, 0, 0),
            labels);
    }

    private static EquipmentQuality Quality(string benchmarkId, int positionIndex) => benchmarkId switch
    {
        "published-budget-raw" or "published-budget-cloudsail" => EquipmentQuality.Normal,
        "published-mid-raw" => positionIndex % 2 == 0 ? EquipmentQuality.High : EquipmentQuality.Normal,
        _ => EquipmentQuality.High,
    };

    private static EquipmentSlot Slot(EquipmentLoadoutPosition position) => position switch
    {
        EquipmentLoadoutPosition.MainHand => EquipmentSlot.MainHand,
        EquipmentLoadoutPosition.OffHand => EquipmentSlot.OffHand,
        EquipmentLoadoutPosition.Head => EquipmentSlot.Head,
        EquipmentLoadoutPosition.Body => EquipmentSlot.Body,
        EquipmentLoadoutPosition.Hands => EquipmentSlot.Hands,
        EquipmentLoadoutPosition.Legs => EquipmentSlot.Legs,
        EquipmentLoadoutPosition.Feet => EquipmentSlot.Feet,
        EquipmentLoadoutPosition.Ears => EquipmentSlot.Ears,
        EquipmentLoadoutPosition.Neck => EquipmentSlot.Neck,
        EquipmentLoadoutPosition.Wrists => EquipmentSlot.Wrists,
        EquipmentLoadoutPosition.LeftRing or EquipmentLoadoutPosition.RightRing => EquipmentSlot.Ring,
        _ => throw new ArgumentOutOfRangeException(nameof(position), position, null),
    };

    private static string PositionLabel(EquipmentLoadoutPosition position) => position switch
    {
        EquipmentLoadoutPosition.LeftRing => "Left ring",
        EquipmentLoadoutPosition.RightRing => "Right ring",
        _ => position.ToString(),
    };
}
#endif
