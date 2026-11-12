using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Automation.Retainers;

namespace MarketMafioso.Quartermaster;

/// <summary>Source coverage authorizes stock/capacity; bag values alone are historical evidence.</summary>
public static class QuartermasterStockCoverage
{
    public static bool CoversItem(IEnumerable<string> observedSources, uint itemId) =>
        DalamudRetainerInventory.HasCoverageForItem(observedSources, itemId);

    public static bool CoversItem(QuartermasterRetainerSnapshot retainer, uint itemId) =>
        CoversItem(retainer.ObservedSources, itemId) &&
        (ElementalCurrencyCatalog.IsElementalCurrency(itemId)
            ? retainer.StockCoverage?.Crystals : retainer.StockCoverage?.Ordinary) is not false;
}
