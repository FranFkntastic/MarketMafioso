using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Squire.Outfitter;

public sealed class OutfitterMarketQuoteService
{
    private readonly IMarketAcquisitionListingSource listingSource;

    public OutfitterMarketQuoteService(IMarketAcquisitionListingSource listingSource)
    {
        this.listingSource = listingSource ?? throw new ArgumentNullException(nameof(listingSource));
    }

    public async Task<IReadOnlyDictionary<uint, OutfitterMarketQuote>> FetchAsync(
        string region,
        IEnumerable<uint> itemIds,
        CancellationToken cancellationToken)
    {
        var quotes = new Dictionary<uint, OutfitterMarketQuote>();
        foreach (var itemId in itemIds.Where(value => value != 0).Distinct().Take(24))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var listings = await listingSource.FetchListingsAsync(region, itemId, 20, cancellationToken)
                .ConfigureAwait(false);
            var cheapest = listings
                .Where(listing => listing.Quantity > 0)
                .OrderBy(listing => listing.UnitPrice)
                .ThenByDescending(listing => listing.LastReviewTimeUtc)
                .FirstOrDefault();
            if (cheapest is null)
                continue;
            quotes[itemId] = new(
                itemId,
                cheapest.UnitPrice,
                cheapest.WorldName,
                cheapest.Quantity,
                cheapest.LastReviewTimeUtc);
        }
        return quotes;
    }
}
