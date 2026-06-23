using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace MarketMafioso.WorkshopPrep;

public sealed class WorkshopProjectCatalog
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private IReadOnlyList<WorkshopProjectDefinition>? cachedProjects;

    public WorkshopProjectCatalog(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
    }

    public IReadOnlyList<WorkshopProjectDefinition> GetProjects()
    {
        if (cachedProjects != null)
            return cachedProjects;

        var supplyItems = dataManager.GetExcelSheet<CompanyCraftSupplyItem>()
            .Where(x => x.RowId > 0)
            .ToDictionary(x => x.RowId, x => x.Item.Value);

        cachedProjects = dataManager.GetExcelSheet<CompanyCraftSequence>()
            .Where(x => x.RowId > 0 && x.ResultItem.RowId > 0)
            .Select(x => BuildProject(x, supplyItems))
            .Where(x => x.Materials.Count > 0)
            .OrderBy(x => x.Name)
            .ToList();

        log.Information($"[MarketMafioso] Loaded {cachedProjects.Count} workshop prep project(s).");
        return cachedProjects;
    }

    public IReadOnlyList<WorkshopMaterialRequirement> BuildRequirements(IReadOnlyList<WorkshopPrepQueueItem> queue)
    {
        var projects = GetProjects().ToDictionary(x => x.WorkshopItemId);

        return queue
            .Where(x => x.Quantity > 0 && projects.ContainsKey(x.WorkshopItemId))
            .SelectMany(x => projects[x.WorkshopItemId].Materials.Select(material => material with
            {
                Quantity = material.Quantity * x.Quantity,
            }))
            .GroupBy(x => x.ItemId)
            .Select(x =>
            {
                var first = x.First();
                return first with { Quantity = x.Sum(y => y.Quantity) };
            })
            .OrderBy(x => x.ItemName)
            .ToList();
    }

    private static WorkshopProjectDefinition BuildProject(
        CompanyCraftSequence sequence,
        IReadOnlyDictionary<uint, Item> supplyItems)
    {
        var materials = sequence.CompanyCraftPart
            .Where(part => part.RowId != 0)
            .SelectMany(part => part.Value.CompanyCraftProcess)
            .SelectMany(process => Enumerable.Range(0, process.Value.SupplyItem.Count)
                .Select(index => new
                {
                    SupplyItemId = process.Value.SupplyItem[index].RowId,
                    Quantity = process.Value.SetQuantity[index] * process.Value.SetsRequired[index],
                }))
            .Where(x => x.SupplyItemId > 0 && x.Quantity > 0 && supplyItems.ContainsKey(x.SupplyItemId))
            .Select(x =>
            {
                var item = supplyItems[x.SupplyItemId];
                return new WorkshopMaterialRequirement(
                    item.RowId,
                    item.Name.ToString(),
                    item.Icon,
                    x.Quantity);
            })
            .GroupBy(x => x.ItemId)
            .Select(x =>
            {
                var first = x.First();
                return first with { Quantity = x.Sum(y => y.Quantity) };
            })
            .OrderBy(x => x.ItemName)
            .ToList();

        return new WorkshopProjectDefinition(
            sequence.RowId,
            sequence.ResultItem.RowId,
            sequence.ResultItem.Value.Name.ToString(),
            sequence.ResultItem.Value.Icon,
            materials);
    }
}
