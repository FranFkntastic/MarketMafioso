using System;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Travel;

namespace MarketMafioso.Automation.Travel;

public sealed class LifestreamIpc
{
    private const string InternalName = "Lifestream";
    private const string IsBusyChannel = "Lifestream.IsBusy";
    private const string TeleportChannel = "Lifestream.Teleport";
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly DalamudLifestreamObjectInteractor objectInteractor;

    public LifestreamIpc(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        objectInteractor = new(pluginInterface);
    }

    public bool IsAvailable => pluginInterface.InstalledPlugins.Any(plugin =>
        plugin.IsLoaded && string.Equals(plugin.InternalName, InternalName, StringComparison.OrdinalIgnoreCase));

    public bool TryIsBusy(out bool isBusy)
    {
        try
        {
            isBusy = pluginInterface.GetIpcSubscriber<bool>(IsBusyChannel).InvokeFunc();
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[MarketMafioso] Lifestream busy-state IPC failed.");
            isBusy = false;
            return false;
        }
    }

    public bool TryEnqueueObjectInteraction(uint dataId)
    {
        var result = objectInteractor.TryEnqueue(
            dataId,
            approachDistance: 3.5f,
            exportedName: "MarketMafioso bridge object interaction");
        if (result.Success)
            return true;

        log.Warning($"[MarketMafioso] Lifestream object-interaction IPC failed ({result.Code}): {result.Message}");
        return false;
    }

    public bool TryTeleport(uint aetheryteId)
    {
        if (aetheryteId == 0 || !IsAvailable)
            return false;
        if (!TryIsBusy(out var isBusy) || isBusy)
            return false;

        try
        {
            return pluginInterface
                .GetIpcSubscriber<uint, byte, bool>(TeleportChannel)
                .InvokeFunc(aetheryteId, 0);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[MarketMafioso] Lifestream teleport IPC failed.");
            return false;
        }
    }
}
