using MarketMafioso.CraftArchitectCompanion;

namespace MarketMafioso.Tests.CraftArchitectCompanion;

public sealed class CraftQuoteDisplayFormatterTests
{
    [Fact]
    public void FormatQuoteSummary_IncludesFreshQuoteAge()
    {
        var quotedAt = DateTimeOffset.Parse("2026-07-05T14:25:00+00:00");
        var now = DateTimeOffset.Parse("2026-07-05T14:30:00+00:00");
        var quote = CreateQuote(quotedAt);

        var summary = CraftQuoteDisplayFormatter.FormatQuoteSummary(quote, now);

        Assert.Contains("WorkshopHostCraftArchitect", summary, StringComparison.Ordinal);
        Assert.Contains("Medium", summary, StringComparison.Ordinal);
        Assert.Contains("80 gil / unit", summary, StringComparison.Ordinal);
        Assert.Contains("5m old", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("stale", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatQuoteSummary_LabelsStaleQuote()
    {
        var quotedAt = DateTimeOffset.Parse("2026-07-05T12:00:00+00:00");
        var now = DateTimeOffset.Parse("2026-07-05T14:30:00+00:00");
        var quote = CreateQuote(quotedAt);

        var summary = CraftQuoteDisplayFormatter.FormatQuoteSummary(quote, now, TimeSpan.FromMinutes(30));

        Assert.Contains("2h 30m old", summary, StringComparison.Ordinal);
        Assert.Contains("stale", summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatQuoteSummary_HandlesQuotesWithoutTimestamp()
    {
        var quote = CreateQuote(null);

        var summary = CraftQuoteDisplayFormatter.FormatQuoteSummary(
            quote,
            DateTimeOffset.Parse("2026-07-05T14:30:00+00:00"));

        Assert.Contains("unknown age", summary, StringComparison.OrdinalIgnoreCase);
    }

    private static CraftAppraisalQuote CreateQuote(DateTimeOffset? quotedAtUtc) => new()
    {
        ItemId = 2,
        ItemName = "Fire Shard",
        RequestedQuantity = 10,
        EstimatedUnitCost = 80m,
        EstimatedTotalCost = 800m,
        Source = "WorkshopHostCraftArchitect",
        Confidence = "Medium",
        QuotedAtUtc = quotedAtUtc,
    };
}
