using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class AdvisorStatFamilyCompatibilityTests
{
    [Fact]
    public void GathererDescriptor_PreservesContextIdsLabelsDefaultAndConfigurationValues()
    {
        var descriptor = GathererAdvisorStatFamily.Instance.ProfileDescriptor;

        Assert.Equal(MinerBotanistUtilityProfile.ProfileId, descriptor.Id);
        Assert.Equal(MinerBotanistUtilityProfile.ProfileVersion, descriptor.Version);
        Assert.Equal(AdvisorProfileCalibrationState.Supported, descriptor.CalibrationState);
        Assert.Equal(
            [
                (MinerBotanistUtilityProfile.OrdinaryResourceBenchmarkContextId, "OrdinaryResourceBenchmark", "Ordinary nodes · general yield"),
                (MinerBotanistUtilityProfile.LegendaryContextId, "LegendaryNodeGeneralYield", "Legendary nodes · general yield"),
                (MinerBotanistUtilityProfile.CollectableContextId, "CollectableEfficiency", "Collectables · i730 efficiency"),
            ],
            descriptor.Contexts.Select(context => (context.Id, context.ConfigurationValue, context.Label)).ToArray());
        Assert.Same(GathererAdvisorStatFamily.OrdinaryResourceContext, descriptor.DefaultContext);
        Assert.Same(
            GathererAdvisorStatFamily.LegendaryNodeContext,
            GathererAdvisorStatFamily.Instance.ResolveContext("LegendaryNodeGeneralYield"));
        Assert.Same(
            GathererAdvisorStatFamily.LegendaryNodeContext,
            GathererAdvisorStatFamily.Instance.ResolveContext(MinerBotanistUtilityProfile.LegendaryContextId));
    }

    [Fact]
    public void CrafterDescriptor_OwnsItsProfileCalibrationAndDefaultContext()
    {
        var family = CrafterAdvisorStatFamily.Instance;
        var descriptor = family.ProfileDescriptor;

        Assert.Equal(CrafterUtilityProfile.ProfileId, descriptor.Id);
        Assert.Equal(CrafterUtilityProfile.ProfileVersion, descriptor.Version);
        Assert.Equal(AdvisorProfileCalibrationState.Supported, descriptor.CalibrationState);
        Assert.Single(descriptor.Contexts);
        Assert.Same(CrafterAdvisorStatFamily.OrdinaryCraftContext, descriptor.DefaultContext);
        Assert.Same(
            CrafterAdvisorStatFamily.OrdinaryCraftContext,
            family.ResolveContext(MinerBotanistUtilityProfile.LegendaryContextId));
    }

    [Fact]
    public void FisherHasNoExpansionFamilyAndUsesTerminalScopeLanguage()
    {
        Assert.Null(AdvisorStatFamilies.Resolve(AdvisorStatFamilies.FisherClassJobId));
        Assert.Equal(
            "Fisher is permanently unsupported and out of scope for Squire Outfitter.",
            AdvisorStatFamilies.UnsupportedDiagnostic(AdvisorStatFamilies.FisherClassJobId));
    }

    [Fact]
    public void PhysicalRangedDescriptor_IsSharedExperimentalAndFamilyOwned()
    {
        var family = PhysicalRangedAdvisorStatFamily.Instance;
        var descriptor = family.ProfileDescriptor;

        Assert.Equal(PhysicalRangedUtilityProfile.ProfileId, descriptor.Id);
        Assert.Equal(AdvisorProfileCalibrationState.Experimental, descriptor.CalibrationState);
        Assert.Same(PhysicalRangedAdvisorStatFamily.GeneralCombatContext, descriptor.DefaultContext);
        Assert.Equal("General physical-ranged combat", descriptor.DefaultContext.Label);
        Assert.Equal("GeneralCombat", descriptor.DefaultContext.ConfigurationValue);
        Assert.Equal(AdvisorCombatRoles.PhysicalRanged.ClassJobIds.Order(), family.SupportedClassJobIds.Order());
    }
}
