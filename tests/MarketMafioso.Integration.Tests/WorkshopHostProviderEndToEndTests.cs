using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using CaWorkshop = FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using MarketMafioso.CraftArchitectCompanion;
using MarketMafioso.Server.WorkshopHost;

namespace MarketMafioso.Integration.Tests.CraftArchitectCompanion;

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

    [Fact]
    public async Task FileQuoteAndWorkshopHostQuoteShareCraftArchitectContractShape()
    {
        var fixturePath = ResolveSharedFixture("craft-appraisal-quote.v1.sample.json");
        var caQuote = JsonSerializer.Deserialize<CaWorkshop.CraftAppraisalQuote>(
            await File.ReadAllTextAsync(fixturePath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(caQuote);
        await using var application = CreateHostedApplication(services =>
            services.AddSingleton<IWorkshopHostCraftQuoteService>(
                new StaticWorkshopHostCraftQuoteService(caQuote)));
        using var client = application.CreateClient();
        var serverUrl = new Uri(client.BaseAddress!, "/marketmafioso/api/inventory").ToString();
        var fileProvider = new CraftArchitectFileQuoteProvider(() => fixturePath);
        var hostProvider = new WorkshopHostCraftQuoteProvider(
            client,
            () => true,
            () => true,
            () => serverUrl,
            () => "client-secret");

        var fileQuote = await fileProvider.GetQuoteAsync(CreateRequest());
        var hostQuote = await hostProvider.GetQuoteAsync(CreateRequest());

        Assert.NotNull(fileQuote);
        Assert.NotNull(hostQuote);
        Assert.Equal(fileQuote.SchemaVersion, hostQuote.SchemaVersion);
        Assert.Equal(fileQuote.ItemId, hostQuote.ItemId);
        Assert.Equal(fileQuote.RequestedQuantity, hostQuote.RequestedQuantity);
        Assert.Equal(fileQuote.EstimatedUnitCost, hostQuote.EstimatedUnitCost);
        Assert.Equal(fileQuote.EstimatedTotalCost, hostQuote.EstimatedTotalCost);
        Assert.Equal(fileQuote.Confidence, hostQuote.Confidence);
        Assert.Equal(fileQuote.Warnings, hostQuote.Warnings);
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

    private static string ResolveSharedFixture(string fileName)
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "Fixtures", "WorkshopHost", fileName);
        return File.Exists(candidate)
            ? candidate
            : throw new FileNotFoundException($"Could not find packaged contract fixture '{fileName}'.", candidate);
    }

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
