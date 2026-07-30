using System.Net;
using System.Text;
using System.Text.Json;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class UniversalisMarketAcquisitionPlanSourceTests
{
    [Fact]
    public async Task FetchListingsAsync_BatchesTheCompletePlanWithFullListingDepth()
    {
        var handler = new RecordingHandler();
        var source = new UniversalisMarketAcquisitionPlanSource(
            new HttpClient(handler),
            new Uri("https://example.test/api/v2/"));
        var itemIds = Enumerable.Range(1, 9).Select(value => (uint)value).ToArray();

        var result = await source.FetchListingsAsync(
            "North America",
            itemIds,
            listingLimit: 100,
            CancellationToken.None);

        Assert.Equal(9, result.Count);
        var requestUri = Assert.Single(handler.Requests);
        Assert.Equal(
            "/api/v2/North-America/1,2,3,4,5,6,7,8,9?listings=100&entries=0",
            requestUri.PathAndQuery);
        Assert.All(result, pair =>
        {
            var listing = Assert.Single(pair.Value);
            Assert.Equal(pair.Key, listing.ItemId);
            Assert.Equal($"listing-{pair.Key}", listing.ListingId);
        });
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var itemIds = request.RequestUri!.AbsolutePath
                .Split('/')
                .Last()
                .Split(',')
                .Select(uint.Parse)
                .ToArray();
            var items = itemIds.ToDictionary(
                itemId => itemId.ToString(),
                itemId => new
                {
                    itemID = itemId,
                    listings = new[]
                    {
                        new
                        {
                            listingID = $"listing-{itemId}",
                            worldName = "Gilgamesh",
                            worldID = 63,
                            retainerName = "Retainer",
                            retainerID = $"retainer-{itemId}",
                            quantity = 10,
                            pricePerUnit = 123,
                            hq = false,
                            lastReviewTime = 1_700_000_000,
                        },
                    },
                });
            var json = JsonSerializer.Serialize(new { itemIDs = itemIds, items });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
