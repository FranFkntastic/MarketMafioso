using System.Collections.Generic;

namespace MarketMafioso.GilVendorBuying;

public interface IGilVendorBuyingGameAdapter
{
    GilVendorBuyResult OpenVendor(GilVendorOffer offer);

    GilVendorShopReadResult ReadOpenGilShopRows();

    GilVendorBuyResult SelectShopRow(GilVendorShopRow row);

    GilVendorBuyResult SetPurchaseQuantity(uint quantity);

    GilVendorBuyResult ConfirmPurchase();

    IReadOnlyDictionary<string, string?> CaptureDiagnostics();
}

public sealed record GilVendorShopReadResult(
    bool IsSuccess,
    string Status,
    string Message,
    IReadOnlyList<GilVendorShopRow> Rows,
    IReadOnlyDictionary<string, string?> Details)
{
    public static GilVendorShopReadResult Success(
        IReadOnlyList<GilVendorShopRow> rows,
        IReadOnlyDictionary<string, string?>? details = null) =>
        new(true, "Ready", "Read ordinary gil shop rows.", rows, details ?? new Dictionary<string, string?>());

    public static GilVendorShopReadResult Fail(
        string status,
        string message,
        IReadOnlyDictionary<string, string?>? details = null) =>
        new(false, status, message, [], details ?? new Dictionary<string, string?>());
}
