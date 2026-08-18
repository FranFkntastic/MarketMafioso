using System;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace MarketMafioso.MarketAcquisition;

public sealed class DalamudMarketAcquisitionInventoryObserver : IMarketAcquisitionInventoryObserver
{
    private static readonly InventoryType[] PlayerBags =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    public unsafe MarketAcquisitionInventoryObservation Observe(uint itemId, string hqPolicy)
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
            return MarketAcquisitionInventoryObservation.Unavailable("Player inventory is unavailable.");

        var normalizedPolicy = MarketAcquisitionPolicy.NormalizeHqPolicy(hqPolicy);
        var quantity = 0u;
        foreach (var inventoryType in PlayerBags)
        {
            var container = manager->GetInventoryContainer(inventoryType);
            if (container == null || !container->IsLoaded)
                return MarketAcquisitionInventoryObservation.Unavailable($"Player inventory bag {inventoryType} is not loaded.");

            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var slot = container->GetInventorySlot(slotIndex);
                if (slot == null || slot->ItemId != itemId)
                    continue;
                var isHq = slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                if (!MarketAcquisitionPolicy.HqMatches(normalizedPolicy, isHq))
                    continue;
                quantity = checked(quantity + (uint)slot->Quantity);
            }
        }

        return MarketAcquisitionInventoryObservation.Available(quantity);
    }
}
