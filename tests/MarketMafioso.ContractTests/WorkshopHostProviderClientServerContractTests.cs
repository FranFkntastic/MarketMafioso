using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using CaWorkshop = FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using MarketMafioso.CraftArchitectCompanion;
using MarketMafioso.Server.WorkshopHost;

namespace MarketMafioso.ContractTests.CraftArchitectCompanion;

public sealed class WorkshopHostProviderClientServerContractTests
{
    [Fact]
    public async Task ProviderDiscoversCapabilityAndReadsQuoteFromWorkshopHostServer()
    {
        var quotedAt = DateTimeOffset.Parse("2026-07-05T14:30:00+00:00");
        await using var application = CreateHostedApplication(services =>
            services.AddSingleton<IWorkshopHostCraftQuoteService>(
                new StaticWorkshopHostCraftQuoteService(new CaWorkshop.CraftAppraisalQuote
                {
                    ItemId = 2,
                    ItemName = "Fire Shard",
                    RequestedQuantity = 10,
                    OutputQuantity = 1,
                    EstimatedUnitCost = 80m,
                    EstimatedTotalCost = 800m,
                    Currency = "gil",
                    QuotedAtUtc = quotedAt,
                    Source = "WorkshopHostCraftArchitect",
                    Confidence = "Medium",
                    PlanId = "0123456789abcdef0123456789abcdef",
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
        Assert.Equal("WorkshopHostCraftArchitect", quote.Source);
        Assert.Equal(
            "http://localhost/?appraisalPlan=%2Fmarketmafioso%2Fapi%2Fcraft%2Fplans%2F0123456789abcdef0123456789abcdef",
            quote.PlanUrl);
    }

    [Fact]
    public async Task SeparateCraftArchitectOrigin_ReceivesAbsolutePlanSnapshotTarget()
    {
        const string planId = "0123456789abcdef0123456789abcdef";
        await using var application = CreateHostedApplication(
            services => services.AddSingleton<IWorkshopHostCraftQuoteService>(
                new StaticWorkshopHostCraftQuoteService(new CaWorkshop.CraftAppraisalQuote
                {
                    ItemId = 2,
                    ItemName = "Fire Shard",
                    RequestedQuantity = 10,
                    PlanId = planId,
                })),
            basePath: string.Empty,
            craftArchitectAppOrigin: "https://craft.example");
        using var client = application.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "client-secret");

        var response = await client.PostAsJsonAsync(
            "/api/craft/appraise",
            new CaWorkshop.CraftAppraisalRequest
            {
                ItemId = 2,
                ItemName = "Fire Shard",
                Quantity = 10,
            });
        response.EnsureSuccessStatusCode();
        var quote = await response.Content.ReadFromJsonAsync<CaWorkshop.CraftAppraisalQuote>();

        Assert.Equal(
            $"https://craft.example/?appraisalPlan=http%3A%2F%2Flocalhost%2Fapi%2Fcraft%2Fplans%2F{planId}",
            quote?.PlanUrl);
    }

    private static WebApplicationFactory<Program> CreateHostedApplication(
        Action<IServiceCollection>? configureServices = null,
        string basePath = "/marketmafioso",
        string? craftArchitectAppOrigin = null)
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
            ["MarketMafioso:CraftArchitectAppOrigin"] = craftArchitectAppOrigin,
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
        CaWorkshop.CraftAppraisalQuote? quote) : IWorkshopHostCraftQuoteService
    {
        public bool IsAvailable => true;

        public Task<CaWorkshop.CraftAppraisalQuote?> AppraiseAsync(
            CaWorkshop.CraftAppraisalRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(quote);
    }
}
