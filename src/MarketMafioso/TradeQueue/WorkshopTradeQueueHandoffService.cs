using System;
using System.Collections.Generic;
using System.Linq;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.TradeQueue;

public sealed record WorkshopTradeQueueHandoffResult(
    bool Success,
    string Message,
    IReadOnlyList<TradeQueueItem> Items);

public static class WorkshopTradeQueueHandoffService
{
    public static WorkshopTradeQueueHandoffResult Build(
        IReadOnlyList<WorkshopMaterialAvailability> availability)
    {
        ArgumentNullException.ThrowIfNull(availability);
        var items = availability
            .Select(material => new TradeQueueItem
            {
                ItemId = material.ItemId,
                ItemName = material.ItemName,
                IsHighQuality = false,
                Quantity = Math.Min(material.Required, material.PlayerInventory),
            })
            .Where(item => item.Quantity > 0)
            .OrderBy(item => item.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (items.Count == 0)
            return new(false, "No required workshop materials are currently available in player inventory.", []);

        return new(
            true,
            $"Replaced Trade Queue with {items.Count:N0} available workshop material(s), {items.Sum(item => item.Quantity):N0} units.",
            items);
    }
}
