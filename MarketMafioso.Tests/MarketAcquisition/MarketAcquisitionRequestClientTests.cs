using System.Net;
using System.Text.Json;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRequestClientTests
{
    [Fact]
    public async Task FetchPendingAsync_DerivesHostedAcquisitionUrlAndAddsCommandKey()
    {
        using var handler = new CapturingHandler("""
            {
              "requests": [
                {
                  "id": "request-1",
                  "status": "PendingPickup",
                  "targetCharacterName": "Wei Ning",
                  "targetWorld": "Gilgamesh",
                  "region": "North America",
                  "itemId": 2,
                  "itemName": "Fire Shard",
                  "quantityMode": "Exact",
                  "quantity": 10,
                  "hqPolicy": "Either",
                  "maxUnitPrice": 99,
                  "maxTotalGil": 990,
                  "worldMode": "Recommended"
                }
              ]
            }
            """);
        using var httpClient = new HttpClient(handler);
        var client = new MarketMafioso.MarketAcquisition.MarketAcquisitionRequestClient(httpClient);

        var requests = await client.FetchPendingAsync(
            "https://dev.xivcraftarchitect.com/api/marketmafioso/inventory",
            "command-secret",
            "Wei Ning",
            "Gilgamesh",
            CancellationToken.None);

        Assert.Single(requests);
        Assert.Equal("request-1", requests[0].Id);
        Assert.Equal(
            "https://dev.xivcraftarchitect.com/api/marketmafioso/acquisition/requests/pending?characterName=Wei%20Ning&world=Gilgamesh",
            handler.LastRequest?.RequestUri?.OriginalString);
        Assert.NotNull(handler.LastRequest);
        Assert.True(handler.LastRequest.Headers.TryGetValues("X-Api-Key", out var values));
        Assert.Equal("command-secret", Assert.Single(values));
    }

    [Fact]
    public async Task ClaimAsync_PostsClaimPayloadAndReturnsClaimToken()
    {
        using var handler = new CapturingHandler("""
            {
              "id": "request-1",
              "status": "Claimed",
              "targetCharacterName": "Wei Ning",
              "targetWorld": "Gilgamesh",
              "region": "North America",
              "itemId": 2,
              "itemName": "Fire Shard",
              "quantityMode": "Exact",
              "quantity": 10,
              "hqPolicy": "Either",
              "maxUnitPrice": 99,
              "maxTotalGil": 990,
              "worldMode": "Recommended",
              "claimToken": "claim-token"
            }
            """);
        using var httpClient = new HttpClient(handler);
        var client = new MarketMafioso.MarketAcquisition.MarketAcquisitionRequestClient(httpClient);

        var claimed = await client.ClaimAsync(
            "https://dev.xivcraftarchitect.com/api/marketmafioso/inventory",
            "command-secret",
            "request-1",
            "Wei Ning",
            "Gilgamesh",
            "plugin-instance",
            CancellationToken.None);

        Assert.Equal("claim-token", claimed.ClaimToken);
        Assert.Equal(HttpMethod.Post, handler.LastRequest?.Method);
        Assert.Equal(
            "https://dev.xivcraftarchitect.com/api/marketmafioso/acquisition/requests/request-1/claim",
            handler.LastRequest?.RequestUri?.ToString());

        var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("Wei Ning", body.RootElement.GetProperty("characterName").GetString());
        Assert.Equal("Gilgamesh", body.RootElement.GetProperty("world").GetString());
        Assert.Equal("plugin-instance", body.RootElement.GetProperty("pluginInstanceId").GetString());
    }

    [Fact]
    public async Task AcceptAsync_PostsClaimTokenAndIdempotencyKey()
    {
        using var handler = new CapturingHandler("""
            {
              "id": "request-1",
              "status": "AcceptedInPlugin",
              "targetCharacterName": "Wei Ning",
              "targetWorld": "Gilgamesh",
              "region": "North America",
              "itemId": 2,
              "itemName": "Fire Shard",
              "quantityMode": "Exact",
              "quantity": 10,
              "hqPolicy": "Either",
              "maxUnitPrice": 99,
              "maxTotalGil": 990,
              "worldMode": "Recommended"
            }
            """);
        using var httpClient = new HttpClient(handler);
        var client = new MarketMafioso.MarketAcquisition.MarketAcquisitionRequestClient(httpClient);

        var accepted = await client.AcceptAsync(
            "https://dev.xivcraftarchitect.com/api/marketmafioso/inventory",
            "command-secret",
            "request-1",
            "claim-token",
            "accept-key",
            CancellationToken.None);

        Assert.Equal("AcceptedInPlugin", accepted.Status);
        Assert.Equal(
            "https://dev.xivcraftarchitect.com/api/marketmafioso/acquisition/requests/request-1/accept",
            handler.LastRequest?.RequestUri?.ToString());
        var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("claim-token", body.RootElement.GetProperty("claimToken").GetString());
        Assert.Equal("accept-key", body.RootElement.GetProperty("idempotencyKey").GetString());
    }

    private sealed class CapturingHandler(string responseJson) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson),
            };
        }
    }
}
