namespace MarketMafioso.Dashboard.Components.Inventory;

using MarketMafioso.Contracts.Inventory;

public static class InventoryFilterPresentation
{
    private static readonly string[] InventoryFields = ["quality", "condition", "location", "equipped", "slot"];
    private static readonly string[] ListingFields = ["price", "total", "age", "offer"];

    public static bool IsIncomplete(string? filter)
    {
        var value = filter?.TrimEnd() ?? string.Empty;
        if (value.Length == 0)
            return false;

        if (value.EndsWith(':') || value.EndsWith('=') || value.EndsWith('<') || value.EndsWith('>') ||
            value.EndsWith('!') || value.EndsWith('(') || value.EndsWith('|') || value.EndsWith('&'))
            return true;

        if (EndsWithKeyword(value, "and") || EndsWithKeyword(value, "or") || EndsWithKeyword(value, "not"))
            return true;

        var quoteCount = value.Count(character => character == '"');
        if (quoteCount % 2 != 0)
            return true;

        var parenthesisDepth = 0;
        foreach (var character in value)
        {
            if (character == '(')
                parenthesisDepth++;
            else if (character == ')' && parenthesisDepth > 0)
                parenthesisDepth--;
        }

        return parenthesisDepth > 0;
    }

    public static InventoryBrowserMode? SuggestedMode(string? filter, InventoryBrowserMode currentMode)
    {
        var value = filter ?? string.Empty;
        if (currentMode != InventoryBrowserMode.Items && InventoryFields.Any(field => ContainsField(value, field)))
            return InventoryBrowserMode.Items;
        if (currentMode != InventoryBrowserMode.Listings && ListingFields.Any(field => ContainsField(value, field)))
            return InventoryBrowserMode.Listings;
        return null;
    }

    private static bool EndsWithKeyword(string value, string keyword) =>
        value.Equals(keyword, StringComparison.OrdinalIgnoreCase) ||
        value.EndsWith($" {keyword}", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsField(string value, string field)
    {
        var index = value.IndexOf(field, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var beforeIsBoundary = index == 0 || !char.IsLetterOrDigit(value[index - 1]);
            var afterIndex = index + field.Length;
            var afterIsOperator = afterIndex < value.Length && value[afterIndex] is ':' or '=' or '<' or '>';
            if (beforeIsBoundary && afterIsOperator)
                return true;
            index = value.IndexOf(field, index + field.Length, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
