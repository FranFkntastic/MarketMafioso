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
            ["crafted-unmelded", "published-mid-crafted", "published-high-crafted"],
            advice.Frontier.Pareto.Frontier
                .OrderBy(solution => solution.AcquisitionCostGil)
                .Select(solution => solution.Candidate.SolutionId));
        Assert.DoesNotContain(
            advice.Frontier.Pareto.Frontier,
            solution => solution.Candidate.SolutionId.StartsWith("derived-", StringComparison.Ordinal));
        Assert.Equal("published-mid-crafted", advice.Nomination?.Candidate.SolutionId);
        Assert.All(advice.OffersByAllocation.Values, offer => Assert.Equal(EquipmentQuality.High, offer.Offer.ResolvedQuality));
        Assert.Contains(advice.OffersByAllocation.Values, offer => offer.Offer.Definition.Name == "Gold Thumb's Pickaxe");
        Assert.Contains(advice.OffersByAllocation.Values, offer => offer.Offer.Definition.Name == "Crested Coat of Gathering");
        Assert.All(advice.OffersByAllocation.Values, offer => Assert.InRange(offer.Offer.Definition.ItemId, 1u, 100_000u));
        Assert.All(advice.OffersByAllocation.Values, offer => Assert.DoesNotContain("illustrative", offer.Offer.SourceLabel, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(advice.OffersByAllocation.Values, offer => offer.Offer.SourceKind != EquipmentAcquisitionSourceKind.MarketBoard);
        Assert.DoesNotContain(advice.OffersByAllocation.Values, offer => offer.Offer.Definition.ItemId is 49334 or 49345 or 49352 or 49353 or 49354 or 49355 or 49356 or 49361 or 49362 or 49363 or 49364 or 51786);
        Assert.Equal(2_721_179UL, Solution(advice, "crafted-unmelded").AcquisitionCostGil);
        Assert.Equal(3_009_117UL, Solution(advice, "published-mid-crafted").AcquisitionCostGil);
        Assert.Equal(3_275_797UL, Solution(advice, "published-high-crafted").AcquisitionCostGil);
        Assert.Contains(Solution(advice, "published-high-crafted").VariantLabels, label => label.Contains("3,136,992 gil materia", StringComparison.Ordinal));
        Assert.Contains(Solution(advice, "published-high-crafted").VariantLabels, label => label.Contains("5,858,171 gil total", StringComparison.Ordinal));
        Assert.All(advice.Frontier.Pareto.Frontier, solution => Assert.Equal(
            solution.AcquisitionCostGil,
            solution.Candidate.Selections.Aggregate(0UL, (total, selection) =>
                checked(total + advice.OffersByAllocation[selection.AllocationKey].AcquisitionCostGil))));
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

        Assert.Contains("ordinary/crafted-unmelded", overlay.DatumIdentities.Keys);
        Assert.Contains("legendary/published-high-crafted", overlay.DatumIdentities.Keys);
        Assert.Contains("collectables/published-mid-crafted", overlay.DatumIdentities.Keys);
        Assert.Equal("Acquisition cost", overlay.Spec.XAxis.Label);
        Assert.Equal("Job utility", overlay.Spec.YAxis.Label);
        Assert.InRange(overlay.Spec.YDomain.Maximum, 0d, 110d);
    }

    private static EquipmentDecisionSolution Solution(MinerBotanistReadOnlyAdvice advice, string id) =>
        advice.Frontier!.Pareto.Frontier
            .Concat(advice.Frontier.Pareto.Dominated.Select(value => value.Solution))
            .Single(value => value.Candidate.SolutionId == id);
}
#endif
