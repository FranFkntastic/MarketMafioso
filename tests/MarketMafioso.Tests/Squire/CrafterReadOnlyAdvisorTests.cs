using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.MarketEvidence;
using MarketMafioso.Squire.Outfitter.Utility;
using Xunit;

namespace MarketMafioso.Tests.Squire;

public sealed class CrafterReadOnlyAdvisorTests
{
    [Fact]
    public void Crafter_family_produces_a_frontier_through_the_shared_advisor()
    {
        var fixture = Fixture();
        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Resolution,
            fixture.Evidence,
            itemId => itemId == fixture.Candidate.ItemId ? [fixture.Candidate] : [],
            CrafterAdvisorStatFamily.Instance,
            CrafterUtilityProfile.OrdinaryCraftBenchmarkContextId);

        Assert.True(advice.Status == MinerBotanistAdvisorStatus.Complete, advice.Diagnostic);
        Assert.NotNull(advice.Frontier);
        Assert.Contains(advice.OffersByAllocation.Values, value => value.Offer.SourceKind == EquipmentAcquisitionSourceKind.MarketBoard);
    }

    [Fact]
    public void Crafter_family_rejects_gatherer_offers_as_cross_discipline_poison()
    {
        var fixture = Fixture();
        var gathererDefinition = Definition(9_000, "Gathering Tool", EquipmentSlot.MainHand, 900, 950, gatheringInsteadOfCrafting: true);
        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Resolution,
            fixture.Evidence,
            itemId => itemId == fixture.Candidate.ItemId ? [fixture.Candidate] : itemId == gathererDefinition.ItemId ? [gathererDefinition] : [],
            CrafterAdvisorStatFamily.Instance,
            CrafterUtilityProfile.OrdinaryCraftBenchmarkContextId,
            vendorOffers: null,
            ownedItems: [new(gathererDefinition.ItemId, true, "Armoury")]);

        Assert.True(advice.Status == MinerBotanistAdvisorStatus.Complete, advice.Diagnostic);
        Assert.DoesNotContain(advice.OffersByAllocation.Values, value => value.Offer.Definition.ItemId == gathererDefinition.ItemId);
    }

    [Fact]
    public void Supported_crafter_calibration_nominates_a_capability_gaining_upgrade()
    {
        var fixture = Fixture();
        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Resolution,
            fixture.Evidence,
            itemId => itemId == fixture.Candidate.ItemId ? [fixture.Candidate] : [],
            CrafterAdvisorStatFamily.Instance,
            CrafterUtilityProfile.OrdinaryCraftBenchmarkContextId);

        Assert.True(advice.Status == MinerBotanistAdvisorStatus.Complete, advice.Diagnostic);
        Assert.NotNull(advice.Frontier);
        Assert.NotNull(advice.Nomination);
        Assert.Contains(advice.AuthorityBySolutionId.Values, authority => authority.AdvisorMayConsider);
        Assert.DoesNotContain(advice.AuthorityBySolutionId.Values, authority =>
            authority.Reasons.Any(reason => reason.Contains("experimental", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Crafter_family_abstains_when_the_rendered_job_is_not_a_crafter()
    {
        var fixture = Fixture(classJobId: 16);
        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Resolution,
            fixture.Evidence,
            itemId => itemId == fixture.Candidate.ItemId ? [fixture.Candidate] : [],
            CrafterAdvisorStatFamily.Instance,
            CrafterUtilityProfile.OrdinaryCraftBenchmarkContextId);

        Assert.Equal(MinerBotanistAdvisorStatus.Abstained, advice.Status);
        Assert.Contains("outside the supported", advice.Diagnostic, StringComparison.Ordinal);
    }

    private static FixtureData Fixture(uint classJobId = 9)
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
                ? new Dictionary<string, int> { ["Craftsmanship"] = 399 }
                : new Dictionary<string, int>();
            var item = new RenderedItemDetailObservation(
                RenderedItemDetailStatus.Complete,
                $"Current {key}",
                RenderedItemQuality.Normal,
                700,
                100,
                "BSM",
                null,
                stats,
                new Dictionary<string, int>(),
                "Complete");
            observed.Add(new(key, slot, RenderedEquipmentSlotObservationStatus.Equipped, item));
            var definition = Definition(itemId++, item.Name!, slot, 399, 399, classJobId: classJobId);
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
            classJobId,
            100,
            new(5_399, 5_200, 950),
            new(5_000, 5_200, 950),
            observed,
            "Complete");
        var resolution = new RenderedEquipmentResolution(
            RenderedEquipmentResolutionStatus.Complete,
            resolved,
            "Complete");
        var candidate = Definition(2_000, "Threshold Hammer", EquipmentSlot.MainHand, 399, 400, classJobId: classJobId);
        var now = new DateTimeOffset(2026, 7, 19, 20, 0, 0, TimeSpan.Zero);
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
                    new(candidate.ItemId, EquipmentQuality.High, "hq", "Siren", 1, "HQ", "2", 1, 25_000, now, now, "r1"),
                ], now, "r1"),
            ]);
        return new(baseline, resolution, evidence, candidate);
    }

    private static EquipmentItemDefinition Definition(
        uint itemId,
        string name,
        EquipmentSlot slot,
        int normalStat,
        int highStat,
        uint classJobId = 9,
        bool gatheringInsteadOfCrafting = false)
    {
        EquipmentStatProfile Profile(int amount) => gatheringInsteadOfCrafting
            ? new(
                [
                    new((uint)72, EquipmentStatSemantic.Gathering, amount, false, "stat"),
                    new((uint)73, EquipmentStatSemantic.Perception, amount, false, "stat2"),
                ],
                0, 0, 0, 0, true)
            : new(
                [
                    new((uint)74, EquipmentStatSemantic.Craftsmanship, amount, false, "stat"),
                    new((uint)75, EquipmentStatSemantic.Control, amount, false, "stat2"),
                ],
                0, 0, 0, 0, true);
        return new(
            itemId,
            name,
            100,
            700,
            slot,
            new HashSet<uint> { classJobId },
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
            StatProfile: Profile(normalStat),
            HighQualityStatProfile: Profile(highStat));
    }

    private sealed record FixtureData(
        RenderedMinerBotanistBaseline Baseline,
        RenderedEquipmentResolution Resolution,
        OutfitterMarketEvidenceBook Evidence,
        EquipmentItemDefinition Candidate);
}
