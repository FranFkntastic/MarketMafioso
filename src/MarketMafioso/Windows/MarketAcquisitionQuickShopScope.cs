namespace MarketMafioso.Windows;

public sealed record MarketAcquisitionQuickShopScope(
    bool HasScope,
    string CharacterName,
    string World,
    bool IsTemporarilyUnavailable);
