using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.GilVendorBuying;

public sealed class GilVendorCatalog
{
    private readonly Dictionary<uint, IReadOnlyList<GilVendorOffer>> offersByItemId;

    private GilVendorCatalog(Dictionary<uint, IReadOnlyList<GilVendorOffer>> offersByItemId)
    {
        this.offersByItemId = offersByItemId;
    }

    public static GilVendorCatalog Create(IEnumerable<GilVendorOffer> offers)
    {
        ArgumentNullException.ThrowIfNull(offers);

        var grouped = offers
            .Where(offer => offer.IsValidOrdinaryGilOffer)
            .GroupBy(offer => offer.ItemId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<GilVendorOffer>)group
                    .OrderBy(offer => offer.UnitPriceGil)
                    .ThenBy(offer => offer.VendorName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(offer => offer.VendorId)
                    .ToList());

        return new GilVendorCatalog(grouped);
    }

    public IReadOnlyList<GilVendorOffer> FindOffersByItemId(uint itemId) =>
        offersByItemId.TryGetValue(itemId, out var offers) ? offers : [];

    public GilVendorBuyRequestCreateResult TryCreateRequest(
        uint itemId,
        uint quantity,
        uint? preferredVendorId = null)
    {
        var offers = FindOffersByItemId(itemId);
        if (offers.Count == 0)
        {
            return GilVendorBuyRequestCreateResult.Fail(
                "OfferNotCataloged",
                $"Item {itemId} is not known to be sold by an ordinary gil vendor.");
        }

        var offer = preferredVendorId is { } vendorId
            ? offers.FirstOrDefault(candidate => candidate.VendorId == vendorId)
            : offers[0];

        if (offer == null)
        {
            return GilVendorBuyRequestCreateResult.Fail(
                "PreferredVendorUnavailable",
                $"Item {itemId} is not known to be sold by ordinary gil vendor {preferredVendorId}.");
        }

        return GilVendorBuyRequest.Create(offer, quantity);
    }
}
