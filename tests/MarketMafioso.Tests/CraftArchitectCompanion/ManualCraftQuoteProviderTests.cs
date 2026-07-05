using MarketMafioso.CraftArchitectCompanion;

namespace MarketMafioso.Tests.CraftArchitectCompanion;

public sealed class ManualCraftQuoteProviderTests
{
    [Fact]
    public async Task GetQuoteAsync_ReturnsNullWhenManualCostIsMissing()
    {
        var provider = new ManualCraftQuoteProvider(_ => null, () => DateTimeOffset.UnixEpoch);

        var quote = await provider.GetQuoteAsync(CreateRequest());

        Assert.Null(quote);
    }

    [Fact]
    public async Task GetQuoteAsync_BuildsAdvisoryManualQuote()
    {
        var quotedAtUtc = new DateTimeOffset(2026, 7, 4, 14, 30, 0, TimeSpan.Zero);
        var provider = new ManualCraftQuoteProvider(_ => 80m, () => quotedAtUtc);

        var quote = await provider.GetQuoteAsync(CreateRequest());

        Assert.NotNull(quote);
        Assert.Equal(1, quote.SchemaVersion);
        Assert.Equal(2u, quote.ItemId);
        Assert.Equal(10u, quote.RequestedQuantity);
        Assert.Equal(80m, quote.EstimatedUnitCost);
        Assert.Equal(800m, quote.EstimatedTotalCost);
        Assert.Equal("Manual", quote.Source);
        Assert.Equal("Manual", quote.Confidence);
        Assert.True(quote.IsComplete);
        Assert.Equal("Complete", quote.AppraisalStatus);
        Assert.Equal(quotedAtUtc, quote.QuotedAtUtc);
        Assert.Contains(quote.Warnings, warning => warning.Contains("advisory", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetQuoteAsync_DoesNotCopyBuyThresholdIntoQuote()
    {
        var provider = new ManualCraftQuoteProvider(_ => 80m, () => DateTimeOffset.UnixEpoch);

        var quote = await provider.GetQuoteAsync(CreateRequest(buyThreshold: 120));

        Assert.NotNull(quote);
        Assert.Equal(80m, quote.EstimatedUnitCost);
        Assert.DoesNotContain(quote.Warnings, warning => warning.Contains("120", StringComparison.Ordinal));
    }

    private static MarketAppraisalRequest CreateRequest(uint buyThreshold = 120) => new()
    {
        ItemId = 2,
        ItemName = "Fire Shard",
        Quantity = 10,
        HqPolicy = "Either",
        BuyThresholdUnitPrice = buyThreshold,
        Region = "North America",
        WorldMode = "Recommended",
        SweepScope = "Region",
    };
}
