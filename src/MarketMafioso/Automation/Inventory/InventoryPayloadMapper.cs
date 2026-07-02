using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.Automation.Inventory;

public static class InventoryPayloadMapper
{
    public static List<InventoryBag> MapInventoryBags(
        IReadOnlyList<AutomationInventoryContainerSnapshot> containers,
        bool includeItemNames,
        Func<uint, string?> resolveItemName)
    {
        var bags = new List<InventoryBag>();

        foreach (var container in containers.Where(container => container.IsLoaded))
        {
            var items = MapGroupedItems(container.Slots, includeItemNames, resolveItemName);
            if (items.Count == 0)
                continue;

            bags.Add(new InventoryBag
            {
                BagName = container.ContainerName,
                Items = items,
            });
        }

        return bags;
    }

    public static List<InventoryBag> MapRetainerInventoryBags(
        IReadOnlyList<AutomationInventoryContainerSnapshot> containers,
        bool includeItemNames,
        Func<uint, string?> resolveItemName)
    {
        var retainerPageSlots = containers
            .Where(container => container.IsLoaded && container.ContainerName.StartsWith("RetainerPage", StringComparison.Ordinal))
            .SelectMany(container => container.Slots)
            .ToList();
        var mergedBags = containers
            .Where(container => container.IsLoaded && !container.ContainerName.StartsWith("RetainerPage", StringComparison.Ordinal))
            .Select(container => new InventoryBag
            {
                BagName = container.ContainerName,
                Items = MapGroupedItems(container.Slots, includeItemNames, resolveItemName),
            })
            .Where(bag => bag.Items.Count > 0)
            .ToList();

        var retainerItems = MapGroupedItems(retainerPageSlots, includeItemNames, resolveItemName);
        if (retainerItems.Count > 0)
        {
            mergedBags.Insert(0, new InventoryBag
            {
                BagName = "RetainerInventory",
                Items = retainerItems,
            });
        }

        return mergedBags;
    }

    public static List<RetainerMarketListing> MapRetainerMarketListings(
        IReadOnlyList<AutomationInventoryContainerSnapshot> containers,
        bool includeItemNames,
        Func<uint, string?> resolveItemName,
        DateTime listedAtUtc)
    {
        return MapInventoryBags(containers, includeItemNames, resolveItemName)
            .SelectMany(bag => bag.Items)
            .Select(item => new RetainerMarketListing
            {
                ItemId = item.ItemId,
                ItemName = item.ItemName,
                ItemType = item.ItemType,
                Quantity = item.Quantity,
                IsHQ = item.IsHQ,
                Condition = item.Condition,
                ListedAt = listedAtUtc.ToString("o"),
            })
            .ToList();
    }

    private static List<ItemSlot> MapGroupedItems(
        IReadOnlyList<AutomationInventorySlot> slots,
        bool includeItemNames,
        Func<uint, string?> resolveItemName)
    {
        return slots
            .GroupBy(slot => new { slot.ItemId, slot.IsHighQuality })
            .Select(group =>
            {
                var first = group.First();
                return new ItemSlot
                {
                    ItemId = first.ItemId,
                    ItemName = includeItemNames ? resolveItemName(first.ItemId) : null,
                    ItemType = null,
                    Quantity = checked((uint)group.Sum(slot => slot.Quantity)),
                    IsHQ = first.IsHighQuality,
                    Condition = group.Max(slot => slot.Condition),
                };
            })
            .ToList();
    }
}
