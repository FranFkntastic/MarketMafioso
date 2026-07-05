using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using MarketMafioso.Server.WorkshopHost;

namespace MarketMafioso.Server.Tests;

public sealed class CraftArchitectWorkshopHostCraftQuoteServiceTests
{
    [Fact]
    public async Task AppraiseAsync_DelegatesToCraftArchitectAppraisalService()
    {
        var request = new CraftAppraisalRequest
        {
            ItemId = 2,
            ItemName = "Fire Shard",
            Quantity = 10,
        };
        var quote = new CraftAppraisalQuote
        {
            ItemId = 2,
            ItemName = "Fire Shard",
            RequestedQuantity = 10,
            EstimatedUnitCost = 80m,
            EstimatedTotalCost = 800m,
            QuotedAtUtc = DateTimeOffset.Parse("2026-07-05T14:30:00+00:00"),
            Source = "CraftArchitectLocal",
        };
        var appraisalService = new RecordingCraftAppraisalService(quote);
        var service = new CraftArchitectWorkshopHostCraftQuoteService(appraisalService);

        var result = await service.AppraiseAsync(request, CancellationToken.None);

        Assert.True(service.IsAvailable);
        Assert.Same(request, appraisalService.Request);
        Assert.Same(quote, result);
    }

    private sealed class RecordingCraftAppraisalService(CraftAppraisalQuote quote) : ICraftAppraisalService
    {
        public CraftAppraisalRequest? Request { get; private set; }

        public Task<CraftAppraisalQuote> AppraiseAsync(
            CraftAppraisalRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(quote);
        }
    }
}
