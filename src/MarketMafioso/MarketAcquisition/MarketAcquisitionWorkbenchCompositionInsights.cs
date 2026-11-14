using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketAcquisitionCompositionFieldChange(string Field, string Before, string After);

public sealed record MarketAcquisitionCompositionLineChange(
    uint ItemId,
    string ItemName,
    IReadOnlyList<MarketAcquisitionCompositionFieldChange> Changes);

public sealed record MarketAcquisitionCompositionDiff(
    IReadOnlyList<MarketAcquisitionCompositionFieldChange> SettingChanges,
    IReadOnlyList<MarketAcquisitionRequestLineDocument> Added,
    IReadOnlyList<MarketAcquisitionRequestLineDocument> Removed,
    IReadOnlyList<MarketAcquisitionCompositionLineChange> Changed)
{
    public int ChangedLineCount => Added.Count + Removed.Count + Changed.Count;

    public bool IsEmpty => ChangedLineCount == 0 && SettingChanges.Count == 0;
}

public sealed record MarketAcquisitionCompositionSpend(
    ulong StockUpCeiling,
    int StockUpLines,
    int BuyBelowLines,
    int UnpricedStockUpLines);

/// <summary>
/// Read-only comparisons and summaries shown by the Compositions window.
/// Uses the Workbench's own vocabulary so both surfaces describe a line the same way.
/// </summary>
public static class MarketAcquisitionWorkbenchCompositionInsights
{
    public const string StockUp = "TargetQuantity";
    public const string BuyBelow = "AllBelowThreshold";

    /// <summary>Compares a saved composition (before) with the Workbench document (after), by item.</summary>
    public static MarketAcquisitionCompositionDiff Compare(
        MarketAcquisitionWorkbenchComposition saved,
        MarketAcquisitionRequestDocument workbench)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(workbench);

        var settings = new List<MarketAcquisitionCompositionFieldChange>();
        var savedRegion = SafeRegion(saved.Region);
        var workbenchRegion = SafeRegion(workbench.Region);
        if (!savedRegion.Equals(workbenchRegion, StringComparison.OrdinalIgnoreCase))
            settings.Add(new("Region", savedRegion, workbenchRegion));
        var savedRoute = FormatRoute(saved.WorldMode, saved.SweepScope, saved.SweepDataCenters);
        var workbenchRoute = FormatRoute(workbench.WorldMode, workbench.SweepScope, workbench.SweepDataCenters);
        if (!savedRoute.Equals(workbenchRoute, StringComparison.Ordinal))
            settings.Add(new("Worlds", savedRoute, workbenchRoute));

        var savedById = FirstByItem(saved.Lines);
        var workbenchById = FirstByItem(workbench.Lines);
        var added = workbench.Lines.Where(line => !savedById.ContainsKey(line.ItemId)).ToList();
        var removed = saved.Lines.Where(line => !workbenchById.ContainsKey(line.ItemId)).ToList();
        var changed = new List<MarketAcquisitionCompositionLineChange>();
        foreach (var before in saved.Lines)
        {
            if (!workbenchById.TryGetValue(before.ItemId, out var after) || !ReferenceEquals(savedById[before.ItemId], before))
                continue;
            var fields = CompareLine(before, after);
            if (fields.Count > 0)
                changed.Add(new(after.ItemId, DisplayName(after), fields));
        }

        return new(settings, added, removed, changed);
    }

    public static MarketAcquisitionCompositionSpend Spend(IEnumerable<MarketAcquisitionRequestLineDocument> lines)
    {
        ulong total = 0;
        int stockUp = 0, buyBelow = 0, unpriced = 0;
        foreach (var line in lines)
        {
            if (line.QuantityMode == StockUp)
            {
                stockUp++;
                if (line.MaxUnitPrice == 0)
                    unpriced++;
                total += (ulong)line.TargetQuantity * line.MaxUnitPrice;
            }
            else
            {
                buyBelow++;
            }
        }

        return new(total, stockUp, buyBelow, unpriced);
    }

    /// <summary>"62.5M", "+3" or "open": the compact worst-case spend label for the list.</summary>
    public static string FormatSpendCell(MarketAcquisitionCompositionSpend spend)
    {
        if (spend.StockUpLines == 0)
            return spend.BuyBelowLines > 0 ? "open" : "-";
        var label = FormatCompactGil(spend.StockUpCeiling);
        return spend.BuyBelowLines > 0 ? $"{label} +{spend.BuyBelowLines}" : label;
    }

    public static string FormatCompactGil(ulong gil) =>
        gil switch
        {
            >= 1_000_000_000 => Compact(gil / 1_000_000_000d, "B"),
            >= 1_000_000 => Compact(gil / 1_000_000d, "M"),
            >= 10_000 => Compact(gil / 1_000d, "K"),
            _ => gil.ToString("N0", CultureInfo.CurrentCulture),
        };

    public static string FormatShortDate(DateTimeOffset utc, DateTimeOffset nowUtc)
    {
        var local = utc.ToLocalTime();
        return local.Year == nowUtc.ToLocalTime().Year
            ? local.ToString("MMM d", CultureInfo.CurrentCulture)
            : local.ToString("MMM d ''yy", CultureInfo.CurrentCulture);
    }

    public static string SafeRegion(string region)
    {
        try
        {
            return MarketAcquisitionWorldCatalog.NormalizeRegion(region);
        }
        catch (InvalidOperationException)
        {
            return string.IsNullOrWhiteSpace(region) ? "-" : region.Trim();
        }
    }

    public static string RegionCode(string region) =>
        SafeRegion(region) switch
        {
            "North America" => "NA",
            "Europe" => "EU",
            "Japan" => "JP",
            "Oceania" => "OCE",
            var other => other,
        };

    public static string? RegionForWorld(string world)
    {
        if (string.IsNullOrWhiteSpace(world))
            return null;
        foreach (var (region, dataCenters) in MarketAcquisitionWorldCatalog.Regions)
        {
            if (dataCenters.Values.Any(worlds => worlds.Contains(world.Trim(), StringComparer.OrdinalIgnoreCase)))
                return region;
        }

        return null;
    }

    public static string FormatRoute(string worldMode, string sweepScope, IReadOnlyCollection<string> sweepDataCenters)
    {
        if (!worldMode.Equals("AllWorldSweep", StringComparison.OrdinalIgnoreCase))
            return "Recommended worlds";
        return sweepScope switch
        {
            "CurrentDataCenter" => "All-world sweep · current data center",
            "DataCenters" when sweepDataCenters.Count > 0 => $"All-world sweep · {string.Join(", ", sweepDataCenters)}",
            "DataCenters" => "All-world sweep · selected data centers",
            _ => "All-world sweep",
        };
    }

    public static string FormatRule(string quantityMode) =>
        quantityMode switch
        {
            StockUp => "Stock up to",
            BuyBelow => "Buy below ceiling",
            _ => quantityMode,
        };

    public static string DisplayName(MarketAcquisitionRequestLineDocument line) =>
        string.IsNullOrWhiteSpace(line.ItemName) ? $"Item {line.ItemId}" : line.ItemName;

    private static List<MarketAcquisitionCompositionFieldChange> CompareLine(
        MarketAcquisitionRequestLineDocument before,
        MarketAcquisitionRequestLineDocument after)
    {
        var fields = new List<MarketAcquisitionCompositionFieldChange>();
        if (before.QuantityMode != after.QuantityMode)
            fields.Add(new("Buying rule", FormatRule(before.QuantityMode), FormatRule(after.QuantityMode)));
        var beforeTarget = before.QuantityMode == StockUp ? before.TargetQuantity : 0;
        var afterTarget = after.QuantityMode == StockUp ? after.TargetQuantity : 0;
        if (beforeTarget != afterTarget)
            fields.Add(new("Target on hand", FormatCount(beforeTarget), FormatCount(afterTarget)));
        var beforeExtra = before.QuantityMode == StockUp ? before.MaximumOverage : 0;
        var afterExtra = after.QuantityMode == StockUp ? after.MaximumOverage : 0;
        if (beforeExtra != afterExtra)
            fields.Add(new("Extra units allowed", FormatCount(beforeExtra), FormatCount(afterExtra)));
        if (before.MaxUnitPrice != after.MaxUnitPrice)
            fields.Add(new("Unit ceiling", FormatCount(before.MaxUnitPrice), FormatCount(after.MaxUnitPrice)));
        if (!string.Equals(before.HqPolicy, after.HqPolicy, StringComparison.OrdinalIgnoreCase))
            fields.Add(new("Quality", FormatQuality(before.HqPolicy), FormatQuality(after.HqPolicy)));
        return fields;
    }

    private static Dictionary<uint, MarketAcquisitionRequestLineDocument> FirstByItem(
        IEnumerable<MarketAcquisitionRequestLineDocument> lines)
    {
        var byItem = new Dictionary<uint, MarketAcquisitionRequestLineDocument>();
        foreach (var line in lines)
            byItem.TryAdd(line.ItemId, line);
        return byItem;
    }

    private static string FormatCount(uint value) => value == 0 ? "-" : value.ToString("N0", CultureInfo.CurrentCulture);

    private static string FormatQuality(string policy) =>
        policy switch
        {
            "HQOnly" => "HQ only",
            "NQOnly" => "NQ only",
            _ => "Any",
        };

    private static string Compact(double value, string suffix) =>
        (value >= 100 ? value.ToString("0", CultureInfo.CurrentCulture) : value.ToString("0.#", CultureInfo.CurrentCulture)) + suffix;
}
