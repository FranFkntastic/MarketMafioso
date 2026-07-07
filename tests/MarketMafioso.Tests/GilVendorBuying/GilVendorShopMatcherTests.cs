namespace MarketMafioso.Tests.GilVendorBuying;

public sealed class GilVendorShopMatcherTests
{
    [Fact]
    public void FindMatchingRow_returns_exact_item_price_and_shop_item_match()
    {
        var request = CreateRequest(shopItemId: 777);
        var rows = new[]
        {
            CreateRow(rowIndex: 0, itemId: 2_002, unitPriceGil: 216, shopItemId: 111),
            CreateRow(rowIndex: 1, itemId: 2_002, unitPriceGil: 216, shopItemId: 777),
        };

        var match = MarketMafioso.GilVendorBuying.GilVendorShopMatcher.FindMatchingRow(request, rows);

        Assert.True(match.IsSuccess);
        Assert.NotNull(match.Row);
        Assert.Equal(1, match.Row.RowIndex);
        Assert.Equal("Matched requested gil vendor offer in the live shop.", match.Message);
    }

    [Fact]
    public void FindMatchingRow_rejects_price_mismatch()
    {
        var request = CreateRequest(unitPriceGil: 216);
        var rows = new[] { CreateRow(rowIndex: 0, itemId: 2_002, unitPriceGil: 999) };

        var match = MarketMafioso.GilVendorBuying.GilVendorShopMatcher.FindMatchingRow(request, rows);

        Assert.False(match.IsSuccess);
        Assert.Equal("PriceMismatch", match.Status);
        Assert.Null(match.Row);
    }

    [Fact]
    public void FindMatchingRow_reports_missing_offer_when_item_is_absent()
    {
        var request = CreateRequest();
        var rows = new[] { CreateRow(rowIndex: 0, itemId: 9_999, unitPriceGil: 216) };

        var match = MarketMafioso.GilVendorBuying.GilVendorShopMatcher.FindMatchingRow(request, rows);

        Assert.False(match.IsSuccess);
        Assert.Equal("OfferNotInLiveShop", match.Status);
    }

    private static MarketMafioso.GilVendorBuying.GilVendorBuyRequest CreateRequest(
        uint unitPriceGil = 216,
        uint? shopItemId = 777)
    {
        var offer = new MarketMafioso.GilVendorBuying.GilVendorOffer(
            ItemId: 2_002,
            ItemName: "Fire Shard",
            VendorId: 10_012,
            VendorName: "Material Supplier",
            TerritoryId: 129,
            Position: new MarketMafioso.GilVendorBuying.GilVendorPosition(12.5f, 0f, -22.25f),
            UnitPriceGil: unitPriceGil,
            ShopItemId: shopItemId);

        return MarketMafioso.GilVendorBuying.GilVendorBuyRequest.Create(offer, 3).Request!;
    }

    private static MarketMafioso.GilVendorBuying.GilVendorShopRow CreateRow(
        int rowIndex,
        uint itemId,
        uint unitPriceGil,
        uint? shopItemId = null) =>
        new(
            RowIndex: rowIndex,
            ItemId: itemId,
            ItemName: $"Item {itemId}",
            UnitPriceGil: unitPriceGil,
            ShopItemId: shopItemId,
            AvailableQuantity: null);
}
