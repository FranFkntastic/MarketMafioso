namespace MarketMafioso.Tests.GilVendorBuying;

public sealed class GilVendorBuyRequestTests
{
    [Fact]
    public void Create_rejects_zero_quantity()
    {
        var offer = CreateOffer();

        var result = MarketMafioso.GilVendorBuying.GilVendorBuyRequest.Create(offer, 0);

        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidQuantity", result.Status);
        Assert.Contains("greater than zero", result.Message);
    }

    [Fact]
    public void Create_rejects_total_gil_overflow()
    {
        var offer = CreateOffer(unitPriceGil: uint.MaxValue);

        var result = MarketMafioso.GilVendorBuying.GilVendorBuyRequest.Create(offer, uint.MaxValue);

        Assert.False(result.IsSuccess);
        Assert.Equal("GilTotalOverflow", result.Status);
    }

    [Fact]
    public void Create_returns_request_with_expected_total_gil()
    {
        var offer = CreateOffer(unitPriceGil: 216);

        var result = MarketMafioso.GilVendorBuying.GilVendorBuyRequest.Create(offer, 12);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Request);
        Assert.Equal(12u, result.Request.Quantity);
        Assert.Equal(2_592UL, result.Request.MaxTotalGil);
        Assert.Equal(216u, result.Request.Offer.UnitPriceGil);
    }

    private static MarketMafioso.GilVendorBuying.GilVendorOffer CreateOffer(uint unitPriceGil = 216) =>
        new(
            ItemId: 2_002,
            ItemName: "Fire Shard",
            VendorId: 10_012,
            VendorName: "Material Supplier",
            TerritoryId: 129,
            Position: new MarketMafioso.GilVendorBuying.GilVendorPosition(12.5f, 0f, -22.25f),
            UnitPriceGil: unitPriceGil,
            ShopItemId: 50_001);
}
