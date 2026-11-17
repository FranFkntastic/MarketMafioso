using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Automation.Inventory;
using Dalamud.Plugin.Services;

namespace MarketMafioso.WorkshopPrep;

internal sealed class WorkshopAssemblyInventoryPreparation(DalamudInventoryMover mover, IDataManager data) : IDisposable
{
    private const int MaximumOperations = 256;
    private int operations;
    private int recoveries;
    private string? recoveryIdentity;
    private WorkshopCraftMaterialState? required;
    // Identities follow stacks across refused swaps so a replan cannot retry the same pair.
    private readonly Dictionary<(FFXIVClientStructs.FFXIV.Client.Game.InventoryType, int), int> stackIdentities = new();
    private readonly HashSet<(int, int)> refusedPairs = new();
    private readonly List<(uint ItemId, string Message)> refusalEvidence = new();
    private int nextIdentity;
    private DalamudInventoryStack? pendingSource;
    private DalamudInventoryStack? pendingTarget;
    private string? pendingContext;
    private string? recoveryContext;
    internal void Require(WorkshopCraftMaterialState material) => required = material;
    internal void NoteProgress() { recoveries = 0; recoveryIdentity = null; }
    internal void BeginRun()
    {
        Suspend(); operations = 0; required = null; NoteProgress();
        stackIdentities.Clear(); refusedPairs.Clear(); refusalEvidence.Clear(); nextIdentity = 0;
        pendingSource = pendingTarget = null;
        pendingContext = recoveryContext = null;
    }
    internal WorkshopAssemblyActionResult Preflight()
    {
        var result = mover.Preflight();
        return new(result.Completed, result.Message, IsWaiting: result.Waiting,
            MaterialBlocker: result.Waiting ? WorkshopMaterialBlocker.InventoryUnknown :
                result.Completed ? WorkshopMaterialBlocker.None : WorkshopMaterialBlocker.NativePreparationUnavailable);
    }
    internal static DalamudInventoryRead Read() => DalamudInventoryStackScanner.ReadComplete(DalamudInventoryMover.Bags);

    internal static WorkshopAssemblyActionResult CheckContribution(uint itemId, string name, uint quantity)
    {
        var read = Read();
        if (!read.IsComplete) return Waiting(read.Message);
        return CheckContribution(read, itemId, name, quantity);
    }

    private static WorkshopAssemblyActionResult CheckContribution(DalamudInventoryRead read, uint itemId, string name, uint quantity)
    {
        var stacks = read.Stacks.Where(x => x.ItemId == itemId && x.IsOrdinary).ToArray();
        if (stacks.Any(x => x.Quantity >= quantity)) return new(true, $"A stack can supply {quantity}x {name}.");
        var total = stacks.Sum(x => (long)x.Quantity);
        var compatible = stacks.GroupBy(x => x.IsHighQuality).Any(g => g.Sum(x => (long)x.Quantity) >= quantity);
        return new(false, $"{name} ({itemId}) needs {quantity}; ordinary total={total}, stacks=[{string.Join(",", stacks.Select(Describe))}]. " +
            (total < quantity ? "Genuine material shortage." : compatible ? "Sufficient compatible stock is fragmented." : "Stock cannot supply one contribution without changing quality/metadata."),
            ActiveMaterialItemId: itemId, MaterialBlocker: total < quantity ? WorkshopMaterialBlocker.Shortage :
                !compatible ? WorkshopMaterialBlocker.NativePreparationUnavailable : WorkshopMaterialBlocker.Fragmented);
    }

    internal WorkshopAssemblyActionResult Evaluate(IReadOnlyList<WorkshopAssemblyQueueEntry> entries,
        WorkshopCraftState? live, WorkshopAssemblyDiagnostics diagnostics, Func<bool> authorized, bool consolidate = false)
    {
        if (mover.HasOutstanding)
        {
            var observation = mover.Observe();
            if (observation.Waiting) return Waiting(observation.Message);
            diagnostics.Record("inventory-operation", observation.Message);
            if (observation.Outcome == InventoryMoveOutcome.MergeRefusedSwapped && pendingSource != null && pendingTarget != null)
            {
                var sourceId = Identity(pendingSource);
                var targetId = Identity(pendingTarget);
                refusedPairs.Add(Pair(sourceId, targetId));
                refusalEvidence.Add((pendingSource.ItemId, $"identities {sourceId}<->{targetId}: {Describe(pendingSource)} <-> {Describe(pendingTarget)}"));
                stackIdentities[(pendingSource.Container, pendingSource.SlotIndex)] = targetId;
                stackIdentities[(pendingTarget.Container, pendingTarget.SlotIndex)] = sourceId;
            }
            pendingSource = pendingTarget = null;
            if (!observation.Reconciled && !observation.Completed)
                return new(false, pendingContext == null ? observation.Message : $"{pendingContext}. {observation.Message}");
            pendingContext = null;
        }
        var read = Read();
        if (!read.IsComplete) return Waiting(read.Message);
        foreach (var empty in read.Stacks.Where(x => x.ItemId == 0))
            stackIdentities.Remove((empty.Container, empty.SlotIndex));
        // Preview asks only whether a contribution is blocked now. Once outside,
        // consolidate all remaining materials, without contribution-size alignment.
        var schedule = entries.Where(x => x.Quantity > 0)
            .SelectMany(x => x.ContributionSchedule).Where(x => x.Count > 0).ToList();
        if (live != null && entries.Count != 0 && live.ResultItem == entries[0].ResultItemId)
            schedule.InsertRange(0, live.Items.Where(x => x.StepsComplete < x.StepsTotal)
                .Select(x => new WorkshopContributionBatch(x.ItemId, x.ItemName, checked((int)x.ItemCountPerStep), 1)));
        if (required != null)
            schedule.Insert(0, new(required.ItemId, required.ItemName, checked((int)required.ItemCountPerStep), 1));

        if (!consolidate)
        {
            foreach (var batch in schedule)
            {
                var check = CheckContribution(read, batch.ItemId, batch.ItemName, checked((uint)batch.Quantity));
                if (check.MaterialBlocker == WorkshopMaterialBlocker.Fragmented)
                {
                    recoveryContext = check.Message;
                    return check;
                }
            }
        }
        else foreach (var material in schedule.GroupBy(x => x.ItemId))
        {
            var batch = material.First();
            var cap = checked((int)data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRow(batch.ItemId).StackSize);
            var ordinary = read.Stacks.Where(x => x.ItemId == batch.ItemId && x.IsOrdinary).ToArray();
            var context = $"{batch.ItemName} ({batch.ItemId}) needs per-step quantities [{string.Join(", ", material.Select(x => x.Quantity).Distinct())}]; stacks=[{string.Join(", ", ordinary.Select(Describe))}]";
            if (material.Any(x => x.Quantity <= 0 || x.Quantity > cap))
                return new(false, $"{context}. Authoritative stack capacity threshold {cap} cannot supply a contribution.",
                    ActiveMaterialItemId: batch.ItemId, MaterialBlocker: WorkshopMaterialBlocker.NativePreparationUnavailable);
            foreach (var group in ordinary.GroupBy(x => x.IsHighQuality))
            {
                var partials = group.Where(x => x.Quantity > 0 && x.Quantity < cap)
                    .OrderByDescending(x => x.Quantity).ThenBy(x => x.Container).ThenBy(x => x.SlotIndex).ToArray();
                foreach (var destination in partials)
                    foreach (var source in partials.Reverse().Where(x => x != destination &&
                        x.CompatibleWith(destination) && !Refused(x, destination)))
                        return Dispatch(source, destination, Math.Min(source.Quantity, cap - destination.Quantity),
                            context, diagnostics, authorized);
            }
            var refusedForItem = refusalEvidence.Where(x => x.ItemId == batch.ItemId).Select(x => x.Message).ToArray();
            if (refusedForItem.Length != 0 && material.Any(x =>
                CheckContribution(read, x.ItemId, x.ItemName, checked((uint)x.Quantity)).MaterialBlocker == WorkshopMaterialBlocker.Fragmented))
                return new(false, $"{context}. No remaining merge pairing can supply a contribution. Refused pairs=[{string.Join("; ", refusedForItem)}].",
                    ActiveMaterialItemId: batch.ItemId, MaterialBlocker: WorkshopMaterialBlocker.MergeRefused);
        }
        if (required != null)
        {
            var check = CheckContribution(read, required.ItemId, required.ItemName, required.ItemCountPerStep);
            if (!check.Success) return check with
            {
                Message = $"Preparation cannot satisfy the current native requirement: {check.Message}",
                MaterialBlocker = check.MaterialBlocker == WorkshopMaterialBlocker.Fragmented
                    ? WorkshopMaterialBlocker.NativePreparationUnavailable : check.MaterialBlocker,
            };
            required = null;
        }
        operations = 0;
        return new(true, consolidate
            ? "Merge-only assembly material preparation complete; rereading native progress after reentry."
            : "No assembly material needs fragmentation recovery now.");
    }

    private WorkshopAssemblyActionResult Dispatch(DalamudInventoryStack source, DalamudInventoryStack target, int amount,
        string context, WorkshopAssemblyDiagnostics diagnostics, Func<bool> authorized)
    {
        if (!authorized()) return new(false, "Assembly materials need preparation outside the station.",
            IsWaiting: true, MaterialBlocker: WorkshopMaterialBlocker.Fragmented);
        if (operations >= MaximumOperations) return new(false,
            $"{context}. Assembly preparation reached the {MaximumOperations}-operation threshold; queue intent retained.",
            ActiveMaterialItemId: source.ItemId, MaterialBlocker: WorkshopMaterialBlocker.NativePreparationUnavailable);
        if (Refused(source, target))
            return new(false, $"{context}. Refused merge pair cannot be dispatched again: {Describe(source)} <-> {Describe(target)}.",
                MaterialBlocker: WorkshopMaterialBlocker.MergeRefused);
        Identity(source);
        Identity(target);
        var result = mover.Begin(source, target, amount, authorized);
        if (result.Dispatched) { operations++; pendingSource = source; pendingTarget = target; pendingContext = context; }
        diagnostics.Record("inventory-operation", result.Message);
        if (result.Reconciled) return Waiting(result.Message);
        return result.Waiting ? Waiting(result.Message, result.Dispatched) : new(false, $"{context}. {result.Message}");
    }
    internal void BeginRecovery(string identity)
    {
        operations = 0;
        if (identity != recoveryIdentity) { recoveryIdentity = identity; recoveries = 0; }
        if (++recoveries > 3) throw new InvalidOperationException($"{recoveryContext} Assembly recovery exceeded the 3-attempt threshold without workshop progress; queue retained. Check competing inventory automation.");
    }
    internal void Suspend() => mover.Suspend();
    private int Identity(DalamudInventoryStack stack)
    {
        var slot = (stack.Container, stack.SlotIndex);
        if (!stackIdentities.TryGetValue(slot, out var identity)) stackIdentities[slot] = identity = ++nextIdentity;
        return identity;
    }
    private static (int, int) Pair(int a, int b) => a <= b ? (a, b) : (b, a);
    private bool Refused(DalamudInventoryStack a, DalamudInventoryStack b) => refusedPairs.Contains(Pair(Identity(a), Identity(b)));
    private static string Describe(DalamudInventoryStack stack) =>
        $"{stack.Container}/{stack.SlotIndex} {stack.Quantity}{(stack.IsHighQuality ? "HQ" : "NQ")} flags={stack.Flags} metadata={stack.Metadata}";
    private static WorkshopAssemblyActionResult Waiting(string message, bool action = false) =>
        new(false, message, IsWaiting: true, ActionTaken: action, MaterialBlocker: WorkshopMaterialBlocker.InventoryUnknown);
    public void Dispose() => mover.Dispose();
}
