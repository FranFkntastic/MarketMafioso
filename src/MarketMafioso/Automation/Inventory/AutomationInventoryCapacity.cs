namespace MarketMafioso.Automation.Inventory;

public sealed record AutomationInventoryCapacity(
    bool IsKnown,
    int AvailableQuantity,
    int EmptySlots,
    int PartialStackSlots);
