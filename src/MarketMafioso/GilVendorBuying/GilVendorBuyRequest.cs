using System;

namespace MarketMafioso.GilVendorBuying;

public sealed record GilVendorBuyRequest(GilVendorOffer Offer, uint Quantity, ulong MaxTotalGil)
{
    public static GilVendorBuyRequestCreateResult Create(GilVendorOffer offer, uint quantity)
    {
        ArgumentNullException.ThrowIfNull(offer);

        if (!offer.IsValidOrdinaryGilOffer)
        {
            return GilVendorBuyRequestCreateResult.Fail(
                "InvalidOffer",
                "The selected vendor offer is not a valid ordinary gil offer.");
        }

        if (quantity == 0)
        {
            return GilVendorBuyRequestCreateResult.Fail(
                "InvalidQuantity",
                "Vendor buy quantity must be greater than zero.");
        }

        var total = checked((ulong)offer.UnitPriceGil * quantity);
        if (total > int.MaxValue)
        {
            return GilVendorBuyRequestCreateResult.Fail(
                "GilTotalOverflow",
                "Vendor buy total exceeds the supported gil guard for one purchase attempt.");
        }

        return GilVendorBuyRequestCreateResult.Success(new GilVendorBuyRequest(offer, quantity, total));
    }
}

public sealed record GilVendorBuyRequestCreateResult(
    bool IsSuccess,
    string Status,
    string Message,
    GilVendorBuyRequest? Request)
{
    public static GilVendorBuyRequestCreateResult Success(GilVendorBuyRequest request) =>
        new(true, "Ready", "Vendor buy request is ready.", request);

    public static GilVendorBuyRequestCreateResult Fail(string status, string message) =>
        new(false, status, message, null);
}
