using FFXIVClientStructs.FFXIV.Client.Game;

namespace MarketMafioso.Automation.Retainers;

public sealed record LiveRetainerStack(
    InventoryType Page,
    int SlotIndex,
    uint ItemId,
    int Quantity);
