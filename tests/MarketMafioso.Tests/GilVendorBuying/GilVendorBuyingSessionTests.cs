namespace MarketMafioso.Tests.GilVendorBuying;

public sealed class GilVendorBuyingSessionTests
{
    [Fact]
    public void Run_completes_one_catalog_request()
    {
        var request = CreateRequest(quantity: 5, unitPriceGil: 12);
        var adapter = new FakeAdapter
        {
            OpenVendorResult = MarketMafioso.GilVendorBuying.GilVendorBuyResult.Success("Vendor opened.", 0, 0),
            Rows =
            [
                new MarketMafioso.GilVendorBuying.GilVendorShopRow(0, request.Offer.ItemId, request.Offer.ItemName, 12, request.Offer.ShopItemId, null),
            ],
            SelectRowResult = MarketMafioso.GilVendorBuying.GilVendorBuyResult.Success("Row selected.", 0, 0),
            SetQuantityResult = MarketMafioso.GilVendorBuying.GilVendorBuyResult.Success("Quantity set.", 0, 0),
            ConfirmResult = MarketMafioso.GilVendorBuying.GilVendorBuyResult.Success("Purchase confirmed.", 5, 60),
        };

        var session = new MarketMafioso.GilVendorBuying.GilVendorBuyingSession(adapter);

        var result = session.Run(request);

        Assert.True(result.IsSuccess);
        Assert.Equal("Complete", result.Status);
        Assert.Equal(MarketMafioso.GilVendorBuying.GilVendorBuyingSessionState.Complete, session.State);
        Assert.Equal(5u, result.PurchasedQuantity);
        Assert.Equal(60UL, result.SpentGil);
        Assert.Equal(1, adapter.OpenVendorCalls);
        Assert.Equal(1, adapter.ReadRowsCalls);
        Assert.Equal(1, adapter.SelectRowCalls);
        Assert.Equal(1, adapter.SetQuantityCalls);
        Assert.Equal(1, adapter.ConfirmCalls);
    }

    [Fact]
    public void Run_fails_when_shop_is_not_readable()
    {
        var request = CreateRequest();
        var adapter = new FakeAdapter
        {
            OpenVendorResult = MarketMafioso.GilVendorBuying.GilVendorBuyResult.Success("Vendor opened.", 0, 0),
            ReadRowsResult = MarketMafioso.GilVendorBuying.GilVendorShopReadResult.Fail("GilShopNotOpen", "Normal gil shop is not open."),
        };

        var session = new MarketMafioso.GilVendorBuying.GilVendorBuyingSession(adapter);

        var result = session.Run(request);

        Assert.False(result.IsSuccess);
        Assert.Equal("GilShopNotOpen", result.Status);
        Assert.Equal(MarketMafioso.GilVendorBuying.GilVendorBuyingSessionState.Failed, session.State);
        Assert.Equal(0, adapter.SelectRowCalls);
    }

    [Fact]
    public void Run_fails_when_live_row_price_mismatches_catalog()
    {
        var request = CreateRequest(unitPriceGil: 12);
        var adapter = new FakeAdapter
        {
            OpenVendorResult = MarketMafioso.GilVendorBuying.GilVendorBuyResult.Success("Vendor opened.", 0, 0),
            Rows =
            [
                new MarketMafioso.GilVendorBuying.GilVendorShopRow(0, request.Offer.ItemId, request.Offer.ItemName, 99, request.Offer.ShopItemId, null),
            ],
        };

        var session = new MarketMafioso.GilVendorBuying.GilVendorBuyingSession(adapter);

        var result = session.Run(request);

        Assert.False(result.IsSuccess);
        Assert.Equal("PriceMismatch", result.Status);
        Assert.Equal(0, adapter.SelectRowCalls);
    }

    private static MarketMafioso.GilVendorBuying.GilVendorBuyRequest CreateRequest(
        uint quantity = 1,
        uint unitPriceGil = 12)
    {
        var offer = new MarketMafioso.GilVendorBuying.GilVendorOffer(
            ItemId: 3_111,
            ItemName: "Bronze Ingot",
            VendorId: 88,
            VendorName: "Tools Supplier",
            TerritoryId: 129,
            Position: new MarketMafioso.GilVendorBuying.GilVendorPosition(1f, 2f, 3f),
            UnitPriceGil: unitPriceGil,
            ShopItemId: 44_444);
        return MarketMafioso.GilVendorBuying.GilVendorBuyRequest.Create(offer, quantity).Request!;
    }

    private sealed class FakeAdapter : MarketMafioso.GilVendorBuying.IGilVendorBuyingGameAdapter
    {
        public int OpenVendorCalls { get; private set; }
        public int ReadRowsCalls { get; private set; }
        public int SelectRowCalls { get; private set; }
        public int SetQuantityCalls { get; private set; }
        public int ConfirmCalls { get; private set; }

        public MarketMafioso.GilVendorBuying.GilVendorBuyResult OpenVendorResult { get; init; } =
            MarketMafioso.GilVendorBuying.GilVendorBuyResult.Success("Vendor opened.", 0, 0);

        public IReadOnlyList<MarketMafioso.GilVendorBuying.GilVendorShopRow> Rows { get; init; } = [];

        public MarketMafioso.GilVendorBuying.GilVendorShopReadResult? ReadRowsResult { get; init; }

        public MarketMafioso.GilVendorBuying.GilVendorBuyResult SelectRowResult { get; init; } =
            MarketMafioso.GilVendorBuying.GilVendorBuyResult.Success("Row selected.", 0, 0);

        public MarketMafioso.GilVendorBuying.GilVendorBuyResult SetQuantityResult { get; init; } =
            MarketMafioso.GilVendorBuying.GilVendorBuyResult.Success("Quantity set.", 0, 0);

        public MarketMafioso.GilVendorBuying.GilVendorBuyResult ConfirmResult { get; init; } =
            MarketMafioso.GilVendorBuying.GilVendorBuyResult.Success("Purchase confirmed.", 0, 0);

        public MarketMafioso.GilVendorBuying.GilVendorBuyResult OpenVendor(MarketMafioso.GilVendorBuying.GilVendorOffer offer)
        {
            OpenVendorCalls++;
            return OpenVendorResult;
        }

        public MarketMafioso.GilVendorBuying.GilVendorShopReadResult ReadOpenGilShopRows()
        {
            ReadRowsCalls++;
            return ReadRowsResult ?? MarketMafioso.GilVendorBuying.GilVendorShopReadResult.Success(Rows);
        }

        public MarketMafioso.GilVendorBuying.GilVendorBuyResult SelectShopRow(MarketMafioso.GilVendorBuying.GilVendorShopRow row)
        {
            SelectRowCalls++;
            return SelectRowResult;
        }

        public MarketMafioso.GilVendorBuying.GilVendorBuyResult SetPurchaseQuantity(uint quantity)
        {
            SetQuantityCalls++;
            return SetQuantityResult;
        }

        public MarketMafioso.GilVendorBuying.GilVendorBuyResult ConfirmPurchase()
        {
            ConfirmCalls++;
            return ConfirmResult;
        }

        public IReadOnlyDictionary<string, string?> CaptureDiagnostics() =>
            new Dictionary<string, string?> { ["adapter"] = "fake" };
    }
}
