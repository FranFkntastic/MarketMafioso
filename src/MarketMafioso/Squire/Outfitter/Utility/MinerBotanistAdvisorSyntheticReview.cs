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
        ulong SupplementalCostGil = 0,
        bool UsesFood = false,
        bool IsDerivedAdversarial = false,
        int PriceScalePermille = 1000);

    private sealed record ItemEvidence(
        EquipmentLoadoutPosition Position,
        uint ItemId,
        string Name,
        uint ItemLevel,
        EquipmentQuality Quality,
        EquipmentAcquisitionSourceKind SourceKind,
        ulong AcquisitionCostGil,
        string SourceLabel);

    private static readonly Benchmark[] Benchmarks =
    [
        new("published-budget-raw", "Budget stopping point",
            new(4_879, 5_444, 884),
            ["No food", "Owned Star Tech baseline; unavailable as a procurement candidate"]),
        new("published-budget-cloudsail", "Budget set with Cloudsail Meuniere HQ",
            new(4_970, 5_620, 884),
            ["Cloudsail Meuniere HQ · 10,800 gil/meal", "Recurring consumable remains outside JobUtilityScore"],
            SupplementalCostGil: 10_800,
            UsesFood: true),
        new("published-mid-raw", "Mid-tier Crested meld set",
            new(5_510, 5_470, 904),
            ["No food", "HQ Crested gear and assigned materia priced from sale history"]),
        new("published-high-raw", "High-tier Crested meld set",
            new(5_700, 5_504, 995),
            ["No food", "HQ Crested gear and heavier meld package priced from sale history"]),
        new("derived-high-regression", "Weaker, dearer adversarial set",
            new(5_690, 5_494, 985),
            ["Derived witness · 10% quote premium over the high-tier snapshot"],
            IsDerivedAdversarial: true,
            PriceScalePermille: 1100),
        new("derived-high-cost-only", "Identical-stat dearer adversarial set",
            new(5_700, 5_504, 995),
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
        MinerBotanistUtilityProfile profile,
        IDictionary<EquipmentOfferAllocationKey, EquipmentExactSolverOffer> offers)
    {
        var evidence = Items(benchmark).ToArray();
        var selections = new List<EquipmentLoadoutSelection>(evidence.Length);
        foreach (var item in evidence)
        {
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
            var offer = new EquipmentLoadoutOffer(
                definition,
                item.SourceKind,
                item.SourceLabel,
                UnitPriceGil: checked((uint)item.AcquisitionCostGil),
                PriceIsEstimate: true,
                Quality: item.Quality,
                SourceCatalogKey: sourceCatalogKey);
            var exact = new EquipmentExactSolverOffer(
                offer,
                ObservationId: sourceCatalogKey,
                Positions: new HashSet<EquipmentLoadoutPosition> { item.Position },
                AvailableQuantity: 1,
                Utility: EquipmentSolverUtilityVector.Empty,
                AcquisitionCostGil: item.AcquisitionCostGil,
                WorldVisitKey: item.SourceKind == EquipmentAcquisitionSourceKind.MarketBoard ? "aether-history-snapshot" : null,
                VendorStopKey: item.SourceKind == EquipmentAcquisitionSourceKind.GilVendor ? "purple-scrip-exchange" : null,
                PurchaseTransactions: item.AcquisitionCostGil > 0 ? 1 : 0,
                EvidenceRisk: new(0, 0, 0),
                VariantLabels: [item.Quality == EquipmentQuality.High ? "HQ" : "NQ", PriceEvidenceLabel]);
            offers.Add(exact.AllocationKey, exact);
            selections.Add(new(item.Position, offer.Key, ObservationId: exact.ObservationId));
        }

        var itemCost = evidence.Aggregate(0UL, (total, item) => checked(total + item.AcquisitionCostGil));
        var totalCost = checked(itemCost + benchmark.SupplementalCostGil);
        var marketPurchases = evidence.Count(item => item.SourceKind == EquipmentAcquisitionSourceKind.MarketBoard);
        var pricedPackages = evidence.Count(item => item.AcquisitionCostGil > 0);
        var labels = new List<string>
        {
            benchmark.Label,
            PriceEvidenceLabel,
            $"Stats {benchmark.Stats.Gathering}/{benchmark.Stats.Perception}/{benchmark.Stats.GatheringPoints}",
        };
        labels.AddRange(benchmark.Assumptions);
        if (benchmark.IsDerivedAdversarial)
            labels.Add("Adversarial witness; not a published recommendation");
        return new(
            new(benchmark.Id, selections),
            profile.Evaluate(benchmark.Stats),
            totalCost,
            new(
                WorldVisits: marketPurchases == 0 ? 0 : 1,
                VendorStops: 0,
                PurchaseTransactions: pricedPackages + (benchmark.UsesFood ? 1 : 0)),
            new(0, 0, 0),
            labels);
    }

    private static IEnumerable<ItemEvidence> Items(Benchmark benchmark)
    {
        if (benchmark.Id is "published-budget-raw" or "published-budget-cloudsail")
            return BudgetItems();

        var items = benchmark.Id == "published-mid-raw" ? MidTierItems() : HighTierItems();
        if (benchmark.PriceScalePermille == 1000)
            return items;
        return items.Select(item => item with
        {
            AcquisitionCostGil = checked(item.AcquisitionCostGil * (ulong)benchmark.PriceScalePermille / 1000UL),
            SourceLabel = $"{item.SourceLabel} · {benchmark.PriceScalePermille / 10}% adversarial quote",
        });
    }

    private static ItemEvidence[] BudgetItems() =>
    [
        OwnedBaseline(EquipmentLoadoutPosition.MainHand, 49334, "Star Tech Pickaxe"),
        OwnedBaseline(EquipmentLoadoutPosition.OffHand, 49345, "Star Tech Sledgehammer"),
        OwnedBaseline(EquipmentLoadoutPosition.Head, 49352, "Star Tech Goggles of Gathering"),
        OwnedBaseline(EquipmentLoadoutPosition.Body, 49353, "Star Tech Coat of Gathering"),
        OwnedBaseline(EquipmentLoadoutPosition.Hands, 49354, "Star Tech Work Gloves of Gathering"),
        OwnedBaseline(EquipmentLoadoutPosition.Legs, 49355, "Star Tech Kecks of Gathering"),
        OwnedBaseline(EquipmentLoadoutPosition.Feet, 49356, "Star Tech Shoes of Gathering"),
        OwnedBaseline(EquipmentLoadoutPosition.Ears, 49361, "Star Tech Ear Cuff of Gathering"),
        OwnedBaseline(EquipmentLoadoutPosition.Neck, 49362, "Star Tech Choker of Gathering"),
        OwnedBaseline(EquipmentLoadoutPosition.Wrists, 49363, "Star Tech Bracelet of Gathering"),
        OwnedBaseline(EquipmentLoadoutPosition.LeftRing, 49364, "Star Tech Ring of Gathering"),
        OwnedBaseline(EquipmentLoadoutPosition.RightRing, 49364, "Star Tech Ring of Gathering"),
    ];

    private static ItemEvidence[] MidTierItems() =>
    [
        Relic(EquipmentLoadoutPosition.MainHand, "Pickaxe of Stars"),
        Market(EquipmentLoadoutPosition.OffHand, 47182, "Gold Thumb's Sledgehammer", 351_713),
        Market(EquipmentLoadoutPosition.Head, 47189, "Crested Hood of Gathering", 263_439),
        Market(EquipmentLoadoutPosition.Body, 47190, "Crested Coat of Gathering", 323_330),
        Market(EquipmentLoadoutPosition.Hands, 47191, "Crested Gloves of Gathering", 300_247),
        Market(EquipmentLoadoutPosition.Legs, 47192, "Crested Slops of Gathering", 304_698),
        Market(EquipmentLoadoutPosition.Feet, 47193, "Crested Boots of Gathering", 262_910),
        Market(EquipmentLoadoutPosition.Ears, 47198, "Crested Earrings of Gathering", 129_193),
        Market(EquipmentLoadoutPosition.Neck, 47199, "Crested Necklace of Gathering", 134_520),
        Market(EquipmentLoadoutPosition.Wrists, 47200, "Crested Bracelet of Gathering", 124_193),
        Market(EquipmentLoadoutPosition.LeftRing, 47201, "Crested Ring of Gathering", 134_190),
        Market(EquipmentLoadoutPosition.RightRing, 47201, "Crested Ring of Gathering", 134_190),
    ];

    private static ItemEvidence[] HighTierItems() =>
    [
        Relic(EquipmentLoadoutPosition.MainHand, "Pickaxe of Stars"),
        Market(EquipmentLoadoutPosition.OffHand, 47182, "Gold Thumb's Sledgehammer", 355_273),
        Market(EquipmentLoadoutPosition.Head, 47189, "Crested Hood of Gathering", 264_095),
        Market(EquipmentLoadoutPosition.Body, 47190, "Crested Coat of Gathering", 325_083),
        Market(EquipmentLoadoutPosition.Hands, 47191, "Crested Gloves of Gathering", 301_978),
        Market(EquipmentLoadoutPosition.Legs, 47192, "Crested Slops of Gathering", 306_517),
        Market(EquipmentLoadoutPosition.Feet, 47193, "Crested Boots of Gathering", 264_281),
        Market(EquipmentLoadoutPosition.Ears, 47198, "Crested Earrings of Gathering", 130_152),
        Market(EquipmentLoadoutPosition.Neck, 47199, "Crested Necklace of Gathering", 135_479),
        Market(EquipmentLoadoutPosition.Wrists, 47200, "Crested Bracelet of Gathering", 125_152),
        Market(EquipmentLoadoutPosition.LeftRing, 47201, "Crested Ring of Gathering", 135_211),
        Market(EquipmentLoadoutPosition.RightRing, 47201, "Crested Ring of Gathering", 135_211),
    ];

    private static ItemEvidence OwnedBaseline(EquipmentLoadoutPosition position, uint itemId, string name) => new(
        position,
        itemId,
        name,
        750,
        EquipmentQuality.Normal,
        EquipmentAcquisitionSourceKind.Owned,
        0,
        "Owned baseline premise · not procured");

    private static ItemEvidence Relic(EquipmentLoadoutPosition position, string name) => new(
        position,
        51786,
        name,
        780,
        EquipmentQuality.Normal,
        EquipmentAcquisitionSourceKind.Owned,
        0,
        "Model-set relic · no gil acquisition price");

    private static ItemEvidence Market(EquipmentLoadoutPosition position, uint itemId, string name, ulong cost) => new(
        position,
        itemId,
        name,
        750,
        EquipmentQuality.High,
        EquipmentAcquisitionSourceKind.MarketBoard,
        cost,
        "Market history · HQ gear plus assigned melds");

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
