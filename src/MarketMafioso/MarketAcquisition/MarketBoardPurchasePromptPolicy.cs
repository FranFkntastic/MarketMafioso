using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MarketMafioso.MarketAcquisition;

public readonly record struct MarketBoardPurchasePromptValidation(bool IsValid, string Status, string Message);

public static partial class MarketBoardPurchasePromptPolicy
{
    [GeneratedRegex(@"\bpurchase\s+(?<quantity>[\d,]+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PurchaseQuantityRegex();

    public static MarketBoardPurchasePromptValidation Validate(string? text, uint expectedQuantity, string? expectedItemName)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new(false, "UnexpectedConfirmation", "The visible confirmation prompt was empty.");

        var match = PurchaseQuantityRegex().Match(text);
        if (!match.Success ||
            !uint.TryParse(match.Groups["quantity"].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var quantity))
            return new(false, "UnexpectedConfirmation", "The visible prompt did not contain a parseable market purchase quantity.");
        if (quantity != expectedQuantity)
            return new(false, "ConfirmationCandidateMismatch", $"The visible prompt quantity {quantity:N0} did not match the selected listing quantity {expectedQuantity:N0}.");
        if (!string.IsNullOrWhiteSpace(expectedItemName) &&
            !text.Contains(expectedItemName, StringComparison.OrdinalIgnoreCase))
            return new(false, "ConfirmationCandidateMismatch", $"The visible prompt did not name the selected item {expectedItemName}.");

        return new(true, "Ready", "The visible prompt matches the selected listing quantity and item.");
    }
}
