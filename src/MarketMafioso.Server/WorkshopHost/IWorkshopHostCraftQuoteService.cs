using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;

namespace MarketMafioso.Server.WorkshopHost;

public interface IWorkshopHostCraftQuoteService
{
    bool IsAvailable { get; }
    Task<CraftAppraisalQuote?> AppraiseAsync(
        CraftAppraisalRequest request,
        CancellationToken cancellationToken);
}
