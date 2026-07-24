using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MarketMafioso.MarketDiagnostics;

internal sealed record ParsedRetainerHistorySale(
    uint ItemId,
    string ItemName,
    uint Quantity,
    bool IsHq,
    uint? UnitPrice,
    ulong TotalGil,
    DateTimeOffset SoldAtUtc,
    string? BuyerName);

internal static class RetainerHistoryParser
{
    private const int NumberFieldCount = 7;
    private const int StringFieldCount = 5;
    private static readonly DateTimeOffset EarliestPlausibleSale =
        new(2013, 8, 24, 0, 0, 0, TimeSpan.Zero);

    public static IReadOnlyList<ParsedRetainerHistorySale> Parse(
        IReadOnlyList<int> numbers,
        IReadOnlyList<string> strings,
        Func<uint, string?> itemNameResolver,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(numbers);
        ArgumentNullException.ThrowIfNull(strings);
        ArgumentNullException.ThrowIfNull(itemNameResolver);

        var numericRuns = FindNumericRuns(numbers, itemNameResolver, observedAtUtc);
        foreach (var run in numericRuns.OrderByDescending(candidate => candidate.Count))
        {
            var parsed = MatchStrings(run, strings);
            if (parsed.Count > 0)
                return parsed;
        }

        return [];
    }

    private static IReadOnlyList<IReadOnlyList<NumericSale>> FindNumericRuns(
        IReadOnlyList<int> numbers,
        Func<uint, string?> itemNameResolver,
        DateTimeOffset observedAtUtc)
    {
        var runs = new List<IReadOnlyList<NumericSale>>();
        for (var offset = 0; offset + NumberFieldCount <= numbers.Count; offset++)
        {
            var current = new List<NumericSale>();
            for (var index = offset; index + NumberFieldCount <= numbers.Count; index += NumberFieldCount)
            {
                if (!TryReadNumericSale(
                        numbers,
                        index,
                        itemNameResolver,
                        observedAtUtc,
                        out var sale))
                {
                    break;
                }

                current.Add(sale);
            }

            if (current.Count > 0)
                runs.Add(current);
        }

        return runs;
    }

    private static bool TryReadNumericSale(
        IReadOnlyList<int> numbers,
        int index,
        Func<uint, string?> itemNameResolver,
        DateTimeOffset observedAtUtc,
        out NumericSale sale)
    {
        sale = default;
        var price = numbers[index];
        var hq = numbers[index + 3];
        var timestamp = numbers[index + 4];
        var rawItemId = numbers[index + 5];
        if (price <= 0 ||
            hq is < 0 or > 1 ||
            timestamp <= 0 ||
            rawItemId <= 0)
        {
            return false;
        }

        DateTimeOffset soldAt;
        try
        {
            soldAt = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        if (soldAt < EarliestPlausibleSale || soldAt > observedAtUtc.AddDays(1))
            return false;

        var itemId = checked((uint)rawItemId);
        var itemName = itemNameResolver(itemId);
        if (string.IsNullOrWhiteSpace(itemName))
            return false;

        sale = new NumericSale(
            itemId,
            itemName.Trim(),
            checked((ulong)price),
            hq != 0,
            soldAt);
        return true;
    }

    private static IReadOnlyList<ParsedRetainerHistorySale> MatchStrings(
        IReadOnlyList<NumericSale> numericSales,
        IReadOnlyList<string> strings)
    {
        var parsed = new List<ParsedRetainerHistorySale>();
        var consumedStringOffsets = new HashSet<int>();
        foreach (var numeric in numericSales)
        {
            var match = FindStringGroup(numeric, strings, consumedStringOffsets);
            if (match == null)
                continue;

            consumedStringOffsets.Add(match.Value.Offset);
            var quantity = match.Value.Quantity;
            parsed.Add(new ParsedRetainerHistorySale(
                numeric.ItemId,
                numeric.ItemName,
                quantity,
                numeric.IsHq,
                numeric.TotalGil % quantity == 0
                    ? checked((uint?)(numeric.TotalGil / quantity))
                    : null,
                numeric.TotalGil,
                numeric.SoldAtUtc,
                NullIfWhiteSpace(match.Value.Buyer)));
        }

        return parsed;
    }

    private static StringGroup? FindStringGroup(
        NumericSale numeric,
        IReadOnlyList<string> strings,
        IReadOnlySet<int> consumedOffsets)
    {
        for (var offset = 0; offset + StringFieldCount <= strings.Count; offset++)
        {
            if (consumedOffsets.Contains(offset) ||
                !ContainsItemName(strings[offset + 4], numeric.ItemName) ||
                !TryReadQuantity(strings[offset + 1], out var quantity))
            {
                continue;
            }

            return new StringGroup(offset, quantity, strings[offset + 2]);
        }

        return null;
    }

    private static bool ContainsItemName(string value, string itemName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Contains(itemName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadQuantity(string value, out uint quantity)
    {
        var digits = string.Concat(value.Where(char.IsDigit));
        return uint.TryParse(
                   digits,
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out quantity) &&
               quantity > 0;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private readonly record struct NumericSale(
        uint ItemId,
        string ItemName,
        ulong TotalGil,
        bool IsHq,
        DateTimeOffset SoldAtUtc);

    private readonly record struct StringGroup(int Offset, uint Quantity, string Buyer);
}
