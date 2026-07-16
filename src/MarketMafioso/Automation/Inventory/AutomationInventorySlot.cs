namespace MarketMafioso.Automation.Inventory;

public sealed record AutomationInventorySlot(
    int SlotIndex,
    uint ItemId,
    int Quantity,
    bool IsHighQuality,
    float Condition = 0,
    float? ConditionPercent = null);
