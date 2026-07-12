using System;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace MarketMafioso.Automation.Travel;

public sealed class LifestreamIpc
{
    private const string InternalName = "Lifestream";
    private const string IsBusyChannel = "Lifestream.IsBusy";
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;

    public LifestreamIpc(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
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
}
