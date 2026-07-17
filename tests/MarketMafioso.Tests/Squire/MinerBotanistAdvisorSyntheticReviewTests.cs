#if DEBUG
using Franthropy.Dalamud.Equipment;
using Franthropy.Dalamud.UI.Plots;
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
        Assert.Contains(advice.OffersByAllocation.Values, offer => offer.Offer.Definition.Name == "Star Tech Pickaxe");
        Assert.Contains(advice.OffersByAllocation.Values, offer => offer.Offer.Definition.Name == "Crested Coat of Gathering");
        Assert.All(advice.OffersByAllocation.Values, offer => Assert.InRange(offer.Offer.Definition.ItemId, 1u, 100_000u));
        Assert.All(advice.OffersByAllocation.Values, offer => Assert.DoesNotContain("illustrative", offer.Offer.SourceLabel, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0UL, Solution(advice, "published-budget-raw").AcquisitionCostGil);
        Assert.Equal(10_800UL, Solution(advice, "published-budget-cloudsail").AcquisitionCostGil);
        Assert.Equal(2_462_623UL, Solution(advice, "published-mid-raw").AcquisitionCostGil);
        Assert.Equal(2_478_432UL, Solution(advice, "published-high-raw").AcquisitionCostGil);
        Assert.All(
            advice.OffersByAllocation.Values.Where(offer => offer.Offer.Definition.Name.StartsWith("Star Tech", StringComparison.Ordinal)),
            offer =>
            {
                Assert.Equal(EquipmentAcquisitionSourceKind.Owned, offer.Offer.SourceKind);
                Assert.Equal(0UL, offer.AcquisitionCostGil);
            });
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

    [Fact]
    public void ContextPlotsCanOverlayWithoutLosingSourceIdentity()
    {
        var builder = new ParetoFrontierPlotBuilder();
        var ordinary = MinerBotanistAdvisorSyntheticReview.Build(MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark);
        var legendary = MinerBotanistAdvisorSyntheticReview.Build(MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield);
        var collectables = MinerBotanistAdvisorSyntheticReview.Build(MinerBotanistUtilityContextKind.CollectableEfficiency);

        var overlay = PlotOverlayComposer.Compose("contexts",
        [
            new("ordinary", builder.Build(ordinary.Frontier!.Pareto).Spec),
            new("legendary", builder.Build(legendary.Frontier!.Pareto).Spec),
            new("collectables", builder.Build(collectables.Frontier!.Pareto).Spec),
        ]);

        Assert.Contains("ordinary/published-budget-raw", overlay.DatumIdentities.Keys);
        Assert.Contains("legendary/published-high-raw", overlay.DatumIdentities.Keys);
        Assert.Contains("collectables/published-mid-raw", overlay.DatumIdentities.Keys);
        Assert.Equal("Acquisition cost", overlay.Spec.XAxis.Label);
        Assert.Equal("Job utility", overlay.Spec.YAxis.Label);
    }

    private static EquipmentDecisionSolution Solution(MinerBotanistReadOnlyAdvice advice, string id) =>
        advice.Frontier!.Pareto.Frontier
            .Concat(advice.Frontier.Pareto.Dominated.Select(value => value.Solution))
            .Single(value => value.Candidate.SolutionId == id);
}
#endif
