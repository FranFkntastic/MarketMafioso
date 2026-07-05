using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using CaWorkshop = FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using MarketMafioso.CraftArchitectCompanion;
using MarketMafioso.Server.WorkshopHost;

namespace MarketMafioso.Tests.CraftArchitectCompanion;

public sealed class WorkshopHostProviderEndToEndTests
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
    }

    private static WebApplicationFactory<Program> CreateHostedApplication(
        Action<IServiceCollection>? configureServices = null)
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "MarketMafioso.ProviderE2E.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        var values = new Dictionary<string, string?>
        {
            ["MarketMafioso:RequireApiKey"] = "true",
            ["MarketMafioso:ClientApiKey"] = "client-secret",
            ["MarketMafioso:BasePath"] = "/marketmafioso",
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
        CaWorkshop.CraftAppraisalQuote? quote) : IWorkshopHostCraftQuoteService
    {
        public bool IsAvailable => true;

        public Task<CaWorkshop.CraftAppraisalQuote?> AppraiseAsync(
            CaWorkshop.CraftAppraisalRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(quote);
    }
}
