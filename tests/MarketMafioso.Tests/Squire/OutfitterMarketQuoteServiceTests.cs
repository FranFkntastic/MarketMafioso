using MarketMafioso.MarketAcquisition;
using MarketMafioso.Squire.Outfitter;

namespace MarketMafioso.Tests.Squire;

public sealed class OutfitterMarketQuoteServiceTests
{
    [Fact]
    public async Task FetchAsync_SelectsCheapestAvailableListingPerItem()
    {
        var source = new StubListingSource(new Dictionary<uint, IReadOnlyList<MarketAcquisitionListing>>
        {
            [10] =
            [
                new() { ItemId = 10, ItemName = "Bronze Sallet", WorldName = "Siren", UnitPrice = 500, Quantity = 0, LastReviewTimeUtc = DateTimeOffset.UtcNow },
                new() { ItemId = 10, ItemName = "Bronze Sallet", WorldName = "Gilgamesh", UnitPrice = 300, Quantity = 2, LastReviewTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-1) },
                new() { ItemId = 10, ItemName = "Bronze Sallet", WorldName = "Sargatanas", UnitPrice = 350, Quantity = 1, LastReviewTimeUtc = DateTimeOffset.UtcNow },
            ],
        });

        var quotes = await new OutfitterMarketQuoteService(source).FetchAsync(
            "North America",
            [10, 10],
            CancellationToken.None);

        var quote = Assert.Single(quotes).Value;
        Assert.Equal(300u, quote.UnitPriceGil);
        Assert.Equal("Gilgamesh", quote.WorldName);
        Assert.Equal(2u, quote.AvailableQuantity);
        Assert.Equal([(10u, 20)], source.Requests);
    }

    private sealed class StubListingSource(IReadOnlyDictionary<uint, IReadOnlyList<MarketAcquisitionListing>> listings)
        : IMarketAcquisitionListingSource
    {
        public List<(uint ItemId, int Limit)> Requests { get; } = [];

        public Task<IReadOnlyList<MarketAcquisitionListing>> FetchListingsAsync(
            string region,
            uint itemId,
            int limit,
            CancellationToken cancellationToken)
        {
            Requests.Add((itemId, limit));
            return Task.FromResult(listings.TryGetValue(itemId, out var values) ? values : []);
        }

        public Task<IReadOnlyList<MarketAcquisitionListing>> FetchListingsForWorldAsync(
            string worldName,
            uint itemId,
            int listingLimit,
            CancellationToken cancellationToken) =>
            FetchListingsAsync(worldName, itemId, listingLimit, cancellationToken);
    }
}
