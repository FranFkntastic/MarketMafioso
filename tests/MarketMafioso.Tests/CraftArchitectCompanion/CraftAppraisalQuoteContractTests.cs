using System.Text.Json;
using MarketMafioso.CraftArchitectCompanion;

namespace MarketMafioso.Tests.CraftArchitectCompanion;

public sealed class CraftAppraisalQuoteContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    [Fact]
    public void Quote_DeserializesV1FixtureWithoutAuthorityCoupling()
    {
        var json = File.ReadAllText(ResolveFixture("craft-appraisal-quote.v1.sample.json"));

        var quote = JsonSerializer.Deserialize<CraftAppraisalQuote>(json, JsonOptions);

        Assert.NotNull(quote);
        Assert.Equal(1, quote.SchemaVersion);
        Assert.Equal(2u, quote.ItemId);
        Assert.Equal(10u, quote.RequestedQuantity);
        Assert.Equal("gil", quote.Currency);
        Assert.Contains(quote.Warnings, warning => warning.Contains("advisory", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(json, "maxUnitPrice", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(json, "buyThreshold", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExistingMarketAppraisal_KeepsCraftQuoteSeparateFromUserThreshold()
    {
        var request = new MarketAppraisalRequest
        {
            ItemId = 2,
            ItemName = "Fire Shard",
            Quantity = 10,
            BuyThresholdUnitPrice = 120,
        };
        var quote = new CraftAppraisalQuote
        {
            ItemId = 2,
            ItemName = "Fire Shard",
            RequestedQuantity = 10,
            EstimatedUnitCost = 80m,
            EstimatedTotalCost = 800m,
            Source = "CraftArchitectLocal",
            Confidence = "Medium",
            Warnings = ["Quote is advisory evidence."],
        };

        var result = new MarketAppraisalResult
        {
            Request = request,
            CraftQuote = quote,
        };

        Assert.Equal(120u, result.Request.BuyThresholdUnitPrice);
        Assert.Equal(80m, result.CraftQuote!.EstimatedUnitCost);
    }

    private static string ResolveFixture(string fileName)
    {
        var root = AppContext.BaseDirectory;
        for (var i = 0; i < 12; i++)
        {
            var candidate = Path.Combine(
                root,
                "FFXIV Craft Architect C# Edition",
                "docs",
                "superpowers",
                "fixtures",
                "workshop-host",
                fileName);
            if (File.Exists(candidate))
                return candidate;

            var parent = Directory.GetParent(root);
            if (parent is null)
                break;

            root = parent.FullName;
        }

        throw new FileNotFoundException($"Could not find shared fixture '{fileName}'.");
    }
}
