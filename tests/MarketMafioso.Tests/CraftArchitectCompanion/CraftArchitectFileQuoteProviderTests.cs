using System.Text.Json;
using MarketMafioso.CraftArchitectCompanion;

namespace MarketMafioso.Tests.CraftArchitectCompanion;

public sealed class CraftArchitectFileQuoteProviderTests
{
    [Fact]
    public async Task GetQuoteAsync_ReturnsNullWhenUnconfigured()
    {
        var provider = new CraftArchitectFileQuoteProvider(() => null);

        var quote = await provider.GetQuoteAsync(CreateRequest());

        Assert.False(provider.IsConfigured);
        Assert.Null(quote);
    }

    [Fact]
    public async Task GetQuoteAsync_ReadsV1QuoteFixture()
    {
        var provider = new CraftArchitectFileQuoteProvider(() => ResolveSharedFixture("craft-appraisal-quote.v1.sample.json"));

        var quote = await provider.GetQuoteAsync(CreateRequest());

        Assert.NotNull(quote);
        Assert.Equal("CraftArchitectLocal", quote.Source);
        Assert.Equal(2u, quote.ItemId);
        Assert.Equal(10u, quote.RequestedQuantity);
        Assert.Equal(80m, quote.EstimatedUnitCost);
        Assert.Contains(quote.Warnings, warning => warning.Contains("advisory", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetQuoteAsync_ConfiguredMissingFileThrows()
    {
        var provider = new CraftArchitectFileQuoteProvider(() => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.json"));

        await Assert.ThrowsAsync<FileNotFoundException>(() => provider.GetQuoteAsync(CreateRequest()));
    }

    [Fact]
    public async Task GetQuoteAsync_InvalidSchemaThrows()
    {
        var path = WriteQuoteFile(new CraftAppraisalQuote
        {
            SchemaVersion = 2,
            ItemId = 2,
            ItemName = "Fire Shard",
            RequestedQuantity = 10,
        });
        var provider = new CraftArchitectFileQuoteProvider(() => path);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetQuoteAsync(CreateRequest()));

        Assert.Contains("schema version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetQuoteAsync_ItemMismatchThrows()
    {
        var path = WriteQuoteFile(new CraftAppraisalQuote
        {
            SchemaVersion = 1,
            ItemId = 999,
            ItemName = "Other Item",
            RequestedQuantity = 10,
        });
        var provider = new CraftArchitectFileQuoteProvider(() => path);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetQuoteAsync(CreateRequest()));

        Assert.Contains("item", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetQuoteAsync_QuantityMismatchThrows()
    {
        var path = WriteQuoteFile(new CraftAppraisalQuote
        {
            SchemaVersion = 1,
            ItemId = 2,
            ItemName = "Fire Shard",
            RequestedQuantity = 99,
        });
        var provider = new CraftArchitectFileQuoteProvider(() => path);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetQuoteAsync(CreateRequest()));

        Assert.Contains("quantity", ex.Message, StringComparison.OrdinalIgnoreCase);
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

    private static string WriteQuoteFile(CraftAppraisalQuote quote)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MarketMafiosoQuoteProviderTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "quote.json");
        var json = JsonSerializer.Serialize(quote, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        });
        File.WriteAllText(path, json);
        return path;
    }

    private static string ResolveSharedFixture(string fileName)
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "Fixtures", "WorkshopHost", fileName);
        return File.Exists(candidate)
            ? candidate
            : throw new FileNotFoundException($"Could not find packaged contract fixture '{fileName}'.", candidate);
    }
}
