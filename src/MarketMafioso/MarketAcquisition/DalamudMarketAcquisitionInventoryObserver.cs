using System;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace MarketMafioso.MarketAcquisition;

public sealed class DalamudMarketAcquisitionInventoryObserver : IMarketAcquisitionInventoryObserver
{
    private readonly Dalamud.Plugin.Services.IDataManager? dataManager;
    public DalamudMarketAcquisitionInventoryObserver(Dalamud.Plugin.Services.IDataManager? dataManager = null) =>
        this.dataManager = dataManager;

    public unsafe MarketAcquisitionCapacityObservation Capacity(uint itemId, bool isHq)
    {
        var stack = dataManager?.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(itemId)?.StackSize;
        var manager = InventoryManager.Instance();
        if (manager == null || stack is null or 0) return new(false, 0, 0);
        var crystal = itemId is >= 2 and <= 19;
        ulong room = 0;
        var free = 0;
        var crystalSlotSeen = false;
        foreach (var type in crystal ? new[] { InventoryType.Crystals } : PlayerBags)
        {
            var bag = manager->GetInventoryContainer(type);
            if (bag == null || !bag->IsLoaded) return new(false, 0, 0);
            for (var i = 0; i < bag->Size; i++)
            {
                var slot = bag->GetInventorySlot(i);
                if (slot == null) return new(false, 0, 0);
                if (slot->ItemId == 0 && !crystal) { free++; room += stack.Value; }
                else if (slot->ItemId == itemId && (crystal || slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) == isHq))
                {
                    crystalSlotSeen |= crystal;
                    room += (ulong)Math.Max(0L, (long)stack.Value - slot->Quantity);
                }
            }
        }
        // Each crystal type owns a fixed pouch slot that reads as empty at zero quantity, so a missing slot means a full stack of room.
        if (crystal && !crystalSlotSeen) room = stack.Value;
        return new(true, room, free);
    }

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
        // Crystals live outside the four main bags; require the pouch to be loaded too.
        var containers = itemId is >= 2 and <= 19
            ? new[] { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4, InventoryType.Crystals }
            : PlayerBags;
        foreach (var inventoryType in containers)
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
