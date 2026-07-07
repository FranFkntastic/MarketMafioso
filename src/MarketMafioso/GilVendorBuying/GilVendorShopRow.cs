namespace MarketMafioso.GilVendorBuying;

public sealed record GilVendorShopRow(
    int RowIndex,
    uint ItemId,
    string ItemName,
    uint UnitPriceGil,
    uint? ShopItemId,
    uint? AvailableQuantity);
