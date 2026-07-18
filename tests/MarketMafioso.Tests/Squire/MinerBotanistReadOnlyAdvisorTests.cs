using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.MarketEvidence;
using MarketMafioso.Squire.Outfitter.Utility;
using Xunit;

namespace MarketMafioso.Tests.Squire;

public sealed class MinerBotanistReadOnlyAdvisorTests
{
    [Fact]
    public void Build_nominates_least_cost_exact_quality_solution_that_crosses_capability()
    {
        var fixture = Fixture();
        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Resolution,
            fixture.Evidence,
            itemId => itemId == fixture.Candidate.ItemId ? [fixture.Candidate] : [],
            MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield);

        Assert.True(advice.Status == MinerBotanistAdvisorStatus.Complete, advice.Diagnostic);
        Assert.NotNull(advice.Frontier);
        Assert.NotNull(advice.Nomination);
        Assert.Contains(
            advice.Nomination!.Candidate.Selections,
            value => value.Position == EquipmentLoadoutPosition.MainHand && value.OfferKey.Quality == EquipmentQuality.High);
        Assert.Equal((ulong)25_000, advice.Nomination.AcquisitionCostGil);
        Assert.Contains("least-cost", advice.AdvisoryRule, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_abstains_before_solver_when_market_generation_is_partial()
    {
        var fixture = Fixture();
        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Resolution,
            fixture.Evidence with { Status = OutfitterMarketEvidenceGenerationStatus.Partial },
            _ => [fixture.Candidate],
            MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield);

        Assert.Equal(MinerBotanistAdvisorStatus.Abstained, advice.Status);
        Assert.Null(advice.Frontier);
        Assert.Null(advice.Nomination);
    }

    [Fact]
    public void Build_abstains_on_unmodeled_item_effects()
    {
        var fixture = Fixture();
        var special = fixture.Candidate with { ItemSpecialBonusId = 99 };
        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Resolution,
            fixture.Evidence,
            _ => [special],
            MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield);

        Assert.Equal(MinerBotanistAdvisorStatus.Abstained, advice.Status);
        Assert.Contains("unmodeled effect", advice.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_considers_vendor_and_market_costs_on_the_same_frontier()
    {
        var fixture = Fixture();
        var vendorDefinition = Definition(3_000, "Vendor Pickaxe", EquipmentSlot.MainHand, 400, 400);
        var vendor = new EquipmentLoadoutOffer(
            vendorDefinition,
            EquipmentAcquisitionSourceKind.GilVendor,
            "Vendor · Old Gridania",
            15_000,
            Quality: EquipmentQuality.Normal,
            SourceCatalogKey: "vendor:old-gridania:3000");
        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Resolution,
            fixture.Evidence,
            itemId => itemId == fixture.Candidate.ItemId ? [fixture.Candidate] : [],
            MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield,
            [vendor]);

        Assert.NotNull(advice.Nomination);
        Assert.Equal((ulong)15_000, advice.Nomination!.AcquisitionCostGil);
        Assert.Contains(advice.Nomination.Candidate.Selections, value => value.OfferKey.SourceKind == EquipmentAcquisitionSourceKind.GilVendor);
    }

    [Fact]
    public void Build_supports_level85_player_in_default_ordinary_context()
    {
        var fixture = Fixture(characterLevel: 85, candidatePerception: 1);

        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Resolution,
            fixture.Evidence,
            itemId => itemId == fixture.Candidate.ItemId ? [fixture.Candidate] : [],
            MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark);

        Assert.Equal(MinerBotanistAdvisorStatus.Complete, advice.Status);
        Assert.NotNull(advice.Nomination);
        Assert.Contains(
            advice.Nomination!.Candidate.Selections,
            value => value.Position == EquipmentLoadoutPosition.MainHand && value.OfferKey.Quality == EquipmentQuality.High);
        Assert.Contains(
            advice.AuthorityBySolutionId[advice.Nomination.Candidate.SolutionId].GainedCapabilityIds,
            value => value == "ordinary-balanced-stat-dominance");
    }

    [Fact]
    public void Build_prunes_higher_priced_duplicate_listings_but_keeps_two_units_for_rings()
    {
        var fixture = Fixture();
        var ring = Definition(4_000, "Threshold Ring", EquipmentSlot.Ring, 0, 1);
        var now = fixture.Evidence.CreatedAtUtc;
        var listings = new[]
        {
            new OutfitterMarketListingEvidence(ring.ItemId, EquipmentQuality.High, "first", "Siren", 1, "A", "1", 1, 10_000, now, now, "r1"),
            new OutfitterMarketListingEvidence(ring.ItemId, EquipmentQuality.High, "second", "Siren", 1, "B", "2", 1, 11_000, now, now, "r1"),
            new OutfitterMarketListingEvidence(ring.ItemId, EquipmentQuality.High, "dominated", "Siren", 1, "C", "3", 1, 99_000, now, now, "r1"),
        };
        var evidence = fixture.Evidence with
        {
            Coverage = new(OutfitterMarketCoverageMode.ExhaustiveWithinScope, 1, 1, 100, [ring.ItemId]),
            Items = [new(ring.ItemId, OutfitterMarketEvidenceItemStatus.Fresh, listings, now, "r1")],
        };

        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Resolution,
            evidence,
            _ => [ring],
            MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield);

        Assert.Equal(2, advice.OffersByAllocation.Values.Count(value => value.Offer.Definition.ItemId == ring.ItemId));
        Assert.DoesNotContain(advice.OffersByAllocation.Keys, value => value.ObservationId == "dominated");
    }

    private static FixtureData Fixture(uint characterLevel = 100, int candidatePerception = 0)
    {
        var positions = new[]
        {
            ("main-hand", EquipmentLoadoutPosition.MainHand, EquipmentSlot.MainHand),
            ("off-hand", EquipmentLoadoutPosition.OffHand, EquipmentSlot.OffHand),
            ("head", EquipmentLoadoutPosition.Head, EquipmentSlot.Head),
            ("body", EquipmentLoadoutPosition.Body, EquipmentSlot.Body),
            ("hands", EquipmentLoadoutPosition.Hands, EquipmentSlot.Hands),
            ("legs", EquipmentLoadoutPosition.Legs, EquipmentSlot.Legs),
            ("feet", EquipmentLoadoutPosition.Feet, EquipmentSlot.Feet),
            ("ears", EquipmentLoadoutPosition.Ears, EquipmentSlot.Ears),
            ("neck", EquipmentLoadoutPosition.Neck, EquipmentSlot.Neck),
            ("wrists", EquipmentLoadoutPosition.Wrists, EquipmentSlot.Wrists),
            ("ring-left", EquipmentLoadoutPosition.LeftRing, EquipmentSlot.Ring),
            ("ring-right", EquipmentLoadoutPosition.RightRing, EquipmentSlot.Ring),
        };
        var observed = new List<RenderedEquipmentSlotObservation>();
        var resolved = new List<ResolvedRenderedEquipmentSlot>();
        var itemId = 1_000u;
        foreach (var (key, position, slot) in positions)
        {
            var stats = key == "main-hand"
                ? new Dictionary<string, int> { ["Gathering"] = 399 }
                : new Dictionary<string, int>();
            var item = new RenderedItemDetailObservation(
                RenderedItemDetailStatus.Complete,
                $"Current {key}",
                RenderedItemQuality.Normal,
                700,
                checked((int)characterLevel),
                "MIN BTN",
                stats,
                new Dictionary<string, int>(),
                "Complete");
            observed.Add(new(key, slot, RenderedEquipmentSlotObservationStatus.Equipped, item));
            var definition = Definition(itemId++, item.Name!, slot, 399, 399, equipLevel: characterLevel);
            var offer = new EquipmentLoadoutOffer(
                definition,
                EquipmentAcquisitionSourceKind.Owned,
                "Currently equipped · rendered UI",
                0,
                Quality: EquipmentQuality.Normal,
                SourceCatalogKey: $"rendered-current:{key}");
            resolved.Add(new(key, position, definition, EquipmentQuality.Normal, offer));
        }

        var baseline = new RenderedMinerBotanistBaseline(
            RenderedMinerBotanistBaselineStatus.Complete,
            16,
            checked((int)characterLevel),
            new(5_399, 5_200, 950),
            new(5_000, 5_200, 950),
            observed,
            "Complete");
        var resolution = new RenderedEquipmentResolution(
            RenderedEquipmentResolutionStatus.Complete,
            resolved,
            "Complete");
        var candidate = Definition(
            2_000,
            "Threshold Pickaxe",
            EquipmentSlot.MainHand,
            399,
            400,
            highPerception: candidatePerception,
            equipLevel: characterLevel);
        var now = new DateTimeOffset(2026, 7, 16, 20, 0, 0, TimeSpan.Zero);
        var evidence = new OutfitterMarketEvidenceBook(
            Guid.NewGuid(),
            1,
            OutfitterMarketEvidenceBook.CurrentSchemaVersion,
            "fixture",
            "NA",
            now,
            now,
            OutfitterMarketEvidenceGenerationStatus.Complete,
            new(OutfitterMarketCoverageMode.ExhaustiveWithinScope, 1, 1, 100, [candidate.ItemId]),
            [
                new(candidate.ItemId, OutfitterMarketEvidenceItemStatus.Fresh,
                [
                    new(candidate.ItemId, EquipmentQuality.Normal, "nq", "Siren", 1, "NQ", "1", 1, 10_000, now, now, "r1"),
                    new(candidate.ItemId, EquipmentQuality.High, "hq", "Siren", 1, "HQ", "2", 1, 25_000, now, now, "r1"),
                ], now, "r1"),
            ]);
        return new(baseline, resolution, evidence, candidate);
    }

    private static EquipmentItemDefinition Definition(
        uint itemId,
        string name,
        EquipmentSlot slot,
        int normalGathering,
        int highGathering,
        int normalPerception = 0,
        int highPerception = 0,
        uint equipLevel = 100)
    {
        EquipmentStatProfile Profile(int gathering, int perception) => new(
            [
                ..gathering == 0 ? [] : new EquipmentStatValue[] { new(72, EquipmentStatSemantic.Gathering, gathering, false, "Gathering") },
                ..perception == 0 ? [] : new EquipmentStatValue[] { new(73, EquipmentStatSemantic.Perception, perception, false, "Perception") },
            ],
            0, 0, 0, 0, true);
        return new(
            itemId,
            name,
            equipLevel,
            700,
            slot,
            new HashSet<uint> { 16, 17 },
            1,
            true,
            false,
            true,
            true,
            1,
            true,
            null,
            null,
            false,
            StatProfile: Profile(normalGathering, normalPerception),
            HighQualityStatProfile: Profile(highGathering, highPerception));
    }

    private sealed record FixtureData(
        RenderedMinerBotanistBaseline Baseline,
        RenderedEquipmentResolution Resolution,
        OutfitterMarketEvidenceBook Evidence,
        EquipmentItemDefinition Candidate);
}
