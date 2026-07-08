namespace MarketMafioso.GilVendorBuying;

public sealed record GilVendorPosition(float X, float Y, float Z);

public sealed record GilVendorOffer(
    uint ItemId,
    string ItemName,
    uint VendorId,
    string VendorName,
    uint TerritoryId,
    GilVendorPosition Position,
    uint UnitPriceGil,
    uint? ShopItemId = null)
{
    public bool IsValidOrdinaryGilOffer =>
        ItemId != 0 &&
        !string.IsNullOrWhiteSpace(ItemName) &&
        VendorId != 0 &&
        !string.IsNullOrWhiteSpace(VendorName) &&
        TerritoryId != 0 &&
        UnitPriceGil > 0;
}
