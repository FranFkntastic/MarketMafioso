using System;
using System.Collections.Generic;
using MarketMafioso.CraftArchitectCompanion;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

public enum MarketAcquisitionSelectedLineSurface
{
    CommandBar,
    Inspector,
}

public enum MarketAcquisitionSelectedLineActionKind
{
    RefreshQuote,
    UseQuote,
    OpenPlan,
    RemoveLine,
}

public sealed record MarketAcquisitionSelectedLineActionPresentation(
    MarketAcquisitionSelectedLineActionKind Kind,
    string Label,
    bool Enabled,
    bool IsPrimary = false,
    bool IsDestructive = false,
    string? Value = null);

public sealed record MarketAcquisitionSelectedLinePresentation(
    int LineIndex,
    uint ItemId,
    string ItemName,
    uint CurrentUnitPrice,
    uint? QuoteUnitPrice,
    string EvidenceSummary,
    string QuoteSource,
    string QuoteConfidence,
    string QuoteStatus,
    IReadOnlyList<string> Warnings,
    string? PlanId,
    string? PlanUrl,
    IReadOnlyList<MarketAcquisitionSelectedLineActionPresentation> Actions);

public static class MarketAcquisitionSelectedLinePresenter
{
    public static MarketAcquisitionSelectedLineSurface ResolveSurface(
        bool inspectorRequested,
        MarketAcquisitionSelectedLinePresentation? selection) =>
        inspectorRequested && selection is not null
            ? MarketAcquisitionSelectedLineSurface.Inspector
            : MarketAcquisitionSelectedLineSurface.CommandBar;

    public static MarketAcquisitionSelectedLinePresentation? Build(
        MarketAcquisitionRequestDocument document,
        int selectedLineIndex,
        CraftAppraisalRequestBuilderState appraisalState,
        bool canEdit,
        bool isAppraising,
        bool isExactAcquisitionLine,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(appraisalState);
        if (selectedLineIndex < 0 || selectedLineIndex >= document.Lines.Count)
            return null;

        var line = document.Lines[selectedLineIndex];
        var identity = CraftAppraisalRequestMapper.BuildLineIdentity(document, line);
        var quote = appraisalState.GetLineQuote(identity)?.Quote;
        var threshold = appraisalState.TryGetLineQuoteThreshold(identity);
        var warnings = quote?.Warnings ?? [];
        var evidenceSummary = FormatEvidenceSummary(line, quote, threshold, warnings.Count);
        var refreshLabel = threshold is > 0 ? "Refresh quote" : "Get quote";
        var canFetch = canEdit && !isAppraising && line.ItemId != 0;
        var canApply = canEdit &&
                       !isExactAcquisitionLine &&
                       threshold is > 0 &&
                       line.MaxUnitPrice != threshold.Value;
        var canOpenPlan = !string.IsNullOrWhiteSpace(quote?.PlanUrl);

        return new MarketAcquisitionSelectedLinePresentation(
            selectedLineIndex,
            line.ItemId,
            FormatItemName(line),
            line.MaxUnitPrice,
            threshold,
            evidenceSummary,
            quote?.Source ?? "Craft Architect",
            quote?.Confidence ?? "Unknown",
            quote is null
                ? appraisalState.CraftQuoteStatus
                : CraftQuoteDisplayFormatter.FormatQuoteSummary(quote, nowUtc),
            warnings,
            quote?.PlanId,
            quote?.PlanUrl,
            [
                new(
                    MarketAcquisitionSelectedLineActionKind.RefreshQuote,
                    refreshLabel,
                    canFetch,
                    Value: quote?.Source),
                new(
                    MarketAcquisitionSelectedLineActionKind.UseQuote,
                    "Use quote",
                    canApply,
                    IsPrimary: true,
                    Value: threshold?.ToString()),
                new(
                    MarketAcquisitionSelectedLineActionKind.OpenPlan,
                    "Open plan",
                    canOpenPlan,
                    Value: quote?.PlanId),
                new(
                    MarketAcquisitionSelectedLineActionKind.RemoveLine,
                    "Remove line",
                    canEdit,
                    IsDestructive: true,
                    Value: line.ItemId.ToString()),
            ]);
    }

    public static string FormatEvidenceSummary(
        MarketAcquisitionRequestLineDocument line,
        CraftAppraisalQuote? quote,
        uint? threshold,
        int warningCount)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (threshold is > 0)
        {
            var state = warningCount > 0
                ? $"{warningCount:N0} warning{(warningCount == 1 ? string.Empty : "s")}"
                : "ready";
            return $"CA · {threshold.Value:N0} gil · {state}";
        }

        if (line.MaxUnitPrice > 0)
            return "Manual";

        return quote is null ? "Missing" : "Quote incomplete";
    }

    private static string FormatItemName(MarketAcquisitionRequestLineDocument line) =>
        string.IsNullOrWhiteSpace(line.ItemName)
            ? $"Item {line.ItemId}"
            : line.ItemName;
}
