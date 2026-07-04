using System;
using System.Threading;
using System.Threading.Tasks;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.CraftArchitectCompanion;

public static class CraftArchitectQuickShopRouteService
{
    public static async Task<CraftArchitectQuickShopRouteResult> CreateAsync(
        MarketAppraisalRequest request,
        ICraftQuoteProvider quoteProvider,
        Func<MarketAcquisitionQuickShopDraft, Task<bool>> createRoute,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(quoteProvider);
        ArgumentNullException.ThrowIfNull(createRoute);

        try
        {
            var quote = await quoteProvider.GetQuoteAsync(request, cancellationToken).ConfigureAwait(false);
            var draft = CraftArchitectQuickShopDraftBuilder.Build(request, quote);
            var created = await createRoute(draft).ConfigureAwait(false);
            return created
                ? new CraftArchitectQuickShopRouteResult(true, "Quick-shop route created and synced.")
                : new CraftArchitectQuickShopRouteResult(false, "Quick-shop route was not created.");
        }
        catch (OperationCanceledException)
        {
            return new CraftArchitectQuickShopRouteResult(false, "Quick-shop route creation cancelled.");
        }
        catch (Exception ex)
        {
            return new CraftArchitectQuickShopRouteResult(false, $"Quick-shop route failed: {ex.Message}");
        }
    }
}

public sealed record CraftArchitectQuickShopRouteResult(
    bool Created,
    string Message);
