using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace MarketMafioso.WorkshopPrep;

public sealed class WorkshopRetainerRestockService
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

    private readonly IPluginLog log;
    private bool isRunning;
    private string lastStatus = "Workshop material restock has not run.";

    public WorkshopRetainerRestockService(IPluginLog log)
    {
        this.log = log;
    }

    public bool IsRunning => isRunning;
    public string LastStatus => lastStatus;

    public Task StartAsync(IReadOnlyList<WorkshopMaterialAvailability> availability)
    {
        if (isRunning)
        {
            lastStatus = "Workshop material restock is already running.";
            return Task.CompletedTask;
        }

        var shortages = availability.Where(x => x.Shortage > 0).ToList();
        if (shortages.Count == 0)
        {
            lastStatus = "No workshop material shortages to restock.";
            return Task.CompletedTask;
        }

        isRunning = true;
        try
        {
            lastStatus = "Workshop material restock planning complete. Retainer UI withdrawal is the next implementation slice.";
            log.Information($"[MarketMafioso] Planned workshop restock for {shortages.Count} shortage item(s).");
        }
        finally
        {
            isRunning = false;
        }

        return Task.CompletedTask;
    }

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

public sealed record LiveRetainerStack(
    InventoryType Page,
    int SlotIndex,
    uint ItemId,
    int Quantity);
