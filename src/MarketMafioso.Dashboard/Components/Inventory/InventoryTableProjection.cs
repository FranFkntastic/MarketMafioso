using System.Globalization;
using MarketMafioso.Contracts.Inventory;

namespace MarketMafioso.Dashboard.Components.Inventory;

public sealed class InventoryTableQueryState
{
    private readonly Dictionary<string, string> filters = new(StringComparer.OrdinalIgnoreCase);

    public string? SortColumn { get; private set; }
    public bool SortDescending { get; private set; }

    public string Filter(string column) => filters.GetValueOrDefault(column, string.Empty);

    public void ToggleSort(string column)
    {
        if (string.Equals(SortColumn, column, StringComparison.OrdinalIgnoreCase))
        {
            SortDescending = !SortDescending;
            return;
        }

        SortColumn = column;
        SortDescending = false;
    }

    public void SetFilter(string column, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            filters.Remove(column);
        else
            filters[column] = value.Trim();
    }
}

public sealed record InventoryColumnFilterChange(string Column, string Value);

public static class InventoryTableProjection
{
    public static IReadOnlyList<InventoryBrowserItemView> Items(
        IReadOnlyList<InventoryBrowserItemView> source,
        InventoryTableQueryState query)
    {
        var filtered = source.Where(item =>
            Text(item.DisplayName, query.Filter("item")) &&
            Text(item.ItemType, query.Filter("category")) &&
            Number(item.TotalQuantity, query.Filter("owned")) &&
            Number(item.HqQuantity, query.Filter("hq")) &&
            Text(InventoryDisplayFormatter.FormatItemLocations(item), query.Filter("location")) &&
            Number(item.OwnerCount, query.Filter("owners")));

        return Sort(filtered, query, item => item.DisplayName, column => column switch
        {
            "category" => item => item.ItemType ?? string.Empty,
            "owned" => item => item.TotalQuantity,
            "hq" => item => item.HqQuantity,
            "location" => item => InventoryDisplayFormatter.FormatItemLocations(item),
            "owners" => item => item.OwnerCount,
            _ => item => item.DisplayName,
        });
    }

    public static IReadOnlyList<InventoryBrowserStackView> Stacks(
        IReadOnlyList<InventoryBrowserStackView> source,
        InventoryTableQueryState query)
    {
        var filtered = source.Where(stack =>
            Text(stack.DisplayName, query.Filter("item")) &&
            Text(stack.OwnerName, query.Filter("owner")) &&
            Text(InventoryDisplayFormatter.FormatStackStorage(stack), query.Filter("storage")) &&
            Number(stack.Quantity, query.Filter("quantity")) &&
            Text(stack.IsHq ? "HQ" : "NQ", query.Filter("quality")) &&
            Number(stack.ConditionPercent, query.Filter("condition")));

        return Sort(filtered, query, stack => stack.DisplayName, column => column switch
        {
            "owner" => stack => stack.OwnerName,
            "storage" => stack => InventoryDisplayFormatter.FormatStackStorage(stack),
            "quantity" => stack => stack.Quantity,
            "quality" => stack => stack.IsHq ? 1 : 0,
            "condition" => stack => stack.ConditionPercent ?? -1,
            _ => stack => stack.DisplayName,
        });
    }

    public static IReadOnlyList<InventoryBrowserMarketListingView> Listings(
        IReadOnlyList<InventoryBrowserMarketListingView> source,
        InventoryTableQueryState query)
    {
        var filtered = source.Where(listing =>
            Text(listing.DisplayName, query.Filter("item")) &&
            Text(listing.OwnerName, query.Filter("retainer")) &&
            Number(listing.Quantity, query.Filter("quantity")) &&
            Text(listing.HqQuantity > 0 ? "HQ" : "NQ", query.Filter("quality")) &&
            Number(listing.UnitPrice, query.Filter("unit-price")) &&
            Number(listing.TotalPrice, query.Filter("total")) &&
            Text(FormatAge(listing.EvidenceAgeSeconds), query.Filter("observed")));

        return Sort(filtered, query, listing => listing.DisplayName, column => column switch
        {
            "retainer" => listing => listing.OwnerName,
            "quantity" => listing => listing.Quantity,
            "quality" => listing => listing.HqQuantity > 0 ? 1 : 0,
            "unit-price" => listing => listing.UnitPrice ?? uint.MaxValue,
            "total" => listing => listing.TotalPrice ?? ulong.MaxValue,
            "observed" => listing => listing.EvidenceAgeSeconds ?? double.MaxValue,
            _ => listing => listing.DisplayName,
        });
    }

    private static IReadOnlyList<T> Sort<T>(
        IEnumerable<T> source,
        InventoryTableQueryState query,
        Func<T, string> tieBreaker,
        Func<string, Func<T, IComparable>> selectorFactory)
    {
        if (string.IsNullOrWhiteSpace(query.SortColumn))
            return source.ToArray();

        var selector = selectorFactory(query.SortColumn);
        var ordered = query.SortDescending
            ? source.OrderByDescending(selector)
            : source.OrderBy(selector);
        return ordered.ThenBy(tieBreaker, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool Text(string? value, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;
        var candidate = value ?? string.Empty;
        return filter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(token => candidate.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool Number(object? value, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;
        if (value is null)
            return false;

        var number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        var expression = filter.Trim();
        if (TryRange(expression, out var minimum, out var maximum))
            return number >= minimum && number <= maximum;

        foreach (var (prefix, comparison) in Comparisons)
        {
            if (!expression.StartsWith(prefix, StringComparison.Ordinal) ||
                !decimal.TryParse(expression[prefix.Length..], NumberStyles.Number, CultureInfo.InvariantCulture, out var operand))
                continue;
            return comparison(number, operand);
        }

        return decimal.TryParse(expression, NumberStyles.Number, CultureInfo.InvariantCulture, out var exact)
            ? number == exact
            : number.ToString(CultureInfo.InvariantCulture).Contains(expression, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryRange(string expression, out decimal minimum, out decimal maximum)
    {
        var parts = expression.Split("..", 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 2 &&
            decimal.TryParse(parts[0], NumberStyles.Number, CultureInfo.InvariantCulture, out minimum) &&
            decimal.TryParse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out maximum))
            return true;
        minimum = maximum = 0;
        return false;
    }

    private static readonly (string Prefix, Func<decimal, decimal, bool> Compare)[] Comparisons =
    [
        (">=", (value, operand) => value >= operand),
        ("<=", (value, operand) => value <= operand),
        (">", (value, operand) => value > operand),
        ("<", (value, operand) => value < operand),
        ("=", (value, operand) => value == operand),
    ];

    private static string FormatAge(double? seconds) => seconds switch
    {
        null => string.Empty,
        < 60 => "<1m",
        < 3600 => $"{seconds / 60:0}m",
        < 86400 => $"{seconds / 3600:0.#}h",
        _ => $"{seconds / 86400:0.#}d",
    };
}
