using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.UI.Filtering;
using MarketMafioso.RetainerRestock;

namespace MarketMafioso.Windows.RetainerRestock;

public sealed class RetainerRestockBrowserState
{
    private readonly HashSet<uint> expandedItemIds = [];

    public RetainerBrowseQueryMode Mode { get; set; } = RetainerBrowseQueryMode.Items;
    public string SelectedScopeKey { get; set; } = RetainerBrowseScopeOption.AllKey;
    public DalamudFilterAutocompleteState ItemsFilter { get; } = new();
    public DalamudFilterAutocompleteState ListingsFilter { get; } = new();
    public RetainerBrowseItemGroup? SelectedItemGroup { get; private set; }
    public Guid? SelectedPlanItemId { get; set; }
    public string StagedDesiredQuantityText { get; set; } = string.Empty;
    public bool FilterReferenceRequested { get; set; }

    public DalamudFilterAutocompleteState ActiveFilter =>
        Mode == RetainerBrowseQueryMode.Items ? ItemsFilter : ListingsFilter;

    public int? StagedDesiredQuantity =>
        int.TryParse(StagedDesiredQuantityText.Trim(), out var quantity)
            ? quantity
            : null;

    public bool CanSaveStagedItem =>
        SelectedItemGroup is { CanWithdrawToPlayer: true } && StagedDesiredQuantity is > 0;

    public string StagedValidationMessage
    {
        get
        {
            if (SelectedItemGroup is null)
                return "Select an item with observed retainer stock and enter a desired quantity.";

            if (!SelectedItemGroup.CanWithdrawToPlayer)
                return "This item is only on the character and cannot be withdrawn from a retainer.";

            if (string.IsNullOrWhiteSpace(StagedDesiredQuantityText) || StagedDesiredQuantity is null)
                return "Enter a desired quantity.";

            if (StagedDesiredQuantity <= 0)
                return "Desired quantity must be greater than zero.";

            return $"Ready to add {SelectedItemGroup.ItemName}.";
        }
    }

    public void EnsureScope(RetainerBrowseProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (projection.Scopes.All(scope => !string.Equals(scope.Key, SelectedScopeKey, StringComparison.Ordinal)))
        {
            SelectedScopeKey = RetainerBrowseScopeOption.AllKey;
            ClearStagedItem();
        }
    }

    public void SelectMode(RetainerBrowseQueryMode mode)
    {
        Mode = mode;
        if (mode == RetainerBrowseQueryMode.Listings)
            ClearStagedItem();
    }

    public void Stage(RetainerBrowseItemGroup itemGroup)
    {
        ArgumentNullException.ThrowIfNull(itemGroup);
        SelectedItemGroup = itemGroup;
        StagedDesiredQuantityText = string.Empty;
    }

    public void ClearStagedItem()
    {
        SelectedItemGroup = null;
        StagedDesiredQuantityText = string.Empty;
    }

    public bool ApplyStagedItem(IList<RetainerRestockPlanItem> planItems)
    {
        ArgumentNullException.ThrowIfNull(planItems);
        if (!CanSaveStagedItem || SelectedItemGroup is null || StagedDesiredQuantity is not { } desiredQuantity)
            return false;

        if (!RetainerBrowseWithdrawalPlanStager.TryUpsert(planItems, SelectedItemGroup, desiredQuantity))
            return false;

        ClearStagedItem();
        return true;
    }

    public bool IsExpanded(uint itemId) => expandedItemIds.Contains(itemId);

    public void ToggleExpanded(uint itemId)
    {
        if (!expandedItemIds.Add(itemId))
            expandedItemIds.Remove(itemId);
    }

    public void RebindSelectedItem(IReadOnlyList<RetainerBrowseItemGroup> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (SelectedItemGroup is null)
            return;

        var current = items.FirstOrDefault(item => item.ItemId == SelectedItemGroup.ItemId);
        if (current is null || !current.CanWithdrawToPlayer)
        {
            ClearStagedItem();
            return;
        }

        SelectedItemGroup = current;
    }

    public void RetainAvailableExpansions(IEnumerable<RetainerBrowseItemGroup> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var available = items.Select(item => item.ItemId).ToHashSet();
        expandedItemIds.RemoveWhere(itemId => !available.Contains(itemId));
    }
}
