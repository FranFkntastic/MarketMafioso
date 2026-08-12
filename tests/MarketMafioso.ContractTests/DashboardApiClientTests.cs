using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MarketMafioso.Dashboard.Services;

namespace MarketMafioso.ContractTests;

public sealed class DashboardApiClientTests
{
    [Fact]
    public async Task GetInventoryBrowserAsync_RetriesOneTransientServiceUnavailableResponse()
    {
        var handler = new SequenceHandler(
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero) },
            },
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"matchingRecordCount\":51}", Encoding.UTF8, "application/json"),
            });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://receiver.example/") };
        var client = new DashboardApiClient(http);

        var view = await client.GetInventoryBrowserAsync(
            characterId: null,
            snapshotId: null,
            filter: null,
            scope: "all",
            mode: MarketMafioso.Contracts.Inventory.InventoryBrowserMode.Listings);

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(51, view.MatchingRecordCount);
    }

    private sealed class SequenceHandler(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int requestCount;

        public int RequestCount => requestCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref requestCount) - 1;
            if (index >= responses.Length)
                throw new InvalidOperationException("No configured response remains.");

            return Task.FromResult(responses[index]());
        }
    }
}
