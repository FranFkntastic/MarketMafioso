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

    public void Draw(
        IReadOnlyList<RetainerRestockStockRow> stockRows,
        RetainerRestockPlan plan,
        Vector4 headerColor,
        Vector4 successColor,
        Vector4 errorColor,
        Vector4 mutedColor)
    {
        ArgumentNullException.ThrowIfNull(stockRows);
        ArgumentNullException.ThrowIfNull(plan);

        var layoutFlags = ImGuiTableFlags.Resizable |
                          ImGuiTableFlags.SizingStretchProp |
                          ImGuiTableFlags.NoSavedSettings;
        if (!ImGui.BeginTable("RetainerRestockBrowserLayout", 2, layoutFlags, new Vector2(0, 0)))
            return;

        ImGui.TableSetupColumn("Accessible Stock", ImGuiTableColumnFlags.WidthStretch, 1.15f);
        ImGui.TableSetupColumn("Plan Queue", ImGuiTableColumnFlags.WidthStretch, 0.85f);
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        DrawAccessibleStock(stockRows, headerColor, mutedColor);

        ImGui.TableNextColumn();
        DrawPlanQueue(plan, headerColor, successColor, errorColor, mutedColor);

        ImGui.EndTable();
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

        var filteredRows = state.FilterRows(stockRows);
        ImGui.SameLine();
        ImGui.TextColored(
            mutedColor,
            $"{filteredRows.Count.ToString("N0", CultureInfo.InvariantCulture)} / {stockRows.Count.ToString("N0", CultureInfo.InvariantCulture)} items");

        if (filteredRows.Count == 0)
        {
            ImGui.TextColored(mutedColor, "No accessible stock matches this filter.");
            return;
        }

        var flags = ImGuiUi.InteractiveTableFlags |
                    ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.ScrollX |
                    ImGuiTableFlags.SizingStretchProp;
        var tableHeight = Math.Max(220f, ImGui.GetContentRegionAvail().Y);
        if (!ImGui.BeginTable("RetainerRestockAccessibleStockRows", 6, flags, new Vector2(0, tableHeight)))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthFixed, 180);
        ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthFixed, 64);
        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthFixed, 64);
        ImGui.TableSetupColumn("Retainers", ImGuiTableColumnFlags.WidthFixed, 76);
        ImGui.TableSetupColumn("Cache", ImGuiTableColumnFlags.WidthFixed, 78);
        ImGui.TableSetupColumn("Sources", ImGuiTableColumnFlags.WidthFixed, 220);
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

    private void DrawPlanQueue(
        RetainerRestockPlan plan,
        Vector4 headerColor,
        Vector4 successColor,
        Vector4 errorColor,
        Vector4 mutedColor)
    {
        ImGuiUi.SectionHeader("Plan Queue", headerColor);
        DrawStagedItem(successColor, errorColor, mutedColor);

        ImGui.Spacing();
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
        var tableHeight = Math.Max(240f, ImGui.GetContentRegionAvail().Y);
        if (!ImGui.BeginTable("RetainerRestockPlanQueueRows", 11, flags, new Vector2(0, tableHeight)))
            return;

        ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 44);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthFixed, 170);
        ImGui.TableSetupColumn("Desired", ImGuiTableColumnFlags.WidthFixed, 76);
        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthFixed, 64);
        ImGui.TableSetupColumn("Need", ImGuiTableColumnFlags.WidthFixed, 64);
        ImGui.TableSetupColumn("Retainers", ImGuiTableColumnFlags.WidthFixed, 76);
        ImGui.TableSetupColumn("Missing", ImGuiTableColumnFlags.WidthFixed, 72);
        ImGui.TableSetupColumn("Cache", ImGuiTableColumnFlags.WidthFixed, 78);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 104);
        ImGui.TableSetupColumn("Candidates / Note", ImGuiTableColumnFlags.WidthFixed, 220);
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
            ImGui.TextUnformatted(FormatItemName(item));

            ImGui.TableNextColumn();
            var desired = item.DesiredPlayerQuantity;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputInt($"##RetainerRestockPlanDesired{rowId}", ref desired))
            {
                item.DesiredPlayerQuantity = Math.Max(1, desired);
                saveConfig();
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatPreviewNumber(preview?.PlayerQuantity));

            ImGui.TableNextColumn();
            var needed = preview?.NeededQuantity;
            if (needed is null)
                ImGui.TextColored(mutedColor, "-");
            else
                ImGui.TextColored(needed.Value > 0 ? errorColor : successColor, needed.Value.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatPreviewNumber(preview?.CachedRetainerQuantity));

            ImGui.TableNextColumn();
            var missing = preview?.MissingQuantity;
            if (missing is null)
                ImGui.TextColored(mutedColor, "-");
            else
                ImGui.TextColored(missing.Value > 0 ? errorColor : successColor, missing.Value.ToString());

            ImGui.TableNextColumn();
            ImGui.TextColored(
                preview?.OldestRelevantCacheAge is null ? mutedColor : headerColor,
                FormatCacheAge(preview?.OldestRelevantCacheAge));

            ImGui.TableNextColumn();
            if (preview is null)
                ImGui.TextColored(mutedColor, item.Enabled ? "No preview" : "Disabled");
            else
                ImGui.TextColored(
                    GetStatusColor(preview.Status, headerColor, successColor, errorColor, mutedColor),
                    FormatStatus(preview.Status));

            ImGui.TableNextColumn();
            DrawCandidatesAndNote(item, preview, mutedColor, rowId);

            ImGui.TableNextColumn();
            if (ImGuiUi.Button($"Remove##RetainerRestockPlanRemove{rowId}", true))
            {
                config.RetainerRestockPlanItems.RemoveAt(index);
                saveConfig();
                index--;
            }
        }

        ImGui.EndTable();
    }

    private void DrawStagedItem(
        Vector4 successColor,
        Vector4 errorColor,
        Vector4 mutedColor)
    {
        if (state.SelectedStockRow is null)
        {
            stagedInputFocusItemId = null;
            ImGui.TextColored(mutedColor, "Select stock to stage a plan row.");
        }
        else
        {
            ImGui.TextColored(mutedColor, "Staged item");
            ImGui.TextUnformatted(state.SelectedStockRow.ItemName);
            ImGui.TextColored(
                mutedColor,
                $"Accessible {state.SelectedStockRow.TotalQuantity}; player {state.SelectedStockRow.PlayerQuantity}; retainers {state.SelectedStockRow.RetainerQuantity}; cache {FormatCacheAge(state.SelectedStockRow)}");
        }

        var desiredQuantityText = state.StagedDesiredQuantityText;
        ImGui.SetNextItemWidth(140);
        if (state.SelectedStockRow is not null && stagedInputFocusItemId != state.SelectedStockRow.ItemId)
        {
            ImGui.SetKeyboardFocusHere();
            stagedInputFocusItemId = state.SelectedStockRow.ItemId;
        }

        if (ImGui.InputText("Desired##RetainerRestockStagedDesired", ref desiredQuantityText, 32, ImGuiInputTextFlags.CharsDecimal))
            state.StagedDesiredQuantityText = desiredQuantityText;

        ImGui.SameLine();
        if (ImGuiUi.Button("Save To Plan##RetainerRestockSaveStaged", state.CanSaveStagedItem))
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

        ImGui.TextColored(state.CanSaveStagedItem ? successColor : errorColor, state.StagedValidationMessage);
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

    private void DrawCandidatesAndNote(
        RetainerRestockPlanItem item,
        RetainerRestockPlanLine? preview,
        Vector4 mutedColor,
        string rowId)
    {
        var summary = preview is null
            ? "-"
            : FormatCandidates(preview.Candidates);
        ImGui.TextColored(preview is null || preview.Candidates.Count == 0 ? mutedColor : Vector4.One, summary);

        var note = item.Note ?? string.Empty;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText($"##RetainerRestockPlanNote{rowId}", ref note, 160))
        {
            item.Note = string.IsNullOrWhiteSpace(note) ? string.Empty : note;
            saveConfig();
        }
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
