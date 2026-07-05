using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using LuminaItem = Lumina.Excel.Sheets.Item;

namespace MarketMafioso.Windows.AcquisitionWorkbench;

public static class ItemAutocompleteControl
{
    public static void Draw(
        string id,
        IReadOnlyList<AcquisitionItemOption> itemOptions,
        ItemAutocompleteState state,
        Action? onSelectionChanged,
        Vector4 mutedColor,
        Vector4 successColor,
        Vector4 errorColor)
    {
        ArgumentNullException.ThrowIfNull(itemOptions);
        ArgumentNullException.ThrowIfNull(state);

        ImGui.TextColored(mutedColor, "Item");
        ImGui.SetNextItemWidth(-1);
        var previous = state.SearchBuffer;
        var current = state.SearchBuffer;
        if (ImGui.InputText($"##{id}ItemSearch", ref current, 160) &&
            !string.Equals(previous, current, StringComparison.Ordinal))
        {
            state.SearchBuffer = current;
            if (state.SelectedItem is not null &&
                !state.SelectedItem.Name.Equals(state.SearchBuffer.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                state.SelectedItem = null;
                onSelectionChanged?.Invoke();
            }
        }

        var resolved = ItemAutocompletePresenter.ResolveSelectedItem(
            itemOptions,
            state.SearchBuffer,
            state.SelectedItem);
        if (resolved is not null)
        {
            if (state.SelectedItem is null || state.SelectedItem.ItemId != resolved.ItemId)
            {
                state.SelectedItem = resolved;
                onSelectionChanged?.Invoke();
            }

            ImGui.TextColored(successColor, ItemAutocompletePresenter.FormatDisplayName(itemOptions, resolved));
            return;
        }

        if (itemOptions.Count == 0)
        {
            ImGui.TextColored(errorColor, "Item catalog unavailable.");
            return;
        }

        if (state.SearchBuffer.Trim().Length < 2)
        {
            ImGui.TextColored(mutedColor, "Type at least 2 characters.");
            return;
        }

        var results = ItemAutocompletePresenter.GetSearchResults(itemOptions, state.SearchBuffer);
        if (results.Count == 0)
        {
            ImGui.TextColored(mutedColor, "No matching items.");
            return;
        }

        var resultHeight = MathF.Min(132f, results.Count * ImGui.GetTextLineHeightWithSpacing() + 10f);
        ImGui.BeginChild($"##{id}ItemResults", new Vector2(0, resultHeight), true);
        foreach (var result in results)
        {
            var label = ItemAutocompletePresenter.FormatDisplayName(itemOptions, result);
            if (ImGui.Selectable($"{label}##{id}Item{result.ItemId}"))
            {
                state.SelectedItem = result;
                state.SearchBuffer = result.Name;
                onSelectionChanged?.Invoke();
            }
        }

        ImGui.EndChild();
    }

    public static IReadOnlyList<AcquisitionItemOption> LoadItemOptions(IDataManager dataManager)
    {
        try
        {
            return dataManager.GetExcelSheet<LuminaItem>()
                .Where(item => item.RowId > 0)
                .Select(item => new AcquisitionItemOption(item.RowId, item.Name.ToString().Trim()))
                .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                .GroupBy(item => item.ItemId)
                .Select(group => group.First())
                .OrderBy(item => item.Name)
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
