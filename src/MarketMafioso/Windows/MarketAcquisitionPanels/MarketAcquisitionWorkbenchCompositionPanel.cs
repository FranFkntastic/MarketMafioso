using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Windows.Main;

namespace MarketMafioso.Windows.MarketAcquisitionPanels;

public sealed class MarketAcquisitionWorkbenchCompositionPanel
{
    private readonly MarketAcquisitionWorkbenchCompositionCatalog catalog;
    private readonly Action<MarketAcquisitionWorkbenchComposition, string, string> loadComposition;
    private readonly Action<MarketAcquisitionWorkbenchComposition> mergeComposition;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private string nameBuffer = string.Empty;
    private string saveNameBuffer = "New composition";
    private string searchBuffer = string.Empty;
    private string? nameBufferCompositionId;
    private bool nameBufferInitialized;
    private bool openDeleteConfirmation;
    private bool closeDeleteConfirmation;

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
    }

    public int Count => catalog.Compositions.Count;

    public void Draw(MarketAcquisitionWorkbenchCompositionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        SyncNameBuffer();

        DrawCreateRow(context);
        ImGui.Spacing();
        DrawManagerLayout(context);
        if (!catalog.Status.Equals("Saved compositions are ready.", StringComparison.Ordinal))
            ImGui.TextColored(catalog.Status.StartsWith("Deleted", StringComparison.Ordinal) ? MainWindow.ColWarning : MainWindow.ColMuted, catalog.Status);
        if (openDeleteConfirmation)
        {
            ImGui.OpenPopup("Delete saved composition?##AcquisitionCompositionDeleteConfirmation");
            openDeleteConfirmation = false;
        }
        DrawDeleteConfirmation();
        ImGui.Spacing();
    }

    private void DrawCreateRow(MarketAcquisitionWorkbenchCompositionContext context)
    {
        ImGui.SetNextItemWidth(Math.Max(220f, ImGui.GetContentRegionAvail().X - 150f));
        ImGui.InputTextWithHint("##AcquisitionCompositionCreateName", "Save current Workbench as...", ref saveNameBuffer, 80);
        ImGui.SameLine();
        var canSave = context.Document.Lines.Count > 0 && !string.IsNullOrWhiteSpace(saveNameBuffer);
        if (ImGuiUi.PrimaryButton("Save new", canSave))
            SaveNew(context.Document);
        RegisterLastControl(
            "acquisition.composition.save-new",
            "Save the current Workbench as a new named composition",
            canSave,
            saveNameBuffer,
            () => SaveNew(context.Document));
    }

    private void DrawManagerLayout(MarketAcquisitionWorkbenchCompositionContext context)
    {
        var flags = ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.SizingStretchProp |
                    ImGuiTableFlags.NoSavedSettings;
        if (!ImGui.BeginTable("AcquisitionCompositionManagerLayout", 2, flags, new Vector2(0, 0)))
            return;

        ImGui.TableSetupColumn("Compositions", ImGuiTableColumnFlags.WidthStretch, 0.9f);
        ImGui.TableSetupColumn("Selected composition", ImGuiTableColumnFlags.WidthStretch, 1.1f);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        DrawCompositionList();
        ImGui.TableNextColumn();
        DrawSelectedComposition(context);
        ImGui.EndTable();
    }

    private void DrawCompositionList()
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##AcquisitionCompositionSearch", "Search compositions...", ref searchBuffer, 128);
        ImGui.Spacing();

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
        if (!ImGui.BeginTable("AcquisitionCompositionList", 3, flags, new Vector2(0, 300f)))
            return;

        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Lines", ImGuiTableColumnFlags.WidthFixed, 48f);
        ImGui.TableSetupColumn("Updated", ImGuiTableColumnFlags.WidthFixed, 105f);
        ImGui.TableHeadersRow();
        foreach (var composition in compositions)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var selected = composition.Id == catalog.SelectedCompositionId;
            if (ImGui.Selectable($"{composition.Name}##AcquisitionCompositionList{composition.Id}", selected, ImGuiSelectableFlags.SpanAllColumns))
                SelectComposition(composition.Id);
            RegisterLastControl(
                $"acquisition.composition.select.{composition.Id}",
                $"Select saved composition {composition.Name}",
                true,
                composition.Id,
                () => SelectComposition(composition.Id),
                AgentBridgeUiControlKind.Select,
                selected);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(composition.Lines.Count.ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(composition.UpdatedAtUtc.ToLocalTime().ToString("g"));
        }

        ImGui.EndTable();
    }

    private void DrawSelectedComposition(MarketAcquisitionWorkbenchCompositionContext context)
    {
        var selected = catalog.SelectedComposition;
        if (selected is null)
        {
            ImGui.TextColored(MainWindow.ColMuted, "Select a composition to preview or manage it.");
            return;
        }

        ImGui.TextColored(MainWindow.ColHeader, selected.Name);
        ImGui.TextColored(
            MainWindow.ColMuted,
            $"{selected.Lines.Count:N0} line(s)  |  {FormatRoute(selected)}  |  updated {selected.UpdatedAtUtc.ToLocalTime():g}");
        ImGui.Spacing();

        if (ImGui.BeginTable("AcquisitionCompositionPreview", 4, ImGuiUi.InteractiveTableFlags, new Vector2(0, 190f)))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Rule", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 58f);
            ImGui.TableSetupColumn("Unit", ImGuiTableColumnFlags.WidthFixed, 72f);
            ImGui.TableHeadersRow();
            foreach (var line in selected.Lines)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(line.ItemName) ? $"Item {line.ItemId}" : line.ItemName);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(MarketAcquisitionQuantityModePresenter.FormatMode(line.QuantityMode));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(line.QuantityMode == "TargetQuantity" ? line.TargetQuantity.ToString("N0") : "-");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(line.MaxUnitPrice == 0 ? "-" : line.MaxUnitPrice.ToString("N0"));
            }
            ImGui.EndTable();
        }

        var canLoad = !context.IsBusy && !context.IsRouteActive && context.HasCharacterScope;
        if (ImGuiUi.PrimaryButton("Load (replace)", canLoad))
            loadComposition(selected, context.CharacterName, context.World);
        RegisterLastControl(
            "acquisition.composition.load",
            $"Load {selected.Name} into the Workbench, replacing current lines",
            canLoad,
            selected.Id,
            () => loadComposition(selected, context.CharacterName, context.World));

        ImGui.SameLine();
        var canMerge = !context.IsBusy && !context.IsRouteActive;
        if (ImGuiUi.Button("Merge", canMerge))
            mergeComposition(selected);
        RegisterLastControl(
            "acquisition.composition.merge",
            $"Merge {selected.Name} into the Workbench",
            canMerge,
            selected.Id,
            () => mergeComposition(selected));

        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##AcquisitionCompositionManageName", "Composition name", ref nameBuffer, 80);

        var canUpdate = context.Document.Lines.Count > 0;
        if (ImGuiUi.Button("Update from Workbench", canUpdate))
            catalog.UpdateSelected(context.Document);
        RegisterLastControl(
            "acquisition.composition.update",
            $"Update {selected.Name} from the current Workbench",
            canUpdate,
            selected.Id,
            () => catalog.UpdateSelected(context.Document));
        ImGui.SameLine();
        if (ImGuiUi.Button("Rename", !string.IsNullOrWhiteSpace(nameBuffer)))
            catalog.RenameSelected(nameBuffer);
        RegisterLastControl(
            "acquisition.composition.rename",
            $"Rename {selected.Name}",
            !string.IsNullOrWhiteSpace(nameBuffer),
            nameBuffer,
            () => catalog.RenameSelected(nameBuffer));
        ImGui.SameLine();
        if (ImGui.Button("Duplicate"))
            DuplicateSelected();
        RegisterLastControl(
            "acquisition.composition.duplicate",
            $"Duplicate {selected.Name}",
            true,
            selected.Id,
            DuplicateSelected);
        ImGui.SameLine();
        if (ImGui.Button("Delete..."))
            openDeleteConfirmation = true;
        RegisterLastControl(
            "acquisition.composition.delete.open",
            $"Open deletion confirmation for {selected.Name}",
            true,
            selected.Id,
            () => openDeleteConfirmation = true);
    }

    private void DrawDeleteConfirmation()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(
            viewport.WorkPos + (viewport.WorkSize * 0.5f),
            ImGuiCond.Always,
            new Vector2(0.5f, 0.5f));
        if (!ImGui.BeginPopupModal(
                "Delete saved composition?##AcquisitionCompositionDeleteConfirmation",
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        var selected = catalog.SelectedComposition;
        if (closeDeleteConfirmation)
        {
            closeDeleteConfirmation = false;
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }
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
            () => closeDeleteConfirmation = true);
        ImGui.SameLine();
        if (ImGuiUi.Button("Delete saved composition##AcquisitionCompositionDeleteConfirm", selected is not null))
        {
            catalog.DeleteSelected();
            nameBufferCompositionId = null;
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
                nameBufferCompositionId = null;
                closeDeleteConfirmation = true;
            });
        ImGui.EndPopup();
    }

    private void SyncNameBuffer()
    {
        if (nameBufferInitialized && nameBufferCompositionId == catalog.SelectedCompositionId)
            return;
        nameBufferInitialized = true;
        nameBufferCompositionId = catalog.SelectedCompositionId;
        nameBuffer = catalog.SelectedComposition?.Name ?? "New composition";
    }

    private static string FormatRoute(MarketAcquisitionWorkbenchComposition composition) =>
        composition.WorldMode.Equals("AllWorldSweep", StringComparison.OrdinalIgnoreCase)
            ? $"Sweep {composition.SweepScope}"
            : "Recommended route";

    private void SaveNew(MarketAcquisitionRequestDocument document)
    {
        var result = catalog.SaveNew(saveNameBuffer, document);
        if (!result.Success)
            return;
        nameBufferCompositionId = result.Composition?.Id;
        saveNameBuffer = "New composition";
    }

    private void SelectComposition(string id)
    {
        catalog.Select(id);
        nameBufferCompositionId = null;
        SyncNameBuffer();
    }

    private void DuplicateSelected()
    {
        if (catalog.DuplicateSelected().Success)
            nameBufferCompositionId = null;
    }

    private void RegisterLastControl(
        string id,
        string label,
        bool enabled,
        string? value,
        Action invoke,
        AgentBridgeUiControlKind kind = AgentBridgeUiControlKind.Button,
        bool selected = false) =>
        reviewRegistry.RegisterLastItem(id, label, kind, enabled, selected, value, invoke);
}
