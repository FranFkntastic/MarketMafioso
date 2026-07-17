#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Outfitter.Utility;

/// <summary>
/// Privacy-minimized debug replay derived from the frozen model-gearset challenge family.
/// Runtime builds never load the research oracle. Item identities are stable game data; gil
/// estimates are a frozen Aether sale-history snapshot so UI review remains deterministic.
/// </summary>
internal static class MinerBotanistAdvisorSyntheticReview
{
    internal const string PriceEvidenceLabel = "Aether sale-history median · 2026-07-16";

    private sealed record Benchmark(
        string Id,
        string Label,
        MinerBotanistUtilityStats Stats,
        string[] Assumptions,
        bool IsDerivedAdversarial = false,
        int PriceScalePermille = 1000);

    private sealed record MateriaEvidence(
        uint ItemId,
        int Tier,
        ulong UnitPriceGil);

    private sealed record ItemEvidence(
        EquipmentLoadoutPosition Position,
        uint ItemId,
        string Name,
        uint ItemLevel,
        EquipmentQuality Quality,
        EquipmentAcquisitionSourceKind SourceKind,
        ulong GearCostGil,
        int GuaranteedMateriaSlots,
        IReadOnlyList<MateriaEvidence> Materia);

    private static readonly Benchmark[] Benchmarks =
    [
        new("crafted-unmelded", "Unmelded HQ crafted set",
            new(4_879, 4_880, 884),
            ["No food", "Every equipment piece is marketable and gil-acquirable"]),
        new("published-mid-crafted", "Mid-tier crafted-tool meld set",
            new(5_403, 5_408, 905),
            ["No food", "Published mid-tier meld map projected onto the marketable Gold Thumb's Pickaxe"]),
        new("published-high-crafted", "High-tier crafted-tool meld set",
            new(5_593, 5_574, 985),
            ["No food", "Published high-tier meld map projected onto the marketable Gold Thumb's Pickaxe"]),
        new("derived-high-regression", "Weaker, dearer adversarial set",
            new(5_583, 5_564, 975),
            ["Derived witness · 10% quote premium over the high-tier snapshot"],
            IsDerivedAdversarial: true,
            PriceScalePermille: 1100),
        new("derived-high-cost-only", "Identical-stat dearer adversarial set",
            new(5_593, 5_574, 985),
            ["Derived witness · 20% quote premium over the high-tier snapshot"],
            IsDerivedAdversarial: true,
            PriceScalePermille: 1200),
    ];

    public static MinerBotanistReadOnlyAdvice Build(MinerBotanistUtilityContextKind context)
    {
        var baseline = Benchmarks[0];
        var profile = new MinerBotanistUtilityProfile(
            context,
            baseline.Stats,
            MinerBotanistUtilityProfile.MinerClassJobId);
        var offers = new Dictionary<EquipmentOfferAllocationKey, EquipmentExactSolverOffer>();
        var solutions = Benchmarks.Select(benchmark => BuildSolution(benchmark, profile, offers)).ToArray();
        var pareto = new EquipmentParetoFrontierBuilder().Build(solutions);
        var authority = pareto.Frontier.ToDictionary(
            solution => solution.Candidate.SolutionId,
            solution => profile.AssessAuthority(
                solution.Utility,
                solution.AcquisitionCostGil),
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
        MinerBotanistUtilityProfile profile,
        IDictionary<EquipmentOfferAllocationKey, EquipmentExactSolverOffer> offers)
    {
        var evidence = Items(benchmark).ToArray();
        var selections = new List<EquipmentLoadoutSelection>(evidence.Length);
        foreach (var item in evidence)
        {
            var itemMeldCost = EstimateMelds([item]);
            var acquisitionCost = checked(item.GearCostGil + itemMeldCost.ExpectedCostGil);
            var definition = new EquipmentItemDefinition(
                ItemId: item.ItemId,
                Name: item.Name,
                EquipLevel: 100,
                ItemLevel: item.ItemLevel,
                Slot: Slot(item.Position),
                EligibleClassJobIds: new HashSet<uint> { MinerBotanistUtilityProfile.MinerClassJobId, MinerBotanistUtilityProfile.BotanistClassJobId },
                Rarity: item.ItemLevel == 780 ? (byte)4 : (byte)3,
                IsEquipment: true,
                IsSoulCrystal: false,
                IsDesynthesizable: null,
                IsVendorSellable: null,
                VendorSellPrice: null,
                IsDiscardable: null,
                IsArmoireEligible: null,
                IsRecoverable: null,
                IsExplicitlyProtectedFamily: false);
            var sourceCatalogKey = $"synthetic-review:{benchmark.Id}:{item.Position}";
            var sourceLabel = item.Materia.Count == 0
                ? "Market history · HQ gear median"
                : "Market history · HQ gear median + expected meld failures";
            var offer = new EquipmentLoadoutOffer(
                definition,
                item.SourceKind,
                sourceLabel,
                UnitPriceGil: checked((uint)acquisitionCost),
                PriceIsEstimate: true,
                Quality: item.Quality,
                SourceCatalogKey: sourceCatalogKey);
            var exact = new EquipmentExactSolverOffer(
                offer,
                ObservationId: sourceCatalogKey,
                Positions: new HashSet<EquipmentLoadoutPosition> { item.Position },
                AvailableQuantity: 1,
                Utility: EquipmentSolverUtilityVector.Empty,
                AcquisitionCostGil: acquisitionCost,
                WorldVisitKey: "aether-history-snapshot",
                VendorStopKey: null,
                PurchaseTransactions: 1,
                EvidenceRisk: new(0, 0, 0),
                VariantLabels: [item.Quality == EquipmentQuality.High ? "HQ" : "NQ", PriceEvidenceLabel]);
            offers.Add(exact.AllocationKey, exact);
            selections.Add(new(item.Position, offer.Key, ObservationId: exact.ObservationId));
        }

        var gearCost = evidence.Aggregate(0UL, (total, item) => checked(total + item.GearCostGil));
        var meldCost = EstimateMelds(evidence);
        var totalCost = selections.Aggregate(0UL, (total, selection) =>
            checked(total + offers[selection.AllocationKey].AcquisitionCostGil));
        var expectedMateriaCost = checked(totalCost - gearCost);
        var planningCost = checked(gearCost + meldCost.PlanningCostGil);
        var labels = new List<string>
        {
            benchmark.Label,
            PriceEvidenceLabel,
            $"Stats {benchmark.Stats.Gathering}/{benchmark.Stats.Perception}/{benchmark.Stats.GatheringPoints}",
        };
        labels.AddRange(benchmark.Assumptions);
        if (meldCost.Lines.Count > 0)
        {
            labels.Add($"Materia if every meld succeeds first try: {meldCost.OneCopyCostGil:N0} gil");
            labels.Add($"Expected materia spend with failures: {expectedMateriaCost:N0} gil");
            labels.Add($"90% whole-set stocking ceiling: {meldCost.PlanningCostGil:N0} gil materia · {planningCost:N0} gil total");
        }
        if (benchmark.IsDerivedAdversarial)
            labels.Add("Adversarial witness; not a published recommendation");
        return new(
            new(benchmark.Id, selections),
            profile.Evaluate(benchmark.Stats),
            totalCost,
            new(
                WorldVisits: 1,
                VendorStops: 0,
                PurchaseTransactions: evidence.Length + evidence.SelectMany(item => item.Materia).Select(materia => materia.ItemId).Distinct().Count()),
            new(0, 0, 0),
            labels,
            new(
                OptimisticCostGil: checked(gearCost + meldCost.OneCopyCostGil),
                ExpectedCostGil: totalCost,
                PlanningCostGil: planningCost,
                PlanningConfidence: meldCost.PlanningConfidence,
                Reasons: meldCost.Lines.Count == 0
                    ? ["The loadout contains no materia, so optimistic, expected, and planning costs are identical."]
                    :
                    [
                        "Expected cost includes geometric materia loss at each advanced-meld success rate.",
                        "The planning ceiling stocks every risky meld so the whole set completes within that stock at least 90% of the time.",
                    ]));
    }

    private static IEnumerable<ItemEvidence> Items(Benchmark benchmark)
    {
        var items = benchmark.Id switch
        {
            "crafted-unmelded" => BaseCraftedItems(),
            "published-mid-crafted" => MidTierItems(),
            _ => HighTierItems(),
        };
        if (benchmark.PriceScalePermille == 1000)
            return items;
        return items.Select(item => item with
        {
            GearCostGil = Scale(item.GearCostGil, benchmark.PriceScalePermille),
            Materia = item.Materia.Select(materia => materia with
            {
                UnitPriceGil = Scale(materia.UnitPriceGil, benchmark.PriceScalePermille),
            }).ToArray(),
        });
    }

    private static ItemEvidence[] BaseCraftedItems() =>
    [
        Market(EquipmentLoadoutPosition.MainHand, 47171, "Gold Thumb's Pickaxe", 300_767, 1),
        Market(EquipmentLoadoutPosition.OffHand, 47182, "Gold Thumb's Sledgehammer", 349_975, 1),
        Market(EquipmentLoadoutPosition.Head, 47189, "Crested Hood of Gathering", 258_926, 2),
        Market(EquipmentLoadoutPosition.Body, 47190, "Crested Coat of Gathering", 319_980, 2),
        Market(EquipmentLoadoutPosition.Hands, 47191, "Crested Gloves of Gathering", 296_897, 2),
        Market(EquipmentLoadoutPosition.Legs, 47192, "Crested Slops of Gathering", 299_868, 2),
        Market(EquipmentLoadoutPosition.Feet, 47193, "Crested Boots of Gathering", 259_445, 2),
        Market(EquipmentLoadoutPosition.Ears, 47198, "Crested Earrings of Gathering", 125_000, 1),
        Market(EquipmentLoadoutPosition.Neck, 47199, "Crested Necklace of Gathering", 130_327, 1),
        Market(EquipmentLoadoutPosition.Wrists, 47200, "Crested Bracelet of Gathering", 120_000, 1),
        Market(EquipmentLoadoutPosition.LeftRing, 47201, "Crested Ring of Gathering", 129_997, 1),
        Market(EquipmentLoadoutPosition.RightRing, 47201, "Crested Ring of Gathering", 129_997, 1),
    ];

    private static ItemEvidence[] MidTierItems() => ApplyMelds(BaseCraftedItems(), new Dictionary<EquipmentLoadoutPosition, MateriaEvidence[]>
    {
        [EquipmentLoadoutPosition.MainHand] = [M(41777, 12, 600)],
        [EquipmentLoadoutPosition.OffHand] = [M(41776, 12, 999), M(33936, 10, 739)],
        [EquipmentLoadoutPosition.Head] = [M(41775, 12, 977), M(41776, 12, 999), M(33936, 10, 739), M(33922, 9, 1_798)],
        [EquipmentLoadoutPosition.Body] = [M(41775, 12, 977), M(41775, 12, 977), M(33935, 10, 746), M(33923, 9, 650)],
        [EquipmentLoadoutPosition.Hands] = [M(41775, 12, 977), M(41775, 12, 977), M(33935, 10, 746), M(33923, 9, 650)],
        [EquipmentLoadoutPosition.Legs] = [M(41775, 12, 977), M(41776, 12, 999), M(33935, 10, 746), M(33922, 9, 1_798), M(5688, 5, 310)],
        [EquipmentLoadoutPosition.Feet] = [M(41776, 12, 999), M(41776, 12, 999), M(33937, 10, 317), M(41763, 11, 1_150)],
        [EquipmentLoadoutPosition.Ears] = [M(41776, 12, 999), M(33935, 10, 746), M(33923, 9, 650), M(33922, 9, 1_798)],
        [EquipmentLoadoutPosition.Neck] = [M(41776, 12, 999), M(33935, 10, 746), M(33923, 9, 650), M(33922, 9, 1_798)],
        [EquipmentLoadoutPosition.Wrists] = [M(41776, 12, 999), M(33935, 10, 746), M(33923, 9, 650), M(33922, 9, 1_798)],
        [EquipmentLoadoutPosition.LeftRing] = [M(41776, 12, 999), M(33935, 10, 746), M(33923, 9, 650), M(33922, 9, 1_798)],
        [EquipmentLoadoutPosition.RightRing] = [M(41776, 12, 999), M(33935, 10, 746), M(33923, 9, 650), M(33922, 9, 1_798)],
    });

    private static ItemEvidence[] HighTierItems() => ApplyMelds(BaseCraftedItems(), new Dictionary<EquipmentLoadoutPosition, MateriaEvidence[]>
    {
        [EquipmentLoadoutPosition.MainHand] = [M(41776, 12, 999), M(41776, 12, 999), M(41763, 11, 1_150), M(41763, 11, 1_150), M(41763, 11, 1_150)],
        [EquipmentLoadoutPosition.OffHand] = [M(41776, 12, 999), M(41776, 12, 999), M(41763, 11, 1_150), M(41763, 11, 1_150), M(41762, 11, 1_000)],
        [EquipmentLoadoutPosition.Head] = [M(41775, 12, 977), M(41775, 12, 977), M(41775, 12, 977), M(41763, 11, 1_150), M(41764, 11, 1_088)],
        [EquipmentLoadoutPosition.Body] = [M(41775, 12, 977), M(41775, 12, 977), M(41776, 12, 999), M(41763, 11, 1_150), M(41762, 11, 1_000)],
        [EquipmentLoadoutPosition.Hands] = [M(41775, 12, 977), M(41775, 12, 977), M(41775, 12, 977), M(41762, 11, 1_000), M(41763, 11, 1_150)],
        [EquipmentLoadoutPosition.Legs] = [M(41775, 12, 977), M(41775, 12, 977), M(41776, 12, 999), M(33922, 9, 1_798), M(5692, 4, 1_898)],
        [EquipmentLoadoutPosition.Feet] = [M(41776, 12, 999), M(41776, 12, 999), M(41777, 12, 600), M(41764, 11, 1_088), M(41763, 11, 1_150)],
        [EquipmentLoadoutPosition.Ears] = [M(41775, 12, 977), M(41776, 12, 999), M(41764, 11, 1_088), M(41762, 11, 1_000), M(41764, 11, 1_088)],
        [EquipmentLoadoutPosition.Neck] = [M(41775, 12, 977), M(41776, 12, 999), M(41764, 11, 1_088), M(41762, 11, 1_000), M(41764, 11, 1_088)],
        [EquipmentLoadoutPosition.Wrists] = [M(41775, 12, 977), M(41776, 12, 999), M(41764, 11, 1_088), M(41762, 11, 1_000), M(41764, 11, 1_088)],
        [EquipmentLoadoutPosition.LeftRing] = [M(41775, 12, 977), M(41776, 12, 999), M(41764, 11, 1_088), M(41762, 11, 1_000), M(41763, 11, 1_150)],
        [EquipmentLoadoutPosition.RightRing] = [M(41775, 12, 977), M(41776, 12, 999), M(41764, 11, 1_088), M(41762, 11, 1_000), M(41763, 11, 1_150)],
    });

    private static ItemEvidence Market(
        EquipmentLoadoutPosition position,
        uint itemId,
        string name,
        ulong gearCost,
        int guaranteedMateriaSlots) => new(
        position,
        itemId,
        name,
        750,
        EquipmentQuality.High,
        EquipmentAcquisitionSourceKind.MarketBoard,
        gearCost,
        guaranteedMateriaSlots,
        []);

    private static MateriaEvidence M(uint itemId, int tier, ulong unitPriceGil) => new(itemId, tier, unitPriceGil);

    private static ItemEvidence[] ApplyMelds(
        IEnumerable<ItemEvidence> items,
        IReadOnlyDictionary<EquipmentLoadoutPosition, MateriaEvidence[]> melds) => items
        .Select(item => item with { Materia = melds.GetValueOrDefault(item.Position) ?? [] })
        .ToArray();

    private static MateriaMeldCostEstimate EstimateMelds(IEnumerable<ItemEvidence> items) =>
        MateriaMeldCostEstimator.Estimate(items.SelectMany(item => item.Materia.Select((materia, index) =>
            new MateriaMeldCostInput(
                $"{item.Position}:{index + 1}",
                materia.UnitPriceGil,
                index < item.GuaranteedMateriaSlots
                    ? 1d
                    : DohDolMateriaMeldingRates.Resolve(true, materia.Tier, index - item.GuaranteedMateriaSlots))))
            .ToArray());

    private static ulong Scale(ulong value, int permille) =>
        checked(value * (ulong)permille / 1000UL);

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
}
#endif
