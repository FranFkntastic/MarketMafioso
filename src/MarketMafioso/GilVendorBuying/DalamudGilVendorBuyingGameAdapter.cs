using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MarketMafioso.GilVendorBuying;

public sealed class DalamudGilVendorBuyingGameAdapter : IGilVendorBuyingGameAdapter
{
    private const string ShopAddon = "Shop";
    private const string InputNumericAddon = "InputNumeric";
    private const string SelectYesNoAddon = "SelectYesno";

    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly Dictionary<string, string?> diagnostics = new();

    public DalamudGilVendorBuyingGameAdapter(IGameGui gameGui, IPluginLog log)
    {
        this.gameGui = gameGui;
        this.log = log;
    }

    public GilVendorBuyResult OpenVendor(GilVendorOffer offer)
    {
        diagnostics.Clear();
        diagnostics["itemId"] = offer.ItemId.ToString();
        diagnostics["itemName"] = offer.ItemName;
        diagnostics["vendorId"] = offer.VendorId.ToString();
        diagnostics["vendorName"] = offer.VendorName;
        diagnostics["territoryId"] = offer.TerritoryId.ToString();
        diagnostics["unitPriceGil"] = offer.UnitPriceGil.ToString();
        diagnostics["shopItemId"] = offer.ShopItemId?.ToString();
        AddAddonDiagnostics("openVendor");

        return GilVendorBuyResult.Fail(
            "VendorOpenFailed",
            "Ordinary gil vendor opening is not wired to live game interaction yet.",
            CaptureDiagnostics());
    }

    public unsafe GilVendorShopReadResult ReadOpenGilShopRows()
    {
        AddAddonDiagnostics("readShop");

        var shop = gameGui.GetAddonByName<AtkUnitBase>(ShopAddon, 1);
        if (!IsAddonReady(shop))
        {
            return GilVendorShopReadResult.Fail(
                "GilShopNotOpen",
                "No ordinary gil shop addon is open and ready.",
                CaptureDiagnostics());
        }

        log.Verbose("[MarketMafioso] Ordinary gil shop addon is open, but shop row extraction is not implemented yet.");
        return GilVendorShopReadResult.Fail(
            "GilShopRowsUnavailable",
            "Ordinary gil shop rows are not readable by the adapter yet.",
            CaptureDiagnostics());
    }

    public GilVendorBuyResult SelectShopRow(GilVendorShopRow row)
    {
        diagnostics["selectRowIndex"] = row.RowIndex.ToString();
        diagnostics["selectItemId"] = row.ItemId.ToString();
        diagnostics["selectItemName"] = row.ItemName;
        diagnostics["selectUnitPriceGil"] = row.UnitPriceGil.ToString();
        diagnostics["selectShopItemId"] = row.ShopItemId?.ToString();
        AddAddonDiagnostics("selectRow");

        return GilVendorBuyResult.Fail(
            "SelectionUnavailable",
            "Ordinary gil shop row selection is not wired to live game interaction yet.",
            CaptureDiagnostics());
    }

    public GilVendorBuyResult SetPurchaseQuantity(uint quantity)
    {
        diagnostics["quantity"] = quantity.ToString();
        AddAddonDiagnostics("setQuantity");

        return GilVendorBuyResult.Fail(
            "QuantityRejected",
            "Ordinary gil shop quantity entry is not wired to live game interaction yet.",
            CaptureDiagnostics());
    }

    public GilVendorBuyResult ConfirmPurchase()
    {
        AddAddonDiagnostics("confirmPurchase");

        return GilVendorBuyResult.Fail(
            "ConfirmationUnavailable",
            "Ordinary gil shop purchase confirmation is not wired to live game interaction yet.",
            CaptureDiagnostics());
    }

    public IReadOnlyDictionary<string, string?> CaptureDiagnostics() =>
        new Dictionary<string, string?>(diagnostics);

    private unsafe void AddAddonDiagnostics(string prefix)
    {
        diagnostics[$"{prefix}:{ShopAddon}"] = DescribeAddon(ShopAddon);
        diagnostics[$"{prefix}:{InputNumericAddon}"] = DescribeAddon(InputNumericAddon);
        diagnostics[$"{prefix}:{SelectYesNoAddon}"] = DescribeAddon(SelectYesNoAddon);
    }

    private unsafe string DescribeAddon(string addonName)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
        if (addon == null)
            return "missing";

        return $"ready={addon->IsReady},visible={addon->IsVisible}";
    }

    private static unsafe bool IsAddonReady(AtkUnitBase* addon) =>
        addon != null && addon->IsReady && addon->IsVisible;
}
