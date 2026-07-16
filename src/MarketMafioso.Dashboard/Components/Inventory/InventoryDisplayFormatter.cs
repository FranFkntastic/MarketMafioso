namespace MarketMafioso.Dashboard.Components.Inventory;

using MarketMafioso.Contracts.Inventory;

public static class InventoryDisplayFormatter
{
    public static string FormatItemLocations(InventoryBrowserItemView item)
    {
        var first = item.Locations.FirstOrDefault();
        if (first is null)
            return "—";

        var primary = $"{first.OwnerName} · {FormatStorage(first.Location, first.BagName)}";
        return item.Locations.Count > 1 ? $"{primary} +{item.Locations.Count - 1}" : primary;
    }

    public static string FormatStackStorage(InventoryBrowserStackView stack)
    {
        var storage = FormatStorage(stack.Location, stack.BagName);
        return stack.SlotIndex is { } slotIndex ? $"{storage} · slot {slotIndex + 1}" : storage;
    }

    public static string FormatStorage(string? location, string? bagName)
    {
        var bag = bagName?.Trim() ?? string.Empty;
        if (bag.Equals("RetainerInventory", StringComparison.OrdinalIgnoreCase))
            return "Retainer inventory";
        if (bag.Equals("RetainerMarket", StringComparison.OrdinalIgnoreCase))
            return "Market listings";
        if (bag.Equals("RetainerCrystals", StringComparison.OrdinalIgnoreCase))
            return "Retainer crystals";
        if (bag.Equals("EquippedItems", StringComparison.OrdinalIgnoreCase))
            return "Equipped gear";

        if (TryNumericSuffix(bag, "Inventory", out var inventoryBag))
            return $"Inventory · bag {inventoryBag}";
        if (TryNumericSuffix(bag, "RetainerPage", out var retainerBag))
            return $"Retainer inventory · bag {retainerBag}";
        if (TryNumericSuffix(bag, "SaddleBag", out var saddlebag))
            return $"Saddlebag · bag {saddlebag}";
        if (TryNumericSuffix(bag, "PremiumSaddleBag", out var premiumSaddlebag))
            return $"Premium saddlebag · bag {premiumSaddlebag}";
        if (bag.StartsWith("Armory", StringComparison.OrdinalIgnoreCase))
            return $"Armoury · {SplitPascalCase(bag["Armory".Length..])}";

        if (!string.IsNullOrWhiteSpace(bag))
            return SplitPascalCase(bag);
        return string.IsNullOrWhiteSpace(location) ? "Unknown storage" : location;
    }

    private static bool TryNumericSuffix(string value, string prefix, out int number)
    {
        number = 0;
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(value[prefix.Length..], out number);
    }

    private static string SplitPascalCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        var output = new System.Text.StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (index > 0 && char.IsUpper(current) && !char.IsUpper(value[index - 1]))
                output.Append(' ');
            output.Append(current);
        }

        var result = output.ToString();
        return char.ToUpperInvariant(result[0]) + result[1..];
    }
}
