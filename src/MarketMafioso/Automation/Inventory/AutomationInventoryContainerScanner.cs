using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace MarketMafioso.Automation.Inventory;

public sealed class AutomationInventoryContainerScanner
{
    private readonly IPluginLog log;

    public AutomationInventoryContainerScanner(IPluginLog log)
    {
        this.log = log;
    }

    public unsafe IReadOnlyList<AutomationInventoryContainerSnapshot> ScanLoadedContainers(
        IReadOnlyList<InventoryType> inventoryTypes)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            log.Warning("[MarketMafioso] InventoryManager.Instance() returned null");
            return [];
        }

        var snapshots = new List<AutomationInventoryContainerSnapshot>();
        foreach (var inventoryType in inventoryTypes)
        {
            try
            {
                var container = inventoryManager->GetInventoryContainer(inventoryType);
                if (container == null || !container->IsLoaded)
                    continue;

                var slots = new List<AutomationInventorySlot>();
                for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
                {
                    var slot = container->GetInventorySlot(slotIndex);
                    if (slot == null || slot->ItemId == 0)
                        continue;

                    slots.Add(new AutomationInventorySlot(
                        slotIndex,
                        slot->ItemId,
                        checked((int)slot->Quantity),
                        slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality),
                        slot->Condition / 30000f));
                }

                snapshots.Add(new AutomationInventoryContainerSnapshot(
                    inventoryType.ToString(),
                    IsLoaded: true,
                    SlotCount: container->Size,
                    Slots: slots));
            }
            catch (Exception ex)
            {
                log.Error(ex, $"[MarketMafioso] Error scanning container {inventoryType}");
            }
        }

        return snapshots;
    }
}
