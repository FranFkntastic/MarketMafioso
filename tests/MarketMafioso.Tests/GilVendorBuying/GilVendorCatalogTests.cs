namespace MarketMafioso.Tests.GilVendorBuying;

public sealed class GilVendorCatalogTests
{
    [Fact]
    public void Create_ignores_invalid_non_gil_offers()
    {
        var catalog = MarketMafioso.GilVendorBuying.GilVendorCatalog.Create(
        [
            CreateOffer(itemId: 1, unitPriceGil: 0),
            CreateOffer(itemId: 2, unitPriceGil: 8),
        ]);

        Assert.Empty(catalog.FindOffersByItemId(1));
        Assert.Single(catalog.FindOffersByItemId(2));
    }

    [Fact]
    public void TryCreateRequest_fails_when_item_has_no_catalog_offer()
    {
        var catalog = MarketMafioso.GilVendorBuying.GilVendorCatalog.Create([CreateOffer(itemId: 2)]);

        var result = catalog.TryCreateRequest(itemId: 3, quantity: 1);

        Assert.False(result.IsSuccess);
        Assert.Equal("OfferNotCataloged", result.Status);
        Assert.Null(result.Request);
    }

    [Fact]
    public void TryCreateRequest_uses_preferred_vendor_when_available()
    {
        var catalog = MarketMafioso.GilVendorBuying.GilVendorCatalog.Create(
        [
            CreateOffer(itemId: 2, vendorId: 10),
            CreateOffer(itemId: 2, vendorId: 99),
        ]);

        var result = catalog.TryCreateRequest(itemId: 2, quantity: 4, preferredVendorId: 99);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Request);
        Assert.Equal(99u, result.Request.Offer.VendorId);
        Assert.Equal(4u, result.Request.Quantity);
    }

    private static MarketMafioso.GilVendorBuying.GilVendorOffer CreateOffer(
        uint itemId,
        uint vendorId = 10,
        uint unitPriceGil = 8) =>
        new(
            ItemId: itemId,
            ItemName: $"Item {itemId}",
            VendorId: vendorId,
            VendorName: $"Vendor {vendorId}",
            TerritoryId: 129,
            Position: new MarketMafioso.GilVendorBuying.GilVendorPosition(1f, 2f, 3f),
            UnitPriceGil: unitPriceGil,
            ShopItemId: 40_000 + itemId);
}
