using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MarketMafioso.CraftArchitectCompanion;
using MarketMafioso.Server.WorkshopHost;
using ServerCraftAppraisalQuote = MarketMafioso.Server.WorkshopHost.CraftAppraisalQuote;
using ServerCraftAppraisalRequest = MarketMafioso.Server.WorkshopHost.CraftAppraisalRequest;

namespace MarketMafioso.ContractTests.CraftArchitectCompanion;

public sealed class WorkshopHostProviderClientServerContractTests
{
    [Fact]
    public async Task ProviderDiscoversCapabilityAndReadsQuoteFromWorkshopHostServer()
    {
        var quotedAt = DateTimeOffset.Parse("2026-07-05T14:30:00+00:00");
        await using var application = CreateHostedApplication(services =>
            services.AddSingleton<IWorkshopHostCraftQuoteService>(
                new StaticWorkshopHostCraftQuoteService(new ServerCraftAppraisalQuote
                {
                    ItemId = 2,
                    ItemName = "Fire Shard",
                    RequestedQuantity = 10,
                    OutputQuantity = 1,
                    EstimatedUnitCost = 80m,
                    EstimatedTotalCost = 800m,
                    Currency = "gil",
                    QuotedAtUtc = quotedAt,
                    Source = "CraftArchitectHosted",
                    Confidence = "Medium",
                    PlanId = "0123456789abcdef0123456789abcdef",
                    PlanUrl = "https://craft.example/?appraisalPlan=https%3A%2F%2Fcraft.example%2Fapi%2Fcraft%2Fplans%2F0123456789abcdef0123456789abcdef",
                    Warnings = ["Quote is advisory evidence."],
                })));
        using var client = application.CreateClient();
        var serverUrl = new Uri(client.BaseAddress!, "/marketmafioso/api/inventory").ToString();
        var capabilitiesClient = new WorkshopHostCapabilitiesClient(client);

        var supportsQuote = await capabilitiesClient.SupportsCraftAppraiseV1Async(
            serverUrl,
            "client-secret",
            CancellationToken.None);
        var provider = new WorkshopHostCraftQuoteProvider(
            client,
            () => true,
            () => supportsQuote,
            () => serverUrl,
            () => "client-secret");

        var quote = await provider.GetQuoteAsync(CreateRequest());

        Assert.True(supportsQuote);
        Assert.NotNull(quote);
        Assert.Equal(2u, quote.ItemId);
        Assert.Equal(10u, quote.RequestedQuantity);
        Assert.Equal(80m, quote.EstimatedUnitCost);
        Assert.Equal(quotedAt, quote.QuotedAtUtc);
        Assert.Equal("CraftArchitectHosted", quote.Source);
        Assert.Equal(
            "https://craft.example/?appraisalPlan=https%3A%2F%2Fcraft.example%2Fapi%2Fcraft%2Fplans%2F0123456789abcdef0123456789abcdef",
            quote.PlanUrl);
    }

    [Fact]
    public async Task GatewayPreservesCraftArchitectOwnedPlanLink()
    {
        const string planId = "0123456789abcdef0123456789abcdef";
        const string planUrl = "https://craft.example/?appraisalPlan=https%3A%2F%2Fcraft.example%2Fapi%2Fcraft%2Fplans%2F0123456789abcdef0123456789abcdef";
        await using var application = CreateHostedApplication(
            services => services.AddSingleton<IWorkshopHostCraftQuoteService>(
                new StaticWorkshopHostCraftQuoteService(new ServerCraftAppraisalQuote
                {
                    ItemId = 2,
                    ItemName = "Fire Shard",
                    RequestedQuantity = 10,
                    PlanId = planId,
                    PlanUrl = planUrl,
                })),
            basePath: string.Empty);
        using var client = application.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "client-secret");

        var response = await client.PostAsJsonAsync(
            "/api/craft/appraise",
            new ServerCraftAppraisalRequest
            {
                ItemId = 2,
                ItemName = "Fire Shard",
                Quantity = 10,
            });
        response.EnsureSuccessStatusCode();
        var quote = await response.Content.ReadFromJsonAsync<ServerCraftAppraisalQuote>();

        Assert.Equal(planUrl, quote?.PlanUrl);
    }

    [Fact]
    public async Task GatewayCallsConfiguredLoopbackCraftArchitectApi()
    {
        var handler = new RecordingHandler(new ServerCraftAppraisalQuote
        {
            ItemId = 2,
            ItemName = "Fire Shard",
            RequestedQuantity = 10,
            Source = "CraftArchitectHosted",
        });
        using var client = new HttpClient(handler);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketMafioso:CraftArchitectAppraiseUrl"] =
                    "http://127.0.0.1:5129/craft/appraise",
            })
            .Build();
        var gateway = new CraftArchitectWorkshopHostCraftQuoteService(client, configuration);

        var quote = await gateway.AppraiseAsync(
            new ServerCraftAppraisalRequest
            {
                ItemId = 2,
                ItemName = "Fire Shard",
                Quantity = 10,
            },
            CancellationToken.None);

        Assert.True(gateway.IsAvailable);
        Assert.Equal("http://127.0.0.1:5129/craft/appraise", handler.RequestUri?.ToString());
        Assert.Equal("CraftArchitectHosted", quote?.Source);
    }

    private static WebApplicationFactory<Program> CreateHostedApplication(
        Action<IServiceCollection>? configureServices = null,
        string basePath = "/marketmafioso")
    {
        var contentRoot = Path.Combine(
            Path.GetTempPath(),
            "MarketMafioso.ProviderClientServerContract.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        var values = new Dictionary<string, string?>
        {
            ["MarketMafioso:RequireApiKey"] = "true",
            ["MarketMafioso:ClientApiKey"] = "client-secret",
            ["MarketMafioso:BasePath"] = basePath,
            ["MarketMafioso:EnableMarketAcquisition"] = "true",
            ["MarketMafioso:DatabasePath"] = Path.Combine(contentRoot, "marketmafioso.db"),
        };

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

    private static MarketAppraisalRequest CreateRequest() => new()
    {
        ItemId = 2,
        ItemName = "Fire Shard",
        Quantity = 10,
        HqPolicy = "Either",
        BuyThresholdUnitPrice = 120,
        Region = "North America",
        WorldMode = "Recommended",
        SweepScope = "Region",
    };

    private sealed class StaticWorkshopHostCraftQuoteService(
        ServerCraftAppraisalQuote? quote) : IWorkshopHostCraftQuoteService
    {
        public bool IsAvailable => true;

        public Task<ServerCraftAppraisalQuote?> AppraiseAsync(
            ServerCraftAppraisalRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(quote);
    }

    private sealed class RecordingHandler(ServerCraftAppraisalQuote quote) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(
                        quote,
                        new System.Text.Json.JsonSerializerOptions(
                            System.Text.Json.JsonSerializerDefaults.Web)),
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }
}
