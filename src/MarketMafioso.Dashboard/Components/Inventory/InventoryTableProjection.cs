using System.Globalization;
using Franthropy.Web.Tables;
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

public enum InventoryGroupedColumn
{
    Item,
    Owner,
    Quantity,
    Quality,
    Location,
    Condition,
}

public sealed record InventoryGroupedItemRow(
    uint ItemId,
    string DisplayName,
    string? ItemType,
    int TotalQuantity,
    int HqQuantity,
    int OwnerCount,
    InventoryBrowserStackView PrimaryStack,
    IReadOnlyList<InventoryBrowserStackView> Stacks)
{
    public decimal? LowestCondition => Stacks
        .Where(stack => stack.ConditionPercent is not null)
        .Select(stack => stack.ConditionPercent)
        .Min();
}

public static class InventoryTableProjection
{
    public static IReadOnlyList<InventoryGroupedItemRow> GroupedInventory(
        IReadOnlyList<InventoryBrowserStackView> source,
        InventoryTableQueryState query)
    {
        var filtered = source.Where(stack =>
            Text($"{stack.DisplayName} {stack.ItemType}", query.Filter("item")) &&
            Text(stack.OwnerName, query.Filter("owner")) &&
            Number(stack.Quantity, query.Filter("quantity")) &&
            Text(stack.IsHq ? "HQ" : "NQ", query.Filter("quality")) &&
            Text(InventoryDisplayFormatter.FormatStackStorage(stack), query.Filter("location")) &&
            Number(stack.ConditionPercent, query.Filter("condition")));

        var groups = filtered
            .GroupBy(stack => stack.ItemId)
            .Select(group =>
            {
                var stacks = group
                    .OrderBy(stack => stack.OwnerName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(stack => InventoryDisplayFormatter.FormatStackStorage(stack), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(stack => stack.SlotIndex ?? int.MaxValue)
                    .ToArray();
                var first = stacks[0];
                var primary = stacks
                    .OrderByDescending(stack => stack.Quantity)
                    .ThenBy(stack => stack.OwnerName, StringComparer.OrdinalIgnoreCase)
                    .First();
                return new InventoryGroupedItemRow(
                    group.Key,
                    first.DisplayName,
                    stacks.Select(stack => stack.ItemType).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                    checked((int)stacks.Sum(stack => (long)stack.Quantity)),
                    checked((int)stacks.Where(stack => stack.IsHq).Sum(stack => (long)stack.Quantity)),
                    stacks.Select(stack => stack.OwnerName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    primary,
                    stacks);
            })
            .ToArray();

        var sortState = Enum.TryParse<InventoryGroupedColumn>(query.SortColumn, ignoreCase: true, out var column)
            ? new WebTableSortState<InventoryGroupedColumn>(column, query.SortDescending)
            : WebTableSortState<InventoryGroupedColumn>.Unsorted;
        var rules = new[]
        {
            WebTableSortRule<InventoryGroupedItemRow, InventoryGroupedColumn>.Create(InventoryGroupedColumn.Item, row => row.DisplayName, StringComparer.OrdinalIgnoreCase),
            WebTableSortRule<InventoryGroupedItemRow, InventoryGroupedColumn>.Create(InventoryGroupedColumn.Owner, row => row.PrimaryStack.OwnerName, StringComparer.OrdinalIgnoreCase),
            WebTableSortRule<InventoryGroupedItemRow, InventoryGroupedColumn>.Create(InventoryGroupedColumn.Quantity, row => row.TotalQuantity),
            WebTableSortRule<InventoryGroupedItemRow, InventoryGroupedColumn>.Create(InventoryGroupedColumn.Quality, row => row.HqQuantity),
            WebTableSortRule<InventoryGroupedItemRow, InventoryGroupedColumn>.Create(InventoryGroupedColumn.Location, row => InventoryDisplayFormatter.FormatStackStorage(row.PrimaryStack), StringComparer.OrdinalIgnoreCase),
            WebTableSortRule<InventoryGroupedItemRow, InventoryGroupedColumn>.Create(InventoryGroupedColumn.Condition, row => row.LowestCondition ?? decimal.MaxValue),
        };
        return WebTableOrdering.Apply(
            groups,
            sortState,
            rules,
            rows => rows.OrderBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.ItemId));
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
