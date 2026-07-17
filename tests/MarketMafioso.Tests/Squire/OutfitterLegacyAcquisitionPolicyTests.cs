using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter;

namespace MarketMafioso.Tests.Squire;

public sealed class OutfitterLegacyAcquisitionPolicyTests
{
    [Fact]
    public void Evaluate_BlocksMarketPlanWithoutExactQualityIdentity()
    {
        var plan = Plan(EquipmentAcquisitionSourceKind.MarketBoard);

        var readiness = OutfitterLegacyAcquisitionPolicy.Evaluate(plan);

        Assert.False(readiness.CanStage);
        Assert.Equal("ExactQualityUnavailable", readiness.Code);
        Assert.Contains("NQ/HQ", readiness.Message);
        Assert.Contains("Workbench", readiness.Message);
    }

    [Fact]
    public void Evaluate_ReportsWhenPlanHasNoMarketItems()
    {
        var plan = Plan(EquipmentAcquisitionSourceKind.Owned);

        var readiness = OutfitterLegacyAcquisitionPolicy.Evaluate(plan);

        Assert.False(readiness.CanStage);
        Assert.Equal("NoMarketItems", readiness.Code);
    }

    private static EquipmentLoadoutPlan Plan(EquipmentAcquisitionSourceKind sourceKind)
    {
        var job = new CharacterJobSnapshot(
            1,
            "GLA",
            "Gladiator",
            20,
            true,
            null,
            "Tank",
            EquipmentStatSemantic.Strength,
            EquipmentDiscipline.Combat);
        var definition = new EquipmentItemDefinition(
            10,
            "Bronze Sallet",
            15,
            17,
            EquipmentSlot.Head,
            new HashSet<uint> { 1 },
            1,
            true,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            false);
        var offer = new EquipmentLoadoutOffer(
            definition,
            sourceKind,
            sourceKind.ToString(),
            sourceKind == EquipmentAcquisitionSourceKind.MarketBoard ? 300u : null);
        var entry = new EquipmentLoadoutPlanEntry(
            EquipmentLoadoutPosition.Head,
            null,
            offer,
            17,
            []);
        return new(job, 20, EquipmentLoadoutStrategy.HighestItemLevel, [entry], 0, 17, 300, 0, 1, 1);
    }
}
