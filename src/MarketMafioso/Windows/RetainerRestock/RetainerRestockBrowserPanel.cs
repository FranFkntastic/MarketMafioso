using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using MarketMafioso.RetainerRestock;

namespace MarketMafioso.Windows.RetainerRestock;

public sealed class RetainerRestockBrowserPanel
{
    private readonly Configuration config;
    private readonly RetainerRestockBrowserState state;
    private readonly Action saveConfig;
    private uint? stagedInputFocusItemId;

    public RetainerRestockBrowserPanel(
        Configuration config,
        RetainerRestockBrowserState state,
        Action saveConfig)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        this.saveConfig = saveConfig ?? throw new ArgumentNullException(nameof(saveConfig));
    }

    public void DrawBrowse(
        IReadOnlyList<RetainerRestockStockRow> stockRows,
        Vector4 headerColor,
        Vector4 mutedColor)
    {
        ArgumentNullException.ThrowIfNull(stockRows);
        DrawAccessibleStock(stockRows, headerColor, mutedColor);
    }

    public void DrawPlan(
        RetainerRestockPlan plan,
        Vector4 headerColor,
        Vector4 successColor,
        Vector4 errorColor,
        Vector4 mutedColor)
    {
        ArgumentNullException.ThrowIfNull(plan);
        DrawPlanQueue(plan, headerColor, successColor, errorColor, mutedColor);
    }

    private void DrawAccessibleStock(
        IReadOnlyList<RetainerRestockStockRow> stockRows,
        Vector4 headerColor,
        Vector4 mutedColor)
    {
        ImGuiUi.SectionHeader("Accessible Stock", headerColor);

        var searchText = state.SearchText;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("Search##RetainerRestockStockSearch", ref searchText, 256))
            state.SearchText = searchText;

        var showPlayerStock = state.ShowPlayerStock;
        if (ImGui.Checkbox("Player##RetainerRestockShowPlayer", ref showPlayerStock))
            state.ShowPlayerStock = showPlayerStock;

        ImGui.SameLine();
        var showRetainerStock = state.ShowRetainerStock;
        if (ImGui.Checkbox("Retainers##RetainerRestockShowRetainers", ref showRetainerStock))
            state.ShowRetainerStock = showRetainerStock;

        ImGui.SameLine();
        DrawVisibleRowLimitSelector();

        var filteredRows = state.FilterRows(stockRows);
        ImGui.SameLine();
        ImGui.TextColored(
            mutedColor,
            $"{filteredRows.Count.ToString("N0", CultureInfo.InvariantCulture)} / {stockRows.Count.ToString("N0", CultureInfo.InvariantCulture)} items");

        DrawStagedItem(MarketMafioso.Windows.Main.MarketMafiosoUiTheme.Success, MarketMafioso.Windows.Main.MarketMafiosoUiTheme.Error, mutedColor);
        ImGui.Spacing();

        if (filteredRows.Count == 0)
        {
            ImGui.TextColored(mutedColor, "No accessible stock matches this filter.");
            return;
        }

        var flags = ImGuiUi.InteractiveTableFlags |
                    ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.ScrollX |
                    ImGuiTableFlags.SizingStretchProp;
        var tableHeight = Math.Max(180f, ImGui.GetContentRegionAvail().Y);
        if (!ImGui.BeginTable("RetainerRestockAccessibleStockRows", 6, flags, new Vector2(0, tableHeight)))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 2.5f);
        ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthFixed, 72);
        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthFixed, 72);
        ImGui.TableSetupColumn("Retainers", ImGuiTableColumnFlags.WidthFixed, 84);
        ImGui.TableSetupColumn("Cache", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultHide, 84);
        ImGui.TableSetupColumn("Sources", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultHide, 2f);
        ImGui.TableHeadersRow();

        foreach (var row in filteredRows)
        {
            var isSelected = state.SelectedStockRow?.ItemId == row.ItemId;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.Selectable($"{row.ItemName}##RetainerRestockStockRow{row.ItemId}", isSelected, ImGuiSelectableFlags.SpanAllColumns))
                state.Stage(row);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.TotalQuantity.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.PlayerQuantity.ToString());

            ImGui.TableNextColumn();
            ImGui.TextColored(row.RetainerQuantity > 0 ? headerColor : mutedColor, row.RetainerQuantity.ToString());

            ImGui.TableNextColumn();
            ImGui.TextColored(row.OldestRetainerCacheAge is null ? mutedColor : headerColor, FormatCacheAge(row));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatStockSources(row));
        }

        ImGui.EndTable();
    }

    private void DrawVisibleRowLimitSelector()
    {
        ImGui.SetNextItemWidth(84);
        if (!ImGui.BeginCombo("Rows##RetainerRestockVisibleRows", state.VisibleRowLimit.ToString(CultureInfo.InvariantCulture)))
            return;

        foreach (var option in RetainerRestockBrowserState.VisibleRowLimitOptions)
        {
            var isSelected = state.VisibleRowLimit == option;
            if (ImGui.Selectable(option.ToString(CultureInfo.InvariantCulture), isSelected))
                state.VisibleRowLimit = option;

            if (isSelected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawPlanQueue(
        RetainerRestockPlan plan,
        Vector4 headerColor,
        Vector4 successColor,
        Vector4 errorColor,
        Vector4 mutedColor)
    {
        ImGuiUi.SectionHeader("Plan Queue", headerColor);
        if (config.RetainerRestockPlanItems.Count == 0)
        {
            ImGui.TextColored(mutedColor, "No restock rows queued.");
            return;
        }

        var previewByPlanItemId = plan.Lines.ToLookup(line => line.PlanItemId);
        var flags = ImGuiUi.InteractiveTableFlags |
                    ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.ScrollX |
                    ImGuiTableFlags.SizingStretchProp;
        var availableHeight = ImGui.GetContentRegionAvail().Y;
        var tableHeight = state.SelectedPlanItemId is null
            ? Math.Max(240f, availableHeight)
            : Math.Max(220f, availableHeight - 116f);
        if (!ImGui.BeginTable("RetainerRestockPlanQueueRows", 7, flags, new Vector2(0, tableHeight)))
            return;

        ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 44);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 2.5f);
        ImGui.TableSetupColumn("Desired", ImGuiTableColumnFlags.WidthFixed, 82);
        ImGui.TableSetupColumn("Need", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Available", ImGuiTableColumnFlags.WidthFixed, 82);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 116);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 84);
        ImGui.TableHeadersRow();

        for (var index = 0; index < config.RetainerRestockPlanItems.Count; index++)
        {
            var item = config.RetainerRestockPlanItems[index];
            var preview = previewByPlanItemId[item.Id].FirstOrDefault();
            var rowId = $"{item.Id:N}_{index}";

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var enabled = item.Enabled;
            if (ImGui.Checkbox($"##RetainerRestockPlanEnabled{rowId}", ref enabled))
            {
                item.Enabled = enabled;
                saveConfig();
            }

            ImGui.TableNextColumn();
            if (ImGui.Selectable($"{FormatItemName(item)}##RetainerRestockPlanRow{rowId}", state.SelectedPlanItemId == item.Id))
                state.SelectedPlanItemId = item.Id;

            ImGui.TableNextColumn();
            var desired = item.DesiredPlayerQuantity;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt($"##RetainerRestockPlanDesired{rowId}", ref desired))
            {
                item.DesiredPlayerQuantity = Math.Max(1, desired);
                saveConfig();
            }

            ImGui.TableNextColumn();
            var needed = preview?.NeededQuantity;
            if (needed is null)
                ImGui.TextColored(mutedColor, "-");
            else
                ImGui.TextColored(needed.Value > 0 ? errorColor : successColor, needed.Value.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatPreviewNumber(preview?.CachedRetainerQuantity));

            ImGui.TableNextColumn();
            if (preview is null)
                ImGui.TextColored(mutedColor, item.Enabled ? "No preview" : "Disabled");
            else
                ImGui.TextColored(
                    GetStatusColor(preview.Status, headerColor, successColor, errorColor, mutedColor),
                    FormatStatus(preview.Status));

            ImGui.TableNextColumn();
            if (ImGuiUi.Button($"Remove##RetainerRestockPlanRemove{rowId}", true))
            {
                config.RetainerRestockPlanItems.RemoveAt(index);
                saveConfig();
                index--;
            }
        }

        ImGui.EndTable();
        DrawSelectedPlanDetails(plan, mutedColor);
    }

    private void DrawSelectedPlanDetails(RetainerRestockPlan plan, Vector4 mutedColor)
    {
        if (state.SelectedPlanItemId is not { } selectedId)
            return;

        var item = config.RetainerRestockPlanItems.FirstOrDefault(candidate => candidate.Id == selectedId);
        if (item is null)
        {
            state.SelectedPlanItemId = null;
            return;
        }

        var preview = plan.Lines.FirstOrDefault(line => line.PlanItemId == selectedId);
        ImGui.Spacing();
        ImGuiUi.SectionHeader("Selected plan row", MarketMafioso.Windows.Main.MarketMafiosoUiTheme.Header);
        ImGui.TextUnformatted(FormatItemName(item));
        ImGui.SameLine();
        ImGui.TextColored(
            mutedColor,
            preview is null
                ? "No current preview."
                : $"Player {preview.PlayerQuantity}; need {preview.NeededQuantity}; retainers {preview.CachedRetainerQuantity}; missing {preview.MissingQuantity}; cache {FormatCacheAge(preview.OldestRelevantCacheAge)}.");

        ImGui.TextColored(mutedColor, $"Candidate retainers: {(preview is null ? "-" : FormatCandidates(preview.Candidates))}");
        var note = item.Note ?? string.Empty;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("Note##RetainerRestockSelectedPlanNote", ref note, 160))
        {
            item.Note = string.IsNullOrWhiteSpace(note) ? string.Empty : note;
            saveConfig();
        }
    }

    private void DrawStagedItem(
        Vector4 successColor,
        Vector4 errorColor,
        Vector4 mutedColor)
    {
        if (state.SelectedStockRow is null)
        {
            stagedInputFocusItemId = null;
            ImGui.TextColored(mutedColor, "Select an item below to add it to the plan.");
        }
        else
        {
            ImGui.TextUnformatted(state.SelectedStockRow.ItemName);
            ImGui.SameLine();
            ImGui.TextColored(
                mutedColor,
                $"Accessible {state.SelectedStockRow.TotalQuantity}; player {state.SelectedStockRow.PlayerQuantity}; retainers {state.SelectedStockRow.RetainerQuantity}; cache {FormatCacheAge(state.SelectedStockRow)}");
        }

        var desiredQuantityText = state.StagedDesiredQuantityText;
        ImGui.TextColored(mutedColor, "Desired quantity");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(140);
        if (state.SelectedStockRow is not null && stagedInputFocusItemId != state.SelectedStockRow.ItemId)
        {
            ImGui.SetKeyboardFocusHere();
            stagedInputFocusItemId = state.SelectedStockRow.ItemId;
        }

        if (state.SelectedStockRow is null)
            ImGui.BeginDisabled();
        if (ImGui.InputText("##RetainerRestockStagedDesired", ref desiredQuantityText, 32, ImGuiInputTextFlags.CharsDecimal))
            state.StagedDesiredQuantityText = desiredQuantityText;
        if (state.SelectedStockRow is null)
            ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGuiUi.PrimaryButton("Add to plan##RetainerRestockSaveStaged", state.CanSaveStagedItem))
        {
            if (state.ApplyStagedItem(config.RetainerRestockPlanItems))
                saveConfig();
        }

        ImGui.SameLine();
        if (ImGuiUi.Button("Clear Selection##RetainerRestockClearStaged", state.SelectedStockRow is not null))
        {
            state.ClearStagedItem();
            stagedInputFocusItemId = null;
        }

        var validationColor = state.SelectedStockRow is null
            ? mutedColor
            : state.CanSaveStagedItem ? successColor : errorColor;
        ImGui.TextColored(validationColor, state.StagedValidationMessage);
    }

    private static string FormatStockSources(RetainerRestockStockRow row)
    {
        if (row.Sources.Count > 0)
        {
            var sources = row.Sources
                .Take(3)
                .Select(source => $"{source.SourceName} x{source.Quantity}");
            var suffix = row.Sources.Count > 3 ? $" +{row.Sources.Count - 3}" : string.Empty;
            return string.Join(", ", sources) + suffix;
        }

        return row.PlayerQuantity > 0
            ? $"Player x{row.PlayerQuantity}"
            : "-";
    }

    private static string FormatItemName(RetainerRestockPlanItem item) =>
        string.IsNullOrWhiteSpace(item.ItemName) ? $"Item {item.ItemId}" : item.ItemName;

    private static string FormatPreviewNumber(int? value) =>
        value is null ? "-" : value.Value.ToString();

    private static string FormatCandidates(IReadOnlyList<RetainerRestockCandidate> candidates)
    {
        if (candidates.Count == 0)
            return "-";

        var visible = candidates
            .Take(3)
            .Select(candidate => $"{candidate.RetainerName} x{candidate.CachedQuantity}");
        var suffix = candidates.Count > 3 ? $" +{candidates.Count - 3}" : string.Empty;
        return string.Join(", ", visible) + suffix;
    }

    private static string FormatCacheAge(RetainerRestockStockRow row) =>
        FormatCacheAge(row.OldestRetainerCacheAge);

    private static string FormatCacheAge(TimeSpan? age)
    {
        if (age is null)
            return "-";

        if (age.Value.TotalDays >= 1)
            return $"{age.Value.TotalDays:0.0}d";

        if (age.Value.TotalHours >= 1)
            return $"{age.Value.TotalHours:0.0}h";

        return $"{Math.Max(0, age.Value.TotalMinutes):0}m";
    }

    private static string FormatStatus(RetainerRestockPlanLineStatus status)
    {
        return status switch
        {
            RetainerRestockPlanLineStatus.NoNeed => "No need",
            RetainerRestockPlanLineStatus.Ready => "Ready",
            RetainerRestockPlanLineStatus.Partial => "Partial",
            RetainerRestockPlanLineStatus.NoCachedStock => "No cached stock",
            _ => status.ToString(),
        };
    }

    private static Vector4 GetStatusColor(
        RetainerRestockPlanLineStatus status,
        Vector4 headerColor,
        Vector4 successColor,
        Vector4 errorColor,
        Vector4 mutedColor)
    {
        return status switch
        {
            RetainerRestockPlanLineStatus.NoNeed or RetainerRestockPlanLineStatus.Ready => successColor,
            RetainerRestockPlanLineStatus.Partial => headerColor,
            RetainerRestockPlanLineStatus.NoCachedStock => errorColor,
            _ => mutedColor,
        };
    }
}
