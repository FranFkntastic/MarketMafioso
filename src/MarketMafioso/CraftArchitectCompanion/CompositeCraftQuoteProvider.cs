using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MarketMafioso.CraftArchitectCompanion;

public sealed class CompositeCraftQuoteProvider : ICraftQuoteProvider
{
    private readonly IReadOnlyList<ICraftQuoteProvider> providers;

    public CompositeCraftQuoteProvider(IReadOnlyList<ICraftQuoteProvider> providers)
    {
        this.providers = providers ?? throw new ArgumentNullException(nameof(providers));
    }

    public string ProviderId => "Composite";

    public bool IsConfigured => providers.Any(provider => provider.IsConfigured);

    public async Task<CraftAppraisalQuote?> GetQuoteAsync(
        MarketAppraisalRequest request,
        CancellationToken cancellationToken = default)
    {
        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!provider.IsConfigured)
                continue;

            var quote = await provider.GetQuoteAsync(request, cancellationToken).ConfigureAwait(false);
            if (quote is not null)
                return quote;
        }

        return null;
    }
}
