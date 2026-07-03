using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace MarketMafioso.Automation.Retainers;

public sealed class RetainerLiveInventoryScanner
{
    private static readonly InventoryType[] RetainerPages =
    [
        InventoryType.RetainerPage1,
        InventoryType.RetainerPage2,
        InventoryType.RetainerPage3,
        InventoryType.RetainerPage4,
        InventoryType.RetainerPage5,
        InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    public unsafe IReadOnlyList<LiveRetainerStack> ScanLiveRetainerStacks(IReadOnlySet<uint> itemIds)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return [];

        var stacks = new List<LiveRetainerStack>();
        foreach (var page in RetainerPages)
        {
            var container = inventoryManager->GetInventoryContainer(page);
            if (container == null || !container->IsLoaded)
                continue;

            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var slot = container->GetInventorySlot(slotIndex);
                if (slot == null || slot->ItemId == 0 || !itemIds.Contains(slot->ItemId))
                    continue;

                stacks.Add(new LiveRetainerStack(page, slotIndex, slot->ItemId, slot->Quantity));
            }
        }

        return stacks;
    }
}
