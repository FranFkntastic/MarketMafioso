using System.Net;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class UniversalisMarketFreshnessVerifierTests
{
    [Fact]
    public async Task VerifyAsyncConfirmsWhenLastUploadIsAfterObservation()
    {
        var observation = new DateTimeOffset(2026, 6, 28, 12, 0, 0, TimeSpan.Zero);
        using var handler = new CapturingHandler("""
            {"lastUploadTime":1782648060,"listings":[]}
            """);
        using var http = new HttpClient(handler);
        var verifier = new MarketMafioso.MarketAcquisition.UniversalisMarketFreshnessVerifier(
            http,
            new Uri("https://example.test/api/v2/"));

        var result = await verifier.VerifyAsync(
            "Siren",
            itemId: 5064,
            observedAtUtc: observation,
            purchasedListingIds: [],
            CancellationToken.None);

        Assert.Equal("Confirmed", result.Status);
        Assert.Equal("https://example.test/api/v2/Siren/5064?listings=100", handler.LastRequest?.RequestUri?.ToString());
    }

    [Fact]
    public async Task VerifyAsyncReturnsUnconfirmedWhenListingStillPresent()
    {
        var observation = new DateTimeOffset(2026, 6, 28, 12, 0, 0, TimeSpan.Zero);
        using var handler = new CapturingHandler("""
            {"lastUploadTime":1782640000,"listings":[{"listingID":"listing-1"}]}
            """);
        using var http = new HttpClient(handler);
        var verifier = new MarketMafioso.MarketAcquisition.UniversalisMarketFreshnessVerifier(
            http,
            new Uri("https://example.test/api/v2/"));

        var result = await verifier.VerifyAsync(
            "Siren",
            itemId: 5064,
            observedAtUtc: observation,
            purchasedListingIds: ["listing-1"],
            CancellationToken.None);

        Assert.Equal("Unconfirmed", result.Status);
    }

    private sealed class CapturingHandler(string responseJson) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson),
            });
        }
    }
}
