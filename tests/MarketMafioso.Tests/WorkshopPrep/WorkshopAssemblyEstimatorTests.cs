using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Tests.WorkshopPrep;

public sealed class WorkshopAssemblyEstimatorTests
{
    [Fact]
    public void Estimate_counts_projects_contributions_and_phase_overhead()
    {
        var plan = new WorkshopAssemblyPlan(
            [
                new WorkshopAssemblyQueueEntry(
                    10,
                    1010,
                    0,
                    0,
                    "Project A",
                    2,
                    [],
                    EstimatedContributionSteps: 5,
                    EstimatedPhaseCount: 3),
            ],
            []);

        var estimate = WorkshopAssemblyEstimator.Estimate(plan);

        Assert.Equal(2, estimate.TotalProjects);
        Assert.Equal(10, estimate.ContributionSteps);
        Assert.Equal(4, estimate.PhaseAdvancePrompts);
        Assert.Equal(2, estimate.FinalConstructionPrompts);
        Assert.Equal(2, estimate.ProductRetrievalPrompts);
        Assert.Equal(2, estimate.CutsceneSkips);
        Assert.True(estimate.Duration > TimeSpan.Zero);
    }

    [Theory]
    [InlineData(0, "0m")]
    [InlineData(59, "<1m")]
    [InlineData(60, "~1m")]
    [InlineData(3599, "~60m")]
    [InlineData(3600, "~1h")]
    [InlineData(5400, "~1h 30m")]
    public void FormatDuration_uses_compact_human_readable_text(int seconds, string expected)
    {
        Assert.Equal(expected, WorkshopAssemblyEstimator.FormatDuration(TimeSpan.FromSeconds(seconds)));
    }
}
