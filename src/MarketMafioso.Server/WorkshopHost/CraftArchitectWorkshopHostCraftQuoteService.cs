using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;

namespace MarketMafioso.Server.WorkshopHost;

public sealed class CraftArchitectWorkshopHostCraftQuoteService(
    ICraftAppraisalService appraisalService) : IWorkshopHostCraftQuoteService
{
    public bool IsAvailable => true;

    public async Task<CraftAppraisalQuote?> AppraiseAsync(
        CraftAppraisalRequest request,
        CancellationToken cancellationToken)
    {
        return await appraisalService.AppraiseAsync(request, cancellationToken);
    }
}
