using System;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Windows.AcquisitionWorkbench;

public enum StockAvailabilityPanelSeverity
{
    Muted,
    Success,
    Warning,
    Error,
}

public enum StockAvailabilityPanelSource
{
    None,
    Cache,
    FreshFetch,
}

public sealed record StockAvailabilityPanelState
{
    public MarketAcquisitionQuickShopLineDraft? SelectedLine { get; init; }
    public StockAvailabilityResult? Result { get; init; }
    public StockAvailabilityPanelSource Source { get; init; }
    public DateTimeOffset? SnapshotFetchedAtUtc { get; init; }
    public DateTimeOffset NowUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool IsFetching { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record StockAvailabilityPanelView
{
    public string Headline { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string SourceLine { get; init; } = string.Empty;
    public StockAvailabilityPanelSeverity Severity { get; init; }
}

public sealed record StockAvailabilitySideSummaryView
{
    public string Title { get; init; } = string.Empty;
    public string Headline { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string Footer { get; init; } = string.Empty;
    public StockAvailabilityPanelSeverity Severity { get; init; }
}

public static class StockAvailabilityPanelPresenter
{
    public static StockAvailabilityPanelView Build(StockAvailabilityPanelState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.SelectedLine is null)
        {
            return new StockAvailabilityPanelView
            {
                Headline = "Select a line",
                Detail = "Choose a queued line to check stock availability.",
                SourceLine = "No stock snapshot loaded.",
                Severity = StockAvailabilityPanelSeverity.Muted,
            };
        }

        if (state.SelectedLine.MaxUnitPrice == 0)
        {
            return new StockAvailabilityPanelView
            {
                Headline = "Set a max unit price",
                Detail = "Stock availability needs a max unit price because only under-threshold listings count as route-available stock.",
                Severity = StockAvailabilityPanelSeverity.Muted,
            };
        }

        if (!string.IsNullOrWhiteSpace(state.ErrorMessage))
        {
            return new StockAvailabilityPanelView
            {
                Headline = "Stock check failed",
                Detail = state.ErrorMessage.Trim(),
                SourceLine = BuildSourceLine(state),
                Severity = StockAvailabilityPanelSeverity.Error,
            };
        }

        if (state.IsFetching)
        {
            return new StockAvailabilityPanelView
            {
                Headline = "Checking stock",
                Detail = "Fetching Universalis listings for the selected route scope.",
                SourceLine = BuildSourceLine(state),
                Severity = StockAvailabilityPanelSeverity.Muted,
            };
        }

        if (state.Result is null)
        {
            return new StockAvailabilityPanelView
            {
                Headline = "No stock check yet",
                Detail = "Use Check Stock to analyze cached listings, or Refresh Stock to fetch fresh Universalis data.",
                SourceLine = BuildSourceLine(state),
                Severity = StockAvailabilityPanelSeverity.Muted,
            };
        }

        var result = state.Result;
        return result.Status switch
        {
            StockAvailabilityStatus.Depth => BuildDepthView(state, result),
            StockAvailabilityStatus.Enough => new StockAvailabilityPanelView
            {
                Headline = $"{result.EligibleQuantity:N0} of {result.RequiredQuantity.GetValueOrDefault():N0} units available",
                Detail = $"{result.EligibleListingCount:N0} eligible listing(s) cover the requested quantity.",
                SourceLine = BuildSourceLine(state),
                Severity = StockAvailabilityPanelSeverity.Success,
            },
            StockAvailabilityStatus.Partial => new StockAvailabilityPanelView
            {
                Headline = $"{result.EligibleQuantity:N0} of {result.RequiredQuantity.GetValueOrDefault():N0} units available",
                Detail = $"{result.ShortfallQuantity.GetValueOrDefault():N0} short across {result.EligibleListingCount:N0} eligible listing(s).",
                SourceLine = BuildSourceLine(state),
                Severity = StockAvailabilityPanelSeverity.Warning,
            },
            StockAvailabilityStatus.None => new StockAvailabilityPanelView
            {
                Headline = "No eligible stock observed",
                Detail = $"No listings matched the selected item, HQ policy, route scope, and max unit price.",
                SourceLine = BuildSourceLine(state),
                Severity = StockAvailabilityPanelSeverity.Muted,
            },
            StockAvailabilityStatus.Invalid => new StockAvailabilityPanelView
            {
                Headline = "Stock check unavailable",
                Detail = result.Diagnostic,
                SourceLine = BuildSourceLine(state),
                Severity = StockAvailabilityPanelSeverity.Error,
            },
            _ => new StockAvailabilityPanelView
            {
                Headline = "Stock check unavailable",
                Detail = result.Diagnostic,
                SourceLine = BuildSourceLine(state),
                Severity = StockAvailabilityPanelSeverity.Muted,
            },
        };
    }

    public static StockAvailabilitySideSummaryView BuildSideSummary(StockAvailabilityPanelState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.SelectedLine is null)
        {
            return new StockAvailabilitySideSummaryView
            {
                Title = "Selected Line",
                Headline = "No line selected",
                Detail = "Select a queued line to view stock context here.",
                Footer = "No stock snapshot loaded.",
                Severity = StockAvailabilityPanelSeverity.Muted,
            };
        }

        var panel = Build(state);
        return new StockAvailabilitySideSummaryView
        {
            Title = string.IsNullOrWhiteSpace(state.SelectedLine.ItemName)
                ? $"Item {state.SelectedLine.ItemId:N0}"
                : state.SelectedLine.ItemName,
            Headline = panel.Headline,
            Detail = panel.Detail,
            Footer = panel.SourceLine,
            Severity = panel.Severity,
        };
    }

    private static StockAvailabilityPanelView BuildDepthView(
        StockAvailabilityPanelState state,
        StockAvailabilityResult result) =>
        new()
        {
            Headline = $"{result.EligibleQuantity:N0} under-threshold units observed",
            Detail = $"Depth only: uncapped buy-all-below-threshold lines do not have a required quantity. {result.EligibleListingCount:N0} eligible listing(s) observed.",
            SourceLine = BuildSourceLine(state),
            Severity = result.EligibleQuantity > 0
                ? StockAvailabilityPanelSeverity.Success
                : StockAvailabilityPanelSeverity.Muted,
        };

    private static string BuildSourceLine(StockAvailabilityPanelState state)
    {
        var source = state.Source switch
        {
            StockAvailabilityPanelSource.Cache => "cache",
            StockAvailabilityPanelSource.FreshFetch => "fresh Universalis fetch",
            _ => "no stock snapshot",
        };

        if (state.SnapshotFetchedAtUtc is not { } fetchedAt)
            return source;

        var age = state.NowUtc - fetchedAt;
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;

        return $"{source}, fetched {FormatAge(age)} ago";
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalHours >= 1)
            return $"{Math.Floor(age.TotalHours):N0}h";
        if (age.TotalMinutes >= 1)
            return $"{Math.Floor(age.TotalMinutes):N0}m";
        return $"{Math.Floor(age.TotalSeconds):N0}s";
    }
}
