using System;
using System.Collections.Generic;

namespace MarketMafioso.GilVendorBuying;

public sealed class GilVendorBuyingSession
{
    private readonly IGilVendorBuyingGameAdapter adapter;

    public GilVendorBuyingSession(IGilVendorBuyingGameAdapter adapter)
    {
        this.adapter = adapter;
    }

    public GilVendorBuyingSessionState State { get; private set; } = GilVendorBuyingSessionState.Idle;

    public GilVendorBuyResult Run(GilVendorBuyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        State = GilVendorBuyingSessionState.OpenVendor;
        var open = adapter.OpenVendor(request.Offer);
        if (!open.IsSuccess)
            return Fail(open);

        State = GilVendorBuyingSessionState.ReadGilShop;
        var read = adapter.ReadOpenGilShopRows();
        if (!read.IsSuccess)
            return Fail(GilVendorBuyResult.Fail(read.Status, read.Message, MergeDetails(read.Details)));

        var match = GilVendorShopMatcher.FindMatchingRow(request, read.Rows);
        if (!match.IsSuccess || match.Row == null)
            return Fail(GilVendorBuyResult.Fail(match.Status, match.Message, MergeDetails(match.Details)));

        State = GilVendorBuyingSessionState.SelectOffer;
        var select = adapter.SelectShopRow(match.Row);
        if (!select.IsSuccess)
            return Fail(select);

        State = GilVendorBuyingSessionState.ConfirmQuantity;
        var quantity = adapter.SetPurchaseQuantity(request.Quantity);
        if (!quantity.IsSuccess)
            return Fail(quantity);

        var confirm = adapter.ConfirmPurchase();
        if (!confirm.IsSuccess)
            return Fail(confirm);

        State = GilVendorBuyingSessionState.Complete;
        return confirm;
    }

    private GilVendorBuyResult Fail(GilVendorBuyResult result)
    {
        State = GilVendorBuyingSessionState.Failed;
        return result;
    }

    private IReadOnlyDictionary<string, string?> MergeDetails(IReadOnlyDictionary<string, string?> details)
    {
        var merged = new Dictionary<string, string?>(details);
        foreach (var pair in adapter.CaptureDiagnostics())
            merged.TryAdd(pair.Key, pair.Value);
        return merged;
    }
}

public enum GilVendorBuyingSessionState
{
    Idle,
    OpenVendor,
    ReadGilShop,
    SelectOffer,
    ConfirmQuantity,
    Complete,
    Failed,
}
