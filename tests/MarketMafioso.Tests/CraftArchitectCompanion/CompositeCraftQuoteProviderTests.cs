using MarketMafioso.CraftArchitectCompanion;

namespace MarketMafioso.Tests.CraftArchitectCompanion;

public sealed class CompositeCraftQuoteProviderTests
{
    [Fact]
    public async Task GetQuoteAsync_ReturnsFirstConfiguredProviderQuote()
    {
        var fileQuote = CreateQuote("CraftArchitectFile", 80m);
        var manualQuote = CreateQuote("Manual", 90m);
        var provider = new CompositeCraftQuoteProvider(
        [
            new TestQuoteProvider("CraftArchitectFile", isConfigured: true, fileQuote),
            new TestQuoteProvider("Manual", isConfigured: true, manualQuote),
        ]);

        var quote = await provider.GetQuoteAsync(CreateRequest());

        Assert.Same(fileQuote, quote);
    }

    [Fact]
    public async Task GetQuoteAsync_FallsBackWhenEarlierProviderIsUnconfigured()
    {
        var manualQuote = CreateQuote("Manual", 90m);
        var provider = new CompositeCraftQuoteProvider(
        [
            new TestQuoteProvider("CraftArchitectFile", isConfigured: false, null),
            new TestQuoteProvider("Manual", isConfigured: true, manualQuote),
        ]);

        var quote = await provider.GetQuoteAsync(CreateRequest());

        Assert.Same(manualQuote, quote);
    }

    [Fact]
    public void IsConfigured_ReturnsTrueWhenAnyProviderIsConfigured()
    {
        var provider = new CompositeCraftQuoteProvider(
        [
            new TestQuoteProvider("CraftArchitectFile", isConfigured: false, null),
            new TestQuoteProvider("Manual", isConfigured: true, null),
        ]);

        Assert.True(provider.IsConfigured);
    }

    private static MarketAppraisalRequest CreateRequest() => new()
    {
        ItemId = 2,
        ItemName = "Fire Shard",
        Quantity = 10,
        HqPolicy = "Either",
        BuyThresholdUnitPrice = 100,
        Region = "North America",
        WorldMode = "Recommended",
        SweepScope = "Region",
    };

    private static CraftAppraisalQuote CreateQuote(string source, decimal unitCost) => new()
    {
        ItemId = 2,
        ItemName = "Fire Shard",
        RequestedQuantity = 10,
        EstimatedUnitCost = unitCost,
        EstimatedTotalCost = unitCost * 10,
        Source = source,
    };

    private sealed class TestQuoteProvider(
        string providerId,
        bool isConfigured,
        CraftAppraisalQuote? quote) : ICraftQuoteProvider
    {
        public string ProviderId => providerId;

        public bool IsConfigured => isConfigured;

        public Task<CraftAppraisalQuote?> GetQuoteAsync(
            MarketAppraisalRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(quote);
    }
}
