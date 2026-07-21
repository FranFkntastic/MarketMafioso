using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class PhysicalRangedUtilityProfileTests
{
    private static readonly PhysicalRangedUtilityStats Baseline = new(4_800, 5_200, 140, 2_500, 2_500, 3_000, 2_200, 1_800, 1_000);

    [Fact]
    public void Role_registry_contains_only_bard_machinist_and_dancer()
    {
        var role = AdvisorCombatRoles.PhysicalRanged;

        Assert.Equal("physical-ranged", role.Id);
        Assert.Equal(
            [PhysicalRangedUtilityProfile.BardClassJobId, PhysicalRangedUtilityProfile.MachinistClassJobId, PhysicalRangedUtilityProfile.DancerClassJobId],
            role.ClassJobIds.Order().ToArray());
        Assert.Same(role, AdvisorCombatRoles.Resolve(PhysicalRangedUtilityProfile.MachinistClassJobId));
        Assert.Null(AdvisorCombatRoles.Resolve(19));
    }

    [Fact]
    public void Componentwise_gain_is_clear_but_any_trade_is_context_dependent()
    {
        var profile = Profile();

        var noLoss = profile.Evaluate(Baseline with { CriticalHit = Baseline.CriticalHit + 20 });
        var trade = profile.Evaluate(Baseline with
        {
            CriticalHit = Baseline.CriticalHit + 20,
            SkillSpeed = Baseline.SkillSpeed - 1,
        });

        Assert.Equal(UpgradeAssessment.ClearImprovement, noLoss.Assessment);
        Assert.Equal(UpgradeAssessment.ContextDependent, trade.Assessment);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Characterization_authority_requires_dexterity_or_weapon_damage_for_paid_gain(
        bool gainDexterity,
        bool gainWeaponDamage)
    {
        var profile = Profile();
        var candidate = profile.Evaluate(Baseline with
        {
            Dexterity = Baseline.Dexterity + (gainDexterity ? 1 : 0),
            PhysicalDamage = Baseline.PhysicalDamage + (gainWeaponDamage ? 1 : 0),
        });

        var authority = profile.AssessAuthorityForCharacterization(candidate, 10_000);

        Assert.True(authority.AdvisorMayConsider);
        Assert.Contains(authority.GainedCapabilityIds, id => id.Contains(gainDexterity ? "dexterity" : "physical-damage", StringComparison.Ordinal));
    }

    [Fact]
    public void Paid_secondary_only_gain_abstains_and_public_authority_remains_experimental()
    {
        var profile = Profile();
        var candidate = profile.Evaluate(Baseline with { DirectHit = Baseline.DirectHit + 50 });

        var characterization = profile.AssessAuthorityForCharacterization(candidate, 10_000);
        var production = profile.AssessAuthority(candidate, 10_000);

        Assert.False(characterization.AdvisorMayConsider);
        Assert.Contains(characterization.Reasons, reason => reason.Contains("requires a Dexterity or physical weapon-damage gain", StringComparison.Ordinal));
        Assert.False(production.AdvisorMayConsider);
        Assert.Contains(production.Reasons, reason => reason.Contains("experimental", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(AdvisorProfileCalibrationState.Experimental, PhysicalRangedUtilityProfile.CalibrationState);
    }

    [Theory]
    [InlineData(19u, 100u)]
    [InlineData(PhysicalRangedUtilityProfile.BardClassJobId, 29u)]
    [InlineData(PhysicalRangedUtilityProfile.DancerClassJobId, 59u)]
    public void Other_jobs_and_pre_unlock_levels_are_unsupported(uint classJobId, uint level)
    {
        var profile = new PhysicalRangedUtilityProfile(
            PhysicalRangedUtilityContextKind.GeneralCombat,
            Baseline,
            classJobId,
            level);

        Assert.Equal(UpgradeAssessment.Unsupported, profile.Evaluate(Baseline with { Dexterity = Baseline.Dexterity + 1 }).Assessment);
    }

    [Fact]
    public void Definition_vector_includes_non_parameter_damage_and_defenses()
    {
        var profile = new EquipmentStatProfile(
            [
                new(2, EquipmentStatSemantic.Dexterity, 400, false),
                new(3, EquipmentStatSemantic.Vitality, 450, false),
                new(27, EquipmentStatSemantic.CriticalHit, 300, false),
                new(44, EquipmentStatSemantic.Determination, 200, false),
                new(22, EquipmentStatSemantic.DirectHit, 180, false),
                new(45, EquipmentStatSemantic.SkillSpeed, 120, false),
            ],
            PhysicalDamage: 141,
            MagicalDamage: 0,
            PhysicalDefense: 500,
            MagicalDefense: 420,
            IsComplete: true);

        var vector = PhysicalRangedAdvisorStatFamily.Instance.VectorFromDefinition(profile);

        Assert.Equal(400, vector.Get("dexterity"));
        Assert.Equal(141, vector.Get("physical-damage"));
        Assert.Equal(500, vector.Get("physical-defense"));
        Assert.Equal(420, vector.Get("magical-defense"));
        Assert.True(MinerBotanistAdvisorCatalog.HasRelevantCompleteProfile(Definition(profile), PhysicalRangedAdvisorStatFamily.Instance));
    }

    private static PhysicalRangedUtilityProfile Profile() => new(
        PhysicalRangedUtilityContextKind.GeneralCombat,
        Baseline,
        PhysicalRangedUtilityProfile.BardClassJobId,
        100);

    private static EquipmentItemDefinition Definition(EquipmentStatProfile profile) => new(
        10_000,
        "Physical ranged fixture",
        100,
        700,
        EquipmentSlot.MainHand,
        new HashSet<uint> { PhysicalRangedUtilityProfile.BardClassJobId },
        1,
        true,
        false,
        true,
        true,
        1,
        true,
        false,
        true,
        false,
        StatProfile: profile);
}
