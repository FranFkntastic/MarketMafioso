using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.RetainerRestock;

public sealed record RetainerRestockCompletionSummary(
    bool IsSuccess,
    bool IsPartial,
    string Message)
{
    public static RetainerRestockCompletionSummary Build(
        IReadOnlyDictionary<uint, int> remaining,
        int totalRetrieved)
    {
        var remainingText = FormatRemainingQuantities(remaining);
        if (string.IsNullOrEmpty(remainingText))
            return new(true, false, $"Retainer restock complete. Retrieved {totalRetrieved} item(s).");

        if (totalRetrieved > 0)
            return new(true, true, $"Retainer restock partially complete. Retrieved {totalRetrieved} item(s); remaining quantities: {remainingText}.");

        return new(false, false, $"No matching live retainer stacks were found for the restock plan: {remainingText}.");
    }

    private static string FormatRemainingQuantities(IReadOnlyDictionary<uint, int> remaining)
    {
        return string.Join(
            ", ",
            remaining
                .Where(item => item.Value > 0)
                .OrderBy(item => item.Key)
                .Select(item => $"{item.Key}:{item.Value}"));
    }
}
