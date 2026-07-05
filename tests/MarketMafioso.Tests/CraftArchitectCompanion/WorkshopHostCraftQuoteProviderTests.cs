using System.Net;
using System.Text;
using System.Text.Json;
using MarketMafioso.CraftArchitectCompanion;

namespace MarketMafioso.Tests.CraftArchitectCompanion;

public sealed class WorkshopHostCraftQuoteProviderTests
{
    [Theory]
    [InlineData(
        "http://localhost:8080/inventory",
        "http://localhost:8080/api/capabilities",
        "http://localhost:8080/api/craft/appraise")]
    [InlineData(
        "https://dev.xivcraftarchitect.com/marketmafioso/api/inventory",
        "https://dev.xivcraftarchitect.com/marketmafioso/api/capabilities",
        "https://dev.xivcraftarchitect.com/marketmafioso/api/craft/appraise")]
    public void BuildWorkshopHostUrls_DerivesFromReceiverUrl(
        string serverUrl,
        string expectedCapabilitiesUrl,
        string expectedAppraiseUrl)
    {
        Assert.Equal(expectedCapabilitiesUrl, ReceiverEndpointClassifier.BuildWorkshopHostCapabilitiesUrl(serverUrl));
        Assert.Equal(expectedAppraiseUrl, ReceiverEndpointClassifier.BuildWorkshopHostCraftAppraiseUrl(serverUrl));
    }

    [Fact]
    public async Task CapabilitiesClient_ReturnsTrueWhenCraftAppraiseV1Available()
    {
        using var handler = new CapturingHandler("""
            {
              "service": "WorkshopHost",
              "schemaVersion": 1,
              "capabilities": [
                {
                  "id": "craft.appraise",
                  "status": "available",
                  "supportedSchemaVersions": [1],
                  "requiredScopes": ["craft:quote"]
                }
              ]
            }
            """);
        using var httpClient = new HttpClient(handler);
        var client = new WorkshopHostCapabilitiesClient(httpClient);

        var supportsQuote = await client.SupportsCraftAppraiseV1Async(
            "https://dev.xivcraftarchitect.com/marketmafioso/api/inventory",
            "client-secret",
            CancellationToken.None);

        Assert.True(supportsQuote);
        Assert.Equal(
            "https://dev.xivcraftarchitect.com/marketmafioso/api/capabilities",
            handler.LastRequest?.RequestUri?.ToString());
        Assert.NotNull(handler.LastRequest);
        Assert.Equal("client-secret", Assert.Single(handler.LastRequest.Headers.GetValues("X-Api-Key")));
    }

    [Fact]
    public async Task CapabilitiesClient_ReturnsFalseWhenCapabilityMissing()
    {
        using var handler = new CapturingHandler("""{ "service": "WorkshopHost", "schemaVersion": 1, "capabilities": [] }""");
        using var httpClient = new HttpClient(handler);
        var client = new WorkshopHostCapabilitiesClient(httpClient);

        var supportsQuote = await client.SupportsCraftAppraiseV1Async(
            "http://localhost:8080/inventory",
            "client-secret",
            CancellationToken.None);

        Assert.False(supportsQuote);
    }

    [Fact]
    public async Task CapabilitiesClient_ReturnsFalseWhenReceiverUrlCannotDeriveApiBase()
    {
        using var handler = new CapturingHandler("""{ "capabilities": [] }""");
        using var httpClient = new HttpClient(handler);
        var client = new WorkshopHostCapabilitiesClient(httpClient);

        var supportsQuote = await client.SupportsCraftAppraiseV1Async(
            "http://localhost:8080",
            "client-secret",
            CancellationToken.None);

        Assert.False(supportsQuote);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task GetQuoteAsync_ReturnsNullWhenProviderDisabled()
    {
        var provider = CreateProvider(enabled: false);

        var quote = await provider.GetQuoteAsync(CreateRequest());

        Assert.False(provider.IsConfigured);
        Assert.Null(quote);
    }

    [Fact]
    public async Task GetQuoteAsync_ReturnsNullWhenCapabilityUnavailable()
    {
        var provider = CreateProvider(enabled: true, capabilityAvailable: false);

        var quote = await provider.GetQuoteAsync(CreateRequest());

        Assert.False(provider.IsConfigured);
        Assert.Null(quote);
    }

    [Fact]
    public async Task GetQuoteAsync_PostsRequestAndReturnsQuoteWhenEnabledAndCapable()
    {
        using var handler = new CapturingHandler(CreateQuoteJson(itemId: 2, requestedQuantity: 10, schemaVersion: 1));
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(enabled: true, capabilityAvailable: true, httpClient: httpClient);

        var quote = await provider.GetQuoteAsync(CreateRequest());

        Assert.NotNull(quote);
        Assert.Equal("WorkshopHostCraftArchitect", quote.Source);
        Assert.Equal(
            "https://dev.xivcraftarchitect.com/marketmafioso/api/craft/appraise",
            handler.LastRequest?.RequestUri?.ToString());
        Assert.Equal(HttpMethod.Post, handler.LastRequest?.Method);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal("client-secret", Assert.Single(handler.LastRequest.Headers.GetValues("X-Api-Key")));

        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(1, body.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(2u, body.RootElement.GetProperty("itemId").GetUInt32());
        Assert.Equal("Fire Shard", body.RootElement.GetProperty("itemName").GetString());
        Assert.Equal(10u, body.RootElement.GetProperty("quantity").GetUInt32());
        Assert.Equal("North America", body.RootElement.GetProperty("scope").GetProperty("region").GetString());
        Assert.Equal("Either", body.RootElement.GetProperty("options").GetProperty("hqPolicy").GetString());
        Assert.Equal("CurrentMarketEvidence", body.RootElement.GetProperty("options").GetProperty("pricingMode").GetString());
    }

    [Fact]
    public async Task GetQuoteAsync_HttpFailureThrowsVisibleProviderError()
    {
        using var handler = new CapturingHandler("""{ "error": "bad key" }""", HttpStatusCode.Unauthorized);
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(enabled: true, capabilityAvailable: true, httpClient: httpClient);

        var ex = await Assert.ThrowsAnyAsync<HttpRequestException>(() => provider.GetQuoteAsync(CreateRequest()));

        Assert.Contains("craft quote", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    [Theory]
    [InlineData(2, 10, 2, 99, "quantity")]
    [InlineData(2, 10, 999, 10, "item")]
    public async Task GetQuoteAsync_ResponseMismatchThrows(
        uint requestItemId,
        uint requestQuantity,
        uint quoteItemId,
        uint quoteQuantity,
        string expectedMessage)
    {
        using var handler = new CapturingHandler(CreateQuoteJson(quoteItemId, quoteQuantity, schemaVersion: 1));
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(enabled: true, capabilityAvailable: true, httpClient: httpClient);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetQuoteAsync(CreateRequest(requestItemId, requestQuantity)));

        Assert.Contains(expectedMessage, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetQuoteAsync_UnsupportedSchemaThrows()
    {
        using var handler = new CapturingHandler(CreateQuoteJson(itemId: 2, requestedQuantity: 10, schemaVersion: 2));
        using var httpClient = new HttpClient(handler);
        var provider = CreateProvider(enabled: true, capabilityAvailable: true, httpClient: httpClient);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetQuoteAsync(CreateRequest()));

        Assert.Contains("schema version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static WorkshopHostCraftQuoteProvider CreateProvider(
        bool enabled = true,
        bool capabilityAvailable = true,
        string serverUrl = "https://dev.xivcraftarchitect.com/marketmafioso/api/inventory",
        string apiKey = "client-secret",
        HttpClient? httpClient = null) =>
        new(
            httpClient ?? new HttpClient(new CapturingHandler("{}")),
            () => enabled,
            () => capabilityAvailable,
            () => serverUrl,
            () => apiKey);

    private static MarketAppraisalRequest CreateRequest(uint itemId = 2, uint quantity = 10) => new()
    {
        ItemId = itemId,
        ItemName = "Fire Shard",
        Quantity = quantity,
        HqPolicy = "Either",
        BuyThresholdUnitPrice = 120,
        Region = "North America",
        WorldMode = "Recommended",
        SweepScope = "Region",
    };

    private static string CreateQuoteJson(uint itemId, uint requestedQuantity, int schemaVersion) =>
        $$"""
          {
            "schemaVersion": {{schemaVersion}},
            "itemId": {{itemId}},
            "itemName": "Fire Shard",
            "requestedQuantity": {{requestedQuantity}},
            "outputQuantity": 1,
            "estimatedUnitCost": 80,
            "estimatedTotalCost": 800,
            "currency": "gil",
            "quotedAtUtc": "2026-07-05T14:30:00+00:00",
            "source": "WorkshopHostCraftArchitect",
            "confidence": "Medium",
            "materials": [],
            "warnings": ["Quote is advisory evidence."]
          }
          """;

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string responseBody;
        private readonly HttpStatusCode statusCode;

        public CapturingHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            this.responseBody = responseBody;
            this.statusCode = statusCode;
        }

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
