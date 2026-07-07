using MarketMafioso.RetainerRestock;

namespace MarketMafioso.Tests.RetainerRestock;

public sealed class RetainerRestockCompletionSummaryTests
{
    [Fact]
    public void Build_ReportsGenericFullSuccess()
    {
        var summary = RetainerRestockCompletionSummary.Build(
            new Dictionary<uint, int>
            {
                [100] = 0,
                [101] = -3,
            },
            totalRetrieved: 42);

        Assert.True(summary.IsSuccess);
        Assert.False(summary.IsPartial);
        Assert.Equal("Retainer restock complete. Retrieved 42 item(s).", summary.Message);
    }

    [Fact]
    public void Build_ReportsGenericPartialSuccess()
    {
        var summary = RetainerRestockCompletionSummary.Build(
            new Dictionary<uint, int>
            {
                [100] = 12,
                [101] = 0,
            },
            totalRetrieved: 24);

        Assert.True(summary.IsSuccess);
        Assert.True(summary.IsPartial);
        Assert.Equal("Retainer restock partially complete. Retrieved 24 item(s); remaining quantities: 100:12.", summary.Message);
    }

    [Fact]
    public void Build_FailsWhenNothingWasRetrieved()
    {
        var summary = RetainerRestockCompletionSummary.Build(
            new Dictionary<uint, int>
            {
                [100] = 12,
            },
            totalRetrieved: 0);

        Assert.False(summary.IsSuccess);
        Assert.False(summary.IsPartial);
        Assert.Equal("No matching live retainer stacks were found for the restock plan: 100:12.", summary.Message);
    }
}
