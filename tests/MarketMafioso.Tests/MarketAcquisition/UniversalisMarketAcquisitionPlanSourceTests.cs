using System.Net;
using System.Text.Json;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class UniversalisMarketAcquisitionPlanSourceTests
{
    [Fact]
    public async Task FetchListingsAsync_RequestsRegionItemEndpointAndParsesListings()
    {
        using var handler = new CapturingHandler("""
            {
              "itemID": 2,
              "regionName": "North-America",
              "listings": [
                {
                  "lastReviewTime": 1782370805,
                  "pricePerUnit": 50,
                  "quantity": 500,
                  "worldName": "Faerie",
                  "worldID": 54,
                  "hq": false,
                  "listingID": "9227171949922142549",
                  "retainerID": "33777097243098909",
                  "retainerName": "Thatguythatoneguy"
                }
              ]
            }
            """);
        using var httpClient = new HttpClient(handler);
        var source = new MarketMafioso.MarketAcquisition.UniversalisMarketAcquisitionPlanSource(httpClient);

        var listings = await source.FetchListingsAsync(
            "North America",
            2,
            5,
            CancellationToken.None);

        var listing = Assert.Single(listings);
        Assert.Equal("https://universalis.app/api/v2/North-America/2?listings=5", handler.LastRequest?.RequestUri?.ToString());
        Assert.Equal("9227171949922142549", listing.ListingId);
        Assert.Equal(2u, listing.ItemId);
        Assert.Equal("Faerie", listing.WorldName);
        Assert.Equal(54u, listing.WorldId);
        Assert.Equal(500u, listing.Quantity);
        Assert.Equal(50u, listing.UnitPrice);
        Assert.False(listing.IsHq);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1782370805), listing.LastReviewTimeUtc);
    }

    [Fact]
    public async Task FetchListingsForWorldAsync_RequestsWorldItemEndpointAndParsesListings()
    {
        using var handler = new CapturingHandler("""
            {
              "itemID": 2,
              "worldName": "Siren",
              "listings": [
                {
                  "lastReviewTime": 1782370805,
                  "pricePerUnit": 50,
                  "quantity": 500,
                  "worldName": "Siren",
                  "worldID": 57,
                  "hq": false,
                  "listingID": "listing-world",
                  "retainerID": "retainer-world",
                  "retainerName": "Worldretainer"
                }
              ]
            }
            """);
        using var httpClient = new HttpClient(handler);
        var source = new MarketMafioso.MarketAcquisition.UniversalisMarketAcquisitionPlanSource(httpClient);

        var listings = await source.FetchListingsForWorldAsync(
            "Siren",
            2,
            5,
            CancellationToken.None);

        var listing = Assert.Single(listings);
        Assert.Equal("https://universalis.app/api/v2/Siren/2?listings=5", handler.LastRequest?.RequestUri?.ToString());
        Assert.Equal("listing-world", listing.ListingId);
        Assert.Equal("Siren", listing.WorldName);
        Assert.Equal(57u, listing.WorldId);
    }

    [Fact]
    public async Task FetchListingsAsync_FailsWhenRequiredListingFieldsAreMissing()
    {
        using var handler = new CapturingHandler("""
            {
              "itemID": 2,
              "listings": [
                {
                  "listingID": "listing",
                  "quantity": 1,
                  "pricePerUnit": 1
                }
              ]
            }
            """);
        using var httpClient = new HttpClient(handler);
        var source = new MarketMafioso.MarketAcquisition.UniversalisMarketAcquisitionPlanSource(httpClient);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => source.FetchListingsAsync(
            "North-America",
            2,
            1,
            CancellationToken.None));
        Assert.Contains("worldName", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchListingsAsync_WhenHttpFails_IncludesStatusAndEndpoint()
    {
        using var handler = new CapturingHandler("gateway timeout", HttpStatusCode.GatewayTimeout);
        using var httpClient = new HttpClient(handler);
        var source = new MarketMafioso.MarketAcquisition.UniversalisMarketAcquisitionPlanSource(httpClient);

        var ex = await Assert.ThrowsAsync<MarketMafioso.MarketAcquisition.UniversalisMarketListingsHttpException>(() =>
            source.FetchListingsAsync(
                "North America",
                7017,
                100,
                CancellationToken.None));

        Assert.Equal(HttpStatusCode.GatewayTimeout, ex.StatusCode);
        Assert.Equal("https://universalis.app/api/v2/North-America/7017?listings=100", ex.RequestUri.ToString());
        Assert.Contains("504", ex.Message, StringComparison.Ordinal);
    }

    private sealed class CapturingHandler(
        string responseJson,
        HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseJson),
            });
        }
    }
}
