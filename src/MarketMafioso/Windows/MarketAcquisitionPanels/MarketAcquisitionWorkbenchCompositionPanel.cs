using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Windows.Main;

namespace MarketMafioso.Windows.MarketAcquisitionPanels;

public sealed class MarketAcquisitionWorkbenchCompositionPanel
{
    private const int NameMaxLength = 80;
    private const string NamePopupId = "Name composition##AcquisitionCompositionName";
    private const string UpdatePopupId = "Update saved composition?##AcquisitionCompositionUpdateConfirmation";
    private const string LoadPopupId = "Discard finalized plan?##AcquisitionCompositionLoadConfirmation";
    private const string DeletePopupId = "Delete saved composition?##AcquisitionCompositionDeleteConfirmation";

    private static readonly AgentBridgeActionArgumentSchema NameSchema = new(
        [new("name", AgentBridgeActionArgumentKind.String)]);

    private readonly MarketAcquisitionWorkbenchCompositionCatalog catalog;
    private readonly Action<MarketAcquisitionWorkbenchComposition, string, string> loadComposition;
    private readonly Action<MarketAcquisitionWorkbenchComposition> mergeComposition;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private string searchBuffer = string.Empty;
    private string nameBuffer = string.Empty;
    private string? renameCompositionId;
    private bool focusNameInput;
    private string? updateTargetId;
    private string? pendingPopup;
    private bool closePopup;
    private bool selectSourceOnNextDraw = true;
    private int shownStatusVersion;
    private MarketAcquisitionRequestDocument? statusDocument;
    private DiffCache sourceDiff;
    private DiffCache selectedDiff;

    public MarketAcquisitionWorkbenchCompositionPanel(
        MarketAcquisitionWorkbenchCompositionCatalog catalog,
        Action<MarketAcquisitionWorkbenchComposition, string, string> loadComposition,
        Action<MarketAcquisitionWorkbenchComposition> mergeComposition,
        AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.loadComposition = loadComposition ?? throw new ArgumentNullException(nameof(loadComposition));
        this.mergeComposition = mergeComposition ?? throw new ArgumentNullException(nameof(mergeComposition));
        this.reviewRegistry = reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry));
        shownStatusVersion = catalog.StatusVersion;
    }

    public int Count => catalog.Compositions.Count;

    /// <summary>Opening the window lands on the composition the Workbench came from, so Update targets it.</summary>
    public void OnOpened() => selectSourceOnNextDraw = true;

    public void Draw(MarketAcquisitionWorkbenchCompositionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var source = catalog.SourceFor(context.Document.LocalRequestId);
        if (selectSourceOnNextDraw)
        {
            selectSourceOnNextDraw = false;
            if (source is not null && source.Id != catalog.SelectedCompositionId)
                catalog.Select(source.Id);
        }

        DrawSourceStrip(context, source);
        DrawManagerLayout(context, source);
        DrawStatus(context.Document);

        if (pendingPopup is not null)
        {
            ImGui.OpenPopup(pendingPopup);
            pendingPopup = null;
            closePopup = false;
        }

        DrawNamePopup(context);
        DrawUpdateConfirmation(context);
        DrawLoadConfirmation(context);
        DrawDeleteConfirmation();
    }

    private void DrawSourceStrip(
        MarketAcquisitionWorkbenchCompositionContext context,
        MarketAcquisitionWorkbenchComposition? source)
    {
        if (source is null)
            return;

        var link = catalog.Source!;
        ImGui.TextColored(MainWindow.ColMuted, link.LoadedFromComposition ? "Workbench loaded from" : "Workbench saved as");
        ImGui.SameLine();
        ImGui.TextColored(MainWindow.ColHeader, source.Name);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Select {source.Name}");
        if (ImGui.IsItemClicked() && source.Id != catalog.SelectedCompositionId)
            SelectComposition(source.Id);

        var changed = DiffFor(ref sourceDiff, source, context.Document);
        ImGui.SameLine();
        ImGui.TextColored(MainWindow.ColMuted, "·");
        ImGui.SameLine();
        if (changed.IsEmpty)
        {
            ImGui.TextColored(MainWindow.ColMuted, "unchanged");
        }
        else
        {
            ImGui.TextColored(MainWindow.ColWarning, DescribeChangeCount(changed));
            if (ImGui.IsItemHovered())
                DrawDiffTooltip(changed);
        }

        reviewRegistry.RegisterLastItem(
            "acquisition.composition.source",
            $"Workbench source composition {source.Name}",
            AgentBridgeUiControlKind.Select,
            true,
            source.Id == catalog.SelectedCompositionId,
            $"{source.Id};changedLines={changed.ChangedLineCount}",
            () => SelectComposition(source.Id));
        ImGui.Separator();
    }

    private void DrawManagerLayout(
        MarketAcquisitionWorkbenchCompositionContext context,
        MarketAcquisitionWorkbenchComposition? source)
    {
        var flags = ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.SizingStretchProp |
                    ImGuiTableFlags.NoSavedSettings;
        if (!ImGui.BeginTable("AcquisitionCompositionManagerLayout##v2", 2, flags, new Vector2(0, 0)))
            return;

        ImGui.TableSetupColumn("Compositions", ImGuiTableColumnFlags.WidthStretch, 1.05f);
        ImGui.TableSetupColumn("Selected composition", ImGuiTableColumnFlags.WidthStretch, 0.95f);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        DrawListToolbar(context);
        DrawCompositionList(context, source);
        ImGui.TableNextColumn();
        DrawSelectedComposition(context, source);
        ImGui.EndTable();
    }

    private void DrawListToolbar(MarketAcquisitionWorkbenchCompositionContext context)
    {
        const string saveLabel = "Save Workbench as...";
        var buttonWidth = ImGui.CalcTextSize(saveLabel).X + (ImGui.GetStyle().FramePadding.X * 2f);
        ImGui.SetNextItemWidth(Math.Max(80f, ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X));
        ImGui.InputTextWithHint("##AcquisitionCompositionSearch", "Search compositions...", ref searchBuffer, 128);
        ImGui.SameLine();
        var canSave = context.Document.Lines.Count > 0;
        if (ImGuiUi.PrimaryButton(saveLabel, canSave))
            OpenSaveAs();
        DisabledReasonTooltip(canSave, "The Workbench has no lines to save.");
        RegisterLastControl(
            "acquisition.composition.save-new",
            "Name and save the current Workbench as a new composition",
            canSave,
            null,
            OpenSaveAs);
        ImGui.Spacing();
    }

    private void DrawCompositionList(
        MarketAcquisitionWorkbenchCompositionContext context,
        MarketAcquisitionWorkbenchComposition? source)
    {
        var search = searchBuffer.Trim();
        var compositions = catalog.Compositions
            .Where(composition => search.Length == 0 || composition.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(composition => composition.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (compositions.Count == 0)
        {
            ImGui.TextColored(MainWindow.ColMuted, catalog.Compositions.Count == 0
                ? "No compositions saved."
                : "No compositions match this search.");
            return;
        }

        var flags = ImGuiUi.InteractiveTableFlags | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit;
        var height = Math.Max(160f, ImGui.GetContentRegionAvail().Y - ImGui.GetFrameHeightWithSpacing());
        if (!ImGui.BeginTable("AcquisitionCompositionList##v2", 5, flags, new Vector2(0, height)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoHide);
        ImGui.TableSetupColumn("Region", ImGuiTableColumnFlags.WidthFixed, 44f);
        ImGui.TableSetupColumn("Lines", ImGuiTableColumnFlags.WidthFixed, 38f);
        ImGui.TableSetupColumn("Up to", ImGuiTableColumnFlags.WidthFixed, 66f);
        ImGui.TableSetupColumn("Updated", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableHeadersRow();

        var now = DateTimeOffset.UtcNow;
        foreach (var composition in compositions)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var selected = composition.Id == catalog.SelectedCompositionId;
            var isSource = composition.Id == source?.Id;
            if (isSource)
                ImGui.PushStyleColor(ImGuiCol.Text, MainWindow.ColHeader);
            var clicked = ImGui.Selectable(
                $"{composition.Name}##AcquisitionCompositionList{composition.Id}",
                selected,
                ImGuiSelectableFlags.SpanAllColumns);
            if (isSource)
                ImGui.PopStyleColor();
            if (clicked)
                SelectComposition(composition.Id);
            RegisterLastControl(
                $"acquisition.composition.select.{composition.Id}",
                $"Select saved composition {composition.Name}",
                true,
                composition.Id,
                () => SelectComposition(composition.Id),
                AgentBridgeUiControlKind.Select,
                selected);
            DrawRowContextMenu(composition);

            ImGui.TableNextColumn();
            DrawRegionChip(composition.Region, context.CharacterRegion);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(composition.Lines.Count.ToString("N0", CultureInfo.CurrentCulture));

            ImGui.TableNextColumn();
            var spend = MarketAcquisitionWorkbenchCompositionInsights.Spend(composition.Lines);
            ImGui.TextUnformatted(MarketAcquisitionWorkbenchCompositionInsights.FormatSpendCell(spend));
            if (ImGui.IsItemHovered())
                DrawSpendTooltip(spend);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(MarketAcquisitionWorkbenchCompositionInsights.FormatShortDate(composition.UpdatedAtUtc, now));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(composition.UpdatedAtUtc.ToLocalTime().ToString("f", CultureInfo.CurrentCulture));
        }

        ImGui.EndTable();
    }

    private void DrawRowContextMenu(MarketAcquisitionWorkbenchComposition composition)
    {
        if (!ImGui.BeginPopupContextItem($"##AcquisitionCompositionRowMenu{composition.Id}"))
            return;

        if (composition.Id != catalog.SelectedCompositionId)
            SelectComposition(composition.Id);
        ImGui.TextColored(MainWindow.ColMuted, composition.Name);
        ImGui.Separator();
        if (ImGui.MenuItem("Rename..."))
            OpenRename(composition.Id);
        if (ImGui.MenuItem("Duplicate"))
            catalog.DuplicateSelected();
        ImGui.Separator();
        if (ImGui.MenuItem("Delete..."))
            pendingPopup = DeletePopupId;
        ImGui.EndPopup();
    }

    private void DrawSelectedComposition(
        MarketAcquisitionWorkbenchCompositionContext context,
        MarketAcquisitionWorkbenchComposition? source)
    {
        var selected = catalog.SelectedComposition;
        if (selected is null)
        {
            ImGui.TextColored(MainWindow.ColMuted, "Select a composition to preview it.");
            return;
        }

        ImGui.TextColored(MainWindow.ColHeader, selected.Name);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Right-click it in the list to rename, duplicate or delete.");
        RegisterManageControls(selected);
        DrawPreviewSummary(selected, context.CharacterRegion);
        ImGui.Spacing();
        DrawPreviewTable(selected);
        ImGui.Spacing();
        DrawSelectedActions(context, selected, source);
    }

    private static void DrawPreviewSummary(MarketAcquisitionWorkbenchComposition selected, string? characterRegion)
    {
        var region = MarketAcquisitionWorkbenchCompositionInsights.SafeRegion(selected.Region);
        var mismatch = IsRegionMismatch(selected.Region, characterRegion);
        ImGui.TextColored(mismatch ? MainWindow.ColWarning : MainWindow.ColMuted, region);
        if (mismatch && ImGui.IsItemHovered())
            ImGui.SetTooltip($"Your character is in {characterRegion}. Load uses {region}.");

        var spend = MarketAcquisitionWorkbenchCompositionInsights.Spend(selected.Lines);
        var counts = new List<string>();
        if (spend.StockUpLines > 0)
            counts.Add($"{spend.StockUpLines:N0} stock-up");
        if (spend.BuyBelowLines > 0)
            counts.Add($"{spend.BuyBelowLines:N0} buy-below");
        var route = MarketAcquisitionWorkbenchCompositionInsights.FormatRoute(selected.WorldMode, selected.SweepScope, selected.SweepDataCenters);
        ImGui.SameLine(0, 0);
        ImGui.TextColored(
            MainWindow.ColMuted,
            counts.Count == 0 ? $" · {route}" : $" · {route} · {string.Join(", ", counts)}");
    }

    private static void DrawPreviewTable(MarketAcquisitionWorkbenchComposition selected)
    {
        var showExtra = selected.Lines.Any(line =>
            line.QuantityMode == MarketAcquisitionWorkbenchCompositionInsights.StockUp && line.MaximumOverage > 0);
        var columns = showExtra ? 5 : 4;
        var height = Math.Max(140f, ImGui.GetContentRegionAvail().Y - (ImGui.GetFrameHeightWithSpacing() * 2f));
        var tableId = showExtra ? "AcquisitionCompositionPreview##v2extra" : "AcquisitionCompositionPreview##v2";
        if (!ImGui.BeginTable(tableId, columns, ImGuiUi.InteractiveTableFlags | ImGuiTableFlags.ScrollY, new Vector2(0, height)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoHide);
        ImGui.TableSetupColumn("Buying rule", ImGuiTableColumnFlags.WidthFixed, 112f);
        ImGui.TableSetupColumn("Target on hand", ImGuiTableColumnFlags.WidthFixed, 64f);
        if (showExtra)
            ImGui.TableSetupColumn("Extra units allowed", ImGuiTableColumnFlags.WidthFixed, 56f);
        ImGui.TableSetupColumn("Unit ceiling", ImGuiTableColumnFlags.WidthFixed, 76f);
        ImGui.TableHeadersRow();
        foreach (var line in selected.Lines)
        {
            var stockUp = line.QuantityMode == MarketAcquisitionWorkbenchCompositionInsights.StockUp;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(MarketAcquisitionWorkbenchCompositionInsights.DisplayName(line));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(MarketAcquisitionWorkbenchCompositionInsights.FormatRule(line.QuantityMode));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(stockUp ? line.TargetQuantity.ToString("N0", CultureInfo.CurrentCulture) : "-");
            if (showExtra)
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(stockUp && line.MaximumOverage > 0
                    ? line.MaximumOverage.ToString("N0", CultureInfo.CurrentCulture)
                    : "-");
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.MaxUnitPrice == 0 ? "-" : line.MaxUnitPrice.ToString("N0", CultureInfo.CurrentCulture));
        }

        ImGui.EndTable();
    }

    private void DrawSelectedActions(
        MarketAcquisitionWorkbenchCompositionContext context,
        MarketAcquisitionWorkbenchComposition selected,
        MarketAcquisitionWorkbenchComposition? source)
    {
        var loadBlocker = LoadBlocker(context);
        if (ImGuiUi.PrimaryButton("Load (replace)", loadBlocker is null))
            RequestLoad(context, selected);
        DisabledReasonTooltip(loadBlocker is null, loadBlocker);
        RegisterLastControl(
            "acquisition.composition.load",
            context.HasReadyPreparedPlan
                ? $"Load {selected.Name} into the Workbench, replacing current lines (asks before discarding the finalized plan)"
                : $"Load {selected.Name} into the Workbench, replacing current lines",
            loadBlocker is null,
            selected.Id,
            () => RequestLoad(context, selected));

        ImGui.SameLine();
        var mergeBlocker = context.IsRouteActive
            ? "Stop the active route first."
            : context.IsBusy ? "Wait for the current Workbench operation." : null;
        if (ImGuiUi.Button("Merge", mergeBlocker is null))
            mergeComposition(selected);
        if (mergeBlocker is null && ImGui.IsItemHovered())
            ImGui.SetTooltip("Adds items that are not already on the Workbench.");
        DisabledReasonTooltip(mergeBlocker is null, mergeBlocker);
        RegisterLastControl(
            "acquisition.composition.merge",
            $"Merge {selected.Name} into the Workbench",
            mergeBlocker is null,
            selected.Id,
            () => mergeComposition(selected));

        ImGui.SameLine();
        var diff = DiffFor(ref selectedDiff, selected, context.Document);
        var updateBlocker = context.Document.Lines.Count == 0
            ? "The Workbench has no lines."
            : diff.IsEmpty ? $"The Workbench already matches {selected.Name}." : null;
        var updateLabel = source?.Id == selected.Id ? "Update from Workbench..." : "Overwrite from Workbench...";
        if (ImGuiUi.Button(updateLabel + "##AcquisitionCompositionUpdate", updateBlocker is null))
            OpenUpdate(selected.Id);
        DisabledReasonTooltip(updateBlocker is null, updateBlocker);
        RegisterLastControl(
            "acquisition.composition.update",
            $"Review and update {selected.Name} from the current Workbench",
            updateBlocker is null,
            selected.Id,
            () => OpenUpdate(selected.Id));
    }

    // Rename / Duplicate / Delete live in the row's context menu; keep them reachable for agents
    // on the selected row so the bridge does not lose capabilities it had before.
    private void RegisterManageControls(MarketAcquisitionWorkbenchComposition selected)
    {
        RegisterLastControl(
            "acquisition.composition.rename",
            $"Open the rename dialog for {selected.Name}",
            true,
            selected.Id,
            () => OpenRename(selected.Id));
        RegisterLastControl(
            "acquisition.composition.duplicate",
            $"Duplicate {selected.Name}",
            true,
            selected.Id,
            () => catalog.DuplicateSelected());
        RegisterLastControl(
            "acquisition.composition.delete.open",
            $"Open deletion confirmation for {selected.Name}",
            true,
            selected.Id,
            () => pendingPopup = DeletePopupId);
    }

    private void DrawStatus(MarketAcquisitionRequestDocument document)
    {
        if (catalog.StatusVersion != shownStatusVersion)
        {
            shownStatusVersion = catalog.StatusVersion;
            statusDocument = document;
        }

        // The message is about the last composition action; once the Workbench changes it is stale.
        if (statusDocument is null || !ReferenceEquals(statusDocument, document) || string.IsNullOrEmpty(catalog.Status))
        {
            statusDocument = null;
            return;
        }

        ImGui.TextColored(
            catalog.Status.StartsWith("Deleted", StringComparison.Ordinal) ? MainWindow.ColWarning : MainWindow.ColMuted,
            catalog.Status);
    }

    private void DrawNamePopup(MarketAcquisitionWorkbenchCompositionContext context)
    {
        CenterNextPopup();
        if (!ImGui.BeginPopupModal(NamePopupId, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        if (ConsumeClose())
            return;

        var renaming = renameCompositionId is not null;
        ImGui.TextColored(MainWindow.ColHeader, renaming ? "Rename composition" : "Save Workbench as");
        if (focusNameInput)
        {
            ImGui.SetKeyboardFocusHere();
            focusNameInput = false;
        }

        ImGui.SetNextItemWidth(320f);
        var submitted = ImGui.InputTextWithHint(
            "##AcquisitionCompositionNameInput",
            "Composition name",
            ref nameBuffer,
            NameMaxLength,
            ImGuiInputTextFlags.EnterReturnsTrue);
        reviewRegistry.Register(
            "acquisition.composition.name.input",
            "Composition name",
            AgentBridgeUiControlKind.Input,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            true,
            false,
            nameBuffer,
            NameSchema,
            SetNameFromArguments);

        var problem = NameProblem(context);
        var confirmLabel = renaming ? "Rename" : "Save";
        if (ImGuiUi.Button("Cancel##AcquisitionCompositionNameCancel", true))
            ImGui.CloseCurrentPopup();
        RegisterLastControl(
            "acquisition.composition.name.cancel",
            "Cancel naming the composition",
            true,
            null,
            () => closePopup = true);
        ImGui.SameLine();
        if ((ImGuiUi.PrimaryButton($"{confirmLabel}##AcquisitionCompositionNameConfirm", problem is null) || submitted) &&
            problem is null &&
            CommitName(context.Document))
        {
            ImGui.CloseCurrentPopup();
        }

        DisabledReasonTooltip(problem is null, problem);
        RegisterLastControl(
            "acquisition.composition.name.confirm",
            renaming ? "Rename the composition" : "Save the Workbench as a new composition",
            problem is null,
            nameBuffer,
            () =>
            {
                if (NameProblem(context) is null && CommitName(context.Document))
                    closePopup = true;
            });
        ImGui.EndPopup();
    }

    private void DrawUpdateConfirmation(MarketAcquisitionWorkbenchCompositionContext context)
    {
        CenterNextPopup();
        if (!ImGui.BeginPopupModal(UpdatePopupId, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        if (ConsumeClose())
            return;

        var target = catalog.Find(updateTargetId);
        if (target is null)
        {
            ImGui.TextColored(MainWindow.ColMuted, "This composition is no longer available.");
            if (ImGui.Button("Close##AcquisitionCompositionUpdateClose"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        var diff = MarketAcquisitionWorkbenchCompositionInsights.Compare(target, context.Document);
        ImGui.TextColored(MainWindow.ColMuted, "Update");
        ImGui.SameLine();
        ImGui.TextColored(MainWindow.ColHeader, target.Name);
        ImGui.SameLine();
        ImGui.TextColored(MainWindow.ColMuted, "from the Workbench");
        ImGui.Spacing();
        DrawDiffSummary(diff);
        ImGui.Spacing();
        DrawDiffTable(diff);
        ImGui.Spacing();

        if (ImGuiUi.Button("Cancel##AcquisitionCompositionUpdateCancel", true))
            ImGui.CloseCurrentPopup();
        RegisterLastControl(
            "acquisition.composition.update.cancel",
            "Cancel updating the saved composition",
            true,
            target.Id,
            () => closePopup = true);
        ImGui.SameLine();
        var canConfirm = context.Document.Lines.Count > 0 && !diff.IsEmpty;
        if (ImGuiUi.PrimaryButton("Update composition##AcquisitionCompositionUpdateConfirm", canConfirm))
        {
            catalog.Update(target.Id, context.Document);
            ImGui.CloseCurrentPopup();
        }

        RegisterLastControl(
            "acquisition.composition.update.confirm",
            $"Overwrite {target.Name} with the current Workbench",
            canConfirm,
            $"{target.Id};added={diff.Added.Count};removed={diff.Removed.Count};changed={diff.Changed.Count}",
            () =>
            {
                catalog.Update(target.Id, context.Document);
                closePopup = true;
            });
        ImGui.EndPopup();
    }

    private void DrawLoadConfirmation(MarketAcquisitionWorkbenchCompositionContext context)
    {
        CenterNextPopup();
        if (!ImGui.BeginPopupModal(LoadPopupId, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        if (ConsumeClose())
            return;

        var selected = catalog.SelectedComposition;
        ImGui.TextUnformatted(selected is null
            ? "This composition is no longer available."
            : $"Loading {selected.Name} discards the finalized plan.");
        var canLoad = selected is not null && LoadBlocker(context) is null;
        if (ImGuiUi.Button("Cancel##AcquisitionCompositionLoadCancel", true))
            ImGui.CloseCurrentPopup();
        RegisterLastControl(
            "acquisition.composition.load.cancel",
            "Keep the finalized plan and cancel loading",
            true,
            selected?.Id,
            () => closePopup = true);
        ImGui.SameLine();
        if (ImGuiUi.PrimaryButton("Discard plan and load##AcquisitionCompositionLoadConfirm", canLoad))
        {
            loadComposition(selected!, context.CharacterName, context.World);
            ImGui.CloseCurrentPopup();
        }

        RegisterLastControl(
            "acquisition.composition.load.confirm",
            "Discard the finalized plan and load the selected composition",
            canLoad,
            selected?.Id,
            () =>
            {
                loadComposition(selected!, context.CharacterName, context.World);
                closePopup = true;
            });
        ImGui.EndPopup();
    }

    private void DrawDeleteConfirmation()
    {
        CenterNextPopup();
        if (!ImGui.BeginPopupModal(DeletePopupId, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        if (ConsumeClose())
            return;

        var selected = catalog.SelectedComposition;
        ImGui.TextWrapped(selected is null
            ? "This composition is no longer available."
            : $"Delete {selected.Name}? This does not change the current Workbench.");
        if (ImGuiUi.Button("Cancel##AcquisitionCompositionDeleteCancel", true))
            ImGui.CloseCurrentPopup();
        RegisterLastControl(
            "acquisition.composition.delete.cancel",
            "Cancel saved composition deletion",
            true,
            selected?.Id,
            () => closePopup = true);
        ImGui.SameLine();
        if (ImGuiUi.Button("Delete saved composition##AcquisitionCompositionDeleteConfirm", selected is not null))
        {
            catalog.DeleteSelected();
            ImGui.CloseCurrentPopup();
        }

        RegisterLastControl(
            "acquisition.composition.delete.confirm",
            "Delete the selected saved composition",
            selected is not null,
            selected?.Id,
            () =>
            {
                catalog.DeleteSelected();
                closePopup = true;
            });
        ImGui.EndPopup();
    }

    private static void DrawDiffSummary(MarketAcquisitionCompositionDiff diff)
    {
        var first = true;
        void Part(string text, Vector4 color)
        {
            if (!first)
            {
                ImGui.SameLine();
                ImGui.TextColored(MainWindow.ColMuted, "·");
                ImGui.SameLine();
            }

            ImGui.TextColored(color, text);
            first = false;
        }

        if (diff.Added.Count > 0)
            Part($"{diff.Added.Count:N0} added", MainWindow.ColSuccess);
        if (diff.Removed.Count > 0)
            Part($"{diff.Removed.Count:N0} removed", MainWindow.ColError);
        if (diff.Changed.Count > 0)
            Part($"{diff.Changed.Count:N0} changed", MainWindow.ColWarning);
        foreach (var setting in diff.SettingChanges)
            Part($"{setting.Field}: {setting.Before} → {setting.After}", MainWindow.ColWarning);
        if (first)
            ImGui.TextColored(MainWindow.ColMuted, "No differences.");
    }

    private static void DrawDiffTable(MarketAcquisitionCompositionDiff diff)
    {
        var rows = diff.ChangedLineCount;
        if (rows == 0)
            return;

        var rowHeight = ImGui.GetTextLineHeightWithSpacing() + 2f;
        var height = Math.Min(280f, (rowHeight * (rows + 1)) + 6f);
        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit;
        if (!ImGui.BeginTable("AcquisitionCompositionUpdateDiff##v1", 3, flags, new Vector2(560f, height)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthFixed, 190f);
        ImGui.TableSetupColumn("Change", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("Saved → Workbench", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();
        foreach (var line in diff.Changed)
            DiffRow(line.ItemName, "Changed", MainWindow.ColWarning, line.Changes.Select(change => $"{change.Field}: {change.Before} → {change.After}"));
        foreach (var line in diff.Added)
            DiffRow(MarketAcquisitionWorkbenchCompositionInsights.DisplayName(line), "Added", MainWindow.ColSuccess, [DescribeLine(line)]);
        foreach (var line in diff.Removed)
            DiffRow(MarketAcquisitionWorkbenchCompositionInsights.DisplayName(line), "Removed", MainWindow.ColError, [DescribeLine(line)]);
        ImGui.EndTable();

        static void DiffRow(string item, string change, Vector4 color, IEnumerable<string> details)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item);
            ImGui.TableNextColumn();
            ImGui.TextColored(color, change);
            ImGui.TableNextColumn();
            foreach (var detail in details)
                ImGui.TextUnformatted(detail);
        }
    }

    private static void DrawDiffTooltip(MarketAcquisitionCompositionDiff diff)
    {
        ImGui.BeginTooltip();
        foreach (var setting in diff.SettingChanges)
            ImGui.TextUnformatted($"{setting.Field}: {setting.Before} → {setting.After}");
        foreach (var line in diff.Changed)
            ImGui.TextUnformatted($"{line.ItemName}: {string.Join(", ", line.Changes.Select(change => $"{change.Field} {change.Before} → {change.After}"))}");
        foreach (var line in diff.Added)
            ImGui.TextColored(MainWindow.ColSuccess, $"+ {MarketAcquisitionWorkbenchCompositionInsights.DisplayName(line)}");
        foreach (var line in diff.Removed)
            ImGui.TextColored(MainWindow.ColError, $"- {MarketAcquisitionWorkbenchCompositionInsights.DisplayName(line)}");
        ImGui.EndTooltip();
    }

    private static void DrawSpendTooltip(MarketAcquisitionCompositionSpend spend)
    {
        ImGui.BeginTooltip();
        if (spend.StockUpLines > 0)
        {
            ImGui.TextUnformatted($"{spend.StockUpCeiling:N0} gil worst case");
            ImGui.TextColored(
                MainWindow.ColMuted,
                $"{spend.StockUpLines:N0} stock-up line(s): target on hand × unit ceiling");
            if (spend.UnpricedStockUpLines > 0)
                ImGui.TextColored(MainWindow.ColWarning, $"{spend.UnpricedStockUpLines:N0} stock-up line(s) have no unit ceiling");
        }

        if (spend.BuyBelowLines > 0)
            ImGui.TextColored(MainWindow.ColMuted, $"{spend.BuyBelowLines:N0} buy-below line(s): open-ended, bounded by unit ceiling only");
        ImGui.EndTooltip();
    }

    private static void DrawRegionChip(string region, string? characterRegion)
    {
        var code = MarketAcquisitionWorkbenchCompositionInsights.RegionCode(region);
        var mismatch = IsRegionMismatch(region, characterRegion);
        var color = mismatch ? MainWindow.ColWarning : MainWindow.ColMuted;
        var padding = new Vector2(4f, 1f);
        var textSize = ImGui.CalcTextSize(code);
        var start = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddRectFilled(
            start - new Vector2(0f, padding.Y),
            start + textSize + new Vector2(padding.X * 2f, padding.Y),
            ImGui.GetColorU32(color with { W = mismatch ? 0.24f : 0.14f }),
            3f);
        ImGui.SetCursorScreenPos(start + new Vector2(padding.X, 0f));
        ImGui.TextColored(color, code);
        if (ImGui.IsItemHovered())
        {
            var name = MarketAcquisitionWorkbenchCompositionInsights.SafeRegion(region);
            ImGui.SetTooltip(mismatch ? $"{name} (your character is in {characterRegion})" : name);
        }
    }

    private static bool IsRegionMismatch(string region, string? characterRegion) =>
        characterRegion is not null &&
        !MarketAcquisitionWorkbenchCompositionInsights.SafeRegion(region)
            .Equals(characterRegion, StringComparison.OrdinalIgnoreCase);

    private static string DescribeChangeCount(MarketAcquisitionCompositionDiff diff)
    {
        var lines = diff.ChangedLineCount;
        if (lines == 0)
            return "route settings changed since";
        return lines == 1 ? "1 line changed since" : $"{lines:N0} lines changed since";
    }

    private static string DescribeLine(MarketAcquisitionRequestLineDocument line)
    {
        var rule = MarketAcquisitionWorkbenchCompositionInsights.FormatRule(line.QuantityMode);
        var ceiling = line.MaxUnitPrice == 0 ? "no unit ceiling" : $"unit ceiling {line.MaxUnitPrice:N0}";
        return line.QuantityMode == MarketAcquisitionWorkbenchCompositionInsights.StockUp
            ? $"{rule} {line.TargetQuantity:N0}, {ceiling}"
            : $"{rule}, {ceiling}";
    }

    private static void DisabledReasonTooltip(bool enabled, string? reason)
    {
        if (!enabled && reason is not null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(reason);
    }

    private static string? LoadBlocker(MarketAcquisitionWorkbenchCompositionContext context) =>
        context.IsRouteActive ? "Stop the active route first."
        : context.IsBusy ? "Wait for the current Workbench operation."
        : !context.HasCharacterScope ? "Log in to a character first."
        : null;

    private static void CenterNextPopup()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(
            viewport.WorkPos + (viewport.WorkSize * 0.5f),
            ImGuiCond.Always,
            new Vector2(0.5f, 0.5f));
    }

    /// <summary>Closes the open modal when an agent action requested it outside the ImGui frame.</summary>
    private bool ConsumeClose()
    {
        if (!closePopup)
            return false;
        closePopup = false;
        ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
        return true;
    }

    private MarketAcquisitionCompositionDiff DiffFor(
        ref DiffCache cache,
        MarketAcquisitionWorkbenchComposition composition,
        MarketAcquisitionRequestDocument document)
    {
        if (!ReferenceEquals(cache.Composition, composition) || !ReferenceEquals(cache.Document, document) || cache.Diff is null)
            cache = new DiffCache(composition, document, MarketAcquisitionWorkbenchCompositionInsights.Compare(composition, document));
        return cache.Diff!;
    }

    private string? NameProblem(MarketAcquisitionWorkbenchCompositionContext context)
    {
        if (nameBuffer.Trim().Length == 0)
            return "Enter a name.";
        if (renameCompositionId is null && context.Document.Lines.Count == 0)
            return "The Workbench has no lines to save.";
        return catalog.NameProblem(nameBuffer, renameCompositionId);
    }

    private bool CommitName(MarketAcquisitionRequestDocument document)
    {
        var result = renameCompositionId is null
            ? catalog.SaveNew(nameBuffer, document)
            : catalog.RenameSelected(nameBuffer);
        if (result.Success)
            nameBuffer = string.Empty;
        return result.Success;
    }

    private AgentBridgeUiActionResult SetNameFromArguments(JsonElement? arguments)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty("name", out var name) ||
            name.ValueKind != JsonValueKind.String)
        {
            return AgentBridgeUiActionResult.Fail("A string 'name' argument is required.");
        }

        var text = name.GetString() ?? string.Empty;
        nameBuffer = text.Length > NameMaxLength ? text[..NameMaxLength] : text;
        return AgentBridgeUiActionResult.Ok("Composition name set.");
    }

    private void OpenSaveAs()
    {
        renameCompositionId = null;
        nameBuffer = string.Empty;
        focusNameInput = true;
        pendingPopup = NamePopupId;
    }

    private void OpenRename(string compositionId)
    {
        if (catalog.Find(compositionId) is not { } composition)
            return;
        if (composition.Id != catalog.SelectedCompositionId)
            SelectComposition(composition.Id);
        renameCompositionId = composition.Id;
        nameBuffer = composition.Name;
        focusNameInput = true;
        pendingPopup = NamePopupId;
    }

    private void OpenUpdate(string compositionId)
    {
        updateTargetId = compositionId;
        pendingPopup = UpdatePopupId;
    }

    private void RequestLoad(MarketAcquisitionWorkbenchCompositionContext context, MarketAcquisitionWorkbenchComposition selected)
    {
        if (context.HasReadyPreparedPlan)
            pendingPopup = LoadPopupId;
        else
            loadComposition(selected, context.CharacterName, context.World);
    }

    private void SelectComposition(string id) => catalog.Select(id);

    private void RegisterLastControl(
        string id,
        string label,
        bool enabled,
        string? value,
        Action invoke,
        AgentBridgeUiControlKind kind = AgentBridgeUiControlKind.Button,
        bool selected = false) =>
        reviewRegistry.RegisterLastItem(id, label, kind, enabled, selected, value, invoke);

    private record struct DiffCache(
        MarketAcquisitionWorkbenchComposition? Composition,
        MarketAcquisitionRequestDocument? Document,
        MarketAcquisitionCompositionDiff? Diff);
}
