using System;
using System.Collections.Generic;
using System.Linq;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

internal static class MarketAcquisitionWorkbenchReviewedControlIds
{
    public static string SelectLine(
        IReadOnlyList<MarketAcquisitionRequestLineDocument> lines,
        int lineIndex)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lineIndex < 0 || lineIndex >= lines.Count)
            throw new ArgumentOutOfRangeException(nameof(lineIndex));

        var line = lines[lineIndex];
        if (lines.Count(value => value.ItemId == line.ItemId) == 1)
            return $"acquisition.workbench.line.{line.ItemId}.select";

        var qualityToken = QualityToken(line.HqPolicy);
        var matchingQualityCount = lines.Count(value =>
            value.ItemId == line.ItemId && QualityToken(value.HqPolicy) == qualityToken);
        if (matchingQualityCount == 1)
            return $"acquisition.workbench.line.{line.ItemId}.{qualityToken}.select";

        var occurrence = lines.Take(lineIndex).Count(value =>
            value.ItemId == line.ItemId && QualityToken(value.HqPolicy) == qualityToken) + 1;
        return $"acquisition.workbench.line.{line.ItemId}.{qualityToken}.{occurrence}.select";
    }

    private static string QualityToken(string? hqPolicy)
    {
        if (hqPolicy is "HQOnly" or "HqOnly")
            return "hq";
        if (hqPolicy is "NQOnly" or "NqOnly")
            return "nq";
        if (hqPolicy == "Either")
            return "either";

        var token = new string((hqPolicy ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        return string.IsNullOrEmpty(token) ? "unspecified" : token;
    }
}
