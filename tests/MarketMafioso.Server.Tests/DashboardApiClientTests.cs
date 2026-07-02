using System.Net;
using MarketMafioso.Dashboard.Services;

namespace MarketMafioso.Server.Tests;

public sealed class DashboardApiClientTests
{
    [Fact]
    public async Task GetCharactersAsync_ThrowsDashboardUnauthorizedExceptionForUnauthorizedResponse()
    {
        using var http = new HttpClient(new StaticResponseHandler(HttpStatusCode.Unauthorized, """{"error":"dashboard_session_required"}"""))
        {
            BaseAddress = new Uri("https://dashboard.test/"),
        };
        var client = new DashboardApiClient(http);

        await Assert.ThrowsAsync<DashboardUnauthorizedException>(() => client.GetCharactersAsync());
    }

    private sealed class StaticResponseHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody),
            });
        }
    }
}
