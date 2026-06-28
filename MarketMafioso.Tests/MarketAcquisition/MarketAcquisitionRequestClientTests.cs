using System.Net;
using System.Text.Json;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRequestClientTests
{
    [Fact]
    public async Task FetchPendingAsync_DerivesHostedAcquisitionUrlAndAddsClientApiKey()
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
                  "quantityMode": "TargetQuantity",
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
            "https://dev.xivcraftarchitect.com/marketmafioso/api/inventory",
            "client-secret",
            "Wei Ning",
            "Gilgamesh",
            CancellationToken.None);

        Assert.Single(requests);
        Assert.Equal("request-1", requests[0].Id);
        Assert.Equal(
            "https://dev.xivcraftarchitect.com/marketmafioso/api/acquisition/requests/pending?characterName=Wei%20Ning&world=Gilgamesh",
            handler.LastRequest?.RequestUri?.OriginalString);
        Assert.NotNull(handler.LastRequest);
        Assert.True(handler.LastRequest.Headers.TryGetValues("X-Api-Key", out var values));
        Assert.Equal("client-secret", Assert.Single(values));
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
              "quantityMode": "TargetQuantity",
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
            "https://dev.xivcraftarchitect.com/marketmafioso/api/inventory",
            "client-secret",
            "request-1",
            "Wei Ning",
            "Gilgamesh",
            "plugin-instance",
            CancellationToken.None);

        Assert.Equal("claim-token", claimed.ClaimToken);
        Assert.Equal(HttpMethod.Post, handler.LastRequest?.Method);
        Assert.Equal(
            "https://dev.xivcraftarchitect.com/marketmafioso/api/acquisition/requests/request-1/claim",
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
              "quantityMode": "TargetQuantity",
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
            "https://dev.xivcraftarchitect.com/marketmafioso/api/inventory",
            "client-secret",
            "request-1",
            "claim-token",
            "accept-key",
            CancellationToken.None);

        Assert.Equal("AcceptedInPlugin", accepted.Status);
        Assert.Equal(
            "https://dev.xivcraftarchitect.com/marketmafioso/api/acquisition/requests/request-1/accept",
            handler.LastRequest?.RequestUri?.ToString());
        var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("claim-token", body.RootElement.GetProperty("claimToken").GetString());
        Assert.Equal("accept-key", body.RootElement.GetProperty("idempotencyKey").GetString());
    }

    [Fact]
    public async Task ResendAsync_PostsResendRequestAndReturnsPendingRequest()
    {
        using var handler = new CapturingHandler("""
            {
              "id": "request-1",
              "status": "PendingPickup",
              "targetCharacterName": "Wei Ning",
              "targetWorld": "Gilgamesh",
              "region": "North America",
              "itemId": 2,
              "itemName": "Fire Shard",
              "quantityMode": "TargetQuantity",
              "quantity": 10,
              "hqPolicy": "Either",
              "maxUnitPrice": 99,
              "maxTotalGil": 990,
              "worldMode": "Recommended"
            }
            """);
        using var httpClient = new HttpClient(handler);
        var client = new MarketMafioso.MarketAcquisition.MarketAcquisitionRequestClient(httpClient);

        var resent = await client.ResendAsync(
            "https://dev.xivcraftarchitect.com/marketmafioso/api/inventory",
            "client-secret",
            "request-1",
            CancellationToken.None);

        Assert.Equal("PendingPickup", resent.Status);
        Assert.Equal(HttpMethod.Post, handler.LastRequest?.Method);
        Assert.Equal(
            "https://dev.xivcraftarchitect.com/marketmafioso/api/acquisition/requests/request-1/resend",
            handler.LastRequest?.RequestUri?.ToString());
        Assert.NotNull(handler.LastRequest);
        Assert.True(handler.LastRequest.Headers.TryGetValues("X-Api-Key", out var values));
        Assert.Equal("client-secret", Assert.Single(values));
        Assert.Equal("{}", handler.LastBody);
    }

    [Fact]
    public async Task ReportProgressAsync_PreservesServerConflictReason()
    {
        using var handler = new CapturingHandler(
            """{"error":"Cannot move acquisition request from Complete to Running."}""",
            HttpStatusCode.Conflict);
        using var httpClient = new HttpClient(handler);
        var client = new MarketMafioso.MarketAcquisition.MarketAcquisitionRequestClient(httpClient);

        var ex = await Assert.ThrowsAsync<MarketMafioso.MarketAcquisition.MarketAcquisitionLifecycleHttpException>(() =>
            client.ReportProgressAsync(
                "https://dev.xivcraftarchitect.com/marketmafioso/api/inventory",
                "client-secret",
                "request-1",
                "claim-token",
                "progress-key",
                "Running",
                "Route running.",
                CancellationToken.None));

        Assert.Equal(HttpStatusCode.Conflict, ex.StatusCode);
        Assert.Equal("progress", ex.Action);
        Assert.Equal("Cannot move acquisition request from Complete to Running.", ex.Error);
        Assert.Contains("Complete to Running", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportAttemptProgressAsync_PostsAttemptEventPayload()
    {
        using var handler = new CapturingHandler("""
            {
              "result": "accepted",
              "request": {
                "id": "request-1",
                "status": "Running",
                "targetCharacterName": "Wei Ning",
                "targetWorld": "Gilgamesh",
                "region": "North America",
                "itemId": 2,
                "itemName": "Fire Shard",
                "quantityMode": "TargetQuantity",
                "quantity": 10,
                "hqPolicy": "Either",
                "maxUnitPrice": 99,
                "maxTotalGil": 990,
                "worldMode": "Recommended",
                "latestAttemptId": "attempt-1",
                "latestAttemptSequence": 7,
                "latestAttemptPhase": "SearchingItem",
                "latestAttemptWorld": "Brynhildr"
              }
            }
            """);
        using var httpClient = new HttpClient(handler);
        var client = new MarketMafioso.MarketAcquisition.MarketAcquisitionRequestClient(httpClient);

        var result = await client.ReportAttemptProgressAsync(
            "https://dev.xivcraftarchitect.com/marketmafioso/api/inventory",
            "client-secret",
            "request-1",
            "claim-token",
            "plugin-instance",
            "attempt-1",
            7,
            "route-stop-brynhildr",
            "Brynhildr",
            "SearchingItem",
            "Searching for Fire Shard.",
            "1.2.3",
            CancellationToken.None);

        Assert.Equal("accepted", result.Result);
        Assert.Equal("attempt-1", result.Request.LatestAttemptId);
        Assert.Equal(7, result.Request.LatestAttemptSequence);
        Assert.Equal(
            "https://dev.xivcraftarchitect.com/marketmafioso/api/acquisition/requests/request-1/progress",
            handler.LastRequest?.RequestUri?.ToString());

        var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("claim-token", body.RootElement.GetProperty("claimToken").GetString());
        Assert.Equal("plugin-instance", body.RootElement.GetProperty("pluginInstanceId").GetString());
        Assert.Equal("attempt-1", body.RootElement.GetProperty("attemptId").GetString());
        Assert.Equal(7, body.RootElement.GetProperty("eventSequence").GetInt64());
        Assert.Equal("progress", body.RootElement.GetProperty("eventType").GetString());
        Assert.Equal("SearchingItem", body.RootElement.GetProperty("phase").GetString());
        Assert.Equal("Brynhildr", body.RootElement.GetProperty("worldName").GetString());
        Assert.Equal("route-stop-brynhildr", body.RootElement.GetProperty("routeStopId").GetString());
        Assert.Equal("1.2.3", body.RootElement.GetProperty("pluginVersion").GetString());
        Assert.True(body.RootElement.TryGetProperty("clientTimestampUtc", out _));
    }

    private sealed class CapturingHandler(string responseJson, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
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

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseJson),
            };
        }
    }
}

