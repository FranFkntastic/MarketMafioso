using System.Collections.Generic;

namespace MarketMafioso.Automation.Inventory;

public sealed record AutomationInventoryContainerSnapshot(
    string ContainerName,
    bool IsLoaded,
    int SlotCount,
    IReadOnlyList<AutomationInventorySlot> Slots);
