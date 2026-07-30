using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using FFXIV_Craft_Architect.Core.Services;

namespace MarketMafioso.Server.WorkshopHost;

public sealed class CraftArchitectWorkshopHostCraftQuoteService(
    ICraftAppraisalService appraisalService,
    RecipeCalculationService recipeCalculationService,
    WorkshopHostCraftQuoteCache quoteCache,
    CraftAppraisalPlanStore planStore) : IWorkshopHostCraftQuoteService
{
    public bool IsAvailable => true;

    public async Task<CraftAppraisalQuote?> AppraiseAsync(
        CraftAppraisalRequest request,
        CancellationToken cancellationToken)
    {
        if (quoteCache.TryGet(request, out var cached))
            return cached;

        var quote = await appraisalService.AppraiseAsync(request, cancellationToken);
        if (quote == null)
            return null;

        if (quote.Plan != null)
        {
            var planJson = recipeCalculationService.SerializePlan(
                quote.Plan,
                includeMarketPrices: true);
            var planId = await planStore.SaveAsync(planJson, cancellationToken);
            quote = quote with { PlanId = planId };
        }

        quoteCache.Set(request, quote);
        return quote;
    }
}
