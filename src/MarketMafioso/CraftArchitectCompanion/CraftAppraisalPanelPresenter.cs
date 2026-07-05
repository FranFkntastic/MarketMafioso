using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.CraftArchitectCompanion;

public static class CraftAppraisalPanelPresenter
{
    public static CraftAppraisalPanelViewState Build(CraftAppraisalPanelState state)
    {
        var workshopHostStatus = state.WorkshopHostEnabled
            ? state.WorkshopHostQuoteAvailable
                ? "Workshop Host: Connected"
                : "Workshop Host: Checking"
            : "Workshop Host: Off";

        var guidance = state.WorkshopHostEnabled
            ? state.WorkshopHostQuoteAvailable
                ? "Get Craft Quote applies a threshold and refreshes market depth."
                : "Refresh Workshop Host status before quoting from the backend."
            : "Enable Workshop Host quotes to appraise from the backend.";

        var quoteHeadline = "No craft quote yet.";
        var showFallbackSection = state.ManualFallbackEnabled || state.HasQuoteFilePath;
        var quoteDetail = showFallbackSection
            ? "Use Workshop Host for the normal path, or open manual / file fallback."
            : "Use Workshop Host for craft appraisal.";
        var canApplyQuoteToThreshold = false;
        IReadOnlyList<string> diagnosticLines = [];
        if (state.LatestQuote is { } quote)
        {
            quoteHeadline = $"Craft quote: {FormatGilDecimal(quote.EstimatedUnitCost)} / unit";
            quoteDetail = CraftQuoteDisplayFormatter.FormatQuoteSummary(quote, state.NowUtc ?? DateTimeOffset.UtcNow);
            canApplyQuoteToThreshold = quote.IsComplete && quote.EstimatedUnitCost > 0;
            diagnosticLines = BuildDiagnosticLines(quote);
        }

        return new CraftAppraisalPanelViewState
        {
            WorkshopHostStatus = workshopHostStatus,
            Guidance = guidance,
            PrimaryQuoteActionLabel = "Get Craft Quote",
            PrimaryQuoteActionEnabled = state.WorkshopHostEnabled && state.WorkshopHostQuoteAvailable,
            FallbackSectionLabel = "Manual / file fallback",
            ShowFallbackSection = showFallbackSection,
            ShowFallbackControlsByDefault = !state.WorkshopHostEnabled || !state.WorkshopHostQuoteAvailable,
            ShowManualFallbackControls = state.ManualFallbackEnabled,
            QuoteHeadline = quoteHeadline,
            QuoteDetail = quoteDetail,
            CanApplyQuoteToThreshold = canApplyQuoteToThreshold,
            DiagnosticLines = diagnosticLines,
        };
    }

    public static IReadOnlyList<string> BuildDiagnosticLines(CraftAppraisalQuote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);

        var lines = new List<string>();
        if (!quote.IsComplete &&
            (quote.EstimatedUnitCost <= 0 || quote.Warnings.Count > 0 || quote.Materials.Any(IsMissingPriceEvidence)))
        {
            lines.Add("Quote is incomplete because material price evidence is missing.");
        }

        if (!quote.IsComplete && !string.IsNullOrWhiteSpace(quote.AppraisalStatus))
        {
            lines.Add($"Quote status: {quote.AppraisalStatus}.");
        }

        lines.AddRange(quote.Warnings.Select(warning => $"Warning: {warning}"));
        lines.AddRange(quote.Materials.Select(FormatMaterialLine));
        return lines;
    }

    public static string BuildLogSummary(CraftAppraisalQuote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);

        var lines = new List<string>
        {
            $"Quote {quote.ItemName} ({quote.ItemId}) x{quote.RequestedQuantity}: {FormatGilDecimal(quote.EstimatedUnitCost)} / unit, total {FormatGilDecimal(quote.EstimatedTotalCost)}, source {quote.Source}, confidence {quote.Confidence}.",
        };
        lines.AddRange(BuildDiagnosticLines(quote));
        return string.Join(" | ", lines);
    }

    private static bool IsMissingPriceEvidence(CraftAppraisalMaterialQuote material) =>
        material.UnitCost <= 0 ||
        material.TotalCost <= 0 ||
        material.CostSource.Contains("missing", StringComparison.OrdinalIgnoreCase) ||
        material.Warnings.Count > 0;

    private static string FormatMaterialLine(CraftAppraisalMaterialQuote material)
    {
        var line =
            $"Material: {material.ItemName} x{FormatDecimal(material.TotalQuantity)}, " +
            $"{FormatGilDecimal(material.UnitCost)}/unit, total {FormatGilDecimal(material.TotalCost)}, source {FormatMaterialSource(material)}";
        return material.Warnings.Count == 0
            ? line
            : $"{line}; {string.Join("; ", material.Warnings)}";
    }

    private static string FormatMaterialSource(CraftAppraisalMaterialQuote material)
    {
        var source = string.IsNullOrWhiteSpace(material.AcquisitionSource) ||
            string.Equals(material.AcquisitionSource, "Unknown", StringComparison.OrdinalIgnoreCase)
                ? material.CostSource
                : $"{material.AcquisitionSource}/{material.CostSource}";
        return string.IsNullOrWhiteSpace(material.CostSourceDetails)
            ? source
            : $"{source}, {material.CostSourceDetails}";
    }

    private static string FormatDecimal(decimal value) =>
        value % 1 == 0
            ? value.ToString("N0")
            : value.ToString("N2");

    private static string FormatGilDecimal(decimal gil) => $"{gil:N0} gil";
}

public sealed record CraftAppraisalPanelState
{
    public bool WorkshopHostEnabled { get; init; }
    public bool WorkshopHostQuoteAvailable { get; init; }
    public bool ManualFallbackEnabled { get; init; }
    public bool HasQuoteFilePath { get; init; }
    public bool HasManualCraftCost { get; init; }
    public CraftAppraisalQuote? LatestQuote { get; init; }
    public DateTimeOffset? NowUtc { get; init; }
}

public sealed record CraftAppraisalPanelViewState
{
    public string WorkshopHostStatus { get; init; } = string.Empty;
    public string Guidance { get; init; } = string.Empty;
    public string PrimaryQuoteActionLabel { get; init; } = string.Empty;
    public bool PrimaryQuoteActionEnabled { get; init; }
    public string FallbackSectionLabel { get; init; } = string.Empty;
    public bool ShowFallbackSection { get; init; }
    public bool ShowFallbackControlsByDefault { get; init; }
    public bool ShowManualFallbackControls { get; init; }
    public string QuoteHeadline { get; init; } = string.Empty;
    public string QuoteDetail { get; init; } = string.Empty;
    public bool CanApplyQuoteToThreshold { get; init; }
    public IReadOnlyList<string> DiagnosticLines { get; init; } = [];
}
