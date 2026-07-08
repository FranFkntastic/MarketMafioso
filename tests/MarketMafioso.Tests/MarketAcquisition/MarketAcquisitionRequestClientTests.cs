using System.Net;
using System.Text.Json;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRequestClientTests
{
    [Fact]
    public async Task FetchPendingAsync_DerivesHostedAcquisitionUrlAndAddsClientApiKey()
    {
        using var handler = new CapturingHandler("""
            {
              "batches": [
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
            "https://dev.xivcraftarchitect.com/marketmafioso/api/acquisition/batches/pending?characterName=Wei%20Ning&world=Gilgamesh",
            handler.LastRequest?.RequestUri?.OriginalString);
        Assert.NotNull(handler.LastRequest);
        Assert.True(handler.LastRequest.Headers.TryGetValues("X-Api-Key", out var values));
        Assert.Equal("client-secret", Assert.Single(values));
    }

    [Fact]
    public async Task CreateBatchAsync_PostsBatchPayloadAndReturnsServerView()
    {
        using var handler = new CapturingHandler("""
            {
              "id": "request-1",
              "status": "PendingPickup",
              "origin": "PluginBuilder",
              "createdByPluginInstanceId": "plugin-instance",
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
        var client = new MarketAcquisitionRequestClient(httpClient);

        var created = await client.CreateBatchAsync(
            "https://dev.xivcraftarchitect.com/marketmafioso/api/inventory",
            "client-secret",
            new MarketAcquisitionBatchCreateRequest
            {
                IdempotencyKey = "request-builder-key",
                Origin = MarketAcquisitionOrigins.PluginBuilder,
                CreatedByPluginInstanceId = "plugin-instance",
                TargetCharacterName = "Wei Ning",
                TargetWorld = "Gilgamesh",
                Region = "North America",
                WorldMode = "Recommended",
                SweepScope = "Region",
                ExpiresInSeconds = 300,
                Lines =
                [
                    new MarketAcquisitionBatchLineCreateRequest
                    {
                        ItemId = 2,
                        ItemName = "Fire Shard",
                        ItemKind = "Crystal",
                        QuantityMode = "TargetQuantity",
                        TargetQuantity = 10,
                        HqPolicy = "Either",
                        MaxUnitPrice = 99,
                        GilCap = 990,
                    },
                    new MarketAcquisitionBatchLineCreateRequest
                    {
                        ItemId = 4,
                        ItemName = "Lightning Shard",
                        ItemKind = "Crystal",
                        QuantityMode = "AllBelowThreshold",
                        MaxQuantity = 999,
                        HqPolicy = "Either",
                        MaxUnitPrice = 120,
                    },
                ],
            },
            CancellationToken.None);

        Assert.Equal("request-1", created.Id);
        Assert.Equal(MarketAcquisitionOrigins.PluginBuilder, created.Origin);
        Assert.Equal("plugin-instance", created.CreatedByPluginInstanceId);
        Assert.Equal(HttpMethod.Post, handler.LastRequest?.Method);
        Assert.Equal(
            "https://dev.xivcraftarchitect.com/marketmafioso/api/acquisition/batches",
            handler.LastRequest?.RequestUri?.ToString());
        Assert.NotNull(handler.LastRequest);
        Assert.True(handler.LastRequest.Headers.TryGetValues("X-Api-Key", out var values));
        Assert.Equal("client-secret", Assert.Single(values));

        var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("request-builder-key", body.RootElement.GetProperty("idempotencyKey").GetString());
        Assert.Equal("PluginBuilder", body.RootElement.GetProperty("origin").GetString());
        Assert.Equal("plugin-instance", body.RootElement.GetProperty("createdByPluginInstanceId").GetString());
        Assert.Equal("Wei Ning", body.RootElement.GetProperty("targetCharacterName").GetString());
        Assert.Equal("Gilgamesh", body.RootElement.GetProperty("targetWorld").GetString());
        Assert.Equal("Recommended", body.RootElement.GetProperty("worldMode").GetString());
        var lines = body.RootElement.GetProperty("lines");
        Assert.Equal(2, lines.GetArrayLength());
        Assert.Equal(2u, lines[0].GetProperty("itemId").GetUInt32());
        Assert.Equal("TargetQuantity", lines[0].GetProperty("quantityMode").GetString());
        Assert.Equal(10u, lines[0].GetProperty("targetQuantity").GetUInt32());
        Assert.Equal(4u, lines[1].GetProperty("itemId").GetUInt32());
        Assert.Equal("AllBelowThreshold", lines[1].GetProperty("quantityMode").GetString());
        Assert.Equal(999u, lines[1].GetProperty("maxQuantity").GetUInt32());
    }

    [Fact]
    public async Task GetBatchAsync_DerivesHostedAcquisitionUrlAndReadsRevision()
    {
        using var handler = new CapturingHandler("""
            {
              "id": "batch 1",
              "revision": 4,
              "status": "PendingPickup",
              "origin": "PluginBuilder",
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
        var client = new MarketAcquisitionRequestClient(httpClient);

        var batch = await client.GetBatchAsync(
            "https://dev.xivcraftarchitect.com/marketmafioso/api/inventory",
            "client-secret",
            "batch 1",
            CancellationToken.None);

        Assert.Equal("batch 1", batch.Id);
        Assert.Equal(4, batch.Revision);
        Assert.Equal(MarketAcquisitionOrigins.PluginBuilder, batch.Origin);
        Assert.Equal(HttpMethod.Get, handler.LastRequest?.Method);
        Assert.Equal(
            "https://dev.xivcraftarchitect.com/marketmafioso/api/acquisition/batches/batch%201",
            handler.LastRequest?.RequestUri?.OriginalString);
        Assert.NotNull(handler.LastRequest);
        Assert.True(handler.LastRequest.Headers.TryGetValues("X-Api-Key", out var values));
        Assert.Equal("client-secret", Assert.Single(values));
    }

    [Fact]
    public async Task ReplaceBatchAsync_PutsReplacementPayloadAndReturnsUpdatedRevision()
    {
        using var handler = new CapturingHandler("""
            {
              "id": "batch-1",
              "revision": 3,
              "status": "PendingPickup",
              "origin": "PluginBuilder",
              "targetCharacterName": "Wei Ning",
              "targetWorld": "Gilgamesh",
              "region": "North America",
              "itemId": 4,
              "itemName": "Lightning Shard",
              "quantityMode": "AllBelowThreshold",
              "quantity": 0,
              "hqPolicy": "Either",
              "maxUnitPrice": 120,
              "maxTotalGil": 0,
              "worldMode": "Recommended"
            }
            """);
        using var httpClient = new HttpClient(handler);
        var client = new MarketAcquisitionRequestClient(httpClient);

        var replaced = await client.ReplaceBatchAsync(
            "https://dev.xivcraftarchitect.com/marketmafioso/api/inventory",
            "client-secret",
            "batch-1",
            new MarketAcquisitionBatchReplaceRequest
            {
                ExpectedRevision = 2,
                Region = "North America",
                WorldMode = "Recommended",
                SweepScope = "Region",
                ExpiresInSeconds = 300,
                Lines =
                [
                    new MarketAcquisitionBatchLineCreateRequest
                    {
                        ItemId = 4,
                        ItemName = "Lightning Shard",
                        ItemKind = "Crystal",
                        QuantityMode = "AllBelowThreshold",
                        MaxQuantity = 999,
                        HqPolicy = "Either",
                        MaxUnitPrice = 120,
                    },
                ],
            },
            CancellationToken.None);

        Assert.Equal("batch-1", replaced.Id);
        Assert.Equal(3, replaced.Revision);
        Assert.Equal(HttpMethod.Put, handler.LastRequest?.Method);
        Assert.Equal(
            "https://dev.xivcraftarchitect.com/marketmafioso/api/acquisition/batches/batch-1",
            handler.LastRequest?.RequestUri?.ToString());
        Assert.NotNull(handler.LastRequest);
        Assert.True(handler.LastRequest.Headers.TryGetValues("X-Api-Key", out var values));
        Assert.Equal("client-secret", Assert.Single(values));

        var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(2, body.RootElement.GetProperty("expectedRevision").GetInt32());
        Assert.Equal("North America", body.RootElement.GetProperty("region").GetString());
        Assert.Equal("Recommended", body.RootElement.GetProperty("worldMode").GetString());
        var lines = body.RootElement.GetProperty("lines");
        Assert.Single(lines.EnumerateArray());
        Assert.Equal(4u, lines[0].GetProperty("itemId").GetUInt32());
        Assert.Equal("AllBelowThreshold", lines[0].GetProperty("quantityMode").GetString());
        Assert.Equal(999u, lines[0].GetProperty("maxQuantity").GetUInt32());
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

    [Fact]
    public async Task PostLineProgressAsync_UsesCanonicalBatchLineEndpoint()
    {
        using var handler = new CapturingHandler("""
            {
              "lineId": "line-1",
              "batchId": "batch-1",
              "status": "Running",
              "purchasedQuantity": 5,
              "spentGil": 2500,
              "latestMessage": "Line running."
            }
            """);
        using var httpClient = new HttpClient(handler);
        var client = new MarketMafioso.MarketAcquisition.MarketAcquisitionRequestClient(httpClient);

        var line = await client.PostLineProgressAsync(
            "https://dev.xivcraftarchitect.com/marketmafioso/api/inventory",
            "client-secret",
            "batch-1",
            "line-1",
            new MarketMafioso.MarketAcquisition.MarketAcquisitionLineProgressRequest
            {
                ClaimToken = "claim-token",
                IdempotencyKey = "line-progress-key",
                AttemptId = "attempt-1",
                Sequence = 1,
                Status = "Running",
                PurchasedQuantity = 5,
                SpentGil = 2500,
                Message = "Line running.",
            },
            CancellationToken.None);

        Assert.Equal("line-1", line.LineId);
        Assert.Equal(
            "https://dev.xivcraftarchitect.com/marketmafioso/api/acquisition/batches/batch-1/lines/line-1/progress",
            handler.LastRequest?.RequestUri?.ToString());
        Assert.NotNull(handler.LastRequest);
        Assert.True(handler.LastRequest.Headers.TryGetValues("X-Api-Key", out var values));
        Assert.Equal("client-secret", Assert.Single(values));

        var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("claim-token", body.RootElement.GetProperty("claimToken").GetString());
        Assert.Equal("line-progress-key", body.RootElement.GetProperty("idempotencyKey").GetString());
        Assert.Equal("attempt-1", body.RootElement.GetProperty("attemptId").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("sequence").GetInt64());
        Assert.Equal("Running", body.RootElement.GetProperty("status").GetString());
        Assert.Equal((uint)5, body.RootElement.GetProperty("purchasedQuantity").GetUInt32());
        Assert.Equal((uint)2500, body.RootElement.GetProperty("spentGil").GetUInt32());
    }

    [Fact]
    public async Task PostPurchaseAuditAsync_UsesCanonicalPurchaseEndpoint()
    {
        using var handler = new CapturingHandler("""
            {
              "auditId": "audit-1",
              "requestId": "batch-1",
              "lineId": "line-1",
              "attemptId": "attempt-1",
              "sequence": 2,
              "worldName": "Siren",
              "itemId": 5064,
              "itemName": "Silver Ingot",
              "listingId": "listing-1",
              "retainerName": "Seller",
              "retainerId": "retainer-1",
              "quantity": 10,
              "unitPrice": 50,
              "totalGil": 500,
              "isHq": false,
              "result": "Purchased"
            }
            """);
        using var httpClient = new HttpClient(handler);
        var client = new MarketMafioso.MarketAcquisition.MarketAcquisitionRequestClient(httpClient);

        var audit = await client.PostPurchaseAuditAsync(
            "https://dev.xivcraftarchitect.com/marketmafioso/api/inventory",
            "client-secret",
            "batch-1",
            new MarketMafioso.MarketAcquisition.MarketAcquisitionPurchaseAuditRequest
            {
                ClaimToken = "claim-token",
                IdempotencyKey = "purchase-audit-key",
                AttemptId = "attempt-1",
                Sequence = 2,
                LineId = "line-1",
                WorldName = "Siren",
                ItemId = 5064,
                ItemName = "Silver Ingot",
                ListingId = "listing-1",
                RetainerName = "Seller",
                RetainerId = "retainer-1",
                Quantity = 10,
                UnitPrice = 50,
                TotalGil = 500,
                IsHq = false,
                Result = "Purchased",
            },
            CancellationToken.None);

        Assert.Equal("audit-1", audit.AuditId);
        Assert.Equal(
            "https://dev.xivcraftarchitect.com/marketmafioso/api/acquisition/batches/batch-1/purchases",
            handler.LastRequest?.RequestUri?.ToString());
        Assert.NotNull(handler.LastRequest);
        Assert.True(handler.LastRequest.Headers.TryGetValues("X-Api-Key", out var values));
        Assert.Equal("client-secret", Assert.Single(values));

        var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("claim-token", body.RootElement.GetProperty("claimToken").GetString());
        Assert.Equal("purchase-audit-key", body.RootElement.GetProperty("idempotencyKey").GetString());
        Assert.Equal("line-1", body.RootElement.GetProperty("lineId").GetString());
        Assert.Equal("Purchased", body.RootElement.GetProperty("result").GetString());
        Assert.Equal((uint)500, body.RootElement.GetProperty("totalGil").GetUInt32());
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

