namespace MarketMafioso.Server.WorkshopHost;

public sealed class UnavailableWorkshopHostCraftQuoteService : IWorkshopHostCraftQuoteService
{
    public bool IsAvailable => false;

    public Task<CraftAppraisalQuote?> AppraiseAsync(
        CraftAppraisalRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult<CraftAppraisalQuote?>(null);
}
