namespace MarketMafioso.Dashboard.Components.Inventory;

using MarketMafioso.Contracts.Inventory;

public static class InventoryDisplayFormatter
{
    public static string FormatLogicalLocationOwner(InventoryBrowserStackView stack) =>
        stack.OwnerName.Equals("Player Inventory", StringComparison.OrdinalIgnoreCase)
            ? FormatCharacter(stack.OwnerCharacterName, stack.OwnerHomeWorld)
            : stack.OwnerName;

    public static string FormatLogicalLocationContext(InventoryBrowserStackView stack)
    {
        var storage = FormatLogicalStorage(stack);
        if (stack.OwnerName.Equals("Player Inventory", StringComparison.OrdinalIgnoreCase))
            return storage;

        var character = FormatCharacter(stack.OwnerCharacterName, stack.OwnerHomeWorld);
        return string.IsNullOrWhiteSpace(character) ? storage : $"{storage} · {character}";
    }

    public static string FormatLogicalLocation(InventoryBrowserStackView stack) =>
        $"{FormatLogicalLocationOwner(stack)} · {FormatLogicalLocationContext(stack)}";

    public static string FormatLogicalStorage(InventoryBrowserStackView stack)
    {
        var bag = stack.BagName?.Trim() ?? string.Empty;
        if (bag.Equals("RetainerInventory", StringComparison.OrdinalIgnoreCase) ||
            TryNumericSuffix(bag, "RetainerPage", out _))
            return "Retainer inventory";
        if (bag.Equals("RetainerMarket", StringComparison.OrdinalIgnoreCase))
            return "Market listings";
        if (bag.Equals("RetainerCrystals", StringComparison.OrdinalIgnoreCase))
            return "Retainer crystals";
        if (bag.Equals("EquippedItems", StringComparison.OrdinalIgnoreCase))
            return "Equipped gear";
        if (TryNumericSuffix(bag, "Inventory", out _))
            return "Player inventory";
        if (TryNumericSuffix(bag, "SaddleBag", out _))
            return "Saddlebag";
        if (TryNumericSuffix(bag, "PremiumSaddleBag", out _))
            return "Premium saddlebag";
        if (bag.StartsWith("Armory", StringComparison.OrdinalIgnoreCase))
            return $"Armoury · {SplitPascalCase(bag["Armory".Length..])}";

        return FormatStorage(stack.Location, bag);
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

    private static string FormatCharacter(string? characterName, string? homeWorld)
    {
        var name = characterName?.Trim() ?? string.Empty;
        var world = homeWorld?.Trim() ?? string.Empty;
        if (name.Length == 0)
            return world.Length == 0 ? "Unknown character" : world;
        return world.Length == 0 ? name : $"{name} @ {world}";
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
