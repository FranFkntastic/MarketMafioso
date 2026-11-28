using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionRouteTablePresenter
{
    public static IReadOnlyList<MarketAcquisitionRouteStopRow> BuildRows(
        IReadOnlyList<MarketAcquisitionGuidedRouteStop> stops)
    {
        ArgumentNullException.ThrowIfNull(stops);

        return stops.Select(BuildRow).ToArray();
    }

    internal static MarketAcquisitionRouteStopRow BuildRow(MarketAcquisitionGuidedRouteStop stop)
    {
        var lines = stop.LineStates
            .Select(BuildLineRow)
            .ToArray();
        var aggregate = BuildAggregate(stop);

        return new MarketAcquisitionRouteStopRow
        {
            WorldName = stop.WorldName,
            DataCenter = stop.DataCenter,
            Key = $"{stop.WorldName}|{stop.DataCenter}",
            RouteLines = FormatContribution(stop),
            LineMix = FormatLineMix(stop.LineStates),
            State = aggregate.State,
            Intent = FormatPlannedIntent(stop.LineStates),
            Result = FormatBoughtResult(stop.LineStates),
            Notes = FormatNotes(stop.LineStates, aggregate),
            Aggregate = aggregate,
            Lines = lines,
        };
    }

    private static MarketAcquisitionRouteLineRow BuildLineRow(MarketAcquisitionRouteLineState line)
    {
        var item = FormatItem(line.ItemName, line.ItemId);
        return new()
        {
            Item = item,
            ItemLabel = "  " + item,
            Source = line.Source == "Opportunistic" ? "Extra check" : string.IsNullOrWhiteSpace(line.Source) ? "Planned" : line.Source,
            State = line.Status == "Failed" ? "Failed" : line.Status == "Complete" ? line.LatestMessage ?? "Bought" : line.Outcome ?? FormatState(line.Status),
            Planned = line.PlannedQuantity == 0 && line.PlannedGil == 0
                ? "-"
                : FormatQuantityGil(line.PlannedQuantity, line.PlannedGil),
            Discovered = string.IsNullOrWhiteSpace(line.LiveCandidateStatus)
                ? "-"
                : FormatQuantityGil(line.LiveObservedQuantity, line.LiveObservedGil) +
                  (line.LiveObservedQuantity != line.WouldBuyQuantity ? $" · eligible {line.WouldBuyQuantity:N0}" : ""),
            Bought = line.PurchasedQuantity == 0 && line.SpentGil == 0
                ? "-"
                : FormatQuantityGil(line.PurchasedQuantity, line.SpentGil),
            Notes = FormatLineNotes(line),
        };
    }

    private static MarketAcquisitionRouteStopAggregate BuildAggregate(MarketAcquisitionGuidedRouteStop stop)
    {
        var completed = stop.LineStates.Count(IsComplete);
        var skipped = stop.LineStates.Count(IsSkipped);
        var failed = stop.LineStates.Count(IsFailed);
        var opportunistic = stop.LineStates.Count(line =>
            line.Source.Equals("Opportunistic", StringComparison.OrdinalIgnoreCase));
        var purchasedLines = stop.LineStates.Count(line => line.PurchasedQuantity > 0 || line.SpentGil > 0);
        var state = ResolveStopState(stop, completed, skipped, failed, purchasedLines);

        return new MarketAcquisitionRouteStopAggregate
        {
            State = state,
            CompletedLineCount = completed,
            SkippedLineCount = skipped,
            FailedLineCount = failed,
            OpportunisticLineCount = opportunistic,
            PurchasedLineCount = purchasedLines,
            TotalLineCount = stop.LineStates.Count,
        };
    }

    private static string ResolveStopState(
        MarketAcquisitionGuidedRouteStop stop,
        int completed,
        int skipped,
        int failed,
        int purchasedLines)
    {
        if (failed > 0 || stop.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase))
            return "Blocked";

        if (stop.Status.Equals("Purchasing", StringComparison.OrdinalIgnoreCase))
            return "Buying";

        if (stop.Status.Equals("TravelCommandSent", StringComparison.OrdinalIgnoreCase))
            return "Traveling";

        if (stop.LineStates.Count > 0 && completed + skipped == stop.LineStates.Count)
            return purchasedLines > 0 ? "Bought" : "Nothing bought";
        if (stop.Status == "Complete") return purchasedLines > 0 ? "Bought" : "Nothing bought";

        return string.IsNullOrWhiteSpace(stop.Status)
            ? "Pending"
            : FormatState(stop.Status);
    }

    private static string FormatContribution(MarketAcquisitionGuidedRouteStop stop)
    {
        var bought = stop.LineStates
            .Where(line => line.PurchasedQuantity > 0)
            .GroupBy(line => line.ItemId)
            .Select(group => new { Name = FormatItem(group.First().ItemName, group.Key), Gil = group.Sum(line => (long)line.SpentGil) })
            .OrderByDescending(item => item.Gil)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (bought.Length > 0)
        {
            var shown = string.Join(" · ", bought.Take(3).Select(item => $"{item.Name} {item.Gil:N0}"));
            return bought.Length <= 3 ? shown : $"{shown} +{bought.Length - 3:N0}";
        }
        return stop.LineStates
            .Where(line => line.Status != "Pending")
            .GroupBy(line => line.Status)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key switch
            {
                "SkippedNoLiveStock" => "No safe stock",
                "SkippedOverageLimit" => "Overage limit",
                "SkippedTargetAlreadyMet" => "Target already met",
                "SkippedIncompleteListingCoverage" => "Incomplete listing coverage",
                "Failed" => "Blocked",
                _ => group.First().LiveCandidateStatus ?? FormatState(group.Key),
            })
            .FirstOrDefault() ?? FormatState(stop.Status);
    }

    private static string FormatLineMix(IReadOnlyList<MarketAcquisitionRouteLineState> lines)
    {
        var planned = lines.Count(line => !line.Source.Equals("Opportunistic", StringComparison.OrdinalIgnoreCase));
        var opportunistic = lines.Count - planned;
        if (planned == 0 && opportunistic == 0)
            return "No route lines";
        if (opportunistic == 0)
            return FormatCount(planned, "planned line");
        if (planned == 0)
            return FormatCount(opportunistic, "extra check");

        return $"{planned:N0} planned / {opportunistic:N0} extra checks";
    }

    private static string FormatNotes(
        IReadOnlyList<MarketAcquisitionRouteLineState> lines,
        MarketAcquisitionRouteStopAggregate aggregate)
    {
        var notes = new List<string>();
        if (aggregate.SkippedLineCount > 0)
            notes.Add(FormatCount(aggregate.SkippedLineCount, "skipped line"));
        if (aggregate.FailedLineCount > 0)
            notes.Add(FormatCount(aggregate.FailedLineCount, "failed line"));
        if (aggregate.OpportunisticLineCount > 0)
            notes.Add(FormatCount(aggregate.OpportunisticLineCount, "extra check"));

        var noSafeStock = lines.Count(line =>
            line.Status.Contains("NoLiveStock", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(line.LiveCandidateStatus, "NoSafeListings", StringComparison.OrdinalIgnoreCase));
        if (noSafeStock > 0)
            notes.Add($"{noSafeStock:N0} no safe stock");

        return notes.Count == 0 ? "-" : string.Join("; ", notes);
    }

    private static string FormatPlannedIntent(IReadOnlyList<MarketAcquisitionRouteLineState> lines)
    {
        var plannedGil = 0ul;
        foreach (var line in lines)
        {
            plannedGil = checked(plannedGil + line.PlannedGil);
        }

        return $"{lines.Where(line => line.Source != "Opportunistic").Select(line => line.ItemId).Distinct().Count():N0} items / {plannedGil:N0} gil";
    }

    private static string FormatBoughtResult(IReadOnlyList<MarketAcquisitionRouteLineState> lines)
    {
        var spentGil = 0ul;
        foreach (var line in lines)
        {
            spentGil = checked(spentGil + line.SpentGil);
        }

        return $"{lines.Where(line => line.PurchasedQuantity > 0).Select(line => line.ItemId).Distinct().Count():N0} items / {spentGil:N0} gil";
    }

    private static string FormatLineNotes(MarketAcquisitionRouteLineState line)
    {
        if (line.Status != "Complete" && !string.IsNullOrWhiteSpace(line.Outcome)) return line.Outcome;
        if (line.Status == "Complete" && line.RemainingNeed > 0 && !string.IsNullOrWhiteSpace(line.Outcome))
            return $"{line.LatestMessage} · {line.Outcome}";
        if (!string.IsNullOrWhiteSpace(line.LatestMessage))
            return line.LatestMessage;
        if (!string.IsNullOrWhiteSpace(line.LiveCandidateStatus))
            return FormatState(line.LiveCandidateStatus);

        return "-";
    }

    private static string FormatItem(string? itemName, uint itemId)
    {
        var name = string.IsNullOrWhiteSpace(itemName)
            ? $"Item {itemId.ToString(CultureInfo.InvariantCulture)}"
            : itemName;
        return name;
    }

    private static string FormatQuantityGil(uint quantity, ulong gil) =>
        $"{quantity.ToString("N0", CultureInfo.InvariantCulture)} / {gil.ToString("N0", CultureInfo.InvariantCulture)} gil";

    private static string FormatCount(int count, string singular)
    {
        if (count == 1)
            return $"1 {singular}";

        return $"{count.ToString("N0", CultureInfo.InvariantCulture)} {singular}s";
    }

    private static string FormatState(string state)
    {
        if (string.IsNullOrWhiteSpace(state))
            return "Pending";

        return state;
    }

    private static bool IsComplete(MarketAcquisitionRouteLineState line) =>
        line.Status.Equals("Complete", StringComparison.OrdinalIgnoreCase);

    private static bool IsSkipped(MarketAcquisitionRouteLineState line) =>
        line.Status.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase);

    private static bool IsFailed(MarketAcquisitionRouteLineState line) =>
        line.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Per-window memo of <see cref="MarketAcquisitionRouteTablePresenter.BuildRows"/>. Route stops and line states are
/// mutated in place by the route engine and carry no revision, so each stop row is keyed on a field-by-field
/// signature of exactly what the presenter's formatters read. Checking that signature allocates nothing; a stop
/// row is rebuilt only when one of its inputs changes. Strings compare by reference: a reassigned string with equal
/// text only costs a rebuild, never staleness.
/// </summary>
public sealed class MarketAcquisitionRouteTableRowCache
{
    private StopEntry[] entries = [];
    private IReadOnlyList<MarketAcquisitionRouteStopRow> rows = [];

    public IReadOnlyList<MarketAcquisitionRouteStopRow> GetRows(IReadOnlyList<MarketAcquisitionGuidedRouteStop> stops)
    {
        ArgumentNullException.ThrowIfNull(stops);

        var changed = stops.Count != entries.Length;
        if (changed)
            Array.Resize(ref entries, stops.Count);

        for (var index = 0; index < stops.Count; index++)
        {
            var stop = stops[index];
            var entry = entries[index];
            if (entry is not null && entry.Matches(stop))
                continue;

            entries[index] = StopEntry.Capture(stop);
            changed = true;
        }

        if (changed)
        {
            var built = new MarketAcquisitionRouteStopRow[entries.Length];
            for (var index = 0; index < entries.Length; index++)
                built[index] = entries[index].Row;
            rows = built;
        }

        return rows;
    }

    /// <summary>Every stop member the presenter reads: identity, WorldName, DataCenter, Status and the line list.</summary>
    private sealed class StopEntry
    {
        private readonly MarketAcquisitionGuidedRouteStop stop;
        private readonly IReadOnlyList<MarketAcquisitionRouteLineState> lineList;
        private readonly string worldName;
        private readonly string dataCenter;
        private readonly string status;
        private readonly LineSignature[] lines;

        private StopEntry(MarketAcquisitionGuidedRouteStop stop)
        {
            this.stop = stop;
            lineList = stop.LineStates;
            worldName = stop.WorldName;
            dataCenter = stop.DataCenter;
            status = stop.Status;
            lines = new LineSignature[lineList.Count];
            for (var index = 0; index < lines.Length; index++)
                lines[index] = new LineSignature(lineList[index]);
            Row = MarketAcquisitionRouteTablePresenter.BuildRow(stop);
        }

        public MarketAcquisitionRouteStopRow Row { get; }

        public static StopEntry Capture(MarketAcquisitionGuidedRouteStop stop) => new(stop);

        public bool Matches(MarketAcquisitionGuidedRouteStop current)
        {
            if (!ReferenceEquals(stop, current) ||
                !ReferenceEquals(lineList, current.LineStates) ||
                !ReferenceEquals(worldName, current.WorldName) ||
                !ReferenceEquals(dataCenter, current.DataCenter) ||
                !ReferenceEquals(status, current.Status) ||
                lines.Length != lineList.Count)
                return false;

            for (var index = 0; index < lines.Length; index++)
            {
                if (!lines[index].Matches(lineList[index]))
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Every <see cref="MarketAcquisitionRouteLineState"/> member the presenter reads. When a formatter starts reading
    /// another member, add it here or the table can show stale purchase progress. Members the presenter does not read:
    /// LineId, LiveReadableListingCount, LiveReportedListingCount, WouldSpendGil.
    /// </summary>
    private readonly struct LineSignature(MarketAcquisitionRouteLineState line)
    {
        private readonly MarketAcquisitionRouteLineState line = line;
        private readonly string? itemName = line.ItemName;
        private readonly string source = line.Source;
        private readonly string status = line.Status;
        private readonly string? outcome = line.Outcome;
        private readonly string? latestMessage = line.LatestMessage;
        private readonly string? liveCandidateStatus = line.LiveCandidateStatus;
        private readonly uint itemId = line.ItemId;
        private readonly uint remainingNeed = line.RemainingNeed;
        private readonly uint plannedQuantity = line.PlannedQuantity;
        private readonly uint plannedGil = line.PlannedGil;
        private readonly uint purchasedQuantity = line.PurchasedQuantity;
        private readonly uint spentGil = line.SpentGil;
        private readonly uint liveObservedQuantity = line.LiveObservedQuantity;
        private readonly ulong liveObservedGil = line.LiveObservedGil;
        private readonly uint wouldBuyQuantity = line.WouldBuyQuantity;

        public bool Matches(MarketAcquisitionRouteLineState current) =>
            ReferenceEquals(line, current) &&
            ReferenceEquals(itemName, current.ItemName) &&
            ReferenceEquals(source, current.Source) &&
            ReferenceEquals(status, current.Status) &&
            ReferenceEquals(outcome, current.Outcome) &&
            ReferenceEquals(latestMessage, current.LatestMessage) &&
            ReferenceEquals(liveCandidateStatus, current.LiveCandidateStatus) &&
            itemId == current.ItemId &&
            remainingNeed == current.RemainingNeed &&
            plannedQuantity == current.PlannedQuantity &&
            plannedGil == current.PlannedGil &&
            purchasedQuantity == current.PurchasedQuantity &&
            spentGil == current.SpentGil &&
            liveObservedQuantity == current.LiveObservedQuantity &&
            liveObservedGil == current.LiveObservedGil &&
            wouldBuyQuantity == current.WouldBuyQuantity;
    }
}

public sealed record MarketAcquisitionRouteStopRow
{
    public string WorldName { get; init; } = string.Empty;
    public string DataCenter { get; init; } = string.Empty;
    /// <summary>Stable expand/collapse key for this stop.</summary>
    public string Key { get; init; } = string.Empty;
    public string RouteLines { get; init; } = string.Empty;
    public string LineMix { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string Intent { get; init; } = string.Empty;
    public string Result { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    public MarketAcquisitionRouteStopAggregate Aggregate { get; init; } = new();
    public IReadOnlyList<MarketAcquisitionRouteLineRow> Lines { get; init; } = [];
}

public sealed record MarketAcquisitionRouteLineRow
{
    public string Item { get; init; } = string.Empty;
    /// <summary>Item name indented for the nested detail table.</summary>
    public string ItemLabel { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string Planned { get; init; } = string.Empty;
    public string Discovered { get; init; } = string.Empty;
    public string Bought { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
}

public sealed record MarketAcquisitionRouteStopAggregate
{
    public string State { get; init; } = string.Empty;
    public int CompletedLineCount { get; init; }
    public int SkippedLineCount { get; init; }
    public int FailedLineCount { get; init; }
    public int OpportunisticLineCount { get; init; }
    public int PurchasedLineCount { get; init; }
    public int TotalLineCount { get; init; }
}
