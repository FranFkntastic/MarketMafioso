using System;
using System.Collections.Generic;
using System.Linq;
using MarketMafioso.RetainerRestock;

namespace MarketMafioso.Windows.RetainerRestock;

public sealed class RetainerRestockBrowserState
{
    public string SearchText { get; set; } = string.Empty;
    public bool ShowPlayerStock { get; set; } = true;
    public bool ShowRetainerStock { get; set; } = true;
    public RetainerRestockStockRow? SelectedStockRow { get; private set; }
    public Guid? SelectedPlanItemId { get; set; }
    public string StagedDesiredQuantityText { get; set; } = string.Empty;

    public int? StagedDesiredQuantity =>
        int.TryParse(StagedDesiredQuantityText.Trim(), out var quantity)
            ? quantity
            : null;

    public bool CanSaveStagedItem =>
        SelectedStockRow is not null && StagedDesiredQuantity is > 0;

    public string StagedValidationMessage
    {
        get
        {
            if (SelectedStockRow is null)
                return "Select an item and enter a desired quantity.";

            if (string.IsNullOrWhiteSpace(StagedDesiredQuantityText) || StagedDesiredQuantity is null)
                return "Enter a desired quantity.";

            if (StagedDesiredQuantity <= 0)
                return "Desired quantity must be greater than zero.";

            return $"Ready to add {SelectedStockRow.ItemName}.";
        }
    }

    public void Stage(RetainerRestockStockRow row)
    {
        SelectedStockRow = row;
        StagedDesiredQuantityText = string.Empty;
    }

    public void ClearStagedItem()
    {
        SelectedStockRow = null;
        StagedDesiredQuantityText = string.Empty;
    }

    public IReadOnlyList<RetainerRestockStockRow> FilterRows(IReadOnlyList<RetainerRestockStockRow> rows)
    {
        var filteredRows = rows
            .Where(row =>
                (ShowPlayerStock && row.PlayerQuantity > 0) ||
                (ShowRetainerStock && row.RetainerQuantity > 0))
            .ToList();

        return RetainerRestockStockCatalog.Search(filteredRows, SearchText);
    }

    public bool ApplyStagedItem(IList<RetainerRestockPlanItem> planItems)
    {
        if (!CanSaveStagedItem || SelectedStockRow is null || StagedDesiredQuantity is not { } desiredQuantity)
            return false;

        var existingItem = planItems.FirstOrDefault(item => item.ItemId == SelectedStockRow.ItemId);
        if (existingItem is null)
        {
            planItems.Add(new RetainerRestockPlanItem
            {
                ItemId = SelectedStockRow.ItemId,
                ItemName = SelectedStockRow.ItemName,
                DesiredPlayerQuantity = desiredQuantity,
                Enabled = true,
            });
        }
        else
        {
            existingItem.ItemName = SelectedStockRow.ItemName;
            existingItem.DesiredPlayerQuantity = desiredQuantity;
            existingItem.Enabled = true;
        }

        ClearStagedItem();
        return true;
    }
}
