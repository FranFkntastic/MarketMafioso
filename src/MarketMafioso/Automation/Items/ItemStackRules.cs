using System;
using System.Collections.Generic;

namespace MarketMafioso.Automation.Items;

public static class ItemStackRules
{
    private static readonly HashSet<uint> ElementalCurrencyItemIds =
    [
        2, 3, 4, 5, 6, 7,
        8, 9, 10, 11, 12, 13,
    ];

    public static bool IsElementalCurrency(uint itemId) => ElementalCurrencyItemIds.Contains(itemId);

    public static int ResolveMaxStack(uint itemId, uint luminaStackSize)
    {
        if (luminaStackSize > int.MaxValue)
            throw new OverflowException($"Lumina stack size {luminaStackSize} for item {itemId} is too large for inventory capacity math.");

        if (luminaStackSize > 0)
            return (int)luminaStackSize;

        return IsElementalCurrency(itemId) ? 9999 : 999;
    }
}
