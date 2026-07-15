using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using MarketMafioso.Server.WorkshopHost;

namespace MarketMafioso.Server.Tests;

public sealed class WorkshopHostQuoteEndpointTests
{
    [Fact]
    public async Task Capabilities_ReportsInventoryAndCraftAppraiseByDefault()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();

        var response = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            "/marketmafioso/api/capabilities",
            "client-secret");

        response.EnsureSuccessStatusCode();
        var capabilities = await response.Content.ReadFromJsonAsync<WorkshopHostCapabilitiesResponse>();
        Assert.NotNull(capabilities);
        Assert.Equal("WorkshopHost", capabilities.Service);
        Assert.Contains(capabilities.Capabilities, capability => capability.Id == "inventory.write");
        Assert.Contains(capabilities.Capabilities, capability => capability.Id == "inventory.read");
        Assert.Contains(capabilities.Capabilities, capability => capability.Id == "diagnostics.read");
        var craft = Assert.Single(capabilities.Capabilities, capability => capability.Id == "craft.appraise");
        Assert.Equal("available", craft.Status);
        Assert.Contains(1, craft.SupportedSchemaVersions);
        Assert.Contains("craft:quote", craft.RequiredScopes);
    }

    [Fact]
    public async Task HostedMode_RejectsUnauthenticatedCapabilities()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();

        var response = await client.GetAsync("/marketmafioso/api/capabilities");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HostedMode_RejectsUnauthenticatedCraftAppraise()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();

        var response = await client.PostAsJsonAsync("/marketmafioso/api/craft/appraise", CreateRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HostedMode_AcceptsPreviousClientKeyForCapabilities()
    {
        await using var application = CreateHostedApplication(
            configureValues: values => values["MarketMafioso:PreviousClientApiKey"] = "previous-client-secret");
        using var client = application.CreateClient();

        var response = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            "/marketmafioso/api/capabilities",
            "previous-client-secret");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HostedMode_AcceptsPreviousClientKeyForCraftAppraise()
    {
        await using var application = CreateHostedApplication(
            services => services.AddSingleton<IWorkshopHostCraftQuoteService>(
                new StaticWorkshopHostCraftQuoteService(CreateQuote())),
            values => values["MarketMafioso:PreviousClientApiKey"] = "previous-client-secret");
        using var client = application.CreateClient();

        var response = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/marketmafioso/api/craft/appraise",
            "previous-client-secret",
            CreateRequest());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HostedMode_AcceptsCraftQuoteScopedKeyForCapabilitiesAndCraftAppraise()
    {
        await using var application = CreateHostedApplication(
            services => services.AddSingleton<IWorkshopHostCraftQuoteService>(
                new StaticWorkshopHostCraftQuoteService(CreateQuote())),
            values => values["MarketMafioso:CraftQuoteApiKey"] = "craft-quote-secret");
        using var client = application.CreateClient();

        var capabilities = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            "/marketmafioso/api/capabilities",
            "craft-quote-secret");
        var quote = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/marketmafioso/api/craft/appraise",
            "craft-quote-secret",
            CreateRequest());

        Assert.Equal(HttpStatusCode.OK, capabilities.StatusCode);
        var advertised = await capabilities.Content.ReadFromJsonAsync<WorkshopHostCapabilitiesResponse>();
        Assert.NotNull(advertised);
        Assert.Equal(["craft.appraise"], advertised.Capabilities.Select(capability => capability.Id));
        Assert.Equal(HttpStatusCode.OK, quote.StatusCode);
    }

    [Fact]
    public async Task HostedMode_RejectsCraftQuoteScopedKeyForInventoryRead()
    {
        await using var application = CreateHostedApplication(
            configureValues: values => values["MarketMafioso:CraftQuoteApiKey"] = "craft-quote-secret");
        using var client = application.CreateClient();

        var response = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            "/marketmafioso/api/reports",
            "craft-quote-secret");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HostedMode_RejectsInventoryReadScopedKeyForCraftAppraise()
    {
        await using var application = CreateHostedApplication(
            services => services.AddSingleton<IWorkshopHostCraftQuoteService>(
                new StaticWorkshopHostCraftQuoteService(CreateQuote())),
            values => values["MarketMafioso:InventoryReadApiKey"] = "inventory-read-secret");
        using var client = application.CreateClient();

        var response = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/marketmafioso/api/craft/appraise",
            "inventory-read-secret",
            CreateRequest());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CraftAppraise_ReturnsAdapterQuote()
    {
        var quotedAt = DateTimeOffset.Parse("2026-07-05T14:30:00+00:00");
        await using var application = CreateHostedApplication(services =>
            services.AddSingleton<IWorkshopHostCraftQuoteService>(
                new StaticWorkshopHostCraftQuoteService(CreateQuote(quotedAtUtc: quotedAt))));
        using var client = application.CreateClient();

        var response = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/marketmafioso/api/craft/appraise",
            "client-secret",
            CreateRequest());

        response.EnsureSuccessStatusCode();
        var quote = await response.Content.ReadFromJsonAsync<CraftAppraisalQuote>();
        Assert.NotNull(quote);
        Assert.Equal(2u, quote.ItemId);
        Assert.Equal(10u, quote.RequestedQuantity);
        Assert.Equal(80m, quote.EstimatedUnitCost);
        Assert.Equal(quotedAt, quote.QuotedAtUtc);
        Assert.Equal("WorkshopHostCraftArchitect", quote.Source);
    }

    [Theory]
    [InlineData(2, 2, 10, "unsupported_schema_version")]
    [InlineData(1, 0, 10, "item_id_required")]
    [InlineData(1, 2, 0, "quantity_required")]
    public async Task CraftAppraise_RejectsInvalidRequests(
        int schemaVersion,
        uint itemId,
        uint quantity,
        string expectedError)
    {
        await using var application = CreateHostedApplication(services =>
            services.AddSingleton<IWorkshopHostCraftQuoteService>(
                new StaticWorkshopHostCraftQuoteService(CreateQuote())));
        using var client = application.CreateClient();

        var response = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/marketmafioso/api/craft/appraise",
            "client-secret",
            CreateRequest(schemaVersion, itemId, quantity));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expectedError, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CraftAppraise_ReturnsNotFoundWhenAdapterCannotQuote()
    {
        await using var application = CreateHostedApplication(services =>
            services.AddSingleton<IWorkshopHostCraftQuoteService>(
                new StaticWorkshopHostCraftQuoteService(null)));
        using var client = application.CreateClient();

        var response = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/marketmafioso/api/craft/appraise",
            "client-secret",
            CreateRequest());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("craft_appraisal_not_found", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static WebApplicationFactory<Program> CreateHostedApplication(
        Action<IServiceCollection>? configureServices = null,
        Action<Dictionary<string, string?>>? configureValues = null)
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "MarketMafioso.Server.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        var values = new Dictionary<string, string?>
        {
            ["MarketMafioso:RequireApiKey"] = "true",
            ["MarketMafioso:ClientApiKey"] = "client-secret",
            ["MarketMafioso:BasePath"] = "/marketmafioso",
            ["MarketMafioso:EnableMarketAcquisition"] = "true",
            ["MarketMafioso:DatabasePath"] = Path.Combine(contentRoot, "marketmafioso.db"),
        };
        configureValues?.Invoke(values);

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(contentRoot);
                builder.ConfigureAppConfiguration(config =>
                {
                    config.AddInMemoryCollection(values);
                });
                if (configureServices != null)
                    builder.ConfigureServices(configureServices);
            });
    }

    private static Task<HttpResponseMessage> SendWithKeyAsync(
        HttpClient client,
        HttpMethod method,
        string requestUri,
        string apiKey,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Add("X-Api-Key", apiKey);
        request.Headers.Accept.ParseAdd("application/json");
        if (body != null)
            request.Content = JsonContent.Create(body);

        return client.SendAsync(request);
    }

    private static CraftAppraisalRequest CreateRequest(
        int schemaVersion = 1,
        uint itemId = 2,
        uint quantity = 10) => new()
    {
        SchemaVersion = schemaVersion,
        ItemId = itemId,
        ItemName = "Fire Shard",
        Quantity = quantity,
        Scope = new CraftAppraisalScope
        {
            Region = "North America",
        },
        Options = new CraftAppraisalOptions
        {
            HqPolicy = "Either",
            PricingMode = "CurrentMarketEvidence",
        },
    };

    private static CraftAppraisalQuote CreateQuote(
        DateTimeOffset? quotedAtUtc = null) => new()
    {
        ItemId = 2,
        ItemName = "Fire Shard",
        RequestedQuantity = 10,
        OutputQuantity = 1,
        EstimatedUnitCost = 80m,
        EstimatedTotalCost = 800m,
        Currency = "gil",
        QuotedAtUtc = quotedAtUtc ?? DateTimeOffset.Parse("2026-07-05T14:30:00+00:00"),
        Source = "WorkshopHostCraftArchitect",
        Confidence = "Medium",
        Warnings = ["Quote is advisory evidence."],
    };

    private sealed class StaticWorkshopHostCraftQuoteService(CraftAppraisalQuote? quote) : IWorkshopHostCraftQuoteService
    {
        public bool IsAvailable => true;

        public Task<CraftAppraisalQuote?> AppraiseAsync(
            CraftAppraisalRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(quote);
    }
}
