using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Characters;
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
    public void Partial_owned_inventory_coverage_blocks_paid_nomination()
    {
        var fixture = Fixture();
        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
            fixture.Evidence,
            itemId => itemId == fixture.Candidate.ItemId ? [fixture.Candidate] : [],
            CrafterAdvisorStatFamily.Instance,
            CrafterUtilityProfile.OrdinaryCraftBenchmarkContextId,
            ownedInventoryCoverageComplete: false);

        Assert.True(advice.Status == MinerBotanistAdvisorStatus.Complete, advice.Diagnostic);
        Assert.Null(advice.Nomination);
        Assert.Contains(advice.AuthorityBySolutionId.Values, authority =>
            authority.Reasons.Any(reason => reason.Contains("coverage is partial", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Crafter_family_abstains_when_the_rendered_job_is_not_a_crafter()
    {
        var fixture = Fixture(classJobId: 16);
        var advice = new MinerBotanistReadOnlyAdvisor().Build(
            fixture.Baseline,
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
        var scope = new CharacterScope(77, "Crafter", 21);
        var equipped = new List<PlayerAdvisorEquippedSlot>();
        var instances = new List<EquipmentInstanceSnapshot>();
        var definitions = new Dictionary<uint, EquipmentItemDefinition>();
        var itemId = 1_000u;
        foreach (var (key, position, slot) in positions)
        {
            var definition = Definition(itemId++, $"Current {key}", slot, 399, 399, classJobId: classJobId);
            var index = PlayerAdvisorEquippedSlotMap.All.Single(value => value.Position == position).EquippedIndex;
            var instance = new EquipmentInstanceSnapshot(
                new EquipmentInstanceFingerprint(scope, "EquippedItems", index, definition.ItemId, false, 1, 30_000, 0, null, [], null, []),
                DateTimeOffset.UtcNow,
                true);
            var semantics = new Dictionary<EquipmentStatSemantic, int>
            {
                [EquipmentStatSemantic.Craftsmanship] = key == "main-hand" ? 399 : 0,
                [EquipmentStatSemantic.Control] = 0,
                [EquipmentStatSemantic.CraftingPoints] = 0,
            };
            equipped.Add(new(position, key, instance, definition, EquipmentQuality.Normal,
                CrafterAdvisorStatFamily.Instance.VectorFromSemantics(semantics), [], []));
            instances.Add(instance);
            definitions.Add(definition.ItemId, definition);
        }

        var snapshot = new CharacterEquipmentSnapshot(
            Guid.NewGuid(),
            new(scope, 21, classJobId, DateTimeOffset.UtcNow, true, SnapshotComponentStatus.Complete),
            [],
            [],
            instances,
            definitions,
            new([new("identity", SnapshotComponentStatus.Complete), new("equipped", SnapshotComponentStatus.Complete)]));
        var baseline = new PlayerAdvisorBaseline(
            PlayerAdvisorBaselineStatus.Complete,
            scope,
            classJobId,
            100,
            100,
            false,
            new Dictionary<EquipmentStatSemantic, int>
            {
                [EquipmentStatSemantic.Craftsmanship] = 5_399,
                [EquipmentStatSemantic.Control] = 5_200,
                [EquipmentStatSemantic.CraftingPoints] = 950,
            },
            new Dictionary<EquipmentStatSemantic, int>
            {
                [EquipmentStatSemantic.Craftsmanship] = 5_000,
                [EquipmentStatSemantic.Control] = 5_200,
                [EquipmentStatSemantic.CraftingPoints] = 950,
            },
            equipped,
            snapshot,
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
        return new(baseline, evidence, candidate);
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
        PlayerAdvisorBaseline Baseline,
        OutfitterMarketEvidenceBook Evidence,
        EquipmentItemDefinition Candidate);
}
