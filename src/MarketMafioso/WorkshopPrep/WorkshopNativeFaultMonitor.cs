using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dalamud.Game.Agent;
using Dalamud.Game.Agent.AgentArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MarketMafioso.WorkshopPrep;

internal sealed record WorkshopNativeFault(int FaultCount, string? LastOperation, IReadOnlyList<string> AgentEvents)
{
    public string Describe()
    {
        var events = AgentEvents.Count == 0 ? "none recorded" : string.Join(" | ", AgentEvents);
        // Opening a category the FC has no schematic for is the one crash with a
        // known, common cause: say so before the technical detail.
        var hint = LastOperation?.StartsWith("crafting-log.category", StringComparison.Ordinal) == true
            ? "The Free Company probably hasn't unlocked the schematic for this project, so the game's crafting log choked on its category. "
            : string.Empty;
        return hint + $"The game crashed inside a UI event handler {FaultCount} time(s) (Dalamud swallowed it) right after MMF dispatched '{LastOperation ?? "nothing"}'. " +
               $"Workshop assembly halted on the first crash to protect the character. Agent events during MMF dispatch: {events}";
    }
}

// Halts assembly on the first native crash in a game UI handler, and records which
// agent and payload each MMF dispatch reached so the crash can be attributed.
public sealed class WorkshopNativeFaultMonitor : IDisposable
{
    private const int MaxRecordedEvents = 24;
    private const int MaxSummarizedValues = 8;

    private readonly IAgentLifecycle? agentLifecycle;
    private readonly IPluginLog log;
    private readonly DalamudLogFaultTail tail;
    private readonly Queue<string> dispatchAgentEvents = new();
    private string? inFlightOperation;
    private string? lastOperation;
    private bool armed;

    public WorkshopNativeFaultMonitor(IAgentLifecycle? agentLifecycle, IPluginLog log, string? dalamudLogPath)
    {
        this.agentLifecycle = agentLifecycle;
        this.log = log;
        tail = new DalamudLogFaultTail(dalamudLogPath);
    }

    public string Status => tail.IsAvailable
        ? $"armed={armed} log={tail.Path}"
        : $"armed={armed} log=unavailable ({tail.Path ?? "no path"}); crash fuse relies on action guards only";

    public void Arm()
    {
        if (armed)
            return;

        tail.MarkStart();
        dispatchAgentEvents.Clear();
        lastOperation = null;
        inFlightOperation = null;
        if (agentLifecycle != null)
        {
            agentLifecycle.RegisterListener(AgentEvent.PreReceiveEvent, OnPreReceiveEvent);
            agentLifecycle.RegisterListener(AgentEvent.PreReceiveEventWithResult, OnPreReceiveEvent);
        }

        armed = true;
    }

    public void Disarm()
    {
        if (!armed)
            return;

        if (agentLifecycle != null)
        {
            agentLifecycle.UnregisterListener(AgentEvent.PreReceiveEvent, OnPreReceiveEvent);
            agentLifecycle.UnregisterListener(AgentEvent.PreReceiveEventWithResult, OnPreReceiveEvent);
        }

        inFlightOperation = null;
        armed = false;
    }

    public DispatchScope Dispatch(string operation)
    {
        inFlightOperation = operation;
        lastOperation = operation;
        return new DispatchScope(this);
    }

    internal WorkshopNativeFault? Poll()
    {
        if (!armed)
            return null;

        var faults = tail.ReadNewFaults();
        return faults == 0 ? null : new WorkshopNativeFault(faults, lastOperation, dispatchAgentEvents.ToArray());
    }

    public IReadOnlyList<string> RecentDispatchAgentEvents => dispatchAgentEvents.ToArray();

    private unsafe void OnPreReceiveEvent(AgentEvent type, AgentArgs args)
    {
        // Only events raised synchronously by an MMF dispatch: that is the payload
        // the game handler is about to run, and the one to blame if it crashes.
        if (inFlightOperation == null || args is not AgentReceiveEventArgs receive)
            return;

        try
        {
            var values = (AtkValue*)receive.AtkValues;
            var summary = new List<string>();
            for (var index = 0; index < Math.Min((int)receive.ValueCount, MaxSummarizedValues) && values != null; index++)
                summary.Add(Summarize(values[index]));

            var entry = string.Create(
                CultureInfo.InvariantCulture,
                $"{DateTime.Now:HH:mm:ss.fff} op={inFlightOperation} agent={receive.AgentId} kind={receive.EventKind} count={receive.ValueCount} values=[{string.Join(',', summary)}]");
            dispatchAgentEvents.Enqueue(entry);
            while (dispatchAgentEvents.Count > MaxRecordedEvents)
                dispatchAgentEvents.Dequeue();
        }
        catch (Exception ex)
        {
            log.Verbose(ex, "[MarketMafioso] Could not record a workshop agent event.");
        }
    }

    private static unsafe string Summarize(AtkValue value) => value.Type switch
    {
        AtkValueType.Int => value.Int.ToString(CultureInfo.InvariantCulture),
        AtkValueType.UInt => value.UInt.ToString(CultureInfo.InvariantCulture) + "u",
        AtkValueType.Bool => value.Byte != 0 ? "true" : "false",
        AtkValueType.Undefined => "_",
        _ => value.Type.ToString(),
    };

    public void Dispose() => Disarm();

    public readonly struct DispatchScope(WorkshopNativeFaultMonitor monitor) : IDisposable
    {
        public void Dispose() => monitor.inFlightOperation = null;
    }
}
