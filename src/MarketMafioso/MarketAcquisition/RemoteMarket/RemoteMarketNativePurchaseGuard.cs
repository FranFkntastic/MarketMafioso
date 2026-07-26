using System;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace MarketMafioso.MarketAcquisition.RemoteMarket;

internal sealed unsafe class RemoteMarketNativePurchaseGuard : IDisposable
{
    private readonly IPluginLog log;
    private readonly Action onBlockedNativePurchase;
    private readonly RemoteMarketPurchaseSessionOwnership ownership = new();
    private Hook<InfoProxyItemSearch.Delegates.SendPurchaseRequestPacket>? hook;
    private long blockedNativePurchaseCount;

    public RemoteMarketNativePurchaseGuard(
        IGameInteropProvider interopProvider,
        IPluginLog log,
        Action onBlockedNativePurchase)
    {
        ArgumentNullException.ThrowIfNull(interopProvider);
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.onBlockedNativePurchase = onBlockedNativePurchase
            ?? throw new ArgumentNullException(nameof(onBlockedNativePurchase));

        try
        {
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

    private bool SendPurchaseRequestPacketDetour(InfoProxyItemSearch* proxy)
    {
        if (!ownership.ShouldBlockInterceptedSend)
            return hook!.Original(proxy);

        Interlocked.Increment(ref blockedNativePurchaseCount);
        try
        {
            onBlockedNativePurchase();
        }
        catch (Exception exception)
        {
            log.Error(exception, "[MarketMafioso] Failed to schedule native remote-purchase recovery.");
        }

        log.Information(
            "[MarketMafioso] Blocked an unowned native market-board purchase request during a remote market session.");
        return false;
    }

    public void Dispose()
    {
        hook?.Dispose();
        hook = null;
    }
}

internal sealed class RemoteMarketPurchaseSessionOwnership
{
    public bool IsRemoteSessionActive { get; private set; }

    public bool ShouldBlockInterceptedSend => IsRemoteSessionActive;

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
