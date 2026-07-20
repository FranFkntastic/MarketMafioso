using System;
using System.Collections.Generic;

namespace MarketMafioso.RetainerRestock;

public static class RetainerRestockStockCatalog
{
    /// <summary>
    /// Builds the native current-character browser projection with stable scopes, physical stacks, listings, and
    /// unknown evidence. Observation timestamps remain an internal cache concern and are not exposed here.
    /// </summary>
    public static RetainerBrowseProjection BuildBrowseProjection(
        IReadOnlyList<InventoryBag> playerBags,
        Configuration config,
        RetainerOwnerScope? ownerScope)
    {
        ArgumentNullException.ThrowIfNull(playerBags);
        ArgumentNullException.ThrowIfNull(config);
        return RetainerBrowseProjectionBuilder.Build(playerBags, config, ownerScope);
    }
}
