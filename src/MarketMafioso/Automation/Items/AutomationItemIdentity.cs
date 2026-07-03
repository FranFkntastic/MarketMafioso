namespace MarketMafioso.Automation.Items;

public sealed record AutomationItemIdentity(
    uint ItemId,
    string? Name,
    bool IsHighQuality = false);
