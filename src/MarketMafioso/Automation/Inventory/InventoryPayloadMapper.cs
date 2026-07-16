using System;
using System.Collections.Generic;
using System.Linq;
using MarketMafioso.Automation.Items;

namespace MarketMafioso.Automation.Inventory;

public static class InventoryPayloadMapper
{
    public static List<InventoryBag> MapInventoryBags(
        IReadOnlyList<AutomationInventoryContainerSnapshot> containers,
        bool includeItemNames,
        Func<uint, string?> resolveItemName,
        Func<uint, AutomationItemMetadata>? resolveItemMetadata = null)
    {
        var bags = new List<InventoryBag>();

        foreach (var container in containers.Where(container => container.IsLoaded))
        {
            var items = MapPhysicalItems(
                container.ContainerName,
                container.Slots,
                includeItemNames,
                resolveItemName,
                resolveItemMetadata);
            if (items.Count == 0)
                continue;

            bags.Add(new InventoryBag
            {
                BagName = container.ContainerName,
                Location = ResolveLocation(container.ContainerName, isRetainer: false),
                Items = items,
            });
        }

        return bags;
    }

    public static List<InventoryBag> MapRetainerInventoryBags(
        IReadOnlyList<AutomationInventoryContainerSnapshot> containers,
        bool includeItemNames,
        Func<uint, string?> resolveItemName,
        Func<uint, AutomationItemMetadata>? resolveItemMetadata = null)
    {
        var retainerPageItems = containers
            .Where(container => container.IsLoaded && container.ContainerName.StartsWith("RetainerPage", StringComparison.Ordinal))
            .SelectMany(container => MapPhysicalItems(
                container.ContainerName,
                container.Slots,
                includeItemNames,
                resolveItemName,
                resolveItemMetadata))
            .ToList();
        var mergedBags = containers
            .Where(container => container.IsLoaded && !container.ContainerName.StartsWith("RetainerPage", StringComparison.Ordinal))
            .Select(container => new InventoryBag
            {
                BagName = container.ContainerName,
                Location = "Retainer",
                Items = MapPhysicalItems(
                    container.ContainerName,
                    container.Slots,
                    includeItemNames,
                    resolveItemName,
                    resolveItemMetadata),
            })
            // RetainerCrystals is a fixed-capacity inventory. Preserve an empty loaded
            // container so callers can distinguish "scanned and empty" from legacy
            // cache entries that never included crystal capacity.
            .Where(bag => bag.Items.Count > 0 || bag.BagName == "RetainerCrystals")
            .ToList();

        if (retainerPageItems.Count > 0)
        {
            mergedBags.Insert(0, new InventoryBag
            {
                BagName = "RetainerInventory",
                Location = "Retainer",
                Items = retainerPageItems,
            });
        }

        return mergedBags;
    }

    public static List<RetainerMarketListing> MapRetainerMarketListings(
        IReadOnlyList<AutomationInventoryContainerSnapshot> containers,
        bool includeItemNames,
        Func<uint, string?> resolveItemName,
        DateTime listedAtUtc,
        Func<uint, AutomationItemMetadata>? resolveItemMetadata = null)
    {
        return MapInventoryBags(containers, includeItemNames, resolveItemName, resolveItemMetadata)
            .SelectMany(bag => bag.Items)
            .Select(item => new RetainerMarketListing
            {
                ItemId = item.ItemId,
                ItemName = item.ItemName,
                ItemType = item.ItemType,
                Quantity = item.Quantity,
                IsHQ = item.IsHQ,
                Condition = item.Condition,
                ContainerKey = item.ContainerKey,
                SlotIndex = item.SlotIndex,
                ConditionPercent = item.ConditionPercent,
                ListedAt = listedAtUtc.ToString("o"),
            })
            .ToList();
    }

    private static List<ItemSlot> MapPhysicalItems(
        string containerName,
        IReadOnlyList<AutomationInventorySlot> slots,
        bool includeItemNames,
        Func<uint, string?> resolveItemName,
        Func<uint, AutomationItemMetadata>? resolveItemMetadata)
    {
        var equipped = containerName.Equals("EquippedItems", StringComparison.Ordinal);
        return slots.Select(slot =>
        {
            var metadata = resolveItemMetadata?.Invoke(slot.ItemId);
            return new ItemSlot
            {
                ItemId = slot.ItemId,
                ItemName = includeItemNames ? metadata?.Identity.Name ?? resolveItemName(slot.ItemId) : null,
                ItemType = metadata?.ItemType,
                Quantity = checked((uint)slot.Quantity),
                IsHQ = slot.IsHighQuality,
                Condition = slot.Condition,
                ContainerKey = containerName,
                SlotIndex = slot.SlotIndex,
                ConditionPercent = metadata is { SupportsCondition: false } ? null : slot.ConditionPercent,
                Equipped = equipped,
            };
        })
            .ToList();
    }

    private static string ResolveLocation(string containerName, bool isRetainer)
    {
        if (isRetainer || containerName.StartsWith("Retainer", StringComparison.Ordinal))
            return "Retainer";
        if (containerName.Equals("EquippedItems", StringComparison.Ordinal))
            return "Equipped";
        if (containerName.StartsWith("Armory", StringComparison.Ordinal))
            return "Armoury";
        if (containerName.Contains("SaddleBag", StringComparison.Ordinal))
            return "Saddlebag";
        return "Inventory";
    }
}
