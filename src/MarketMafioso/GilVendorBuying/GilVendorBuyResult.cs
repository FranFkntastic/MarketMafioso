using System.Collections.Generic;

namespace MarketMafioso.GilVendorBuying;

public sealed record GilVendorBuyResult(
    bool IsSuccess,
    string Status,
    string Message,
    IReadOnlyDictionary<string, string?> Details,
    uint PurchasedQuantity = 0,
    ulong SpentGil = 0)
{
    public static GilVendorBuyResult Success(
        string message,
        uint purchasedQuantity,
        ulong spentGil,
        IReadOnlyDictionary<string, string?>? details = null) =>
        new(true, "Complete", message, details ?? new Dictionary<string, string?>(), purchasedQuantity, spentGil);

    public static GilVendorBuyResult Fail(
        string status,
        string message,
        IReadOnlyDictionary<string, string?>? details = null) =>
        new(false, status, message, details ?? new Dictionary<string, string?>());
}
