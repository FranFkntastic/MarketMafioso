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
        var missingCount = plan.TotalMaterials.Count(material =>
        {
            playerInventory.TryGetValue(material.ItemId, out var available);
            return material.Quantity > available;
        });

        var message = missingCount == 0
            ? "Workshop assembly preflight complete."
            : "Workshop assembly preflight complete. Player inventory is short for some queued materials; live workshop progress will be checked during assembly.";
        return new(true, message, plan);
    }
}
