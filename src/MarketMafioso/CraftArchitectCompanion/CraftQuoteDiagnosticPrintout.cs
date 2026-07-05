using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace MarketMafioso.CraftArchitectCompanion;

public static class CraftQuoteDiagnosticPrintout
{
    public static string Write(
        string directory,
        MarketAppraisalRequest request,
        CraftAppraisalQuote quote,
        DateTimeOffset writtenAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(quote);

        Directory.CreateDirectory(directory);
        var path = GetAvailablePath(directory, writtenAtUtc);
        File.WriteAllLines(path, BuildLines(request, quote, writtenAtUtc));
        return path;
    }

    private static IReadOnlyList<string> BuildLines(
        MarketAppraisalRequest request,
        CraftAppraisalQuote quote,
        DateTimeOffset writtenAtUtc)
    {
        var lines = new List<string>
        {
            "Craft Architect quote diagnostic",
            $"Written: {writtenAtUtc:O}",
            string.Empty,
            $"Request: {request.ItemName} ({request.ItemId}) x{request.Quantity}",
            $"Scope: region {request.Region}, HQ {request.HqPolicy}",
            $"Quote: {FormatGilDecimal(quote.EstimatedUnitCost)} / unit, total {FormatGilDecimal(quote.EstimatedTotalCost)}, source {quote.Source}, confidence {quote.Confidence}",
            $"Status: {quote.AppraisalStatus}, complete {quote.IsComplete}",
        };

        if (quote.QuotedAtUtc is { } quotedAt)
            lines.Add($"Quoted: {quotedAt:O}");

        lines.Add(string.Empty);
        lines.AddRange(CraftAppraisalPanelPresenter.BuildDiagnosticLines(quote));
        return lines;
    }

    private static string GetAvailablePath(string directory, DateTimeOffset writtenAtUtc)
    {
        var baseName = $"craft-quote-{writtenAtUtc:yyyyMMdd-HHmmss}";
        var path = Path.Combine(directory, $"{baseName}.log");
        if (!File.Exists(path))
            return path;

        for (var suffix = 1; suffix < 1000; suffix++)
        {
            path = Path.Combine(directory, $"{baseName}-{suffix.ToString(CultureInfo.InvariantCulture)}.log");
            if (!File.Exists(path))
                return path;
        }

        throw new IOException($"Unable to create a unique quote diagnostic file under {directory}.");
    }

    private static string FormatGilDecimal(decimal gil) => $"{gil:N0} gil";
}
