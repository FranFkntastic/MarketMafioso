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
    private string? nameBufferCompositionId;

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

        if (!ImGui.CollapsingHeader(
                $"Saved compositions ({catalog.Compositions.Count:N0})##AcquisitionSavedCompositions",
                ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextColored(MainWindow.ColMuted, "Reusable local purchase loadouts. Load replaces the Workbench; Merge only adds missing items.");
        DrawSelectionRow(context);
        DrawSaveAndManageRow(context);
        ImGui.TextColored(catalog.Status.StartsWith("Deleted", StringComparison.Ordinal) ? MainWindow.ColWarning : MainWindow.ColMuted, catalog.Status);
        DrawDeleteConfirmation();
        ImGui.Spacing();
    }

    private void DrawSelectionRow(MarketAcquisitionWorkbenchCompositionContext context)
    {
        var selected = catalog.SelectedComposition;
        ImGui.TextColored(MainWindow.ColMuted, "Saved composition");
        ImGui.SetNextItemWidth(360f);
        if (catalog.Compositions.Count == 0)
            ImGui.BeginDisabled();
        if (ImGui.BeginCombo("##AcquisitionCompositionSelect", selected?.Name ?? "No saved compositions yet"))
        {
            foreach (var composition in catalog.Compositions.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
            {
                var isSelected = composition.Id == catalog.SelectedCompositionId;
                if (ImGui.Selectable($"{composition.Name}##AcquisitionComposition{composition.Id}", isSelected))
                {
                    catalog.Select(composition.Id);
                    nameBufferCompositionId = null;
                    SyncNameBuffer();
                }
                if (isSelected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        if (catalog.Compositions.Count == 0)
            ImGui.EndDisabled();

        if (selected is null)
            return;

        ImGui.SameLine();
        var canLoad = !context.IsBusy && !context.IsRouteActive && context.HasCharacterScope;
        if (ImGuiUi.PrimaryButton("Load (replace)##AcquisitionCompositionLoad", canLoad))
            loadComposition(selected, context.CharacterName, context.World);
        RegisterLastControl(
            "acquisition.composition.load",
            $"Load {selected.Name} into the Workbench, replacing current lines",
            canLoad,
            selected.Id,
            () => loadComposition(selected, context.CharacterName, context.World));

        ImGui.SameLine();
        var canMerge = !context.IsBusy && !context.IsRouteActive;
        if (ImGuiUi.Button("Merge##AcquisitionCompositionMerge", canMerge))
            mergeComposition(selected);
        RegisterLastControl(
            "acquisition.composition.merge",
            $"Merge {selected.Name} into the Workbench",
            canMerge,
            selected.Id,
            () => mergeComposition(selected));

        ImGui.TextColored(
            MainWindow.ColMuted,
            $"{selected.Lines.Count:N0} line(s)  |  {FormatRoute(selected)}  |  updated {selected.UpdatedAtUtc.ToLocalTime():g}");
    }

    private void DrawSaveAndManageRow(MarketAcquisitionWorkbenchCompositionContext context)
    {
        ImGui.SetNextItemWidth(360f);
        ImGui.InputTextWithHint("##AcquisitionCompositionName", "Composition name", ref nameBuffer, 80);

        ImGui.SameLine();
        var canSaveNew = context.Document.Lines.Count > 0 && !string.IsNullOrWhiteSpace(nameBuffer);
        if (ImGuiUi.Button("Save new##AcquisitionCompositionSaveNew", canSaveNew))
        {
            var result = catalog.SaveNew(nameBuffer, context.Document);
            if (result.Success)
                nameBufferCompositionId = result.Composition?.Id;
        }
        RegisterLastControl(
            "acquisition.composition.save-new",
            "Save the current Workbench as a new named composition",
            canSaveNew,
            nameBuffer,
            () => catalog.SaveNew(nameBuffer, context.Document));

        var selected = catalog.SelectedComposition;
        ImGui.SameLine();
        var canUpdate = selected is not null && context.Document.Lines.Count > 0;
        if (ImGuiUi.Button("Update##AcquisitionCompositionUpdate", canUpdate))
            catalog.UpdateSelected(context.Document);
        RegisterLastControl(
            "acquisition.composition.update",
            "Update the selected composition from the current Workbench",
            canUpdate,
            selected?.Id,
            () => catalog.UpdateSelected(context.Document));

        ImGui.SameLine();
        var canRename = selected is not null && !string.IsNullOrWhiteSpace(nameBuffer);
        if (ImGuiUi.Button("Rename##AcquisitionCompositionRename", canRename))
            catalog.RenameSelected(nameBuffer);
        RegisterLastControl(
            "acquisition.composition.rename",
            "Rename the selected composition",
            canRename,
            nameBuffer,
            () => catalog.RenameSelected(nameBuffer));

        ImGui.SameLine();
        if (ImGuiUi.Button("Duplicate##AcquisitionCompositionDuplicate", selected is not null))
        {
            var result = catalog.DuplicateSelected();
            if (result.Success)
                nameBufferCompositionId = null;
        }

        ImGui.SameLine();
        if (ImGuiUi.Button("Delete...##AcquisitionCompositionDelete", selected is not null))
            ImGui.OpenPopup("Delete saved composition?##AcquisitionCompositionDeleteConfirmation");
    }

    private void DrawDeleteConfirmation()
    {
        if (!ImGui.BeginPopupModal(
                "Delete saved composition?##AcquisitionCompositionDeleteConfirmation",
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        var selected = catalog.SelectedComposition;
        ImGui.TextWrapped(selected is null
            ? "This composition is no longer available."
            : $"Delete {selected.Name}? This does not change the current Workbench.");
        if (ImGuiUi.Button("Cancel##AcquisitionCompositionDeleteCancel", true))
            ImGui.CloseCurrentPopup();
        ImGui.SameLine();
        if (ImGuiUi.Button("Delete saved composition##AcquisitionCompositionDeleteConfirm", selected is not null))
        {
            catalog.DeleteSelected();
            nameBufferCompositionId = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void SyncNameBuffer()
    {
        if (nameBufferCompositionId == catalog.SelectedCompositionId)
            return;
        nameBufferCompositionId = catalog.SelectedCompositionId;
        nameBuffer = catalog.SelectedComposition?.Name ?? string.Empty;
    }

    private static string FormatRoute(MarketAcquisitionWorkbenchComposition composition) =>
        composition.WorldMode.Equals("AllWorldSweep", StringComparison.OrdinalIgnoreCase)
            ? $"Sweep {composition.SweepScope}"
            : "Recommended route";

    private void RegisterLastControl(string id, string label, bool enabled, string? value, Action invoke) =>
        reviewRegistry.Register(
            id,
            label,
            AgentBridgeUiControlKind.Button,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            enabled,
            false,
            value,
            invoke);
}
