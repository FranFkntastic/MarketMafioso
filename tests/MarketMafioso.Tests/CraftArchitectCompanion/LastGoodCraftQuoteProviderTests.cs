using MarketMafioso.CraftArchitectCompanion;

namespace MarketMafioso.Tests.CraftArchitectCompanion;

public sealed class LastGoodCraftQuoteProviderTests
{
    [Fact]
    public async Task GetQuoteAsync_ReturnsLiveQuoteAndCachesIt()
    {
        var liveQuote = CreateQuote("WorkshopHostCraftArchitect");
        var inner = new SequenceQuoteProvider(liveQuote);
        var provider = new LastGoodCraftQuoteProvider(inner);

        var quote = await provider.GetQuoteAsync(CreateRequest());

        Assert.Same(liveQuote, quote);
    }

    [Fact]
    public async Task GetQuoteAsync_ReturnsLabeledLastGoodQuoteWhenInnerProviderFails()
    {
        var liveQuote = CreateQuote("WorkshopHostCraftArchitect");
        var inner = new SequenceQuoteProvider(liveQuote);
        var provider = new LastGoodCraftQuoteProvider(inner);
        await provider.GetQuoteAsync(CreateRequest());
        inner.Exception = new InvalidOperationException("receiver unavailable");

        var quote = await provider.GetQuoteAsync(CreateRequest());

        Assert.NotNull(quote);
        Assert.NotSame(liveQuote, quote);
        Assert.Equal("WorkshopHostCraftArchitect (last-good)", quote.Source);
        Assert.Contains(quote.Warnings, warning => warning.Contains("last-good", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(quote.Warnings, warning => warning.Contains("receiver unavailable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetQuoteAsync_DoesNotReuseLastGoodQuoteForDifferentRequest()
    {
        var liveQuote = CreateQuote("WorkshopHostCraftArchitect");
        var inner = new SequenceQuoteProvider(liveQuote);
        var provider = new LastGoodCraftQuoteProvider(inner);
        await provider.GetQuoteAsync(CreateRequest());
        inner.Exception = new InvalidOperationException("receiver unavailable");

        var quote = await provider.GetQuoteAsync(CreateRequest(quantity: 11));

        Assert.Null(quote);
    }

    private static MarketAppraisalRequest CreateRequest(uint quantity = 10) => new()
    {
        ItemId = 2,
        ItemName = "Fire Shard",
        Quantity = quantity,
        HqPolicy = "Either",
        BuyThresholdUnitPrice = 100,
        Region = "North America",
        WorldMode = "Recommended",
        SweepScope = "Region",
    };

    private static CraftAppraisalQuote CreateQuote(string source) => new()
    {
        ItemId = 2,
        ItemName = "Fire Shard",
        RequestedQuantity = 10,
        EstimatedUnitCost = 80m,
        EstimatedTotalCost = 800m,
        Source = source,
        Confidence = "Medium",
        Warnings = ["Quote is advisory evidence."],
    };

    private sealed class SequenceQuoteProvider(CraftAppraisalQuote? quote) : ICraftQuoteProvider
    {
        public string ProviderId => "Sequence";

        public bool IsConfigured => true;

        public Exception? Exception { get; set; }

        public Task<CraftAppraisalQuote?> GetQuoteAsync(
            MarketAppraisalRequest request,
            CancellationToken cancellationToken = default)
        {
            return Exception is null
                ? Task.FromResult(quote)
                : Task.FromException<CraftAppraisalQuote?>(Exception);
        }
    }
}
