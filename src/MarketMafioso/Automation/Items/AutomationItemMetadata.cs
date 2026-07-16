namespace MarketMafioso.Automation.Items;

public sealed record AutomationItemMetadata(
    AutomationItemIdentity Identity,
    int MaxStack,
    string? ItemType = null,
    bool SupportsCondition = false);
