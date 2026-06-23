using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.WorkshopPrep;

public static class WorkshopAssemblyPlanBuilder
{
    public static WorkshopAssemblyPlan Build(
        IReadOnlyList<WorkshopPrepQueueItem> queue,
        IReadOnlyList<WorkshopProjectDefinition> projects)
    {
        if (queue.Count == 0)
            throw new InvalidOperationException("Workshop assembly queue is empty.");

        var projectById = projects.ToDictionary(x => x.WorkshopItemId);
        var entries = new List<WorkshopAssemblyQueueEntry>();
        var materialTotals = new Dictionary<uint, WorkshopMaterialRequirement>();

        foreach (var queueItem in queue)
        {
            if (!projectById.TryGetValue(queueItem.WorkshopItemId, out var project))
                throw new InvalidOperationException($"Unknown workshop project id {queueItem.WorkshopItemId} cannot be assembled.");

            if (queueItem.Quantity <= 0)
                throw new InvalidOperationException($"Workshop project {project.Name} has invalid quantity {queueItem.Quantity}.");

            entries.Add(new WorkshopAssemblyQueueEntry(
                project.WorkshopItemId,
                project.Name,
                queueItem.Quantity,
                project.Materials));

            foreach (var material in project.Materials)
            {
                var requiredQuantity = material.Quantity * queueItem.Quantity;
                if (materialTotals.TryGetValue(material.ItemId, out var existing))
                {
                    materialTotals[material.ItemId] = existing with
                    {
                        Quantity = existing.Quantity + requiredQuantity,
                    };
                }
                else
                {
                    materialTotals.Add(material.ItemId, material with
                    {
                        Quantity = requiredQuantity,
                    });
                }
            }
        }

        return new WorkshopAssemblyPlan(
            entries,
            materialTotals.Values.OrderBy(x => x.ItemName, StringComparer.OrdinalIgnoreCase).ToList());
    }
}
