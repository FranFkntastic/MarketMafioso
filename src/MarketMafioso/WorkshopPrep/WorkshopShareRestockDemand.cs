using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.WorkshopPrep;

/// <summary>Where a share restock run's demand came from; null on a run means the active prep queue.</summary>
public sealed record WorkshopRestockDemandSource(
    Guid ShareJobId,
    string ShareName,
    IReadOnlyList<WorkshopRestockDemandLine> FrozenRequirements);

/// <summary>
/// Translates a split share's handover demand into the runner's raw bag terms.
/// The runner treats Required as a total bag target compared with live bag counts, so a share
/// that owes O while R of the bags are kept for the sender's own plan targets O + R raw units.
/// Shortage is unchanged: max(0, (O + R) − bags) = max(0, O − (bags − R)) = max(0, O − spare).
/// </summary>
public static class WorkshopShareRestockDemand
{
    /// <summary>Units kept for the active plan: min(bags, planNeed) per item.</summary>
    public static IReadOnlyDictionary<uint, int> Reserve(
        IReadOnlyDictionary<uint, int> bags,
        IReadOnlyList<WorkshopMaterialRequirement> activePlanNeed)
    {
        ArgumentNullException.ThrowIfNull(bags);
        ArgumentNullException.ThrowIfNull(activePlanNeed);
        return activePlanNeed
            .GroupBy(requirement => requirement.ItemId)
            .Select(group => (ItemId: group.Key,
                Reserved: Math.Min(Math.Max(0, bags.GetValueOrDefault(group.Key)), Math.Max(0, group.Sum(item => item.Quantity)))))
            .Where(entry => entry.Reserved > 0)
            .ToDictionary(entry => entry.ItemId, entry => entry.Reserved);
    }

    /// <summary>
    /// The active plan's full need per item: the restock-side claim. Handover keeps min(bags, need),
    /// so a restock that targeted owed + min(bags, need) would have its new units re-kept for the plan
    /// whenever the plan is itself short. Targeting owed + need leaves exactly owed spare afterwards.
    /// </summary>
    public static IReadOnlyDictionary<uint, int> PlanClaim(IReadOnlyList<WorkshopMaterialRequirement> activePlanNeed)
    {
        ArgumentNullException.ThrowIfNull(activePlanNeed);
        return activePlanNeed
            .GroupBy(requirement => requirement.ItemId)
            .Select(group => (ItemId: group.Key, Need: group.Sum(item => Math.Max(0, item.Quantity))))
            .Where(entry => entry.Need > 0)
            .ToDictionary(entry => entry.ItemId, entry => entry.Need);
    }

    /// <summary>
    /// Raw-terms demand: owed + reserved per item with something owed. Items owing nothing are
    /// dropped so the run never targets (or buys toward) the sender's own plan on its own.
    /// </summary>
    public static IReadOnlyList<WorkshopRestockDemandLine> ToRawDemand(
        IReadOnlyList<WorkshopMaterialRequirement> owed,
        IReadOnlyDictionary<uint, int> reserved)
    {
        ArgumentNullException.ThrowIfNull(owed);
        ArgumentNullException.ThrowIfNull(reserved);
        return owed
            .GroupBy(requirement => requirement.ItemId)
            .Select(group => (First: group.First(), Owed: group.Sum(item => Math.Max(0, item.Quantity))))
            .Where(entry => entry.Owed > 0)
            .Select(entry => new WorkshopRestockDemandLine
            {
                ItemId = entry.First.ItemId,
                ItemName = entry.First.ItemName,
                IconId = entry.First.IconId,
                Quantity = checked(entry.Owed + reserved.GetValueOrDefault(entry.First.ItemId)),
            })
            .OrderBy(line => line.ItemId)
            .ToList();
    }

    public static IReadOnlyList<WorkshopMaterialRequirement> ToRequirements(IEnumerable<WorkshopRestockDemandLine> demand) =>
        demand.Select(line => new WorkshopMaterialRequirement(line.ItemId, line.ItemName, line.IconId, line.Quantity)).ToArray();

    /// <summary>
    /// The queue signature a run started from this demand carries. Start and every Tick/Resume
    /// derive it through this one function, so a share run never compares against the prep queue.
    /// </summary>
    public static string Signature(IEnumerable<WorkshopRestockDemandLine> demand) =>
        WorkshopVendorProcurementPlanner.BuildQueueSignature(
            WorkshopMaterialAvailabilityService.BuildAvailabilityFromRetainers(
                ToRequirements(demand), new Dictionary<uint, int>(), []));
}
