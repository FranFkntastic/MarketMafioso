using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Diagnostics;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MarketMafioso.Diagnostics;

public sealed class UiStateCaptureService : IDisposable
{
    private static readonly AddonEvent[] CapturedAddonEvents =
    [
        AddonEvent.PostSetup,
        AddonEvent.PostRefresh,
        AddonEvent.PostReceiveEvent,
        AddonEvent.PostShow,
        AddonEvent.PostHide,
        AddonEvent.PreFinalize,
    ];

    private readonly IAddonLifecycle addonLifecycle;
    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly string directory;
    private readonly UiStateRecorder recorder = new();
    private readonly HashSet<string> observedAddons = new(StringComparer.Ordinal);
    private DateTimeOffset nextStateSampleUtc;

    public UiStateCaptureService(IAddonLifecycle addonLifecycle, IFramework framework, ICondition condition, string directory)
    {
        this.addonLifecycle = addonLifecycle;
        this.framework = framework;
        this.condition = condition;
        this.directory = directory;
    }

    public bool IsRecording => recorder.IsRecording;
    public string Status { get; private set; } = "UI-state recorder is idle.";
    public string? LastCapturePath { get; private set; }
    public int EventCount => recorder.Snapshot().Events.Count;

    public void Start(string name = "manual-ui-transaction")
    {
        if (IsRecording)
            return;
        observedAddons.Clear();
        recorder.Start(name, DateTimeOffset.UtcNow);
        foreach (var addonEvent in CapturedAddonEvents)
            addonLifecycle.RegisterListener(addonEvent, OnAddonEvent);
        framework.Update += OnFrameworkUpdate;
        nextStateSampleUtc = DateTimeOffset.MinValue;
        Status = $"Recording {name}. Perform the UI transaction, then finish capture.";
    }

    public string? Stop()
    {
        if (!IsRecording)
            return LastCapturePath;
        Unregister();
        var session = recorder.Stop(DateTimeOffset.UtcNow);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"ui-state-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.jsonl");
        using (var writer = new StreamWriter(path, false))
        {
            writer.WriteLine(JsonSerializer.Serialize(new { type = "manifest", session.SessionId, session.Name, session.StartedAtUtc, session.StoppedAtUtc, session.Truncated }));
            foreach (var value in session.Events)
                writer.WriteLine(JsonSerializer.Serialize(new { type = "event", value.Sequence, value.TimestampUtc, kind = value.Kind.ToString(), value.Source, value.Name, value.Details }));
        }
        LastCapturePath = path;
        Status = $"Captured {session.Events.Count:N0} event(s): {Path.GetFileName(path)}";
        return path;
    }

    public void Mark(string name, IReadOnlyDictionary<string, string?>? details = null) =>
        recorder.Record(DateTimeOffset.UtcNow, UiStateEventKind.Marker, "plugin", name, details);

    private unsafe void OnAddonEvent(AddonEvent type, AddonArgs args)
    {
        observedAddons.Add(args.AddonName);
        var details = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["addon"] = args.AddonName,
            ["addonArgsType"] = args.Type.ToString(),
            ["address"] = FormatPointer(args.Addon.Address),
        };
        var kind = UiStateEventKind.AddonLifecycle;
        if (args is AddonReceiveEventArgs received)
        {
            kind = UiStateEventKind.AddonReceiveEvent;
            details["atkEventType"] = received.AtkEventType.ToString();
            details["eventParam"] = received.EventParam.ToString(CultureInfo.InvariantCulture);
            details["atkEvent"] = FormatPointer(received.AtkEvent);
            details["atkEventData"] = FormatPointer(received.AtkEventData);
        }
        else if (args is AddonRefreshArgs refreshed)
        {
            details["atkValueCount"] = refreshed.AtkValueCount.ToString(CultureInfo.InvariantCulture);
            details["atkValues"] = DescribeAtkValues((AtkValue*)refreshed.AtkValues, refreshed.AtkValueCount);
        }
        recorder.Record(DateTimeOffset.UtcNow, kind, "dalamud-addon-lifecycle", type.ToString(), details);
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < nextStateSampleUtc)
            return;
        nextStateSampleUtc = now.AddMilliseconds(100);
        recorder.RecordStateChange(now, "framework", CaptureState());
    }

    private unsafe IReadOnlyDictionary<string, string?> CaptureState()
    {
        var state = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var addonName in observedAddons.Order(StringComparer.Ordinal))
        {
            var addon = Plugin.GameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
            state[$"addon.{addonName}"] = addon == null
                ? "missing"
                : $"id={addon->Id},ready={addon->IsReady},visible={addon->IsVisible},focus={FormatPointer(addon->FocusNode)}";
        }

        var activeConditions = Enum.GetValues<ConditionFlag>().Where(flag => condition[flag]).Select(flag => flag.ToString());
        state["conditions.active"] = string.Join(",", activeConditions);
        var actionManager = ActionManager.Instance();
        state["animationLock"] = actionManager == null ? "unavailable" : actionManager->AnimationLock.ToString("R", CultureInfo.InvariantCulture);

        var stage = AtkStage.Instance();
        state["focus.stage"] = stage == null ? "unavailable" : FormatPointer(stage->GetFocus());
        var agentModule = AgentModule.Instance();
        if (agentModule != null)
        {
            var activeAgents = new List<string>();
            foreach (var id in Enum.GetValues<AgentId>())
            {
                var agent = agentModule->GetAgentByInternalId(id);
                if (agent != null && agent->IsAgentActive())
                    activeAgents.Add($"{id}:{agent->GetAddonId()}");
            }
            state["agents.active"] = string.Join(",", activeAgents);
        }
        return state;
    }

    private static unsafe string DescribeAtkValues(AtkValue* values, uint count)
    {
        if (values == null || count == 0)
            return string.Empty;
        var result = new List<string>();
        for (var index = 0u; index < Math.Min(count, 64u); index++)
        {
            var value = values[index];
            result.Add(value.Type switch
            {
                AtkValueType.Int => $"{index}:Int:{value.Int}",
                AtkValueType.UInt => $"{index}:UInt:{value.UInt}",
                AtkValueType.Bool => $"{index}:Bool:{value.Byte != 0}",
                AtkValueType.Float => $"{index}:Float:{value.Float.ToString("R", CultureInfo.InvariantCulture)}",
                AtkValueType.String or AtkValueType.ManagedString or AtkValueType.WideString or AtkValueType.ConstString => $"{index}:{value.Type}:redacted-length={value.GetValueAsString().Length}",
                _ => $"{index}:{value.Type}",
            });
        }
        return string.Join("|", result);
    }

    private void Unregister()
    {
        framework.Update -= OnFrameworkUpdate;
        foreach (var addonEvent in CapturedAddonEvents)
            addonLifecycle.UnregisterListener(addonEvent, OnAddonEvent);
    }

    public void Dispose()
    {
        if (IsRecording)
            Stop();
        else
            Unregister();
    }

    private static string FormatPointer(nint value) => value == 0 ? "null" : $"0x{value:X}";
    private static unsafe string FormatPointer(void* value) => value == null ? "null" : $"0x{(nuint)value:X}";
}
