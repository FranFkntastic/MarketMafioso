using System;

namespace MarketMafioso.CraftArchitectCompanion;

public static class CraftQuoteDisplayFormatter
{
    public static string FormatQuoteSummary(
        CraftAppraisalQuote quote,
        DateTimeOffset now,
        TimeSpan? staleAfter = null)
    {
        ArgumentNullException.ThrowIfNull(quote);

        var age = FormatAge(quote.QuotedAtUtc, now, staleAfter ?? TimeSpan.FromMinutes(30));
        return $"Quote source: {quote.Source} ({quote.Confidence}), {FormatGilDecimal(quote.EstimatedUnitCost)} / unit, {age}";
    }

    private static string FormatAge(
        DateTimeOffset? quotedAtUtc,
        DateTimeOffset now,
        TimeSpan staleAfter)
    {
        if (quotedAtUtc == null)
            return "unknown age";

        var age = now - quotedAtUtc.Value;
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;

        var formatted = age.TotalHours >= 1
            ? $"{(int)age.TotalHours}h {age.Minutes}m old"
            : $"{Math.Max(0, (int)Math.Round(age.TotalMinutes))}m old";
        return age > staleAfter
            ? $"{formatted}, stale"
            : formatted;
    }

    private static string FormatGilDecimal(decimal value) =>
        $"{value:N0} gil";
}
