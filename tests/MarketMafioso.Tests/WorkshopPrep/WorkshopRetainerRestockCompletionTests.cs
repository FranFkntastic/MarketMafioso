using MarketMafioso.WorkshopPrep;

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
}
