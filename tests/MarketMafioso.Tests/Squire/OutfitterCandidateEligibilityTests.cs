using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter;

namespace MarketMafioso.Tests.Squire;

public sealed class OutfitterCandidateEligibilityTests
{
    [Theory]
    [InlineData(EquipmentAcquisitionSourceKind.Owned)]
    [InlineData(EquipmentAcquisitionSourceKind.GilVendor)]
    [InlineData(EquipmentAcquisitionSourceKind.MarketBoard)]
    public void IsRecommendable_RejectsCraftingOnlyAllClassesItemForGathererAcrossEverySource(
        EquipmentAcquisitionSourceKind source)
    {
        var item = Definition(
            "Velveteen Doublet Vest of Crafting",
            EquipmentStatSemantic.Craftsmanship,
            EquipmentStatSemantic.Control);

        Assert.False(OutfitterCandidateEligibility.IsRecommendable(Gatherer(), Offer(item, source)));
    }

    [Theory]
    [InlineData(EquipmentAcquisitionSourceKind.Owned)]
    [InlineData(EquipmentAcquisitionSourceKind.GilVendor)]
    [InlineData(EquipmentAcquisitionSourceKind.MarketBoard)]
    public void IsRecommendable_RejectsGatheringOnlyAllClassesItemForCrafterAcrossEverySource(
        EquipmentAcquisitionSourceKind source)
    {
        var item = Definition("Gatherer's Work Shirt", EquipmentStatSemantic.Gathering, EquipmentStatSemantic.Perception);

        Assert.False(OutfitterCandidateEligibility.IsRecommendable(Crafter(), Offer(item, source)));
    }

    [Theory]
    [InlineData(EquipmentAcquisitionSourceKind.Owned)]
    [InlineData(EquipmentAcquisitionSourceKind.GilVendor)]
    [InlineData(EquipmentAcquisitionSourceKind.MarketBoard)]
    public void IsRecommendable_KeepsLegitimateHybridForEitherRelevantDiscipline(
        EquipmentAcquisitionSourceKind source)
    {
        var item = Definition("Steel Goggles", EquipmentStatSemantic.Control, EquipmentStatSemantic.Perception);
        var offer = Offer(item, source);

        Assert.True(OutfitterCandidateEligibility.IsRecommendable(Crafter(), offer));
        Assert.True(OutfitterCandidateEligibility.IsRecommendable(Gatherer(), offer));
    }

    [Fact]
    public void IsRecommendable_KeepsCombatWeaponDamageAndRejectsNonCombatStats()
    {
        var weapon = Definition("Bronze Sword", physicalDamage: 12, slot: EquipmentSlot.MainHand);
        var craftingBody = Definition("All Classes Crafting Body", EquipmentStatSemantic.Craftsmanship);

        Assert.True(OutfitterCandidateEligibility.IsRecommendable(Combat(), Offer(weapon, EquipmentAcquisitionSourceKind.Owned)));
        Assert.False(OutfitterCandidateEligibility.IsRecommendable(Combat(), Offer(craftingBody, EquipmentAcquisitionSourceKind.Owned)));
    }

    [Fact]
    public void CurrentLoadout_KeepsRenderedBaselineEvenWhenItIsNotAnUpgradeCandidate()
    {
        var job = Gatherer();
        var poison = Definition("Velveteen Doublet Vest of Crafting", EquipmentStatSemantic.Craftsmanship);
        var gearset = new GearsetSnapshot(2, "Botanist", job.ClassJobId,
            [new(EquipmentSlot.Body, poison.ItemId, false)], true);
        var snapshot = new CharacterEquipmentSnapshot(
            Guid.NewGuid(),
            new(null, null, null, DateTimeOffset.UtcNow, true, SnapshotComponentStatus.Complete),
            [job],
            [gearset],
            [],
            new Dictionary<uint, EquipmentItemDefinition> { [poison.ItemId] = poison },
            new([new("equipment", SnapshotComponentStatus.Complete)]));
        var target = new OutfitterTarget("gearset:2", OutfitterTargetKind.Gearset, "Botanist", "Gearset 3", job, gearset);

        var current = OutfitterCandidateCatalog.BuildCurrentItemsCore(snapshot, target);

        Assert.Equal(poison.ItemId, current[EquipmentLoadoutPosition.Body].Definition.ItemId);
        Assert.False(OutfitterCandidateEligibility.IsRecommendable(job, current[EquipmentLoadoutPosition.Body]));
    }

    private static CharacterJobSnapshot Gatherer() => new(
        16, "MIN", "Miner", 33, true, null, "Gatherer",
        EquipmentStatSemantic.Gathering, EquipmentDiscipline.Gatherer);

    private static CharacterJobSnapshot Crafter() => new(
        8, "CRP", "Carpenter", 33, true, null, "Crafter",
        EquipmentStatSemantic.Craftsmanship, EquipmentDiscipline.Crafter);

    private static CharacterJobSnapshot Combat() => new(
        1, "GLA", "Gladiator", 33, true, null, "Tank",
        EquipmentStatSemantic.Strength, EquipmentDiscipline.Combat);

    private static EquipmentLoadoutOffer Offer(
        EquipmentItemDefinition definition,
        EquipmentAcquisitionSourceKind source) => new(
        definition,
        source,
        source.ToString(),
        source == EquipmentAcquisitionSourceKind.Owned ? null : 100u,
        Quality: EquipmentQuality.Normal);

    private static EquipmentItemDefinition Definition(
        string name,
        EquipmentStatSemantic first = EquipmentStatSemantic.Unknown,
        EquipmentStatSemantic second = EquipmentStatSemantic.Unknown,
        int physicalDamage = 0,
        EquipmentSlot slot = EquipmentSlot.Body)
    {
        var parameters = new[] { first, second }
            .Where(value => value != EquipmentStatSemantic.Unknown)
            .Select((value, index) => new EquipmentStatValue((uint)(index + 1), value, 20, false))
            .ToArray();
        return new(
            100,
            name,
            1,
            32,
            slot,
            new HashSet<uint> { 1, 8, 16 },
            1,
            true,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            new(parameters, physicalDamage, 0, 0, 0, true),
            IsAllClasses: true);
    }
}
