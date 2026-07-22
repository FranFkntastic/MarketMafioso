namespace MarketMafioso.Dashboard.Components.Inventory;

using Franthropy.FFXIV.Filtering;
using Franthropy.Filtering.Completion;
using Franthropy.Filtering.Semantics;
using Franthropy.Filtering.Syntax;
using MarketMafioso.Contracts.Inventory;

public static class InventoryFilterPresentation
{
    private static readonly FfxivFilterCatalog Catalog = FfxivFilterCatalog.Create(new FfxivFilterResolvers(
        new FilterNamedValueCatalog<FfxivItemKey>([]),
        new FilterNamedValueCatalog<FfxivJobKey>([]),
        new FilterNamedValueCatalog<FfxivUiCategoryKey>([]),
        new FilterNamedValueCatalog<FfxivCharacterKey>([]),
        new FilterNamedValueCatalog<FfxivRetainerKey>([]),
        new FilterNamedValueCatalog<FfxivWorldKey>([]),
        new FilterNamedValueCatalog<FfxivDataCenterKey>([])));

    private static readonly HashSet<string> InventoryFieldKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "instance.quality",
        "instance.condition",
        "instance.location",
        "instance.equipped",
        "instance.quantity",
        "item.slot",
        "ownership.owned",
        "ownership.quantity",
        "ownership.quality",
        "ownership.location",
    };

    private static readonly HashSet<string> ListingFieldKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "offer.price",
        "offer.totalPrice",
        "offer.quantity",
        "offer.age",
        "offer.source",
    };

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

    public static bool HasValueCompletion(IReadOnlyList<FilterCompletionItem> completions) =>
        completions.Any(completion => completion.Kind == FilterCompletionKind.Value);

    public static InventoryBrowserMode? SuggestedMode(string? filter, InventoryBrowserMode currentMode)
    {
        var referencedKeys = CollectReferencedKeys(filter);
        if (currentMode != InventoryBrowserMode.Items && referencedKeys.Overlaps(InventoryFieldKeys))
            return InventoryBrowserMode.Items;
        if (currentMode != InventoryBrowserMode.Listings && referencedKeys.Overlaps(ListingFieldKeys))
            return InventoryBrowserMode.Listings;
        return null;
    }

    private static HashSet<string> CollectReferencedKeys(string? filter)
    {
        var tree = FilterSyntaxTree.Parse(filter);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectReferencedKeys(tree.Root.Expression, keys);
        return keys;
    }

    private static void CollectReferencedKeys(FilterExpressionSyntax? node, HashSet<string> keys)
    {
        switch (node)
        {
            case null:
            case FilterMissingExpressionSyntax:
            case FilterFreeTextSyntax:
                return;
            case FilterFieldExpressionSyntax fieldExpression:
                AddResolvedFieldKey(fieldExpression.Field.Value, keys);
                return;
            case FilterFunctionCallSyntax functionCall:
                AddResolvedFieldKey(functionCall.Field.Value, keys);
                return;
            case FilterUnaryExpressionSyntax unary:
                CollectReferencedKeys(unary.Operand, keys);
                return;
            case FilterBinaryExpressionSyntax binary:
                CollectReferencedKeys(binary.Left, keys);
                CollectReferencedKeys(binary.Right, keys);
                return;
            case FilterParenthesizedExpressionSyntax parenthesized:
                CollectReferencedKeys(parenthesized.Expression, keys);
                return;
            case FilterReservedNestedQualifierSyntax:
                return;
        }
    }

    private static void AddResolvedFieldKey(string text, HashSet<string> keys)
    {
        var resolution = Catalog.Catalog.Resolve(text);
        if (resolution.Kind == FilterFieldResolutionKind.Success && resolution.Field is not null)
        {
            keys.Add(resolution.Field.Key);
            return;
        }

        if (resolution.Kind != FilterFieldResolutionKind.Ambiguous || resolution.Candidates.Count == 0)
            return;

        if (resolution.Candidates.All(candidate => InventoryFieldKeys.Contains(candidate.Key)) ||
            resolution.Candidates.All(candidate => ListingFieldKeys.Contains(candidate.Key)))
        {
            keys.UnionWith(resolution.Candidates.Select(candidate => candidate.Key));
        }
    }

    private static bool EndsWithKeyword(string value, string keyword) =>
        value.Equals(keyword, StringComparison.OrdinalIgnoreCase) ||
        value.EndsWith($" {keyword}", StringComparison.OrdinalIgnoreCase);
}
