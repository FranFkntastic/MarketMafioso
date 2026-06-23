using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.WorkshopPrep;

public static class WorkshopAssemblyPreflightService
{
    public static WorkshopAssemblyPreflightResult Check(
        IReadOnlyList<WorkshopPrepQueueItem> queue,
        IReadOnlyList<WorkshopProjectDefinition> projects,
        IReadOnlyDictionary<uint, int> playerInventory)
    {
        var plan = WorkshopAssemblyPlanBuilder.Build(queue, projects);
        var missing = plan.TotalMaterials
            .Select(material =>
            {
                playerInventory.TryGetValue(material.ItemId, out var available);
                return new
                {
                    material.ItemName,
                    Missing = material.Quantity - available,
                };
            })
            .Where(x => x.Missing > 0)
            .OrderBy(x => x.ItemName)
            .ToList();

        if (missing.Count > 0)
        {
            var missingText = string.Join(", ", missing.Select(x => $"{x.ItemName} x{x.Missing}"));
            return new(false, $"Missing player materials: {missingText}.", null);
        }

        return new(true, "Workshop assembly preflight complete.", plan);
    }
}
