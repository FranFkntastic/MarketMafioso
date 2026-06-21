using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Concurrent;

namespace MarketMafioso.Services;

public sealed class ItemNameResolver
{
    private readonly IDataManager _dataManager;
    private readonly ConcurrentDictionary<uint, string> _itemNameCache = new();

    public ItemNameResolver(IDataManager dataManager)
    {
        _dataManager = dataManager;
    }

    public string GetItemName(uint itemId)
    {
        return _itemNameCache.GetOrAdd(itemId, ResolveItemName);
    }

    private string ResolveItemName(uint itemId)
    {
        var itemSheet = _dataManager.GetExcelSheet<Item>();
        if (!itemSheet.TryGetRow(itemId, out var item))
        {
            throw new InvalidOperationException($"Failed to resolve item name for item id {itemId}.");
        }

        var itemName = item.Name.ExtractText();
        if (string.IsNullOrWhiteSpace(itemName))
        {
            throw new InvalidOperationException($"Resolved empty item name for item id {itemId}.");
        }

        return itemName;
    }
}
