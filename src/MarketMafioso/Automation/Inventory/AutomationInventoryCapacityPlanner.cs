using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.Automation.Inventory;

public static class AutomationInventoryCapacityPlanner
{
    public static AutomationInventoryCapacity CalculateCapacity(
        IReadOnlyList<AutomationInventoryContainerSnapshot> containers,
        uint itemId,
        bool isHighQuality,
        int maxStack)
    {
        var loaded = containers.Where(container => container.IsLoaded).ToList();
        if (loaded.Count == 0)
            return new AutomationInventoryCapacity(false, 0, 0, 0);

        var emptySlots = 0;
        var partialSlots = 0;
        var availableQuantity = 0;

        foreach (var container in loaded)
        {
            var occupiedSlots = container.Slots.Count;
            emptySlots += Math.Max(container.SlotCount - occupiedSlots, 0);

            foreach (var slot in container.Slots.Where(slot => slot.ItemId == itemId && slot.IsHighQuality == isHighQuality))
            {
                var remaining = Math.Max(maxStack - slot.Quantity, 0);
                if (remaining <= 0)
                    continue;

                partialSlots++;
                availableQuantity = checked(availableQuantity + remaining);
            }
        }

        availableQuantity = checked(availableQuantity + (emptySlots * maxStack));
        return new AutomationInventoryCapacity(true, availableQuantity, emptySlots, partialSlots);
    }
}
