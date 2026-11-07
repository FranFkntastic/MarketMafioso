using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using Franthropy.Dalamud.Automation.Inventory;

namespace MarketMafioso.TradeQueue;

/// <summary>
/// The exact HQ stacks a quality-normalizing Trade Queue run would lower, derived from the same
/// reconciled queue and planner the runner executes. The signature binds a user's confirmation
/// to this plan: the runner refuses to lower anything when its own preview signature differs.
/// </summary>
public sealed record TradeQueueQualityLoweringPreview(
    bool Success,
    string Message,
    IReadOnlyList<ItemQualityLoweringPreviewStep> Steps,
    string Signature)
{
    public bool RequiresLowering => Steps.Count > 0;
    public int ConvertedUnits => Steps.Sum(step => step.ConvertedUnits);
    public int ExcessConvertedUnits => Steps.Sum(step => step.ExcessConvertedUnits);

    public static TradeQueueQualityLoweringPreview Build(
        IEnumerable<TradeQueueItem> queue,
        IReadOnlyList<TradeQueueInventoryStack> inventory)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(inventory);

        // Start reconciles the persisted queue against live inventory before planning; mirror that on a copy.
        var reconciled = queue
            .Select(item => new TradeQueueItem { ItemId = item.ItemId, ItemName = item.ItemName, Quantity = item.Quantity })
            .ToList();
        TradeQueueInventoryReconciler.Reconcile(reconciled, inventory);

        var stacks = inventory
            .Where(stack => stack.ItemId != TradeQueuePlanner.GilItemId && stack.Quantity > 0)
            .Select(stack => new DalamudInventoryStack(
                (InventoryType)stack.ContainerId,
                stack.SlotIndex,
                stack.ItemId,
                stack.Quantity,
                stack.IsHighQuality))
            .ToArray();
        var preview = ItemQualityLoweringPlanner.Preview(BuildRequirements(reconciled), stacks);
        return new(preview.Success, preview.Message, preview.Steps, Sign(preview.Steps));
    }

    internal static IReadOnlyList<ItemQualityLoweringRequirement> BuildRequirements(IEnumerable<TradeQueueItem> queue) =>
        queue
            .Where(item => item.ItemId != TradeQueuePlanner.GilItemId)
            .GroupBy(item => item.ItemId)
            .Select(group => new ItemQualityLoweringRequirement(
                group.Key,
                group.First().ItemName,
                checked(group.Sum(item => item.Quantity))))
            .ToArray();

    public string Describe() =>
        RequiresLowering
            ? string.Join(
                "; ",
                Steps.Select(step =>
                    $"{step.ItemName} x{step.ConvertedUnits:N0} ({step.Container} slot {step.SlotIndex + 1:N0}" +
                    (step.ExcessConvertedUnits > 0 ? $", {step.ExcessConvertedUnits:N0} beyond the queue" : string.Empty) +
                    ")"))
            : "No HQ items need to be lowered.";

    private static string Sign(IReadOnlyList<ItemQualityLoweringPreviewStep> steps) =>
        string.Join(
            "|",
            steps.Select(step => $"{(uint)step.Container}:{step.SlotIndex}:{step.ItemId}:{step.ConvertedUnits}"));
}

/// <summary>Per-run choices made at the Start boundary; they never change persisted policy.</summary>
public sealed record TradeQueueStartOptions(
    bool? NormalizeHighQualityItems = null,
    string? ApprovedQualityLoweringSignature = null);
