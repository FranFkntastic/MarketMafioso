using System.Net;
using System.Text.Json;
using MarketMafioso.Dashboard.Models;
using MarketMafioso.Dashboard.Services;
using MarketMafioso.Contracts.Inventory;
using DashboardBatchLineCreateRequest = MarketMafioso.MarketAcquisition.MarketAcquisitionBatchLineCreateRequest;
using DashboardBatchReplaceRequest = MarketMafioso.MarketAcquisition.MarketAcquisitionBatchReplaceRequest;

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

    [Fact]
    public async Task ReplaceAcquisitionBatchAsync_PutsReplacementPayload()
    {
        var handler = new CapturingResponseHandler(HttpStatusCode.OK, """
            {
              "id": "batch-1",
              "revision": 8,
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
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://dashboard.test/"),
        };
        var client = new DashboardApiClient(http);

        var replaced = await client.ReplaceAcquisitionBatchAsync(
            "batch-1",
            new DashboardBatchReplaceRequest
            {
                ExpectedRevision = 7,
                Region = "North America",
                WorldMode = "Recommended",
                SweepScope = "Region",
                ExpiresInSeconds = 300,
                Lines =
                [
                    new DashboardBatchLineCreateRequest
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
            });

        Assert.Equal("batch-1", replaced.Id);
        Assert.Equal(8, replaced.Revision);
        Assert.Equal(HttpMethod.Put, handler.LastRequest?.Method);
        Assert.Equal("https://dashboard.test/api/acquisition/batches/batch-1", handler.LastRequest?.RequestUri?.ToString());

        var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(7, body.RootElement.GetProperty("expectedRevision").GetInt32());
        Assert.Equal("North America", body.RootElement.GetProperty("region").GetString());
        Assert.Equal("Recommended", body.RootElement.GetProperty("worldMode").GetString());
        Assert.Single(body.RootElement.GetProperty("lines").EnumerateArray());
    }

    [Fact]
    public async Task GetInventoryBrowserAsync_SendsTheEditorCaret()
    {
        var handler = new CapturingResponseHandler(HttpStatusCode.OK, "{}");
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://dashboard.test/"),
        };
        var client = new DashboardApiClient(http);

        await client.GetInventoryBrowserAsync(
            null,
            "snapshot 1",
            "quantity darksteel",
            "all",
            InventoryBrowserMode.Items,
            caretPosition: 8);

        Assert.Equal(
            "https://dashboard.test/api/inventory/browser?snapshotId=snapshot 1&filter=quantity darksteel&scope=all&caret=8&mode=Items",
            handler.LastRequest?.RequestUri?.ToString());
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

    private sealed class CapturingResponseHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody),
            };
        }
    }
}
