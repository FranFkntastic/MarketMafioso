using MarketMafioso.CraftArchitectCompanion;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Tests.CraftArchitectCompanion;

public sealed class CraftArchitectQuickShopRouteServiceTests
{
    [Fact]
    public async Task CreateAsync_ReturnsFailureWhenQuoteProviderThrows()
    {
        var provider = new ThrowingQuoteProvider(new InvalidOperationException("Configured Craft Architect quote file does not exist."));

        var result = await CraftArchitectQuickShopRouteService.CreateAsync(
            CreateRequest(),
            provider,
            _ => Task.FromResult(true));

        Assert.False(result.Created);
        Assert.Contains("Quick-shop route failed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("quote file", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_BuildsDraftWithProviderQuote()
    {
        var quote = new CraftAppraisalQuote
        {
            ItemId = 2,
            ItemName = "Fire Shard",
            RequestedQuantity = 10,
            EstimatedUnitCost = 80m,
            EstimatedTotalCost = 800m,
            Source = "CraftArchitectFile",
        };
        var provider = new FixedQuoteProvider(quote);
        MarketAcquisitionQuickShopDraft? capturedDraft = null;

        var result = await CraftArchitectQuickShopRouteService.CreateAsync(
            CreateRequest(),
            provider,
            draft =>
            {
                capturedDraft = draft;
                return Task.FromResult(true);
            });

        Assert.True(result.Created);
        Assert.Equal("Quick-shop route created and synced.", result.Message);
        Assert.NotNull(capturedDraft);
        var line = Assert.Single(capturedDraft.Lines);
        Assert.Equal(2u, line.ItemId);
        Assert.Equal(100u, line.MaxUnitPrice);
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

    private sealed class FixedQuoteProvider(CraftAppraisalQuote? quote) : ICraftQuoteProvider
    {
        public string ProviderId => "Fixed";
        public bool IsConfigured => true;

        public Task<CraftAppraisalQuote?> GetQuoteAsync(
            MarketAppraisalRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(quote);
    }

    private sealed class ThrowingQuoteProvider(Exception exception) : ICraftQuoteProvider
    {
        public string ProviderId => "Throwing";
        public bool IsConfigured => true;

        public Task<CraftAppraisalQuote?> GetQuoteAsync(
            MarketAppraisalRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<CraftAppraisalQuote?>(exception);
    }
}
