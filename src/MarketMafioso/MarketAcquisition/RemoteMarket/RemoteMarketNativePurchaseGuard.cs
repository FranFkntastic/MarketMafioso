using System;
using System.Threading;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using MarketMafioso.Automation.Runtime;

namespace MarketMafioso.MarketAcquisition.RemoteMarket;

internal sealed unsafe class RemoteMarketNativePurchaseGuard : IDisposable
{
    private const string ItemSearchResultAddon = "ItemSearchResult";
    private const string ApprovedGameVersion = "2026.07.16.0001.0000";
    private const string PatchContractId = "mmf.remote-market-send-purchase";

    private readonly IAddonLifecycle addonLifecycle;
    private readonly IPluginLog log;
    private readonly Action onBlockedNativePurchase;
    private readonly RemoteMarketPurchaseSessionOwnership ownership = new();
    private Hook<InfoProxyItemSearch.Delegates.SendPurchaseRequestPacket>? hook;
    private long blockedNativePurchaseCount;

    public RemoteMarketNativePurchaseGuard(
        IGameInteropProvider interopProvider,
        IAddonLifecycle addonLifecycle,
        IPluginLog log,
        Action onBlockedNativePurchase)
    {
        ArgumentNullException.ThrowIfNull(interopProvider);
        this.addonLifecycle = addonLifecycle ?? throw new ArgumentNullException(nameof(addonLifecycle));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.onBlockedNativePurchase = onBlockedNativePurchase
            ?? throw new ArgumentNullException(nameof(onBlockedNativePurchase));

        addonLifecycle.RegisterListener(
            AddonEvent.PreReceiveEvent,
            ItemSearchResultAddon,
            OnItemSearchResultPreReceiveEvent);

        try
        {
            var compatibility = GamePatchCompatibilityGate.Evaluate(PatchContractId, ApprovedGameVersion);
            if (!compatibility.IsApproved)
                throw new InvalidOperationException(compatibility.Message);

            var address = InfoProxyItemSearch.Addresses.SendPurchaseRequestPacket.Value;
            if (address == 0)
                throw new InvalidOperationException("InfoProxyItemSearch.SendPurchaseRequestPacket address is unavailable.");

            hook = interopProvider.HookFromAddress<InfoProxyItemSearch.Delegates.SendPurchaseRequestPacket>(
                address,
                SendPurchaseRequestPacketDetour);
            hook.Enable();
        }
        catch (Exception exception)
        {
            hook?.Dispose();
            hook = null;
            log.Error(
                exception,
                "[MarketMafioso] Remote market native-purchase guard is unavailable; remote purchases remain blocked.");
        }
    }

    public bool IsAvailable => hook?.IsEnabled == true;
    public bool IsRemoteSessionActive => ownership.IsRemoteSessionActive;
    public long BlockedNativePurchaseCount => Interlocked.Read(ref blockedNativePurchaseCount);

    public void ObserveRemoteOpen(bool agentWasActive, bool agentIsActive) =>
        ownership.ObserveRemoteOpen(agentWasActive, agentIsActive);

    public void ObserveMarketAgentActive(bool active) => ownership.ObserveMarketAgentActive(active);

    public bool SendOwned(InfoProxyItemSearch* proxy)
    {
        if (proxy == null || hook?.IsEnabled != true)
            return false;

        // MMF owns this call explicitly. Calling the trampoline keeps it out of the
        // detour that rejects unowned client/UI sends during a remote session.
        return hook.Original(proxy);
    }

    private void OnItemSearchResultPreReceiveEvent(AddonEvent _, AddonArgs args)
    {
        if (args is not AddonReceiveEventArgs receiveEvent ||
            !ownership.ShouldBlockNativeListingActivation(receiveEvent.AtkEventType))
        {
            return;
        }

        receiveEvent.PreventOriginal();
        Interlocked.Increment(ref blockedNativePurchaseCount);
        NotifyBlockedNativePurchase();
        log.Information(
            "[MarketMafioso] Blocked native remote market result activation before client confirmation setup. EventType={EventType} EventParam={EventParam}",
            receiveEvent.AtkEventType,
            receiveEvent.EventParam);
    }

    private bool SendPurchaseRequestPacketDetour(InfoProxyItemSearch* proxy)
    {
        if (!ownership.ShouldBlockInterceptedSend)
            return hook!.Original(proxy);

        Interlocked.Increment(ref blockedNativePurchaseCount);
        NotifyBlockedNativePurchase();
        log.Information(
            "[MarketMafioso] Blocked an unowned native market-board purchase request during a remote market session.");
        return false;
    }

    private void NotifyBlockedNativePurchase()
    {
        try
        {
            onBlockedNativePurchase();
        }
        catch (Exception exception)
        {
            log.Error(exception, "[MarketMafioso] Failed to schedule native remote-purchase recovery.");
        }
    }

    public void Dispose()
    {
        addonLifecycle.UnregisterListener(
            AddonEvent.PreReceiveEvent,
            ItemSearchResultAddon,
            OnItemSearchResultPreReceiveEvent);
        hook?.Dispose();
        hook = null;
    }
}

internal sealed class RemoteMarketPurchaseSessionOwnership
{
    public bool IsRemoteSessionActive { get; private set; }

    public bool ShouldBlockInterceptedSend => IsRemoteSessionActive;

    public bool ShouldBlockNativeListingActivation(AddonEventType eventType) =>
        IsRemoteSessionActive &&
        eventType is AddonEventType.ListButtonPress
            or AddonEventType.ListItemClick
            or AddonEventType.ListItemDoubleClick
            or AddonEventType.ListItemSelect;

    public void ObserveRemoteOpen(bool agentWasActive, bool agentIsActive)
    {
        if (!agentWasActive && agentIsActive)
            IsRemoteSessionActive = true;
    }

    public void ObserveMarketAgentActive(bool active)
    {
        if (!active)
            IsRemoteSessionActive = false;
    }
}
