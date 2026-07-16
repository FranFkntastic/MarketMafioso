using System;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace MarketMafioso.Automation.Items;

public sealed class AutomationItemCatalog
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    public AutomationItemCatalog(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
    }

    public AutomationItemMetadata Resolve(uint itemId, bool isHighQuality = false)
    {
        try
        {
            var item = dataManager.GetExcelSheet<Item>()?.GetRowOrDefault(itemId);
            var name = item?.Name.ToString();
            var luminaStackSize = item?.StackSize ?? 0;
            var itemType = item is { } resolvedItem ? ResolveItemType(resolvedItem) : null;
            var supportsCondition = item?.ClassJobRepair.RowId != 0;

            return new AutomationItemMetadata(
                new AutomationItemIdentity(itemId, name, isHighQuality),
                ItemStackRules.ResolveMaxStack(itemId, luminaStackSize),
                itemType,
                supportsCondition);
        }
        catch (Exception ex)
        {
            log.Verbose(ex, $"[MarketMafioso] Could not resolve metadata for item {itemId}");
            return new AutomationItemMetadata(
                new AutomationItemIdentity(itemId, null, isHighQuality),
                ItemStackRules.ResolveMaxStack(itemId, 0));
        }
    }

    private string? ResolveItemType(Item item)
    {
        if (item.ItemUICategory.RowId == 0)
            return null;

        try
        {
            var itemType = item.ItemUICategory.Value.Name.ToString();
            return string.IsNullOrWhiteSpace(itemType) ? null : itemType;
        }
        catch (Exception ex)
        {
            log.Verbose(ex, $"[MarketMafioso] Could not resolve UI category for item {item.RowId}");
            return null;
        }
    }

    public string? ResolveItemName(uint itemId) => Resolve(itemId).Identity.Name;
}
