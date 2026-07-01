using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace MarketMafioso.WorkshopPrep;

public interface IVIWIWorkshoppaIpcAdapter
{
    bool IsAvailable { get; }
    bool ClearQueue();
    bool AddQueueItem(uint workshopItemId, int quantity);
}

public sealed record VIWIWorkshoppaIpcResult(bool Success, string Message);

public sealed class VIWIWorkshoppaIpc
{
    private readonly IVIWIWorkshoppaIpcAdapter adapter;

    public VIWIWorkshoppaIpc(IVIWIWorkshoppaIpcAdapter adapter)
    {
        this.adapter = adapter;
    }

    public VIWIWorkshoppaIpcResult SendQueue(IReadOnlyList<WorkshopPrepQueueItem> queue, bool clearExisting)
    {
        if (!adapter.IsAvailable)
            return new(false, "VIWI Workshoppa IPC is not available.");

        if (clearExisting && !adapter.ClearQueue())
            return new(false, "Unable to clear VIWI Workshoppa queue.");

        foreach (var item in queue)
        {
            if (item.WorkshopItemId == 0 || item.Quantity <= 0)
                continue;

            if (!adapter.AddQueueItem(item.WorkshopItemId, item.Quantity))
                return new(false, $"Unable to add workshop item {item.WorkshopItemId} to VIWI.");
        }

        return new(true, "Sent prep queue to VIWI Workshoppa.");
    }
}

public sealed class DalamudVIWIWorkshoppaIpcAdapter : IVIWIWorkshoppaIpcAdapter
{
    private const string InternalName = "VIWI";
    private const string ClearQueueChannel = "VIWI.Workshoppa.ClearQueue";
    private const string AddQueueItemChannel = "VIWI.Workshoppa.AddQueueItem";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;

    public DalamudVIWIWorkshoppaIpcAdapter(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
    }

    public bool IsAvailable => pluginInterface.InstalledPlugins.Any(plugin =>
        plugin.IsLoaded &&
        string.Equals(plugin.InternalName, InternalName, StringComparison.OrdinalIgnoreCase));

    public bool ClearQueue()
    {
        try
        {
            return pluginInterface.GetIpcSubscriber<bool>(ClearQueueChannel).InvokeFunc();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[MarketMafioso] VIWI Workshoppa clear queue IPC failed.");
            return false;
        }
    }

    public bool AddQueueItem(uint workshopItemId, int quantity)
    {
        try
        {
            return pluginInterface.GetIpcSubscriber<uint, int, bool>(AddQueueItemChannel)
                .InvokeFunc(workshopItemId, quantity);
        }
        catch (Exception ex)
        {
            log.Warning(
                ex,
                $"[MarketMafioso] VIWI Workshoppa add queue item IPC failed for {workshopItemId} x{quantity}.");
            return false;
        }
    }
}
