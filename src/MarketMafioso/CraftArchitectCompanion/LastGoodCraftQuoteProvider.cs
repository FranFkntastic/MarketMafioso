using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MarketMafioso.CraftArchitectCompanion;

public sealed class LastGoodCraftQuoteProvider : ICraftQuoteProvider
{
    private readonly ICraftQuoteProvider inner;
    private readonly Dictionary<QuoteCacheKey, CraftAppraisalQuote> cache = new();

    public LastGoodCraftQuoteProvider(ICraftQuoteProvider inner)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public string ProviderId => $"{inner.ProviderId}+LastGood";

    public bool IsConfigured => inner.IsConfigured;

    public async Task<CraftAppraisalQuote?> GetQuoteAsync(
        MarketAppraisalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var key = QuoteCacheKey.FromRequest(request);
        try
        {
            var quote = await inner.GetQuoteAsync(request, cancellationToken).ConfigureAwait(false);
            if (quote is not null)
                cache[key] = quote;

            return quote;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return cache.TryGetValue(key, out var lastGood)
                ? LabelLastGood(lastGood, ex)
                : null;
        }
    }

    private static CraftAppraisalQuote LabelLastGood(CraftAppraisalQuote quote, Exception exception)
    {
        var warnings = new List<string>(quote.Warnings)
        {
            $"Using last-good craft quote because live quote evidence failed: {exception.Message}",
            "Last-good quote is advisory only. User acquisition threshold remains authoritative.",
        };

        return quote with
        {
            Source = string.IsNullOrWhiteSpace(quote.Source)
                ? "Last-good"
                : $"{quote.Source} (last-good)",
            Warnings = warnings,
        };
    }

    private readonly record struct QuoteCacheKey(
        uint ItemId,
        uint Quantity,
        string HqPolicy,
        string Region)
    {
        public static QuoteCacheKey FromRequest(MarketAppraisalRequest request) => new(
            request.ItemId,
            request.Quantity,
            Normalize(request.HqPolicy),
            Normalize(request.Region));

        private static string Normalize(string value) =>
            value.Trim().ToUpperInvariant();
    }
}
