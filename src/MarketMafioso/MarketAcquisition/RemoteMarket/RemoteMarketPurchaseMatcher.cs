namespace MarketMafioso.MarketAcquisition.RemoteMarket;

internal static class RemoteMarketPurchaseMatcher
{
    public static bool PacketMatchesIntent(
        ulong intentListingId,
        uint intentItemId,
        uint intentQuantity,
        uint intentUnitPrice,
        ulong packetListingId,
        uint packetItemId,
        uint packetQuantity,
        uint packetUnitPrice) =>
        packetListingId == intentListingId &&
        packetItemId == intentItemId &&
        packetQuantity == intentQuantity &&
        packetUnitPrice == intentUnitPrice;

    public static bool ConfirmationMatchesIntent(
        uint intentItemId,
        uint intentQuantity,
        uint confirmationItemId,
        uint confirmationQuantity) =>
        confirmationItemId == intentItemId &&
        confirmationQuantity == intentQuantity;
}
