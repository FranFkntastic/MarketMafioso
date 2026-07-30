using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace MarketMafioso.Automation.Travel;

public interface IVNavmeshIpcAdapter
{
    bool IsAvailable { get; }
    bool IsReady();
    bool IsRunning();
    bool MoveCloseTo(Vector3 destination, float range);
    bool MoveDirect(IReadOnlyList<Vector3> waypoints);
    bool SetMovementAllowed(bool allowed);
    bool Stop();
}

public sealed record VNavmeshMoveResult(bool Success, string Message);
public sealed record VNavmeshStopResult(bool Success, string Message);

public sealed class VNavmeshIpc
{
    private readonly IVNavmeshIpcAdapter adapter;

    public VNavmeshIpc(IVNavmeshIpcAdapter adapter)
    {
        this.adapter = adapter;
    }

    public bool IsReady => adapter.IsAvailable && adapter.IsReady();
    public bool IsRunning => adapter.IsAvailable && adapter.IsRunning();

    public VNavmeshMoveResult MoveCloseTo(Vector3 destination, float range)
    {
        if (!adapter.IsAvailable)
            return new(false, "vnavmesh is not loaded; open the market board manually.");

        if (!adapter.IsReady())
            return new(false, "vnavmesh is not ready; open the market board manually.");

        return adapter.MoveCloseTo(destination, range)
            ? new(true, "vnavmesh accepted the market board approach request.")
            : new(false, "vnavmesh rejected the market board approach request.");
    }

    public VNavmeshMoveResult MoveDirect(Vector3 start, Vector3 destination)
    {
        if (!adapter.IsAvailable)
            return new(false, "vnavmesh is not loaded; the direct path was not queued.");

        if (!adapter.IsReady())
            return new(false, "vnavmesh is not ready; the direct path was not queued.");

        return adapter.MoveDirect([start, destination])
            ? new(true, "vnavmesh accepted the route-owned direct path.")
            : new(false, "vnavmesh rejected the route-owned direct path.");
    }

    public bool SetMovementAllowed(bool allowed) =>
        adapter.IsAvailable && adapter.SetMovementAllowed(allowed);

    public VNavmeshStopResult Stop()
    {
        if (!adapter.IsAvailable)
            return new(false, "vnavmesh is unavailable; its path could not be stopped.");

        return adapter.Stop()
            ? new(true, "vnavmesh accepted the route-owned path stop request.")
            : new(false, "vnavmesh rejected the route-owned path stop request.");
    }
}

public sealed class DalamudVNavmeshIpcAdapter : IVNavmeshIpcAdapter
{
    private const string InternalName = "vnavmesh";
    private const string NavIsReadyChannel = "vnavmesh.Nav.IsReady";
    private const string PathIsRunningChannel = "vnavmesh.Path.IsRunning";
    private const string PathStopChannel = "vnavmesh.Path.Stop";
    private const string PathMoveToChannel = "vnavmesh.Path.MoveTo";
    private const string PathSetMovementAllowedChannel = "vnavmesh.Path.SetMovementAllowed";
    private const string MoveCloseToChannel = "vnavmesh.SimpleMove.PathfindAndMoveCloseTo";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;

    public DalamudVNavmeshIpcAdapter(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
    }

    public bool IsAvailable => pluginInterface.InstalledPlugins.Any(plugin =>
        plugin.IsLoaded &&
        string.Equals(plugin.InternalName, InternalName, StringComparison.OrdinalIgnoreCase));

    public bool IsReady()
    {
        try
        {
            return pluginInterface.GetIpcSubscriber<bool>(NavIsReadyChannel).InvokeFunc();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[MarketMafioso] vnavmesh readiness IPC failed.");
            return false;
        }
    }

    public bool IsRunning()
    {
        try
        {
            return pluginInterface.GetIpcSubscriber<bool>(PathIsRunningChannel).InvokeFunc();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[MarketMafioso] vnavmesh running-state IPC failed.");
            return false;
        }
    }

    public bool MoveCloseTo(Vector3 destination, float range)
    {
        try
        {
            return pluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>(MoveCloseToChannel)
                .InvokeFunc(destination, false, range);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[MarketMafioso] vnavmesh move-close IPC failed.");
            return false;
        }
    }

    public bool MoveDirect(IReadOnlyList<Vector3> waypoints)
    {
        try
        {
            pluginInterface.GetIpcSubscriber<List<Vector3>, bool, object>(PathMoveToChannel)
                .InvokeAction([.. waypoints], false);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[MarketMafioso] vnavmesh direct-path IPC failed.");
            return false;
        }
    }

    public bool SetMovementAllowed(bool allowed)
    {
        try
        {
            pluginInterface.GetIpcSubscriber<bool, object>(PathSetMovementAllowedChannel).InvokeAction(allowed);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[MarketMafioso] vnavmesh movement-gate IPC failed.");
            return false;
        }
    }

    public bool Stop()
    {
        try
        {
            pluginInterface.GetIpcSubscriber<object>(PathStopChannel).InvokeAction();
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[MarketMafioso] vnavmesh stop IPC failed.");
            return false;
        }
    }
}
