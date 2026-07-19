using System;
using System.Collections.Generic;
using System.Linq;
using MarketMafioso.AgentBridge;

namespace MarketMafioso.Squire.Observation;

public enum RenderedArmouryDifferentialStatus
{
    Idle,
    Working,
    Complete,
    Failed,
    Cancelled,
}

public sealed record RenderedArmouryDifferentialMismatch(
    string Container,
    int SlotIndex,
    string StructIdentity,
    string RenderedIdentity,
    string Reason);

public sealed record RenderedArmouryDifferentialProgress(
    RenderedArmouryDifferentialStatus Status,
    int CompletedSlots,
    int TotalSlots,
    string CurrentContainer,
    int MatchCount,
    IReadOnlyList<RenderedArmouryDifferentialMismatch> Mismatches,
    IReadOnlyList<string> OccupancyConflicts,
    string Diagnostic);

/// <summary>
/// Pure differential-proof bookkeeping. For every struct-occupied armoury slot, the
/// rendered tooltip identity (resolved to an item id and quality) must equal the struct
/// read. Any disagreement fails the proof: the struct read is not ownership authority.
/// </summary>
public sealed class RenderedArmouryDifferentialCoordinator
{
    private static readonly string[] ContainerOrder =
    [
        "ArmoryMainHand",
        "ArmoryOffHand",
        "ArmoryHead",
        "ArmoryBody",
        "ArmoryHands",
        "ArmoryLegs",
        "ArmoryFeets",
        "ArmoryEar",
        "ArmoryNeck",
        "ArmoryWrist",
        "ArmoryRings",
        "ArmorySoulCrystal",
    ];

    private readonly List<(string Container, int SlotIndex, uint ItemId, bool IsHighQuality)> structSlots = [];
    private readonly List<RenderedArmouryDifferentialMismatch> mismatches = [];
    private readonly List<string> occupancyConflicts = [];
    private readonly HashSet<string> comparedKeys = new(StringComparer.Ordinal);
    private RenderedArmouryDifferentialStatus status = RenderedArmouryDifferentialStatus.Idle;
    private string diagnostic = "The armoury differential proof has not started.";
    private int cursor;
    private int matchCount;

    public void Begin(IReadOnlyList<AgentBridgeInventoryStructItem> structBaseline)
    {
        ArgumentNullException.ThrowIfNull(structBaseline);
        structSlots.Clear();
        mismatches.Clear();
        occupancyConflicts.Clear();
        comparedKeys.Clear();
        structSlots.AddRange(structBaseline
            .Where(item => ContainerOrder.Contains(item.Container, StringComparer.Ordinal))
            .Select(item => (item.Container, item.SlotIndex, item.ItemId, item.IsHighQuality))
            .OrderBy(item => Array.IndexOf(ContainerOrder, item.Container))
            .ThenBy(item => item.SlotIndex));
        cursor = 0;
        matchCount = 0;
        status = structSlots.Count == 0 ? RenderedArmouryDifferentialStatus.Failed : RenderedArmouryDifferentialStatus.Working;
        diagnostic = structSlots.Count == 0
            ? "The struct baseline contains no armoury items; the differential proof cannot run."
            : $"Comparing {structSlots.Count} struct-occupied armoury slots against rendered tooltips.";
    }

    public (string Container, int SlotIndex, uint ItemId, bool IsHighQuality)? Current =>
        status == RenderedArmouryDifferentialStatus.Working && cursor < structSlots.Count
            ? structSlots[cursor]
            : null;

    public RenderedArmouryDifferentialProgress RecordRenderedObservation(
        string container, int slotIndex, uint? renderedItemId, bool? renderedIsHighQuality, string? renderedName)
    {
        if (Current is not { } current || current.Container != container || current.SlotIndex != slotIndex)
            return Fail($"The differential observation sequence drifted at {container}:{slotIndex}.");
        if (renderedItemId is null || renderedIsHighQuality is null || string.IsNullOrWhiteSpace(renderedName))
            mismatches.Add(new(container, slotIndex, Identity(current.ItemId, current.IsHighQuality), "nothing rendered",
                "No rendered tooltip identity was produced for a struct-occupied slot."));
        else if (renderedItemId != current.ItemId || renderedIsHighQuality != current.IsHighQuality)
            mismatches.Add(new(container, slotIndex, Identity(current.ItemId, current.IsHighQuality), Identity(renderedItemId.Value, renderedIsHighQuality.Value),
                $"Rendered identity '{renderedName}' disagrees with the struct read."));
        else
            matchCount++;
        comparedKeys.Add($"{container}:{slotIndex}");
        cursor++;
        if (cursor < structSlots.Count)
        {
            diagnostic = $"Compared {cursor} of {structSlots.Count} armoury slots.";
            return Snapshot();
        }
        status = mismatches.Count == 0 && occupancyConflicts.Count == 0
            ? RenderedArmouryDifferentialStatus.Complete
            : RenderedArmouryDifferentialStatus.Failed;
        diagnostic = mismatches.Count == 0 && occupancyConflicts.Count == 0
            ? $"Differential proof passed: all {matchCount} struct-occupied armoury slots render the same identity and quality."
            : $"Differential proof failed with {mismatches.Count} identity mismatch(es) and {occupancyConflicts.Count} occupancy conflict(s).";
        return Snapshot();
    }

    public RenderedArmouryDifferentialProgress RecordOccupancyCount(string container, int structCount, int renderedIconCount)
    {
        if (structCount != renderedIconCount)
            occupancyConflicts.Add($"{container}: struct shows {structCount} occupied slot(s) but the rendered tab shows {renderedIconCount} icon(s).");
        return Snapshot();
    }

    public int StructCountFor(string container) =>
        structSlots.Count(value => value.Container == container);

    public RenderedArmouryDifferentialProgress Fail(string message) => FailCore(message);

    public RenderedArmouryDifferentialProgress Cancel()
    {
        if (status is RenderedArmouryDifferentialStatus.Complete or RenderedArmouryDifferentialStatus.Failed)
            return Snapshot();
        status = RenderedArmouryDifferentialStatus.Cancelled;
        diagnostic = "The armoury differential proof was cancelled.";
        return Snapshot();
    }

    public RenderedArmouryDifferentialProgress Snapshot() => new(
        status,
        cursor,
        structSlots.Count,
        Current?.Container ?? string.Empty,
        matchCount,
        mismatches.ToArray(),
        occupancyConflicts.ToArray(),
        diagnostic);

    private RenderedArmouryDifferentialProgress FailCore(string message)
    {
        status = RenderedArmouryDifferentialStatus.Failed;
        diagnostic = message;
        return Snapshot();
    }

    private static string Identity(uint itemId, bool isHighQuality) => $"{itemId}{(isHighQuality ? " HQ" : " NQ")}";
}
