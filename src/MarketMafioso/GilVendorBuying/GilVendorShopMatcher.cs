using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.GilVendorBuying;

public static class GilVendorShopMatcher
{
    public static GilVendorShopMatchResult FindMatchingRow(
        GilVendorBuyRequest request,
        IReadOnlyList<GilVendorShopRow> rows)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rows);

        var itemRows = rows.Where(row => row.ItemId == request.Offer.ItemId).ToList();
        if (itemRows.Count == 0)
        {
            return GilVendorShopMatchResult.Fail(
                "OfferNotInLiveShop",
                "The open gil shop does not contain the requested catalog offer.",
                request,
                rows);
        }

        var priceRows = itemRows.Where(row => row.UnitPriceGil == request.Offer.UnitPriceGil).ToList();
        if (priceRows.Count == 0)
        {
            return GilVendorShopMatchResult.Fail(
                "PriceMismatch",
                "The open gil shop contains the requested item, but not at the catalog gil price.",
                request,
                rows);
        }

        var exactShopItem = request.Offer.ShopItemId is { } shopItemId
            ? priceRows.FirstOrDefault(row => row.ShopItemId == shopItemId)
            : null;
        var selected = exactShopItem ?? priceRows.OrderBy(row => row.RowIndex).First();

        return GilVendorShopMatchResult.Success(
            selected,
            "Matched requested gil vendor offer in the live shop.",
            new Dictionary<string, string?>
            {
                ["expectedItemId"] = request.Offer.ItemId.ToString(),
                ["expectedItemName"] = request.Offer.ItemName,
                ["expectedUnitPriceGil"] = request.Offer.UnitPriceGil.ToString(),
                ["matchedRowIndex"] = selected.RowIndex.ToString(),
                ["matchedShopItemId"] = selected.ShopItemId?.ToString(),
                ["liveRowCount"] = rows.Count.ToString(),
            });
    }
}

public sealed record GilVendorShopMatchResult(
    bool IsSuccess,
    string Status,
    string Message,
    GilVendorShopRow? Row,
    IReadOnlyDictionary<string, string?> Details)
{
    public static GilVendorShopMatchResult Success(
        GilVendorShopRow row,
        string message,
        IReadOnlyDictionary<string, string?> details) =>
        new(true, "Ready", message, row, details);

    public static GilVendorShopMatchResult Fail(
        string status,
        string message,
        GilVendorBuyRequest request,
        IReadOnlyList<GilVendorShopRow> rows) =>
        new(
            false,
            status,
            message,
            null,
            new Dictionary<string, string?>
            {
                ["expectedItemId"] = request.Offer.ItemId.ToString(),
                ["expectedItemName"] = request.Offer.ItemName,
                ["expectedUnitPriceGil"] = request.Offer.UnitPriceGil.ToString(),
                ["liveRowCount"] = rows.Count.ToString(),
            });
}
