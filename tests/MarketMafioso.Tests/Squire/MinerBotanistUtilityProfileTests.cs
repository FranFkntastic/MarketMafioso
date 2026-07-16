using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Utility;

namespace MarketMafioso.Tests.Squire;

public sealed class MinerBotanistUtilityProfileTests
{
    [Fact]
    public void HqCrossingLegendaryYieldThresholdEarnsCapabilityAuthority()
    {
        var profile = Legendary(new(5_399, 5_200, 950));

        var candidate = profile.Evaluate(new MinerBotanistUtilityStats(5_400, 5_200, 950));
        var authority = profile.AssessAuthorityForCalibration(candidate, additionalCostGil: 25_000);

        Assert.Equal(UpgradeAssessment.ClearImprovement, candidate.Assessment);
        Assert.True(authority.AdvisorMayConsider);
        Assert.Contains("node-yield-plus-one", authority.GainedCapabilityIds);
    }

    [Fact]
    public void PaidNonCrossingImprovementRemainsVisibleButCannotNominateItself()
    {
        var profile = Legendary(new(5_401, 5_200, 950));

        var candidate = profile.Evaluate(new MinerBotanistUtilityStats(5_500, 5_200, 950));
        var authority = profile.AssessAuthorityForCalibration(candidate, additionalCostGil: 25_000);

        Assert.True(candidate.UtilityScore > profile.BaselineEvaluation.UtilityScore);
        Assert.Equal(UpgradeAssessment.ClearImprovement, candidate.Assessment);
        Assert.False(authority.AdvisorMayConsider);
        Assert.Contains(authority.Reasons, reason => reason.Contains("no supported capability step", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FreeMonotonicImprovementMayBeConsideredWithoutThresholdGain()
    {
        var profile = Legendary(new(5_401, 5_200, 950));

        var candidate = profile.Evaluate(new MinerBotanistUtilityStats(5_500, 5_200, 950));
        var authority = profile.AssessAuthorityForCalibration(candidate, additionalCostGil: 0);

        Assert.True(authority.AdvisorMayConsider);
        Assert.Empty(authority.GainedCapabilityIds);
    }

    [Fact]
    public void ConflictingGatheringAndPerceptionTradeAbstains()
    {
        var profile = Legendary(new(5_399, 5_600, 950));

        var candidate = profile.Evaluate(new MinerBotanistUtilityStats(5_400, 5_599, 950));
        var authority = profile.AssessAuthorityForCalibration(candidate, additionalCostGil: 0);

        Assert.Equal(UpgradeAssessment.ContextDependent, candidate.Assessment);
        Assert.False(authority.AdvisorMayConsider);
    }

    [Fact]
    public void CollectableCompositeCapabilityRequiresBothStats()
    {
        var profile = Collectable(new(5_172, 5_172, 950));

        var gatheringOnly = profile.Evaluate(new MinerBotanistUtilityStats(5_173, 5_172, 950));
        var both = profile.Evaluate(new MinerBotanistUtilityStats(5_173, 5_173, 950));

        Assert.DoesNotContain(gatheringOnly.Thresholds, threshold => threshold.ThresholdId == "collectable-actions-i730" && threshold.Satisfied);
        Assert.Contains(both.Thresholds, threshold => threshold.ThresholdId == "collectable-actions-i730" && threshold.Satisfied);
        Assert.True(both.UtilityScore - gatheringOnly.UtilityScore > 900);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void EvidencePatchAndUnmodeledEffectsIndependentlyBlockAuthority(
        bool evidenceComplete,
        bool patchMatches,
        bool hasUnmodeledEffect)
    {
        var profile = Legendary(new(5_399, 5_200, 950));
        var candidate = profile.Evaluate(new MinerBotanistUtilityStats(5_400, 5_200, 950));

        var authority = profile.AssessAuthorityForCalibration(
            candidate,
            0,
            evidenceComplete,
            patchMatches,
            hasUnmodeledEffect);

        Assert.False(authority.AdvisorMayConsider);
    }

    [Fact]
    public void OrdinaryResourceBenchmarkDoesNotLeakLegendaryThresholdStep()
    {
        var profile = new MinerBotanistUtilityProfile(
            MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark,
            new(5_399, 5_200, 950),
            MinerBotanistUtilityProfile.MinerClassJobId);

        var candidate = profile.Evaluate(new MinerBotanistUtilityStats(5_400, 5_200, 950));
        var authority = profile.AssessAuthorityForCalibration(candidate, 0);

        Assert.InRange(candidate.UtilityScore - profile.BaselineEvaluation.UtilityScore, 0.001, 1);
        Assert.Empty(candidate.Thresholds);
        Assert.Equal(UpgradeAssessment.Unsupported, candidate.Assessment);
        Assert.False(authority.AdvisorMayConsider);
    }

    [Fact]
    public void FixedNonOfferStatsParticipateInWholeLoadoutThresholds()
    {
        var profile = new MinerBotanistUtilityProfile(
            MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield,
            new(399, 200, 50),
            MinerBotanistUtilityProfile.MinerClassJobId,
            fixedStats: new(5_000, 5_000, 900));

        var candidate = profile.Evaluate(new MinerBotanistUtilityStats(400, 200, 50));

        Assert.Contains(candidate.Thresholds, threshold => threshold.ThresholdId == "node-yield-plus-one" && threshold.Satisfied);
        Assert.Equal(5_400, candidate.RawStats.Single(stat => stat.Semantic == EquipmentStatSemantic.Gathering).Value);
    }

    [Fact]
    public void OracleApprovedProfileStillRequiresExplicitRuntimePromotionAfterReview()
    {
        var profile = Legendary(new(5_399, 5_200, 950));
        var candidate = profile.Evaluate(new MinerBotanistUtilityStats(5_400, 5_200, 950));

        var authority = profile.AssessAuthority(candidate, additionalCostGil: 0);

        Assert.Equal(MinerBotanistCalibrationState.Experimental, MinerBotanistUtilityProfile.CalibrationState);
        Assert.False(authority.AdvisorMayConsider);
        Assert.Contains(authority.Reasons, reason => reason.Contains("experimental", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FisherCannotUseSharedMinerBotanistProfile()
    {
        var profile = new MinerBotanistUtilityProfile(
            MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield,
            new(5_399, 5_200, 950),
            classJobId: 18);

        var evaluation = profile.Evaluate(new MinerBotanistUtilityStats(5_400, 5_200, 950));

        Assert.Equal(UpgradeAssessment.Unsupported, evaluation.Assessment);
        Assert.Contains(evaluation.Diagnostics, diagnostic => diagnostic.Contains("Fisher", StringComparison.Ordinal));
    }

    private static MinerBotanistUtilityProfile Legendary(MinerBotanistUtilityStats baseline) => new(
        MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield,
        baseline,
        MinerBotanistUtilityProfile.MinerClassJobId);

    private static MinerBotanistUtilityProfile Collectable(MinerBotanistUtilityStats baseline) => new(
        MinerBotanistUtilityContextKind.CollectableEfficiency,
        baseline,
        MinerBotanistUtilityProfile.BotanistClassJobId);
}
