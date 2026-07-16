#if DEBUG
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire.Outfitter.Utility;
using Xunit;

namespace MarketMafioso.Tests.Squire;

public sealed class MinerBotanistAdvisorSyntheticReviewTests
{
    [Fact]
    public void LegendaryReplayExercisesRealFrontierAndExactQualityPresentation()
    {
        var advice = MinerBotanistAdvisorSyntheticReview.Build(
            MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield);

        Assert.Equal(MinerBotanistAdvisorStatus.Complete, advice.Status);
        Assert.NotNull(advice.Frontier);
        Assert.Equal(
            ["published-budget-raw", "published-budget-cloudsail", "published-mid-raw", "published-high-raw"],
            advice.Frontier.Pareto.Frontier
                .OrderBy(solution => solution.AcquisitionCostGil)
                .Select(solution => solution.Candidate.SolutionId));
        Assert.DoesNotContain(
            advice.Frontier.Pareto.Frontier,
            solution => solution.Candidate.SolutionId.StartsWith("derived-", StringComparison.Ordinal));
        Assert.Equal("published-mid-raw", advice.Nomination?.Candidate.SolutionId);
        Assert.Contains(advice.OffersByAllocation.Values, offer => offer.Offer.ResolvedQuality == EquipmentQuality.Normal);
        Assert.Contains(advice.OffersByAllocation.Values, offer => offer.Offer.ResolvedQuality == EquipmentQuality.High);
        Assert.All(advice.OffersByAllocation.Values, offer => Assert.StartsWith("Synthetic ", offer.Offer.Definition.Name));
    }

    [Fact]
    public void OrdinaryNodeReplayDisplaysFrontierButCannotNominate()
    {
        var advice = MinerBotanistAdvisorSyntheticReview.Build(
            MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark);

        Assert.NotNull(advice.Frontier);
        Assert.NotEmpty(advice.Frontier.Pareto.Frontier);
        Assert.Null(advice.Nomination);
        Assert.All(advice.AuthorityBySolutionId.Values, authority => Assert.False(authority.AdvisorMayConsider));
    }
}
#endif
