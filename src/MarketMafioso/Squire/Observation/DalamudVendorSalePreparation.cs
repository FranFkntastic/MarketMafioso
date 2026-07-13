using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons.ExcelServices.Sheets;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using MarketMafioso.Automation.Travel;
using ClientGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace MarketMafioso.Squire.Observation;

internal sealed class DalamudVendorSalePreparation
{
    private static readonly TimeSpan TravelTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MovementTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan InteractionTimeout = TimeSpan.FromSeconds(30);
    private readonly ICommandManager commandManager;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly IGameGui gameGui;
    private readonly IFramework framework;
    private readonly HashSet<uint> vendorDataIds;
    private readonly HashSet<string> shopEntryNames;
    private readonly LifestreamIpc lifestream;
    private readonly VNavmeshIpc vnavmesh;
    private bool ownsShop;
    private bool ownsNavigation;

    public bool OwnsUi => ownsShop;

    public DalamudVendorSalePreparation(
        ICommandManager commandManager,
        IObjectTable objectTable,
        ITargetManager targetManager,
        IGameGui gameGui,
        IFramework framework,
        IDataManager dataManager,
        IDalamudPluginInterface pluginInterface,
        IPluginLog log)
    {
        this.commandManager = commandManager;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.gameGui = gameGui;
        this.framework = framework;
        lifestream = new LifestreamIpc(pluginInterface, log);
        vnavmesh = new VNavmeshIpc(new DalamudVNavmeshIpcAdapter(pluginInterface, log));
        vendorDataIds = dataManager.GetExcelSheet<ENpcBase>()
            .Where(row => row.ENpcData.Any(data =>
                data.Is<GilShop>() ||
                (data.Is<PreHandler>() && data.TryGetValue(out PreHandler preHandler) && preHandler.Target.Is<GilShop>()) ||
                (data.Is<TopicSelect>() && data.TryGetValue(out TopicSelect topic) && topic.Shop.Any(shop => shop.Is<GilShop>()))))
            .Select(row => row.RowId)
            .ToHashSet();
        shopEntryNames = dataManager.GetExcelSheet<GilShop>()
            .Select(row => row.Name.ExtractText().Trim())
            .Concat(dataManager.GetExcelSheet<TopicSelect>()
                .Where(row => row.Shop.Any(shop => shop.Is<GilShop>()))
                .Select(row => row.Name.ExtractText().Trim()))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<SquireActionResult> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (await framework.RunOnTick(IsShopReady).ConfigureAwait(false))
            return SquireActionResult.Completed();

        var vendor = await framework.RunOnTick(FindNearestVendor).ConfigureAwait(false);
        if (vendor is null)
        {
            if (!lifestream.IsAvailable)
                return SquireActionResult.Fail("LifestreamUnavailable", "Lifestream is not loaded, so Squire could not travel to a general vendor.");
            if (!commandManager.ProcessCommand("/li gc"))
                return SquireActionResult.Fail("VendorTravelUnavailable", "Lifestream did not accept /li gc for vendor travel.");
            var stateFailed = false;
            var arrived = await WaitUntilAsync(() =>
            {
                if (!lifestream.TryIsBusy(out var busy))
                {
                    stateFailed = true;
                    return false;
                }
                return !busy && FindNearestVendor() is not null;
            }, TravelTimeout, cancellationToken).ConfigureAwait(false);
            if (stateFailed)
                return SquireActionResult.Fail("LifestreamStateUnavailable", "Vendor travel began, but Lifestream busy state could not be observed safely.");
            if (!arrived)
                return SquireActionResult.Fail("VendorTravelTimeout", "Lifestream did not finish where a general vendor could be discovered.");
            vendor = await framework.RunOnTick(FindNearestVendor).ConfigureAwait(false);
        }
        if (vendor is null)
            return SquireActionResult.Fail("VendorUnavailable", "No sheet-classified general vendor is loaded in the current area.");

        if (!IsInInteractionRange(vendor))
        {
            var move = vnavmesh.MoveCloseTo(vendor.Position, 4f);
            if (!move.Success)
                return SquireActionResult.Fail("VendorNavigationUnavailable", move.Message.Replace("market board", "vendor", StringComparison.OrdinalIgnoreCase));
            ownsNavigation = true;
            var reached = await WaitUntilAsync(() =>
            {
                var current = FindVendor(vendor.BaseId);
                return current is not null && IsInInteractionRange(current) && !vnavmesh.IsRunning;
            }, MovementTimeout, cancellationToken).ConfigureAwait(false);
            ownsNavigation = false;
            if (!reached)
            {
                _ = vnavmesh.Stop();
                return SquireActionResult.Fail("VendorNavigationTimeout", "vnavmesh did not finish within interaction range of the selected vendor.");
            }
            vendor = await framework.RunOnTick(() => FindVendor(vendor.BaseId)).ConfigureAwait(false);
        }
        if (vendor is null || !await framework.RunOnTick(() => TryInteract(vendor)).ConfigureAwait(false))
            return SquireActionResult.Fail("VendorInteractionUnavailable", "The selected general vendor could not be targeted and interacted with.");

        ownsShop = true;
        var shopEntrySubmitted = false;
        var ready = await WaitUntilAsync(() =>
        {
            if (IsShopReady())
                return true;
            if (!shopEntrySubmitted && TrySelectShopEntry())
                shopEntrySubmitted = true;
            return false;
        }, InteractionTimeout, cancellationToken).ConfigureAwait(false);
        return ready
            ? SquireActionResult.Completed()
            : SquireActionResult.Fail("VendorShopTimeout", "The selected vendor's normal Shop UI did not become ready.");
    }

    public unsafe void CloseOwnedUi()
    {
        if (ownsNavigation)
        {
            _ = vnavmesh.Stop();
            ownsNavigation = false;
        }
        if (!ownsShop)
            return;
        CloseVisibleUi();
    }

    public unsafe void CloseVisibleUi()
    {
        FirePresentedCloseCallbacks("Shop");
        FirePresentedCloseCallbacks("SelectString");
        FirePresentedCloseCallbacks("SelectIconString");
        ownsShop = false;
    }

    public unsafe void RecoverDiagnosticUi()
    {
        FireAllCloseCallbacks("Shop");
        FireAllCloseCallbacks("SelectString");
        FireAllCloseCallbacks("SelectIconString");
        ownsShop = false;
    }

    private unsafe void FirePresentedCloseCallbacks(string addonName)
    {
        for (var index = 1; index <= 8; index++)
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, index);
            if (IsPresented(addon))
                addon->FireCallbackInt(-1);
        }
    }

    private unsafe void FireAllCloseCallbacks(string addonName)
    {
        for (var index = 1; index <= 8; index++)
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, index);
            if (addon != null)
                addon->FireCallbackInt(-1);
        }
    }

    private IGameObject? FindNearestVendor() => objectTable
        .Where(value => value.ObjectKind == ObjectKind.EventNpc && value.IsTargetable && vendorDataIds.Contains(value.BaseId))
        .OrderBy(value => value.YalmDistanceX + value.YalmDistanceZ)
        .FirstOrDefault();

    private IGameObject? FindVendor(uint baseId) => objectTable
        .Where(value => value.ObjectKind == ObjectKind.EventNpc && value.IsTargetable && value.BaseId == baseId)
        .OrderBy(value => value.YalmDistanceX + value.YalmDistanceZ)
        .FirstOrDefault();

    private static bool IsInInteractionRange(IGameObject vendor) => vendor.YalmDistanceX <= 7 && vendor.YalmDistanceZ <= 7;

    private unsafe bool TryInteract(IGameObject vendor)
    {
        var targetSystem = TargetSystem.Instance();
        if (!IsInInteractionRange(vendor) || targetSystem is null)
            return false;
        targetManager.Target = vendor;
        targetSystem->InteractWithObject((ClientGameObject*)vendor.Address, false);
        return true;
    }

    private unsafe bool TrySelectShopEntry()
    {
        var stringAddon = gameGui.GetAddonByName<AddonSelectString>("SelectString", 1);
        if (stringAddon != null && stringAddon->AtkUnitBase.IsReady && stringAddon->AtkUnitBase.IsVisible &&
            TrySelectPopup(&stringAddon->AtkUnitBase, stringAddon->PopupMenu.PopupMenu))
            return true;
        var iconAddon = gameGui.GetAddonByName<AddonSelectIconString>("SelectIconString", 1);
        return iconAddon != null && iconAddon->AtkUnitBase.IsReady && iconAddon->AtkUnitBase.IsVisible &&
               TrySelectPopup(&iconAddon->AtkUnitBase, iconAddon->PopupMenu.PopupMenu);
    }

    private unsafe bool TrySelectPopup(AtkUnitBase* addon, PopupMenu popup)
    {
        for (var index = 0; index < popup.EntryCount; index++)
        {
            var observed = popup.EntryNames[index].ToString().Trim();
            if (!shopEntryNames.Any(target => Automation.Retainers.RetainerUiAutomationText.IsSelectStringEntryMatch(observed, target)))
                continue;
            addon->FireCallbackInt(index);
            return true;
        }
        return false;
    }

    private unsafe bool IsShopReady()
    {
        // Shop is fully usable in its inventory sell mode even though the
        // generic AtkUnitBase.IsReady flag can remain false.  Visibility is
        // sufficient here because the exact item's context menu is separately
        // required to expose the enabled semantic Sell action.
        for (var index = 1; index <= 8; index++)
            if (IsPresented(gameGui.GetAddonByName<AtkUnitBase>("Shop", index)))
                return true;
        return false;
    }

    private static unsafe bool IsPresented(AtkUnitBase* addon) =>
        addon != null && addon->RootNode != null && addon->RootNode->IsVisible();

    private async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await framework.RunOnTick(predicate).ConfigureAwait(false))
                return true;
            await framework.DelayTicks(6).ConfigureAwait(false);
        }
        return false;
    }
}
