using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.UI.Filtering;
using Franthropy.FFXIV.Filtering;
using Franthropy.Filtering.Diagnostics;
using MarketMafioso.RetainerRestock;
using MarketMafioso.Windows.Main;

namespace MarketMafioso.Windows.RetainerRestock;

public sealed class RetainerRestockBrowserPanel
{
    private readonly Configuration config;
    private readonly RetainerRestockBrowserState state;
    private readonly Action saveConfig;
    private readonly RetainerBrowseQueryController queryController = new();
    private uint? stagedInputFocusItemId;
    private Vector2 filterReferenceAnchor;

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
        RetainerBrowseProjection projection,
        Vector4 headerColor,
        Vector4 mutedColor)
    {
        ArgumentNullException.ThrowIfNull(projection);
        state.EnsureScope(projection);

        DrawBrowseToolbar(projection, headerColor, mutedColor);
        ImGui.Spacing();

        if (state.Mode == RetainerBrowseQueryMode.Items)
        {
            var result = queryController.QueryItems(projection, state.ItemsFilter.Expression, state.SelectedScopeKey);
            state.RetainAvailableExpansions(result.Items);
            state.RebindSelectedItem(result.Items);
            DrawFilterStatus(result.Filter, mutedColor);
            DrawStagedItem(MarketMafiosoUiTheme.Success, MarketMafiosoUiTheme.Error, mutedColor);
            ImGui.Spacing();
            DrawItemsTable(result.Items, headerColor, mutedColor);
        }
        else
        {
            var result = queryController.QueryListings(projection, state.ListingsFilter.Expression, state.SelectedScopeKey);
            DrawFilterStatus(result.Filter, mutedColor);
            DrawListingsTable(result.Listings, headerColor, mutedColor);
        }
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

    private void DrawBrowseToolbar(
        RetainerBrowseProjection projection,
        Vector4 headerColor,
        Vector4 mutedColor)
    {
        ImGuiUi.SectionHeader("Browse stock", headerColor);

        if (ImGui.RadioButton("Items##RetainerBrowseMode", state.Mode == RetainerBrowseQueryMode.Items))
            state.SelectMode(RetainerBrowseQueryMode.Items);
        ImGui.SameLine();
        if (ImGui.RadioButton("Listings##RetainerBrowseMode", state.Mode == RetainerBrowseQueryMode.Listings))
            state.SelectMode(RetainerBrowseQueryMode.Listings);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(220);
        var selectedScope = projection.Scopes.First(scope => scope.Key == state.SelectedScopeKey);
        if (ImGui.BeginCombo("##RetainerBrowseScope", selectedScope.DisplayName))
        {
            foreach (var scope in projection.Scopes)
            {
                var selected = string.Equals(scope.Key, state.SelectedScopeKey, StringComparison.Ordinal);
                if (ImGui.Selectable($"{scope.DisplayName}##RetainerBrowseScope{scope.Key}", selected))
                {
                    state.SelectedScopeKey = scope.Key;
                    state.ClearStagedItem();
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.TextColored(mutedColor, state.Mode == RetainerBrowseQueryMode.Items
            ? $"{projection.GetItemGroups(state.SelectedScopeKey).Count:N0} item types"
            : $"{projection.GetListings(state.SelectedScopeKey).Count:N0} listings");

        var filterWidth = Math.Max(320f, ImGui.GetContentRegionAvail().X - 34f);
        if (state.Mode == RetainerBrowseQueryMode.Items)
        {
            DalamudFilterAutocompleteRenderer.Draw(
                "RetainerBrowseItems",
                "Filter items (darksteel, quantity:>100, retainer:name)...",
                queryController.GetItemsContext(projection, state.SelectedScopeKey),
                state.ItemsFilter,
                filterWidth);
        }
        else
        {
            DalamudFilterAutocompleteRenderer.Draw(
                "RetainerBrowseListings",
                "Filter listings (is:hq, price:<1000, retainer:name)...",
                queryController.GetListingsContext(projection, state.SelectedScopeKey),
                state.ListingsFilter,
                filterWidth);
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("?##RetainerBrowseFilterReference"))
            state.FilterReferenceRequested = true;
        filterReferenceAnchor = new Vector2(ImGui.GetItemRectMax().X, ImGui.GetItemRectMax().Y + 4);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Filter reference");
        if (state.FilterReferenceRequested)
        {
            ImGui.OpenPopup("##RetainerBrowseFilterReferencePopup");
            state.FilterReferenceRequested = false;
        }

        DrawFilterReference(projection);
    }

    private void DrawFilterReference(RetainerBrowseProjection projection)
    {
        ImGui.SetNextWindowPos(filterReferenceAnchor, ImGuiCond.Appearing, new Vector2(1, 0));
        ImGui.SetNextWindowSizeConstraints(new Vector2(410, 0), new Vector2(620, 460));
        if (!ImGui.BeginPopup("##RetainerBrowseFilterReferencePopup"))
            return;

        ImGui.TextColored(MarketMafiosoUiTheme.Header, state.Mode == RetainerBrowseQueryMode.Items
            ? "Filter item ownership"
            : "Filter retainer listings");
        ImGui.Separator();
        ImGui.TextWrapped(state.Mode == RetainerBrowseQueryMode.Items
            ? "darksteel   quantity:>100   retainer:name   -retainer:other"
            : "is:hq   price:<1000   quantity:20..99   retainer:name");
        ImGui.Spacing();

        var reference = queryController.GetReference(projection, state.Mode, state.SelectedScopeKey);
        if (ImGui.BeginTable("##RetainerBrowseFilterFields", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Field", ImGuiTableColumnFlags.WidthFixed, 125);
            ImGui.TableSetupColumn("Matches", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();
            foreach (var field in reference.Fields.Where(field => field.IsAvailable))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(field.PreferredName);
                ImGui.TableNextColumn();
                ImGui.TextWrapped(field.Description);
            }
            ImGui.EndTable();
        }

        ImGui.EndPopup();
    }

    private static void DrawFilterStatus(RetainerBrowseFilterStatus status, Vector4 mutedColor)
    {
        var diagnostic = status.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Severity == FilterDiagnosticSeverity.Error)
                         ?? status.Diagnostics.FirstOrDefault();
        if (diagnostic is not null)
        {
            ImGui.TextColored(
                diagnostic.Severity == FilterDiagnosticSeverity.Error ? MarketMafiosoUiTheme.Error : MarketMafiosoUiTheme.Warning,
                diagnostic.Message);
        }

        if (status.IsShowingLastValidResults)
            ImGui.TextColored(mutedColor, "Showing results from the last valid filter while this expression is incomplete.");
    }

    private void DrawItemsTable(
        IReadOnlyList<RetainerBrowseItemGroup> source,
        Vector4 headerColor,
        Vector4 mutedColor)
    {
        if (source.Count == 0)
        {
            ImGui.TextColored(mutedColor, "No item ownership matches this filter and scope.");
            return;
        }

        var flags = ImGuiUi.InteractiveTableFlags |
                    ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.ScrollX |
                    ImGuiTableFlags.Sortable |
                    ImGuiTableFlags.SizingStretchProp;
        var tableHeight = Math.Max(220f, ImGui.GetContentRegionAvail().Y);
        if (!ImGui.BeginTable("RetainerBrowseItems", 7, flags, new Vector2(0, tableHeight)))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort, 2.5f);
        ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthFixed, 76);
        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthFixed, 76);
        ImGui.TableSetupColumn("Retainers", ImGuiTableColumnFlags.WidthFixed, 94);
        ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, 72);
        ImGui.TableSetupColumn("Source / storage", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("Condition", ImGuiTableColumnFlags.WidthFixed, 88);
        ImGui.TableHeadersRow();

        foreach (var item in SortItems(source, ImGui.TableGetSortSpecs()))
        {
            var selected = state.SelectedItemGroup?.ItemId == item.ItemId;
            var expanded = state.IsExpanded(item.ItemId);
            var hqQuantity = item.Stacks.Where(stack => stack.Quality == FfxivItemQuality.HQ).Sum(stack => stack.Quantity);
            var retainerCount = item.Retainers.Count;
            var primary = item.Stacks.OrderByDescending(stack => stack.Quantity).ThenBy(stack => stack.OwnerName, StringComparer.OrdinalIgnoreCase).First();
            var lowestCondition = item.Stacks.Where(stack => stack.Condition.IsKnown).Select(stack => stack.Condition.Value).DefaultIfEmpty().Min();
            var hasCondition = item.Stacks.Any(stack => stack.Condition.IsKnown);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"{(expanded ? "▼" : "▶")}##RetainerBrowseExpand{item.ItemId}"))
                state.ToggleExpanded(item.ItemId);
            ImGui.SameLine();
            if (ImGui.Selectable($"{item.ItemName}##RetainerBrowseItem{item.ItemId}", selected, ImGuiSelectableFlags.SpanAllColumns))
                state.Stage(item);
            if (!string.IsNullOrWhiteSpace(item.ItemType) && ImGui.IsItemHovered())
                ImGui.SetTooltip(item.ItemType);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.TotalQuantity.ToString("N0", CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.PlayerQuantity.ToString("N0", CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            ImGui.TextColored(item.RetainerQuantity > 0 ? headerColor : mutedColor,
                retainerCount > 0 ? $"{item.RetainerQuantity:N0} ({retainerCount})" : "0");
            ImGui.TableNextColumn();
            ImGui.TextColored(hqQuantity > 0 ? headerColor : mutedColor, hqQuantity.ToString("N0", CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{primary.OwnerName} · {FormatStorage(primary)}");
            ImGui.TableNextColumn();
            ImGui.TextColored(mutedColor, hasCondition ? $"{lowestCondition:0.#}% min" : "-");

            if (!expanded)
                continue;

            foreach (var stack in item.Stacks)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Indent(18);
                ImGui.TextColored(mutedColor, "↳ physical stack");
                ImGui.Unindent(18);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(stack.Quantity.ToString("N0", CultureInfo.InvariantCulture));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(stack.ScopeKind == RetainerBrowseScopeKind.Player ? stack.Quantity.ToString("N0", CultureInfo.InvariantCulture) : "-");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(stack.ScopeKind == RetainerBrowseScopeKind.Retainer ? stack.Quantity.ToString("N0", CultureInfo.InvariantCulture) : "-");
                ImGui.TableNextColumn();
                ImGui.TextColored(stack.Quality == FfxivItemQuality.HQ ? headerColor : mutedColor,
                    stack.Quality == FfxivItemQuality.HQ ? "HQ" : "NQ");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{stack.OwnerName} · {FormatStorage(stack)}");
                ImGui.TableNextColumn();
                ImGui.TextColored(mutedColor, stack.Condition.IsKnown ? $"{stack.Condition.Value:0.#}%" : "-");
            }
        }

        ImGui.EndTable();
    }

    private static void DrawListingsTable(
        IReadOnlyList<RetainerBrowseMarketListing> source,
        Vector4 headerColor,
        Vector4 mutedColor)
    {
        if (source.Count == 0)
        {
            ImGui.TextColored(mutedColor, "No retainer listings match this filter and scope.");
            return;
        }

        var showPrices = source.Any(listing => listing.UnitPrice.IsKnown);
        var columns = showPrices ? 7 : 5;
        var flags = ImGuiUi.InteractiveTableFlags |
                    ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.ScrollX |
                    ImGuiTableFlags.Sortable |
                    ImGuiTableFlags.SizingStretchProp;
        var tableHeight = Math.Max(260f, ImGui.GetContentRegionAvail().Y);
        if (!ImGui.BeginTable("RetainerBrowseListings", columns, flags, new Vector2(0, tableHeight)))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort, 2.4f);
        ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 72);
        ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthFixed, 76);
        ImGui.TableSetupColumn("Condition", ImGuiTableColumnFlags.WidthFixed, 90);
        if (showPrices)
        {
            ImGui.TableSetupColumn("Unit price", ImGuiTableColumnFlags.WidthFixed, 112);
            ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthFixed, 120);
        }
        ImGui.TableHeadersRow();

        foreach (var listing in SortListings(source, ImGui.TableGetSortSpecs(), showPrices))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(listing.ItemName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(listing.RetainerName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(listing.Quantity.ToString("N0", CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();
            ImGui.TextColored(listing.Quality == FfxivItemQuality.HQ ? headerColor : mutedColor,
                listing.Quality == FfxivItemQuality.HQ ? "HQ" : "NQ");
            ImGui.TableNextColumn();
            ImGui.TextColored(mutedColor, listing.Condition.IsKnown ? $"{listing.Condition.Value:0.#}%" : "-");
            if (showPrices)
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(listing.UnitPrice.IsKnown ? $"{listing.UnitPrice.Value:N0} gil" : "Not recorded");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(listing.TotalPrice.IsKnown ? $"{listing.TotalPrice.Value:N0} gil" : "Not recorded");
            }
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
        if (config.RetainerRestockPlanItems.Count == 0)
        {
            ImGui.TextColored(mutedColor, "No withdrawal rows queued. Choose an item in Browse stock to add one.");
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
        ImGui.TableSetupColumn("Observed", ImGuiTableColumnFlags.WidthFixed, 82);
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
        ImGuiUi.SectionHeader("Selected plan row", MarketMafiosoUiTheme.Header);
        ImGui.TextUnformatted(FormatItemName(item));
        ImGui.SameLine();
        ImGui.TextColored(
            mutedColor,
            preview is null
                ? "No current preview."
                : $"Player {preview.PlayerQuantity}; need {preview.NeededQuantity}; observed in retainers {preview.CachedRetainerQuantity}; missing {preview.MissingQuantity}.");

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
        if (state.SelectedItemGroup is null)
        {
            stagedInputFocusItemId = null;
            ImGui.TextColored(mutedColor, "Select an item to add it to the withdrawal plan.");
        }
        else
        {
            ImGui.TextUnformatted(state.SelectedItemGroup.ItemName);
            ImGui.SameLine();
            ImGui.TextColored(
                mutedColor,
                $"Total {state.SelectedItemGroup.TotalQuantity:N0}; player {state.SelectedItemGroup.PlayerQuantity:N0}; retainers {state.SelectedItemGroup.RetainerQuantity:N0}.");
        }

        var desiredQuantityText = state.StagedDesiredQuantityText;
        ImGui.TextColored(mutedColor, "Desired on character");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(140);
        if (state.SelectedItemGroup is not null && stagedInputFocusItemId != state.SelectedItemGroup.ItemId)
        {
            ImGui.SetKeyboardFocusHere();
            stagedInputFocusItemId = state.SelectedItemGroup.ItemId;
        }

        if (state.SelectedItemGroup is null)
            ImGui.BeginDisabled();
        if (ImGui.InputText("##RetainerRestockStagedDesired", ref desiredQuantityText, 32, ImGuiInputTextFlags.CharsDecimal))
            state.StagedDesiredQuantityText = desiredQuantityText;
        if (state.SelectedItemGroup is null)
            ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGuiUi.PrimaryButton("Add to withdrawal plan##RetainerRestockSaveStaged", state.CanSaveStagedItem))
        {
            if (state.ApplyStagedItem(config.RetainerRestockPlanItems))
                saveConfig();
        }

        ImGui.SameLine();
        if (ImGuiUi.Button("Clear selection##RetainerRestockClearStaged", state.SelectedItemGroup is not null))
        {
            state.ClearStagedItem();
            stagedInputFocusItemId = null;
        }

        var validationColor = state.SelectedItemGroup is null
            ? mutedColor
            : state.CanSaveStagedItem ? successColor : errorColor;
        ImGui.TextColored(validationColor, state.StagedValidationMessage);
    }

    private static IReadOnlyList<RetainerBrowseItemGroup> SortItems(
        IReadOnlyList<RetainerBrowseItemGroup> rows,
        ImGuiTableSortSpecsPtr sortSpecs)
    {
        if (sortSpecs.SpecsCount == 0)
            return rows.OrderBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.ItemId).ToArray();

        var spec = sortSpecs.Specs;
        return spec.ColumnIndex switch
        {
            0 => SortBy(rows, row => row.ItemName, spec.SortDirection),
            1 => SortBy(rows, row => row.TotalQuantity, spec.SortDirection),
            2 => SortBy(rows, row => row.PlayerQuantity, spec.SortDirection),
            3 => SortBy(rows, row => row.RetainerQuantity, spec.SortDirection),
            4 => SortBy(rows, row => row.Stacks.Where(stack => stack.Quality == FfxivItemQuality.HQ).Sum(stack => stack.Quantity), spec.SortDirection),
            5 => SortBy(rows, row => row.Stacks.OrderByDescending(stack => stack.Quantity).First().StorageName, spec.SortDirection),
            6 => SortKnownDecimal(rows, row =>
            {
                var values = row.Stacks
                    .Where(stack => stack.Condition.IsKnown)
                    .Select(stack => stack.Condition.Value)
                    .ToArray();
                return values.Length == 0 ? (false, 0m) : (true, values.Min());
            }, spec.SortDirection),
            _ => rows,
        };
    }

    private static IReadOnlyList<RetainerBrowseMarketListing> SortListings(
        IReadOnlyList<RetainerBrowseMarketListing> rows,
        ImGuiTableSortSpecsPtr sortSpecs,
        bool showPrices)
    {
        if (sortSpecs.SpecsCount == 0)
            return rows.OrderBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.RetainerName, StringComparer.OrdinalIgnoreCase).ToArray();

        var spec = sortSpecs.Specs;
        return spec.ColumnIndex switch
        {
            0 => SortBy(rows, row => row.ItemName, spec.SortDirection),
            1 => SortBy(rows, row => row.RetainerName, spec.SortDirection),
            2 => SortBy(rows, row => row.Quantity, spec.SortDirection),
            3 => SortBy(rows, row => row.Quality, spec.SortDirection),
            4 => SortKnownDecimal(rows, row => row.Condition.IsKnown ? (true, row.Condition.Value) : (false, 0m), spec.SortDirection),
            5 when showPrices => SortKnownDecimal(rows, row => row.UnitPrice.IsKnown ? (true, row.UnitPrice.Value) : (false, 0m), spec.SortDirection),
            6 when showPrices => SortKnownDecimal(rows, row => row.TotalPrice.IsKnown ? (true, row.TotalPrice.Value) : (false, 0m), spec.SortDirection),
            _ => rows,
        };
    }

    private static IReadOnlyList<T> SortKnownDecimal<T>(
        IReadOnlyList<T> rows,
        Func<T, (bool IsKnown, decimal Value)> selector,
        ImGuiSortDirection direction)
    {
        var knownFirst = rows.OrderBy(row => selector(row).IsKnown ? 0 : 1);
        return direction == ImGuiSortDirection.Descending
            ? knownFirst.ThenByDescending(row => selector(row).Value).ToArray()
            : knownFirst.ThenBy(row => selector(row).Value).ToArray();
    }

    private static IReadOnlyList<T> SortBy<T, TKey>(
        IReadOnlyList<T> rows,
        Func<T, TKey> selector,
        ImGuiSortDirection direction)
    {
        var ordered = direction == ImGuiSortDirection.Descending
            ? rows.OrderByDescending(selector)
            : rows.OrderBy(selector);
        return ordered.ToArray();
    }

    private static string FormatStorage(RetainerBrowseStockStack stack)
    {
        var storage = stack.StorageName
            .Replace("RetainerPage", "Retainer page ", StringComparison.OrdinalIgnoreCase)
            .Replace("Inventory", "Inventory ", StringComparison.OrdinalIgnoreCase)
            .Trim();
        return stack.SlotIndex is { } slot ? $"{storage} · slot {slot + 1}" : storage;
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

    private static string FormatStatus(RetainerRestockPlanLineStatus status) => status switch
    {
        RetainerRestockPlanLineStatus.NoNeed => "No need",
        RetainerRestockPlanLineStatus.Ready => "Ready",
        RetainerRestockPlanLineStatus.Partial => "Partial",
        RetainerRestockPlanLineStatus.NoCachedStock => "No observed stock",
        _ => status.ToString(),
    };

    private static Vector4 GetStatusColor(
        RetainerRestockPlanLineStatus status,
        Vector4 headerColor,
        Vector4 successColor,
        Vector4 errorColor,
        Vector4 mutedColor) => status switch
        {
            RetainerRestockPlanLineStatus.NoNeed or RetainerRestockPlanLineStatus.Ready => successColor,
            RetainerRestockPlanLineStatus.Partial => headerColor,
            RetainerRestockPlanLineStatus.NoCachedStock => errorColor,
            _ => mutedColor,
        };
}
