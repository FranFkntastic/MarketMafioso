using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.AgentBridge;

namespace MarketMafioso.Squire.Observation;

public enum RenderedEquipmentScanStatus
{
    Idle,
    ReadyToHover,
    Observing,
    Complete,
    Failed,
    Cancelled,
}

public enum RenderedEquipmentSlotObservationStatus
{
    Equipped,
}

public sealed record RenderedEquipmentSlotObservation(
    string PositionKey,
    EquipmentSlot Slot,
    RenderedEquipmentSlotObservationStatus Status,
    RenderedItemDetailObservation? Item);

public sealed record RenderedEquipmentScanProgress(
    RenderedEquipmentScanStatus Status,
    int CompletedSlots,
    int TotalSlots,
    RenderedEquipmentSlotTarget? CurrentTarget,
    IReadOnlyList<RenderedEquipmentSlotObservation> Observations,
    string Diagnostic);

/// <summary>
/// Pure scan state machine. A UI adapter performs the requested hover and feeds rendered snapshots
/// back in; this coordinator never reads inventory, character, agent, or gearset structs.
/// </summary>
public sealed class RenderedCharacterEquipmentScanCoordinator
{
    private readonly TimeSpan settleWindow;
    private readonly TimeSpan observationTimeout;
    private IReadOnlyList<RenderedEquipmentSlotTarget> targets = [];
    private readonly List<RenderedEquipmentSlotObservation> observations = [];
    private int index;
    private DateTimeOffset hoverStartedAt;
    private DateTimeOffset candidateStartedAt;
    private string? candidateSignature;
    private RenderedEquipmentScanStatus status = RenderedEquipmentScanStatus.Idle;
    private string diagnostic = "Equipment scan has not started.";

    public RenderedCharacterEquipmentScanCoordinator(TimeSpan? settleWindow = null, TimeSpan? observationTimeout = null)
    {
        this.settleWindow = settleWindow ?? TimeSpan.FromMilliseconds(300);
        this.observationTimeout = observationTimeout ?? TimeSpan.FromSeconds(2);
    }

    public RenderedEquipmentScanProgress Begin(AgentBridgeRenderedUiSnapshot snapshot)
    {
        var layout = RenderedCharacterEquipmentLayoutParser.Parse(snapshot);
        observations.Clear();
        index = 0;
        candidateSignature = null;
        if (layout.Status != RenderedEquipmentLayoutStatus.Complete)
            return Fail(layout.Diagnostic);

        targets = layout.Slots.Where(value => value.Slot != EquipmentSlot.SoulCrystal).ToArray();
        status = RenderedEquipmentScanStatus.ReadyToHover;
        diagnostic = "Ready to observe the first rendered equipment slot.";
        return Snapshot();
    }

    public RenderedEquipmentScanProgress MarkHoverStarted(string nodePath, DateTimeOffset nowUtc)
    {
        if (status != RenderedEquipmentScanStatus.ReadyToHover || CurrentTarget() is not { } target ||
            !string.Equals(target.NodePath, nodePath, StringComparison.Ordinal))
            return Fail("The UI adapter did not hover the requested rendered equipment slot.");

        hoverStartedAt = nowUtc;
        candidateStartedAt = default;
        candidateSignature = null;
        status = RenderedEquipmentScanStatus.Observing;
        diagnostic = $"Observing {target.PositionKey}.";
        return Snapshot();
    }

    public RenderedEquipmentScanProgress Observe(AgentBridgeRenderedUiSnapshot snapshot, DateTimeOffset nowUtc)
    {
        if (status != RenderedEquipmentScanStatus.Observing || CurrentTarget() is not { } target)
            return Snapshot();

        var layout = RenderedCharacterEquipmentLayoutParser.Parse(snapshot);
        if (layout.Status != RenderedEquipmentLayoutStatus.Complete ||
            !layout.Slots.Any(value => value.PositionKey == target.PositionKey && value.NodePath == target.NodePath))
            return Fail("The rendered Character equipment layout changed during observation.");

        if (nowUtc - hoverStartedAt < settleWindow)
            return Snapshot();

        var item = RenderedItemDetailParser.Parse(snapshot);
        string signature;
        if (item.Status == RenderedItemDetailStatus.Complete)
            signature = $"item:{item.Name}:{item.Quality}:{item.ItemLevel}:{item.EquipLevel}:{item.JobCategory}:{string.Join(',', item.Stats.OrderBy(value => value.Key).Select(value => $"{value.Key}={value.Value}"))}";
        else if (nowUtc - hoverStartedAt >= observationTimeout)
            return Fail($"Rendered Item Detail was not complete for {target.PositionKey}: {item.Diagnostic} The observer will not infer an empty slot from a missing tooltip.");
        else
            return Snapshot();

        if (!string.Equals(candidateSignature, signature, StringComparison.Ordinal))
        {
            candidateSignature = signature;
            candidateStartedAt = nowUtc;
            return Snapshot();
        }
        if (nowUtc - candidateStartedAt < settleWindow)
            return Snapshot();

        observations.Add(new(
            target.PositionKey,
            target.Slot,
            RenderedEquipmentSlotObservationStatus.Equipped,
            item));
        index++;
        candidateSignature = null;
        if (index >= targets.Count)
        {
            status = RenderedEquipmentScanStatus.Complete;
            diagnostic = "Rendered equipment observation is complete.";
        }
        else
        {
            status = RenderedEquipmentScanStatus.ReadyToHover;
            diagnostic = $"Ready to observe {targets[index].PositionKey}.";
        }
        return Snapshot();
    }

    public RenderedEquipmentScanProgress Cancel()
    {
        if (status is RenderedEquipmentScanStatus.Complete or RenderedEquipmentScanStatus.Failed)
            return Snapshot();
        status = RenderedEquipmentScanStatus.Cancelled;
        diagnostic = "Rendered equipment observation was cancelled.";
        return Snapshot();
    }

    public RenderedEquipmentScanProgress Snapshot() =>
        new(status, observations.Count, targets.Count, CurrentTarget(), observations.ToArray(), diagnostic);

    private RenderedEquipmentSlotTarget? CurrentTarget() => index >= 0 && index < targets.Count ? targets[index] : null;

    private RenderedEquipmentScanProgress Fail(string message)
    {
        status = RenderedEquipmentScanStatus.Failed;
        diagnostic = message;
        return Snapshot();
    }
}
