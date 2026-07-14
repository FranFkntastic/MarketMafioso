using MarketMafioso.WorkshopPrep;
using MarketMafioso.RetainerRestock;

namespace MarketMafioso.Tests.WorkshopPrep;

public sealed class WorkshopRetainerRestockCompletionTests
{
    [Fact]
    public void BuildCompletionSummary_ReportsPartialSuccessWhenSomeItemsWereRetrieved()
    {
        var summary = WorkshopRetainerRestockService.BuildCompletionSummary(
            new Dictionary<uint, int>
            {
                [5378] = 12,
                [5380] = 0,
            },
            totalRetrieved: 24);

        Assert.True(summary.IsSuccess);
        Assert.True(summary.IsPartial);
        Assert.Equal("Workshop material restock partially complete. Retrieved 24 item(s); remaining shortages: 5378:12.", summary.Message);
    }

    [Fact]
    public void BuildCompletionSummary_FailsWhenNothingWasRetrieved()
    {
        var summary = WorkshopRetainerRestockService.BuildCompletionSummary(
            new Dictionary<uint, int> { [5378] = 12 },
            totalRetrieved: 0);

        Assert.False(summary.IsSuccess);
        Assert.False(summary.IsPartial);
        Assert.Equal("No matching live retainer stacks were found for the workshop material shortages: 5378:12.", summary.Message);
    }

    [Theory]
    [InlineData(true, "Close the current retainer inventory before starting automated workshop material restock.")]
    [InlineData(false, null)]
    public void GetAutomatedRestockStartError_AllowsOpeningNearbyBell(
        bool isRetainerInventoryReady,
        string? expected)
    {
        Assert.Equal(expected, WorkshopRetainerRestockService.GetAutomatedRestockStartError(isRetainerInventoryReady));
    }

    [Fact]
    public void BuildRestockRunRequest_UsesNeededQuantitiesAndDistinctCandidateRetainers()
    {
        var lines = new[]
        {
            new RetainerRestockPlanLine(
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                100,
                "Elm Lumber",
                55,
                20,
                35,
                80,
                0,
                [
                    new RetainerRestockCandidate(10, "A", new DateTime(2026, 7, 7, 12, 0, 0, DateTimeKind.Utc), 80),
                    new RetainerRestockCandidate(11, "B", new DateTime(2026, 7, 7, 12, 0, 0, DateTimeKind.Utc), 20),
                ],
                RetainerRestockPlanLineStatus.Ready,
                TimeSpan.Zero),
            new RetainerRestockPlanLine(
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                101,
                "Ash Lumber",
                10,
                0,
                10,
                20,
                0,
                [
                    new RetainerRestockCandidate(10, "A", new DateTime(2026, 7, 7, 12, 0, 0, DateTimeKind.Utc), 10),
                ],
                RetainerRestockPlanLineStatus.Ready,
                TimeSpan.Zero),
        };

        var request = WorkshopRetainerRestockService.BuildRestockRunRequest(lines);

        Assert.Equal(
            new Dictionary<uint, int>
            {
                [100] = 35,
                [101] = 10,
            },
            request.RemainingQuantities);
        Assert.Collection(
            request.CandidateRetainers,
            candidate => Assert.Equal(10UL, candidate.RetainerId),
            candidate => Assert.Equal(11UL, candidate.RetainerId));
    }
}
