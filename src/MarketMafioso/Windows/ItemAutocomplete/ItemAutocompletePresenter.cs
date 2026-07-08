using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.Windows.ItemAutocomplete;

public sealed record AcquisitionItemOption(uint ItemId, string Name);

public sealed class ItemAutocompleteState
{
    public string SearchBuffer { get; set; } = string.Empty;
    public AcquisitionItemOption? SelectedItem { get; set; }
}

public static class ItemAutocompletePresenter
{
    public static AcquisitionItemOption? ResolveSelectedItem(
        IReadOnlyList<AcquisitionItemOption> itemOptions,
        string searchBuffer,
        AcquisitionItemOption? selectedItem)
    {
        var search = searchBuffer.Trim();
        if (selectedItem is not null &&
            selectedItem.Name.Equals(search, StringComparison.OrdinalIgnoreCase))
        {
            return selectedItem;
        }

        if (search.Length == 0)
            return null;

        AcquisitionItemOption? exactMatch = null;
        foreach (var option in itemOptions)
        {
            if (!option.Name.Equals(search, StringComparison.OrdinalIgnoreCase))
                continue;

            if (exactMatch is not null)
                return null;

            exactMatch = option;
        }

        return exactMatch;
    }

    public static IReadOnlyList<AcquisitionItemOption> GetSearchResults(
        IReadOnlyList<AcquisitionItemOption> itemOptions,
        string searchBuffer,
        int limit = 10)
    {
        var search = searchBuffer.Trim();
        if (search.Length < 2)
            return [];

        return itemOptions
            .Where(item => item.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Name.StartsWith(search, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(item => item.Name.Length)
            .ThenBy(item => item.Name)
            .Take(limit)
            .ToList();
    }

    public static string FormatDisplayName(
        IReadOnlyList<AcquisitionItemOption> itemOptions,
        AcquisitionItemOption option)
    {
        var duplicates = itemOptions
            .Where(item => item.Name.Equals(option.Name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.ItemId)
            .ToArray();
        if (duplicates.Length <= 1)
            return option.Name;

        var ordinal = Array.FindIndex(duplicates, item => item.ItemId == option.ItemId);
        return ordinal < 0
            ? $"{option.Name} - duplicate"
            : $"{option.Name} - duplicate {ordinal + 1}";
    }
}
