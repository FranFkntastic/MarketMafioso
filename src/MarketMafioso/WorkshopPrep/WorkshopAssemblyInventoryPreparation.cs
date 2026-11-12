using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Automation.Inventory;
using Dalamud.Plugin.Services;

namespace MarketMafioso.WorkshopPrep;

internal sealed class WorkshopAssemblyInventoryPreparation(DalamudInventoryMover mover, IDataManager data) : IDisposable
{
    private DalamudInventoryStack? mergeTarget;
    private DalamudInventoryStack? splitPiece;
    private int operations;
    private int recoveries;
    private string? recoveryIdentity;
    private WorkshopCraftMaterialState? required;
    internal void Require(WorkshopCraftMaterialState material) => required = material;
    internal void NoteProgress() { recoveries = 0; recoveryIdentity = null; }
    internal void BeginRun() { Suspend(); operations = 0; required = null; NoteProgress(); }
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
        var stacks = read.Stacks.Where(x => x.ItemId == itemId && x.IsOrdinary).ToArray();
        if (stacks.Any(x => x.Quantity >= quantity)) return new(true, $"A stack can supply {quantity}x {name}.");
        var total = stacks.Sum(x => (long)x.Quantity);
        var compatible = stacks.GroupBy(x => (x.Flags, x.CompatibilityMetadata)).Any(g => g.Sum(x => (long)x.Quantity) >= quantity);
        return new(false, $"{name} ({itemId}) needs {quantity}; ordinary total={total}, stacks=[{string.Join(",", stacks.Select(x => $"{x.Quantity}{(x.IsHighQuality ? "HQ" : "NQ")}"))}]. " +
            (total < quantity ? "Genuine material shortage." : compatible ? "Sufficient compatible stock is fragmented." : "Stock cannot supply one contribution without changing quality/metadata."),
            ActiveMaterialItemId: itemId, MaterialBlocker: total < quantity ? WorkshopMaterialBlocker.Shortage :
                !compatible ? WorkshopMaterialBlocker.NativePreparationUnavailable : WorkshopMaterialBlocker.Fragmented);
    }

    internal WorkshopAssemblyActionResult Evaluate(IReadOnlyList<WorkshopAssemblyQueueEntry> entries,
        WorkshopCraftState? live, WorkshopAssemblyDiagnostics diagnostics, Func<bool> authorized)
    {
        if (mover.HasOutstanding)
        {
            var observation = mover.Observe();
            if (observation.Waiting) return Waiting(observation.Message);
            diagnostics.Record("inventory-operation", observation.Message);
            if (observation.Reconciled) mergeTarget = splitPiece = null;
            else if (!observation.Completed) return new(false, observation.Message);
            splitPiece = observation.SplitDestination;
        }
        var read = Read();
        if (!read.IsComplete) return Waiting(read.Message);
        if (splitPiece != null && mergeTarget != null)
        {
            var piece = splitPiece;
            var target = mergeTarget;
            splitPiece = mergeTarget = null;
            if (read.Stacks.Contains(piece) && read.Stacks.Contains(target))
                return Dispatch(piece, target, piece.Quantity, diagnostics, authorized);
            diagnostics.Record("inventory-replan", "Split continuation changed outside assembly; discarded addresses and reread all materials.");
        }
        // A size change, not a phase/copy/entry boundary, ends a material's horizon.
        var ordered = entries.SelectMany(entry => Enumerable.Range(0, Math.Max(0, entry.Quantity))
            .SelectMany(_ => entry.ContributionSchedule)).ToList();
        if (live != null && entries.Count != 0 && live.ResultItem == entries[0].ResultItemId)
            foreach (var row in live.Items)
            {
                // Part/process mapping is not uniquely known. For the active
                // copy, conservatively include ALL matching-size rows rather
                // than mistake an earlier size change for a future boundary.
                // Subsequent copies/entries still stop at their first size change.
                var activeRows = entries[0].ContributionSchedule.Where(x => x.ItemId == row.ItemId).ToArray();
                var matching = activeRows.Where(x => x.Quantity == row.ItemCountPerStep).ToArray();
                var conservativeCount = matching.Sum(x => x.Count) - (matching.FirstOrDefault()?.Count ?? 0) +
                    checked((int)(row.StepsTotal - row.StepsComplete));
                foreach (var old in activeRows) ordered.Remove(old);
                var batch = new WorkshopContributionBatch(0, row.ItemId, row.ItemName, checked((int)row.ItemCountPerStep),
                    conservativeCount, 0);
                ordered.Insert(0, batch);
            }
        if (required != null)
            ordered.Insert(0, new(0, required.ItemId, required.ItemName, checked((int)required.ItemCountPerStep), 1, 0));
        var schedule = ordered.GroupBy(x => x.ItemId).ToDictionary(g => g.Key, g =>
        {
            var first = g.First();
            return first with { Count = g.TakeWhile(x => x.Quantity == first.Quantity).Sum(x => x.Count),
                StackCapacity = checked((int)data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRow(g.Key).StackSize) };
        });
        foreach (var batch in schedule.Values)
        {
            var q = batch.Quantity;
            var cap = batch.StackCapacity;
            if (q <= 0 || cap < q) return new(false, $"Authoritative capacity {cap} cannot supply {q}x {batch.ItemName}.");
            var ordinary = read.Stacks.Where(x => x.ItemId == batch.ItemId && x.IsOrdinary).ToArray();
            if (ordinary.Sum(x => (long)x.Quantity / q) >= Math.Min(batch.Count, ordinary.Sum(x => (long)x.Quantity) / q)) continue;
            foreach (var group in ordinary.GroupBy(x => (x.Flags, x.CompatibilityMetadata)))
            {
                var slots = group.OrderByDescending(x => x.Quantity).ToArray();
                var total = slots.Sum(x => (long)x.Quantity);
                if (total < q) continue;
                // For a constant-size horizon the sum of whole contributions
                // in each stack is invariant under ANY eligible selector choice.
                // Reuse sufficient stacks rather than align them needlessly.
                if (slots.Sum(x => (long)x.Quantity / q) >= Math.Min(batch.Count, total / q)) continue;
                var unaligned = slots.Where(x => x.Quantity % q != 0).ToArray();
                if (unaligned.Length == 0 || unaligned.Length == 1 && unaligned[0].Quantity < q) continue;
                var scratch = read.Stacks.Any(x => x.ItemId == 0 && x.Quantity == 0 && !x.IsSymbolic);
                // No scratch required for genuine incomplete merges. If a stack
                // is already actionable, use that smaller safe horizon instead.
                if (!scratch)
                {
                    if (slots.Any(x => x.Quantity >= q)) continue;
                    return Dispatch(slots[1], slots[0], Math.Min(slots[1].Quantity, cap - slots[0].Quantity), diagnostics, authorized);
                }
                var alignedCapacity = cap / q * q;
                var full = unaligned.FirstOrDefault(x => x.Quantity > alignedCapacity);
                if (full != null) return Dispatch(full, null, full.Quantity - alignedCapacity, diagnostics, authorized);
                if (unaligned.Length == 1)
                    return Dispatch(unaligned[0], null, unaligned[0].Quantity % q, diagnostics, authorized);
                var destination = unaligned[0];
                var source = unaligned[^1];
                var desired = Math.Min(alignedCapacity, (destination.Quantity / q + 1) * q);
                var amount = Math.Min(source.Quantity, desired - destination.Quantity);
                if (amount <= 0) continue;
                if (source.Quantity < cap && amount == Math.Min(source.Quantity, cap - destination.Quantity))
                    return Dispatch(source, destination, amount, diagnostics, authorized);
                mergeTarget = destination;
                return Dispatch(source, null, amount, diagnostics, authorized);
            }
        }
        if (required != null)
        {
            var check = CheckContribution(required.ItemId, required.ItemName, required.ItemCountPerStep);
            if (!check.Success) return new(false, $"Preparation cannot satisfy the current native requirement: {check.Message}");
            required = null;
        }
        operations = 0;
        return new(true, "Assembly materials prepared for the current contribution-size horizon; rereading native progress after reentry.");
    }

    private WorkshopAssemblyActionResult Dispatch(DalamudInventoryStack source, DalamudInventoryStack? target, int amount,
        WorkshopAssemblyDiagnostics diagnostics, Func<bool> authorized)
    {
        if (!authorized()) return new(false, "Assembly materials need preparation outside the station.",
            IsWaiting: true, MaterialBlocker: WorkshopMaterialBlocker.Fragmented);
        if (operations >= 1024) return new(false, "Assembly preparation did not converge within 1024 operations; queue intent retained.");
        var result = mover.Begin(source, target, amount, authorized);
        if (result.Dispatched) operations++;
        diagnostics.Record("inventory-operation", result.Message);
        if (result.Reconciled) { mergeTarget = splitPiece = null; return Waiting(result.Message); }
        return result.Waiting ? Waiting(result.Message, result.Dispatched) : new(false, result.Message);
    }
    internal void BeginRecovery(string identity)
    {
        mergeTarget = splitPiece = null;
        operations = 0;
        if (identity != recoveryIdentity) { recoveryIdentity = identity; recoveries = 0; }
        if (++recoveries > 3) throw new InvalidOperationException("Assembly recovery repeatedly invalidated without workshop progress; queue retained. Check competing inventory automation.");
    }
    internal void Suspend() { mergeTarget = splitPiece = null; mover.Suspend(); }
    private static WorkshopAssemblyActionResult Waiting(string message, bool action = false) =>
        new(false, message, IsWaiting: true, ActionTaken: action, MaterialBlocker: WorkshopMaterialBlocker.InventoryUnknown);
    public void Dispose() => mover.Dispose();
}
