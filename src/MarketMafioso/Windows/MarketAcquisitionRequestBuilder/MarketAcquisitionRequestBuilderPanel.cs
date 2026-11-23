using Franthropy.Dalamud.UI.Inputs;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Feedback;
using Franthropy.Dalamud.UI.Items;
using Franthropy.Dalamud.UI.Layout;
using Franthropy.Dalamud.UI.Tables;
using MarketMafioso.AgentBridge;
using MarketMafioso.CraftArchitectCompanion;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.MarketAcquisition.ExactAuthority;
using MarketMafioso.Windows;

namespace MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

public sealed class MarketAcquisitionRequestBuilderPanel
{
    private readonly IReadOnlyList<DalamudItemOption> itemOptions;
    private readonly Configuration config;
    private readonly CraftAppraisalRequestBuilderController craftAppraisal;
    private readonly MarketAcquisitionRequestBuilderController controller;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly DalamudItemAutocompleteState itemAutocomplete = new();

    private readonly MarketAcquisitionWorkbenchStockService? workbenchStock;
    private string quantityMode = "AllBelowThreshold";
    private string targetQuantityBuffer = string.Empty;
    private string maximumOverageBuffer = string.Empty;
    private long syncedReserveVersion;
    private string maxUnitPriceBuffer = string.Empty;
    private string gilCapBuffer = string.Empty;
    private string hqPolicy = "Either";
    private bool isAppraising;
    private bool selectedLineInspectorRequested;

    private readonly DalamudSelectionBar selectionBar = new("AcquisitionWorkbenchSelectionBar")
    {
        EmptyText = "No lines selected",
        FormatCount = count => $"{count:N0} selected",
    };
    private readonly DalamudTimedUndoStrip<MarketAcquisitionRequestBuilderController.LineRemoval> removalUndo = new();
    // "Count stock from" tree expansion, by character; kept for the session only.
    private readonly Dictionary<(ulong LocalContentId, uint HomeWorldId), bool> scopeExpanded = new();
    // Numeric cell text being typed, by line and field; written to the line on blur (or, if the cell stops
    // being drawn mid-edit, at the start of the next Draw), dropped on Escape.
    private readonly Dictionary<(WorkbenchLineKey Line, CellField Field), PendingCellEdit> pendingCellEdits = new();
    private string? pendingCellEditsRequestId;
    private BulkField bulkField = BulkField.UnitCeiling;
    private string bulkValueBuffer = string.Empty;
    // "Stock up to" chosen in the bulk editor and waiting for a target on hand.
    private bool bulkStockUpPending;

    private const float StockScopeRailWidth = 184f;
    // Quartermaster's rail colours: the logged-in character, and characters on other service accounts.
    private static readonly Vector4 CurrentCharacterTint = new(.56f, .70f, .85f, 1f);
    private static readonly Vector4 OtherAccountTint = new(.86f, .68f, .42f, .9f);
    private static readonly AgentBridgeActionArgumentSchema ValueSchema = new(
        [new("value", AgentBridgeActionArgumentKind.String)]);

    private enum BulkField
    {
        UnitCeiling,
        SpendCeiling,
        TargetOnHand,
        ExtraUnits,
        Quality,
        BuyingRule,
    }

    private enum CellField
    {
        Quantity,
        Unit,
        Overage,
        Spend,
    }

    private readonly record struct PendingCellEdit(long Value, int Frame);

    private MarketAcquisitionRequestDocument document => controller.Document;
    private string status => controller.Status;

    public MarketAcquisitionRequestBuilderPanel(
        Configuration config,
        IDataManager dataManager,
        CraftAppraisalRequestBuilderController craftAppraisal,
        Func<MarketAcquisitionRequestDocument, Task<MarketAcquisitionRequestBuilderSyncOutcome>> syncRequest,
        Func<MarketAcquisitionRequestDocument, Task<MarketAcquisitionRequestBuilderRefreshOutcome>> refreshRequest,
        Action<MarketAcquisitionRequestDocument, MarketAcquisitionRequestView?> documentAdopted,
        AgentBridgeUiReviewRegistry reviewRegistry,
        MarketAcquisitionWorkbenchStockService? workbenchStock = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.workbenchStock = workbenchStock;
        this.craftAppraisal = craftAppraisal;
        this.reviewRegistry = reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry));
        controller = new MarketAcquisitionRequestBuilderController(
            config,
            syncRequest,
            refreshRequest,
            documentAdopted);
        itemOptions = DalamudItemAutocompleteRenderer.LoadItemOptions(dataManager);
    }

    public MarketAcquisitionRequestDocument CurrentDocument => document;

    public string CurrentIntentHash => controller.CurrentIntentHash;

    public int LineCount => document.Lines.Count;

    public bool HasExactAcquisitionAuthority => document.ExactAcquisitionAuthority is not null;

    public bool HasPreviousWorkbench => controller.HasPreviousWorkbench;

    public bool IsHostedSyncEnabled => controller.IsHostedSyncEnabled;

    public bool HasHostedAssociation => controller.HasHostedAssociation;

    public void SetStatus(string status) => controller.SetStatus(status);

    public void RequestHostedSync() => controller.RequestHostedSync();

    public void PauseHostedSync() => controller.PauseHostedSync();

    public bool DetachHostedAssociation() => controller.DetachHostedAssociation();

    public void MarkHostedCopyShelved() => controller.MarkHostedCopyShelved();

    public void MarkPlanPrepared(string planHash) =>
        controller.MarkPlanPrepared(planHash, workbenchStock is { } stock ? stock.CountedStockFor : null);

    /// <summary>Lines whose stock goes to a retainer home after delivery; zero unless stowing is on.</summary>
    public int StowToRetainersCount =>
        document.StowAfterDelivery && workbenchStock is { View.HasItemHomes: true } stock ? stock.StowToRetainersCount(document) : 0;

    public void AdoptRequest(MarketAcquisitionRequestView request) => controller.AdoptRequest(request);

    public bool AdoptRestoredRequestIfSafe(MarketAcquisitionRequestView request) =>
        controller.AdoptRestoredRequestIfSafe(request);

    public int StageLines(IEnumerable<MarketAcquisitionRequestLineDocument> lines) =>
        controller.AddLines(lines);

    public int StageLines(
        IEnumerable<MarketAcquisitionRequestLineDocument> lines,
        string sourceLabel) =>
        controller.AddLines(lines, sourceLabel);

    public void StageExactAcquisitionTransfer(ExactAcquisitionWorkbenchTransfer transfer) =>
        controller.StageExactAcquisitionTransfer(transfer);

    public bool FinalizeExactAcquisitionAuthority() => controller.FinalizeExactAcquisitionAuthority();

    public ExactAcquisitionWorkbenchAuthorityValidation ExactAcquisitionFinalizationValidation =>
        controller.ExactAcquisitionFinalizationValidation;

    public int ReturnLines(IEnumerable<uint> itemIds) =>
        controller.RemoveLinesByItemId(itemIds);

    public int MergeComposition(MarketAcquisitionWorkbenchComposition composition) =>
        controller.MergeComposition(composition);

    public void LoadComposition(
        MarketAcquisitionWorkbenchComposition composition,
        string characterName,
        string world) =>
        controller.LoadComposition(composition, characterName, world);

    public void StartBlankWorkbench(MarketAcquisitionRequestBuilderContext context)
    {
        controller.StartBlankWorkbench(
            context.HasCharacterScope ? context.CharacterName : string.Empty,
            context.HasCharacterScope ? context.World : string.Empty);
        ClearLineEditor();
    }

    public bool RestorePreviousWorkbench()
    {
        var restored = controller.RestorePreviousWorkbench();
        if (restored)
            ClearLineEditor();
        return restored;
    }

    public void Draw(MarketAcquisitionRequestBuilderContext context, float reservedFooterHeight = 0)
    {
        craftAppraisal.State.WorkshopHostEnabled = config.EnableWorkshopHostCraftQuotes;
        EnsureCharacterScope(context);
        controller.PumpAutomaticSynchronization(
            context.CharacterName,
            context.World,
            context.HasCharacterScope &&
            !context.IsBusy &&
            !context.IsRouteActive &&
            !string.IsNullOrWhiteSpace(config.ServerUrl) &&
            !string.IsNullOrWhiteSpace(WorkshopHostApiKeyRouting.ResolveAcquisitionKey(config)));

        workbenchStock?.MarkVisible();
        CommitOrphanedCellEdits(context);
        SyncRestockReserveTargets(context);
        DrawExactAcquisitionAuthority(context);
        DrawRouteScope(context);
        ImGui.BeginDisabled(context.IsBusy || context.IsRouteActive || IsSynchronizing);
        DrawRunBudgetInput();
        DrawStowAfterDelivery();
        ImGui.EndDisabled();
        DrawRouteScopeExtras?.Invoke();
        ImGui.Spacing();
        DrawExceptionalStatus(context);

        var height = Math.Max(150f, ImGui.GetContentRegionAvail().Y - Math.Max(0, reservedFooterHeight));
        if (workbenchStock?.View is { Scope.IsDefaultOrEmpty: false } view)
        {
            DrawStockScopeRail(context, view.Scope, height);
            ImGui.SameLine();
        }
        // The buy list always sits in this child, so its widget IDs do not change when the rail appears.
        if (ImGui.BeginChild("AcquisitionWorkbenchMain", new Vector2(0, height), false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            DrawBuyListBar(context);
            DrawSelectedLineWorkspace(context);
        }
        ImGui.EndChild();
    }

    /// <summary>Optional host-drawn group under Route Scope / Run Budget (cross-regional shopping).</summary>
    public Action? DrawRouteScopeExtras { get; set; }

    public bool IsSynchronizing => controller.IsSyncing;

    public bool IsRefreshing => controller.IsRefreshing;

    public Task WaitForRefreshAsync() => controller.WaitForRefreshAsync();

    public string SyncStatus => document.SyncStatus;

    public string VisibleStatus => status;

    public MarketAcquisitionRequestValidationResult DraftValidation => controller.DraftValidation;

    public ulong TotalSpendCeiling => document.Lines.Aggregate(
        0ul,
        (total, line) => total + line.GilCap);

    public uint TargetQuantityTotal => document.Lines
        .Where(line => line.QuantityMode.Equals("TargetQuantity", StringComparison.OrdinalIgnoreCase))
        .Aggregate(0u, (total, line) =>
        {
            var sum = (ulong)total + line.TargetQuantity;
            return sum > uint.MaxValue ? uint.MaxValue : (uint)sum;
        });

    public AgentBridgeCraftAppraisalTruth CreateAgentBridgeCraftAppraisalTruth()
    {
        var selected = craftAppraisal.State.SelectedLine;
        var quote = selected is null
            ? craftAppraisal.State.LatestQuote
            : craftAppraisal.State.GetLineQuote(selected)?.Quote;
        return new AgentBridgeCraftAppraisalTruth
        {
            IsFetching = isAppraising || craftAppraisal.IsFetchingCraftQuote,
            Status = craftAppraisal.State.CraftQuoteStatus,
            WorkshopHostEnabled = craftAppraisal.State.WorkshopHostEnabled,
            WorkshopHostAvailable = craftAppraisal.State.WorkshopHostAvailable,
            SelectedItemId = selected?.ItemId,
            SelectedItemName = selected?.ItemName,
            RequestedQuantity = selected?.Quantity,
            HqPolicy = selected?.HqPolicy,
            Region = selected?.Region,
            HasQuote = quote is not null,
            QuoteComplete = quote?.IsComplete == true,
            QuoteUnitCost = quote?.EstimatedUnitCost,
            QuoteSource = quote?.Source,
            QuoteConfidence = quote?.Confidence,
            WarningCount = quote?.Warnings.Count ?? 0,
            PlanId = quote?.PlanId,
            CanOpenPlan = !string.IsNullOrWhiteSpace(quote?.PlanUrl),
        };
    }

    private void DrawExactAcquisitionAuthority(MarketAcquisitionRequestBuilderContext context)
    {
        if (document.ExactAcquisitionAuthority is not { } authority)
            return;

        ImGuiUi.SectionHeader("external exact-acquisition solution", MainWindow.ColHeader);
        if (!authority.IsLineageValid)
        {
            ImGui.TextColored(MainWindow.ColError, authority.InvalidationReason ?? "The selected gear solution changed; return to Advisor.");
            ImGui.TextColored(MainWindow.ColMuted, "Historical Advisor lineage is retained, but this Workbench cannot be finalized as that solution.");
            ImGui.Spacing();
            return;
        }

        ImGui.TextColored(MainWindow.ColHeader, authority.Transfer.SelectedSolutionId);
        ImGui.SameLine();
        ImGui.TextColored(MainWindow.ColMuted,
            $"{authority.Lines.Count:N0} exact-quality line(s) · observed {authority.Transfer.ObservedMarketTotalGil:N0} gil");
        if (authority.Transfer.DryRunOnly)
            ImGui.TextColored(MainWindow.ColWarning, "DIAGNOSTIC CONTRACT - permanently restricted to non-spending dry runs");
        var flex = authority.PriceFlexPercent;
        ImGui.SetNextItemWidth(105f);
        var canEdit = !context.IsBusy && !context.IsRouteActive && !IsSynchronizing;
        if (!canEdit)
            ImGui.BeginDisabled();
        if (DalamudGroupedIntegerInput.InputGroupedInt("Price flexibility %##ExternalExactAcquisitionFlex", ref flex, 1, 5))
            controller.UpdateExactAcquisitionPriceFlex(flex);
        if (!canEdit)
            ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextColored(MainWindow.ColMuted,
            $"fixed plan ceiling {authority.PlanCapGil:N0} gil · {authority.RecoveryPolicyId}");
        ImGui.Spacing();
    }

    public void ClearWorkbench(MarketAcquisitionRequestBuilderContext context) => ClearDraft(context);

    private void DrawExceptionalStatus(MarketAcquisitionRequestBuilderContext context)
    {
        if (context.IsRouteActive)
        {
            ImGui.TextColored(MainWindow.ColMuted, "Editing is paused while the active route finishes.");
            ImGui.Spacing();
            return;
        }

        if (!context.HasCharacterScope && !context.CharacterScopeTemporarilyUnavailable)
        {
            ImGui.TextColored(MainWindow.ColError, "Character scope is unavailable; the Workbench cannot synchronize or finalize.");
            ImGui.Spacing();
            return;
        }

        if (document.SyncStatus.Equals("SyncFailed", StringComparison.OrdinalIgnoreCase))
        {
            ImGui.TextColored(MainWindow.ColWarning, status);
            ImGui.Spacing();
            return;
        }

        if (IsPlanStale(context))
        {
            ImGui.TextColored(MainWindow.ColWarning, "The finalized plan is stale because the buy list changed.");
            ImGui.Spacing();
        }
    }

    private void DrawSelectedLineWorkspace(MarketAcquisitionRequestBuilderContext context)
    {
        var selection = BuildSelectedLinePresentation(context);
        var surface = MarketAcquisitionSelectedLinePresenter.ResolveSurface(
            selectedLineInspectorRequested,
            selection);
        DrawRemovalUndo(context);
        if (surface == MarketAcquisitionSelectedLineSurface.CommandBar)
            DrawSelectionBar(context, selection);

        var tableHeight = Math.Max(150f, ImGui.GetContentRegionAvail().Y);
        if (surface == MarketAcquisitionSelectedLineSurface.Inspector)
            DrawCompactLineTableWithInspector(context, tableHeight, selection!);
        else
            DrawCompactLineTable(context, tableHeight);
    }

    private void DrawCompactLineTableWithInspector(
        MarketAcquisitionRequestBuilderContext context,
        float tableHeight,
        MarketAcquisitionSelectedLinePresentation selection)
    {
        var flags = ImGuiTableFlags.SizingStretchProp |
                    ImGuiTableFlags.BordersInnerV |
                    ImGuiTableFlags.NoSavedSettings;
        if (!ImGui.BeginTable("AcquisitionWorkbenchSelectionLayout", 2, flags, new Vector2(0, tableHeight)))
            return;

        ImGui.TableSetupColumn("Buy list", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Selected line", ImGuiTableColumnFlags.WidthFixed, 330f);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        DrawCompactLineTable(context, tableHeight);
        ImGui.TableNextColumn();
        if (ImGui.BeginChild("AcquisitionWorkbenchSelectedLineInspector", new Vector2(0, tableHeight), true))
            DrawSelectedLineInspector(context, selection);
        ImGui.EndChild();
        ImGui.EndTable();
    }

    private void DrawCompactLineTable(
        MarketAcquisitionRequestBuilderContext context,
        float tableHeight)
    {
        var flags = AcquisitionRequestTableStyle.LineTableFlags |
                    ImGuiTableFlags.SizingStretchProp |
                    ImGuiTableFlags.NoSavedSettings;
        // Goes to exists only while Quartermaster reports reserve items and offers its placement editor.
        var showHomes = workbenchStock?.View.HasItemHomes == true;
        if (!ImGui.BeginTable(showHomes ? "AcquisitionWorkbenchLinesV7Homes" : "AcquisitionWorkbenchLinesV7", showHomes ? 12 : 11, flags,
                new Vector2(0, tableHeight)))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 2.4f);
        ImGui.TableSetupColumn("Buying rule", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn("Target on hand", ImGuiTableColumnFlags.WidthStretch, 0.9f);
        ImGui.TableSetupColumn("Extra units allowed", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn("On hand", ImGuiTableColumnFlags.WidthStretch, 1.0f);
        ImGui.TableSetupColumn("Still need", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn("Unit ceiling", ImGuiTableColumnFlags.WidthStretch, 1.1f);
        ImGui.TableSetupColumn("Spend ceiling", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn("Price source", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        if (showHomes)
            ImGui.TableSetupColumn("Goes to", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn(string.Empty, ImGuiTableColumnFlags.WidthFixed, 72f);
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        var headers = new List<string> { "Item", "Buying rule", "Target on hand", "Extra units allowed", "On hand", "Still need", "Unit ceiling", "Spend ceiling", "Quality", "Price source" };
        var tooltips = new List<string> { "Item to acquire.", "Stock up to a target, or buy below a unit ceiling.", "Desired stock, not units to buy. Exact acquisition retains its required purchase quantity.", "Units accepted above the target when whole market stacks don't fit exactly; 0 = never exceed.", "Stock counted from the sources chosen under Count stock from. Hover for each source.", "Target minus counted stock; unknown while any counted source is unknown.", "Maximum gil per unit.", "Maximum gil spent on this line.", "Accepted item quality.", "Typed ceiling or Craft Architect quote; a quote is not necessarily the active ceiling." };
        if (showHomes)
        {
            headers.Add("Goes to");
            tooltips.Add("Where Quartermaster stows the item.");
        }
        headers.Add(string.Empty);
        tooltips.Add(string.Empty);
        for (var column = 0; column < headers.Count; column++)
        {
            ImGui.TableNextColumn();
            var headerX = ImGui.GetCursorPosX();
            ImGui.TableHeader(headers[column]);
            if (tooltips[column].Length > 0 && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltips[column]);
            if (showHomes && headers[column] == "Goes to")
                DrawPlacementHeaderButton(headerX + ImGui.CalcTextSize(headers[column]).X + 8);
        }

        // Edits made by a row land on the next frame's rows; this frame keeps drawing the lines it started with.
        var lines = document.Lines.ToArray();
        var orderedKeys = WorkbenchLineKey.For(lines);
        controller.Selection.Retain(orderedKeys);
        for (var index = 0; index < lines.Length; index++)
            DrawCompactLineRow(context, lines[index], index, showHomes, orderedKeys);
        DalamudTableSelectionRenderer.EndRows(controller.Selection);
        SelectAllOnShortcut(orderedKeys);

        DrawCompactAddRow(context, showHomes);
        ImGui.EndTable();
    }

    private void DrawCompactLineRow(
        MarketAcquisitionRequestBuilderContext context,
        MarketAcquisitionRequestLineDocument line,
        int index,
        bool showHomes,
        IReadOnlyList<WorkbenchLineKey> orderedKeys)
    {
        var canEdit = CanEdit(context);
        var isExactAcquisitionLine = controller.IsExactAcquisitionLine(index);
        var itemName = FormatLineItemName(line);
        var key = orderedKeys[index];
        ImGui.PushID($"AcquisitionWorkbenchLine{key.Id}");
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        var isSelected = controller.Selection.IsSelected(key);
        var selectionCursor = ImGui.GetCursorPos();
        if (DalamudTableSelectionRenderer.DrawRow(
                controller.Selection,
                orderedKeys,
                index,
                "##SelectLine",
                new Vector2(0f, ImGui.GetFrameHeight())))
            selectedLineInspectorRequested = false;
        var rowHovered = DalamudRowRemoveButton.IsLastItemRowHovered();
        RegisterLastControl(
            $"acquisition.workbench.line.{key.Id}.select",
            $"Select {itemName} in the Workbench",
            enabled: true,
            selected: isSelected,
            value: line.ItemId.ToString(),
            () =>
            {
                controller.Selection.SelectOnly(key);
                selectedLineInspectorRequested = false;
            });
        ImGui.SetCursorPos(selectionCursor);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(itemName);

        if (!canEdit || isExactAcquisitionLine)
            ImGui.BeginDisabled();

        ImGui.TableNextColumn();
        DrawCompactModeCell(line, index);

        ImGui.TableNextColumn();
        DrawCompactQuantityCell(line, index, key);

        ImGui.TableNextColumn();
        DrawCompactOverageCell(line, index, key);

        var stockUp = line.QuantityMode == "TargetQuantity" && !isExactAcquisitionLine;
        var counted = stockUp ? workbenchStock?.View.Lines.GetValueOrDefault(line.ItemId) : null;
        ImGui.TableNextColumn();
        DrawOnHandCell(stockUp, counted);
        ImGui.TableNextColumn();
        if (!stockUp)
            ImGui.TextUnformatted("-");
        else if (counted?.Total is { } total)
            ImGui.TextUnformatted($"{(line.TargetQuantity > total ? line.TargetQuantity - total : 0):N0}");
        else
            ImGui.TextColored(MainWindow.ColMuted, "—");

        ImGui.TableNextColumn();
        DrawCompactUnitCell(line, index, key);
        DrawUnitPriceContextMenu(line, index, key, canEdit);

        ImGui.TableNextColumn();
        DrawCompactSpendCell(line, index, key);

        ImGui.TableNextColumn();
        DrawCompactHqCell(line, index);

        ImGui.TableNextColumn();
        DrawCompactEvidenceState(line);

        if (showHomes)
        {
            ImGui.TableNextColumn();
            DrawGoesToCell(line);
        }

        if (!canEdit || isExactAcquisitionLine)
            ImGui.EndDisabled();

        ImGui.TableNextColumn();
        if (DalamudRowRemoveButton.Draw("RemoveLine", rowHovered, canEdit))
            RemoveLinesWithUndo([key]);
        if (rowHovered && ImGui.IsItemHovered())
            ImGui.SetTooltip($"Remove {itemName}");
        RegisterLastControl(
            $"acquisition.workbench.line.{key.Id}.remove",
            $"Remove {itemName}",
            canEdit,
            selected: false,
            line.ItemId.ToString(),
            () => RemoveLinesWithUndo([key]));

        ImGui.PopID();
    }

    /// <summary>Ctrl+A selects every line while the buy list has focus and no text is being typed.</summary>
    private void SelectAllOnShortcut(IReadOnlyList<WorkbenchLineKey> orderedKeys)
    {
        var io = ImGui.GetIO();
        if (orderedKeys.Count == 0 || !io.KeyCtrl || io.WantTextInput || !ImGui.IsWindowFocused() ||
            !ImGui.IsKeyPressed(ImGuiKey.A, false))
            return;
        foreach (var key in orderedKeys)
            controller.Selection.SetSelected(key, true);
    }

    private static void DrawOnHandCell(bool stockUp, WorkbenchLineStock? counted)
    {
        if (!stockUp)
        {
            ImGui.TextUnformatted("-");
            return;
        }
        ImGui.BeginGroup();
        if (counted?.Total is { } total)
        {
            ImGui.TextUnformatted($"{total:N0}");
            if (counted.StaleAge is { } age)
            {
                ImGui.SameLine(0, 0);
                ImGui.TextColored(MainWindow.ColWarning, $" · {FormatAge(age)}");
            }
        }
        else
        {
            ImGui.TextColored(MainWindow.ColMuted, "—");
        }
        ImGui.EndGroup();

        if (counted is null || counted.Rows.IsDefaultOrEmpty || !ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            return;
        ImGui.BeginTooltip();
        if (ImGui.BeginTable("OnHandSources", 2, ImGuiTableFlags.SizingFixedFit))
        {
            foreach (var row in counted.Rows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Label);
                if (row.StaleAge is { } rowAge)
                {
                    ImGui.SameLine(0, 0);
                    ImGui.TextColored(MainWindow.ColWarning, $" · {FormatAge(rowAge, row.NewestAge)}");
                }
                ImGui.TableNextColumn();
                if (row.Quantity is { } quantity)
                    DalamudUiLayout.DrawRightAligned($"{quantity:N0}");
                else
                    ImGui.TextColored(MainWindow.ColMuted, "Unavailable");
            }
            ImGui.EndTable();
        }
        ImGui.Separator();
        ImGui.TextUnformatted("Counted");
        ImGui.SameLine();
        ImGui.TextUnformatted(counted.Total is { } countedTotal ? $"{countedTotal:N0}" : "—");
        ImGui.EndTooltip();
    }

    private static string FormatAge(TimeSpan age) =>
        age.TotalHours >= 48 ? $"{age.TotalDays:N1}d" : $"{age.TotalHours:N1}h";

    /// <summary>A group of retainers seen at different times reads as a range, newest to oldest.</summary>
    private static string FormatAge(TimeSpan oldest, TimeSpan? newest) =>
        newest is { } fresh && FormatAge(fresh) != FormatAge(oldest) ? $"{FormatAge(fresh)}–{FormatAge(oldest)}" : FormatAge(oldest);

    /// <summary>Read-only placement summary; clicking opens Quartermaster's placement editor for this item.</summary>
    private void DrawGoesToCell(MarketAcquisitionRequestLineDocument line)
    {
        if (workbenchStock?.View is not { HasItemHomes: true } || line.ItemId == 0)
            return;
        var summary = workbenchStock.PlacementSummary(line);
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        var clicked = ImGui.Selectable($"{summary}##GoesTo");
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered() && ImGui.CalcTextSize(summary).X > ImGui.GetItemRectSize().X)
            ImGui.SetTooltip(summary);
        if (clicked)
            workbenchStock.RequestOpenPlacement([line]);
    }

    /// <summary>Opens Quartermaster's placement editor for every item on the buy list.</summary>
    private void DrawPlacementHeaderButton(float x)
    {
        ImGui.SameLine();
        ImGui.SetCursorPosX(x);
        ImGui.BeginDisabled(!document.Lines.Any(line => line.ItemId > 0));
        if (ImGui.SmallButton("Placement##WorkbenchPlacement"))
            workbenchStock!.RequestOpenPlacement(document.Lines);
        ImGui.EndDisabled();
    }

    private void DrawStockScopeRail(
        MarketAcquisitionRequestBuilderContext context,
        ImmutableArray<WorkbenchStockScopeCharacter> scope,
        float height)
    {
        var collapsed = config.MarketAcquisitionStockScopeRailCollapsed;
        var width = collapsed
            ? DalamudUiLayout.ButtonWidth(">") + (ImGui.GetStyle().WindowPadding.X * 2f)
            : StockScopeRailWidth;
        if (ImGui.BeginChild("AcquisitionWorkbenchStockScope", new Vector2(width, height), true))
        {
            if (collapsed)
            {
                DrawStockScopeRailToggle(collapse: false);
            }
            else
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(MainWindow.ColHeader, "Count stock from");
                DalamudUiLayout.SameLineRight(DalamudUiLayout.ButtonWidth("<"));
                DrawStockScopeRailToggle(collapse: true);
                ImGui.Separator();
                var canEdit = CanEdit(context);
                var otherAccounts = false;
                foreach (var character in scope)
                {
                    if (character.OtherAccount && !otherAccounts)
                    {
                        otherAccounts = true;
                        ImGui.Spacing();
                        ImGui.Separator();
                        ImGui.TextDisabled("Other accounts");
                    }
                    DrawStockScopeCharacter(character, canEdit);
                }
            }
        }
        ImGui.EndChild();
    }

    private void DrawStockScopeRailToggle(bool collapse)
    {
        if (ImGui.Button(collapse ? "<##StockScopeRail" : ">##StockScopeRail"))
            SetStockScopeRailCollapsed(collapse);
        if (!collapse && ImGui.IsItemHovered())
            ImGui.SetTooltip("Count stock from");
        RegisterLastControl(
            "acquisition.workbench.stock.rail",
            collapse ? "Collapse Count stock from" : "Expand Count stock from",
            enabled: true,
            selected: collapse,
            collapse ? "Expanded" : "Collapsed",
            () => SetStockScopeRailCollapsed(collapse));
    }

    private void SetStockScopeRailCollapsed(bool collapsed)
    {
        if (config.MarketAcquisitionStockScopeRailCollapsed == collapsed)
            return;
        config.MarketAcquisitionStockScopeRailCollapsed = collapsed;
        config.Save();
    }

    private void DrawStockScopeCharacter(WorkbenchStockScopeCharacter character, bool canEdit)
    {
        var id = (character.Character.LocalContentId, character.Character.HomeWorldId);
        var name = string.IsNullOrWhiteSpace(character.Character.CharacterName) ? "Character" : character.Character.CharacterName;
        var sources = character.Sources.ToList();
        var ticked = sources.Count(source => source.Selected);
        var all = ticked == sources.Count;
        var mixed = ticked > 0 && !all;
        var toggleable = canEdit && sources.Any(source => !source.Fixed && (source.Available || source.Selected));
        var controlId = $"acquisition.workbench.stock.{id.LocalContentId}.{id.HomeWorldId}";
        ImGui.PushID($"{id.LocalContentId}:{id.HomeWorldId}");
        var rightEdge = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;

        var check = all;
        ImGui.BeginDisabled(!toggleable);
        if (mixed)
            ImGuiP.PushItemFlag(ImGuiItemFlags.MixedValue, true);
        var toggled = ImGui.Checkbox("##Character", ref check);
        if (mixed)
            ImGuiP.PopItemFlag();
        ImGui.EndDisabled();
        if (toggled)
            SetCharacterStockScope(character, !all);
        RegisterLastToggle(
            controlId,
            $"Count all of {name}'s stock",
            toggleable,
            all,
            all ? "All" : mixed ? $"{ticked:N0} of {sources.Count:N0}" : "None",
            () => SetCharacterStockScope(character, !all));

        var expanded = scopeExpanded.TryGetValue(id, out var remembered) ? remembered : character.IsCurrent;
        var age = expanded ? null : CollapsedStaleAge(sources);
        var ageText = age is { } stale ? FormatAge(stale) : null;
        ImGui.SameLine();
        var room = rightEdge - ImGui.GetCursorPosX() - ImGui.GetTreeNodeToLabelSpacing() -
                   (ageText is null ? 0f : ImGui.CalcTextSize(ageText).X + ImGui.GetStyle().ItemSpacing.X);
        var shown = DalamudUiLayout.ElideText(name, Math.Max(0f, room));
        Vector4? tint = !character.Available ? MainWindow.ColMuted
            : character.IsCurrent ? CurrentCharacterTint
            : character.OtherAccount ? OtherAccountTint
            : null;
        if (tint is { } color)
            ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.SetNextItemOpen(expanded, ImGuiCond.Always);
        var open = ImGui.TreeNodeEx($"{shown}##Node", ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.NoTreePushOnOpen);
        if (tint is not null)
            ImGui.PopStyleColor();
        if (open != expanded)
            scopeExpanded[id] = open;
        if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(character.Character.HomeWorldName))
            ImGui.SetTooltip($"{name} · {character.Character.HomeWorldName}");
        RegisterLastControl(
            $"{controlId}.expand",
            $"{(open ? "Collapse" : "Expand")} {name}'s stock sources",
            enabled: true,
            selected: open,
            open ? "Expanded" : "Collapsed",
            () => scopeExpanded[id] = !(scopeExpanded.TryGetValue(id, out var current) ? current : character.IsCurrent));
        if (ageText is not null)
        {
            ImGui.SameLine(rightEdge - ImGui.CalcTextSize(ageText).X);
            ImGui.TextColored(MainWindow.ColMuted, ageText);
        }

        if (open)
        {
            ImGui.Indent(ImGui.GetTreeNodeToLabelSpacing());
            foreach (var source in sources)
                DrawStockScopeSource(character, source, name, canEdit);
            ImGui.Unindent(ImGui.GetTreeNodeToLabelSpacing());
        }
        ImGui.PopID();
    }

    private void DrawStockScopeSource(
        WorkbenchStockScopeCharacter character,
        WorkbenchStockScopeSource source,
        string characterName,
        bool canEdit)
    {
        var bags = source.Key.Container == MarketAcquisitionStockSourceKey.Bags;
        var part = bags ? "bags" : source.Key.RetainerId == 0 ? "retainers" : source.Key.RetainerId.ToString();
        ImGui.PushID(part);
        var rightEdge = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        var check = source.Selected;
        var enabled = canEdit && !source.Fixed && (source.Available || source.Selected);
        ImGui.BeginDisabled(!enabled);
        var toggled = ImGui.Checkbox("##Source", ref check);
        ImGui.EndDisabled();
        if (toggled)
            SetSourceStockScope(character, source, check);
        if (source.Fixed && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Purchases arrive in these bags, so they always count.");
        RegisterLastToggle(
            $"acquisition.workbench.stock.{source.Key.LocalContentId}.{source.Key.HomeWorldId}.{part}",
            $"Count {characterName} · {source.Label}",
            enabled,
            source.Selected,
            source.Selected ? "Counted" : "Not counted",
            () => SetSourceStockScope(character, source, !source.Selected));

        var ageText = StaleAgeSince(source.ObservedAtUtc) is { } age ? FormatAge(age) : null;
        ImGui.SameLine();
        var room = rightEdge - ImGui.GetCursorPosX() -
                   (ageText is null ? 0f : ImGui.CalcTextSize(ageText).X + ImGui.GetStyle().ItemSpacing.X);
        var shown = DalamudUiLayout.ElideText(source.Label, Math.Max(0f, room));
        if (source.Available)
            ImGui.TextUnformatted(shown);
        else
            ImGui.TextColored(MainWindow.ColMuted, shown);
        if (shown.Length != source.Label.Length && ImGui.IsItemHovered())
            ImGui.SetTooltip(source.Label);
        if (ageText is not null)
        {
            ImGui.SameLine(rightEdge - ImGui.CalcTextSize(ageText).X);
            ImGui.TextColored(MainWindow.ColMuted, ageText);
        }
        ImGui.PopID();
    }

    /// <summary>A collapsed character's age: its oldest ticked source, else its oldest source; only when stale.</summary>
    private static TimeSpan? CollapsedStaleAge(IReadOnlyCollection<WorkbenchStockScopeSource> sources)
    {
        var observed = sources.Where(source => source.ObservedAtUtc is not null).ToList();
        var candidates = observed.Any(source => source.Selected) ? observed.Where(source => source.Selected) : observed;
        var oldest = candidates.Select(source => source.ObservedAtUtc!.Value).DefaultIfEmpty().Min();
        return oldest == default ? null : StaleAgeSince(oldest);
    }

    private static TimeSpan? StaleAgeSince(DateTimeOffset? observedAtUtc) =>
        observedAtUtc is { } at && DateTimeOffset.UtcNow - at is var age &&
        age > RetainerAwareMarketAcquisitionInventoryObserver.RetainerStockStaleThreshold
            ? age
            : null;

    private void SetCharacterStockScope(WorkbenchStockScopeCharacter character, bool include) =>
        controller.SetCharacterStockScope(
            character.Character,
            character.IsCurrent,
            character.Bags is { Fixed: false } ? include : null,
            character.Retainers.IsDefaultOrEmpty
                ? null
                : include ? MarketAcquisitionRetainerScope.Every : MarketAcquisitionRetainerScope.None,
            RetainerNames(character));

    private void SetSourceStockScope(WorkbenchStockScopeCharacter character, WorkbenchStockScopeSource source, bool include)
    {
        if (source.Fixed)
            return;
        if (source.Key.Container == MarketAcquisitionStockSourceKey.Bags)
        {
            controller.SetCharacterStockScope(character.Character, character.IsCurrent, include, null, RetainerNames(character));
            return;
        }
        // The whole-retainers node stands for every retainer; a named one narrows or widens the scope by one.
        var scope = source.Key.RetainerId == 0
            ? include ? MarketAcquisitionRetainerScope.Every : MarketAcquisitionRetainerScope.None
            : MarketAcquisitionStockScope.Toggle(character.RetainerScope, source.Key.RetainerId, include, character.KnownRetainerIds);
        controller.SetCharacterStockScope(character.Character, character.IsCurrent, null, scope, RetainerNames(character));
    }

    private static Func<ulong, string> RetainerNames(WorkbenchStockScopeCharacter character) =>
        retainerId => character.Retainers.FirstOrDefault(source => source.Key.RetainerId == retainerId)?.Key.RetainerName ?? string.Empty;

    private void DrawStowAfterDelivery()
    {
        if (workbenchStock?.View.HasItemHomes != true)
            return;
        var stow = document.StowAfterDelivery;
        ImGui.SameLine();
        if (ImGui.Checkbox("Stow after delivery##StowAfterDelivery", ref stow))
            controller.SetStowAfterDelivery(stow);
    }

    private void DrawBuyListBar(MarketAcquisitionRequestBuilderContext context)
    {
        var shortfalls = workbenchStock is { View.HasReserveLevels: true } stock
            ? stock.ReserveShortfalls(document)
            : [];
        if (shortfalls.IsDefaultOrEmpty)
        {
            ImGuiUi.SectionHeader("Buy list", MainWindow.ColHeader);
            return;
        }

        ImGuiUi.SectionHeaderWithActions("Buy list", MainWindow.ColHeader, () =>
        {
            ImGui.BeginDisabled(!CanEdit(context));
            if (ImGui.SmallButton($"Add reserve shortfalls ({shortfalls.Length:N0})"))
                controller.AddLines(shortfalls.Select(item => new MarketAcquisitionRequestLineDocument
                {
                    ItemId = item.ItemId,
                    ItemName = item.ItemName,
                    QuantityMode = "TargetQuantity",
                    TargetQuantity = (uint)item.ReserveUnits,
                    TargetBasis = MarketAcquisitionTargetBases.OnHandTotal,
                    HqPolicy = item.Quality == "HqOnly" ? "HQOnly" : "NQOnly",
                    RestockReserve = true,
                }), "reserve shortfall");
            ImGui.EndDisabled();
        });
    }

    /// <summary>Restock lines follow reserve levels edited in Quartermaster (never mid-edit or mid-route).</summary>
    private void SyncRestockReserveTargets(MarketAcquisitionRequestBuilderContext context)
    {
        if (workbenchStock?.View is not { HasReserveLevels: true } view || view.Version == syncedReserveVersion ||
            !CanEdit(context) || pendingCellEdits.Keys.Any(key => key.Field == CellField.Quantity))
            return;
        syncedReserveVersion = view.Version;
        if (document.ExactAcquisitionAuthority is null && document.Lines.Any(line => line.RestockReserve))
            controller.SyncRestockReserveTargets(workbenchStock.ReserveLevelFor);
    }

    private bool IsRestockReserveLine(MarketAcquisitionRequestLineDocument line) =>
        line.RestockReserve && workbenchStock?.View.HasReserveLevels == true;

    private void DrawCompactModeCell(MarketAcquisitionRequestLineDocument line, int index)
    {
        const string restockReserve = "RestockReserve";
        var current = IsRestockReserveLine(line)
            ? restockReserve
            : string.IsNullOrWhiteSpace(line.QuantityMode) ? "AllBelowThreshold" : line.QuantityMode;
        // Restock reserve is offered only for items Quartermaster keeps a reserve level for.
        var reserveLevel = workbenchStock?.View.HasReserveLevels == true && !controller.IsExactAcquisitionLine(index)
            ? workbenchStock.ReserveLevelFor(line)
            : null;
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##Mode", current == restockReserve ? "Restock reserve" : FormatEditorOption(current)))
            return;

        if (reserveLevel is not null || current == restockReserve)
        {
            if (ImGui.Selectable("Restock reserve", current == restockReserve) && current != restockReserve)
                controller.SetRestockReserve(index, true, reserveLevel ?? line.TargetQuantity);
            if (current == restockReserve)
                ImGui.SetItemDefaultFocus();
        }

        foreach (var mode in new[] { "TargetQuantity", "AllBelowThreshold" })
        {
            var selected = string.Equals(mode, current, StringComparison.Ordinal);
            if (ImGui.Selectable(FormatEditorOption(mode), selected) && !selected)
            {
                if (current == restockReserve && mode == "TargetQuantity")
                    controller.SetRestockReserve(index, false, line.TargetQuantity);
                else
                    ApplyLineEdit(
                        index,
                        line,
                        quantityMode: mode,
                        targetQuantity: mode == "TargetQuantity" ? Math.Max(1u, line.TargetQuantity) : 0,
                        maxQuantity: 0,
                        message: "Buying rule updated.");
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawCompactQuantityCell(MarketAcquisitionRequestLineDocument line, int index, WorkbenchLineKey key)
    {
        if (!line.QuantityMode.Equals("TargetQuantity", StringComparison.OrdinalIgnoreCase))
        {
            if (line.MaxQuantity == 0)
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(MainWindow.ColMuted, "-");
                return;
            }

            ImGui.PushStyleColor(ImGuiCol.Text, MainWindow.ColWarning);
            if (ImGui.Button($"Clear cap {line.MaxQuantity:N0}"))
                ApplyLineEdit(index, line, maxQuantity: 0, message: "Legacy quantity cap cleared.");
            ImGui.PopStyleColor();
            return;
        }

        ImGui.SetNextItemWidth(-1);
        // While a reserve level's target is typed, Quartermaster's level is not synced over it (see the pending edit).
        if (DrawDeferredNumberCell(key, CellField.Quantity, "##Quantity", CellValue(line, CellField.Quantity), out var quantity))
            ApplyCellValue(index, line, CellField.Quantity, quantity);
    }

    /// <summary>
    /// A grouped integer cell that keeps typed text to itself until the cell loses focus (Tab and click away
    /// commit, Escape cancels). Returns true on the frame a changed value should be written.
    /// </summary>
    private bool DrawDeferredNumberCell(WorkbenchLineKey line, CellField field, string id, long current, out long value)
    {
        var key = (line, field);
        var frame = ImGui.GetFrameCount();
        value = pendingCellEdits.TryGetValue(key, out var pending) ? pending.Value : current;
        DalamudGroupedIntegerInput.InputGroupedInt(id, ref value, 0, 0);
        if (ImGui.IsItemActive())
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Escape))
            {
                pendingCellEdits.Remove(key);
                value = current;
                return false;
            }
            pendingCellEdits[key] = new PendingCellEdit(value, frame);
            return false;
        }
        var hadPending = pendingCellEdits.Remove(key);
        if (!ImGui.IsItemDeactivated() || ImGui.IsKeyPressed(ImGuiKey.Escape) || !hadPending)
        {
            value = current;
            return false;
        }
        return value != current;
    }

    /// <summary>
    /// Writes typed cell values whose cell stopped being drawn while it was being edited (the line moved, the
    /// layout changed, or the Workbench was hidden), so leaving a cell never loses what was typed.
    /// </summary>
    private void CommitOrphanedCellEdits(MarketAcquisitionRequestBuilderContext context)
    {
        // A typed value belongs to the Workbench it was typed in; a loaded or new one starts clean.
        if (!string.Equals(pendingCellEditsRequestId, document.LocalRequestId, StringComparison.Ordinal))
        {
            pendingCellEdits.Clear();
            pendingCellEditsRequestId = document.LocalRequestId;
        }
        if (pendingCellEdits.Count == 0)
            return;
        var frame = ImGui.GetFrameCount();
        var orphaned = pendingCellEdits.Where(entry => entry.Value.Frame < frame - 1).ToArray();
        if (orphaned.Length == 0)
            return;
        var canEdit = CanEdit(context);
        foreach (var ((lineKey, field), pending) in orphaned)
        {
            pendingCellEdits.Remove((lineKey, field));
            var index = controller.LineIndex(lineKey);
            if (!canEdit || index < 0 || controller.IsExactAcquisitionLine(index))
                continue;
            var line = document.Lines[index];
            var stockUp = IsStockUpLine(line);
            if (field is CellField.Quantity or CellField.Overage && !stockUp)
                continue;
            if (pending.Value != CellValue(line, field))
                ApplyCellValue(index, line, field, pending.Value);
        }
    }

    private long CellValue(MarketAcquisitionRequestLineDocument line, CellField field) =>
        field switch
        {
            CellField.Quantity => IsRestockReserveLine(line) ? Math.Min((long)line.TargetQuantity, MaxReserveLevel) : line.TargetQuantity,
            CellField.Unit => line.MaxUnitPrice,
            CellField.Overage => line.MaximumOverage,
            CellField.Spend => line.GilCap,
            _ => 0,
        };

    private void ApplyCellValue(int index, MarketAcquisitionRequestLineDocument line, CellField field, long value)
    {
        switch (field)
        {
            case CellField.Quantity:
                var restock = IsRestockReserveLine(line);
                var target = restock ? (uint)Math.Clamp(value, 1, MaxReserveLevel) : Math.Max(1u, ClampToUInt(value));
                ApplyLineEdit(
                    index,
                    line,
                    targetQuantity: target,
                    maxQuantity: 0,
                    message: restock ? "Reserve level updated." : "Target quantity updated.");
                if (restock)
                    workbenchStock!.RequestReserveLevel(line, target);
                break;
            case CellField.Unit:
                ApplyLineEdit(index, line, maxUnitPrice: ClampToUInt(value), message: "Unit ceiling updated.");
                break;
            case CellField.Overage:
                ApplyLineEdit(index, line, maximumOverage: ClampToUInt(value), message: "Maximum target overage updated.");
                break;
            case CellField.Spend:
                ApplyLineEdit(index, line, gilCap: ClampToUInt(value), message: "Spend ceiling updated.");
                break;
        }
    }

    // Quartermaster's reserve level ceiling (ListingReserveRule.MaxReserveUnits).
    private const long MaxReserveLevel = 999_999;

    private void DrawCompactUnitCell(MarketAcquisitionRequestLineDocument line, int index, WorkbenchLineKey key)
    {
        ImGui.SetNextItemWidth(-1);
        if (DrawDeferredNumberCell(key, CellField.Unit, "##Unit", CellValue(line, CellField.Unit), out var maxUnit))
            ApplyCellValue(index, line, CellField.Unit, maxUnit);
    }

    private void DrawCompactOverageCell(MarketAcquisitionRequestLineDocument line, int index, WorkbenchLineKey key)
    {
        if (!line.QuantityMode.Equals("TargetQuantity", StringComparison.OrdinalIgnoreCase))
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(MainWindow.ColMuted, "-");
            return;
        }

        ImGui.SetNextItemWidth(-1);
        if (DrawDeferredNumberCell(key, CellField.Overage, "##MaximumOverage", CellValue(line, CellField.Overage), out var maximumOverage))
            ApplyCellValue(index, line, CellField.Overage, maximumOverage);
    }

    private void DrawUnitPriceContextMenu(
        MarketAcquisitionRequestLineDocument line,
        int index,
        WorkbenchLineKey key,
        bool canEdit)
    {
        if (!ImGui.BeginPopupContextItem("##UnitQuoteActions"))
            return;

        var identity = CraftAppraisalRequestMapper.BuildLineIdentity(document, line);
        var threshold = craftAppraisal.State.TryGetLineQuoteThreshold(identity);
        var canFetch = canEdit &&
                       !isAppraising &&
                       line.ItemId != 0;
        if (ImGuiUi.MenuItem(
                threshold is > 0 ? "Refresh Craft Architect quote" : "Get Craft Architect quote",
                canFetch))
        {
            _ = FetchCraftQuotesAsync([key]);
        }

        if (threshold is > 0 &&
            ImGuiUi.MenuItem("Use Craft Architect quote", canEdit && line.MaxUnitPrice != threshold.Value))
        {
            SetLineMaxUnitPrice(index, threshold.Value, "Unit ceiling set from Craft Architect quote.");
        }

        var quote = craftAppraisal.State.GetLineQuote(identity)?.Quote;
        if (!string.IsNullOrWhiteSpace(quote?.PlanUrl) &&
            ImGuiUi.MenuItem("Open Craft Architect plan", enabled: true))
        {
            OpenCraftArchitectPlan(quote.PlanUrl);
        }

        ImGui.EndPopup();
    }

    private void DrawCompactSpendCell(MarketAcquisitionRequestLineDocument line, int index, WorkbenchLineKey key)
    {
        ImGui.SetNextItemWidth(-1);
        if (DrawDeferredNumberCell(key, CellField.Spend, "##Spend", CellValue(line, CellField.Spend), out var gilCap))
            ApplyCellValue(index, line, CellField.Spend, gilCap);
    }

    public void DrawRunBudgetInput()
    {
        var text = document.RunSpendCap == 0 ? string.Empty : document.RunSpendCap.ToString();
        ImGui.SetNextItemWidth(180);
        if (ImGui.InputTextWithHint("Run budget##RunSpendCap", "Unset", ref text, 10, ImGuiInputTextFlags.CharsDecimal) &&
            (text.Length == 0 || uint.TryParse(text, out _)))
            controller.SetRunSpendCap(ParseUInt(text));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Confirmed wallet spend including market tax across this finalized plan, including pause, recovery and restart. A newly finalized plan starts fresh. Plugin reload resets the tally: plans are not persisted and you must re-finalize.");
    }

    private void DrawCompactHqCell(MarketAcquisitionRequestLineDocument line, int index)
    {
        var current = string.IsNullOrWhiteSpace(line.HqPolicy) ? "Either" : line.HqPolicy;
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##Quality", FormatEditorOption(current)))
            return;

        foreach (var policy in new[] { "Either", "HQOnly", "NQOnly" })
        {
            var selected = string.Equals(policy, current, StringComparison.Ordinal);
            if (ImGui.Selectable(FormatEditorOption(policy), selected))
                ApplyLineEdit(index, line, hqPolicy: policy, message: "Quality updated.");
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawCompactEvidenceState(MarketAcquisitionRequestLineDocument line)
    {
        var identity = CraftAppraisalRequestMapper.BuildLineIdentity(document, line);
        var quote = craftAppraisal.State.GetLineQuote(identity)?.Quote;
        var threshold = craftAppraisal.State.TryGetLineQuoteThreshold(identity);
        var warningCount = quote?.Warnings.Count ?? 0;
        var summary = MarketAcquisitionSelectedLinePresenter.FormatEvidenceSummary(
            line,
            quote,
            threshold,
            warningCount);
        ImGui.TextColored(
            warningCount > 0 || threshold is null && line.MaxUnitPrice == 0
                ? MainWindow.ColWarning
                : threshold is > 0
                    ? MainWindow.ColSuccess
                    : MainWindow.ColMuted,
            summary);
        if (warningCount > 0 && ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 34f);
            foreach (var warning in quote!.Warnings)
                ImGui.TextWrapped(warning);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    private MarketAcquisitionSelectedLinePresentation? BuildSelectedLinePresentation(
        MarketAcquisitionRequestBuilderContext context)
    {
        var index = controller.SingleSelectedLineIndex;
        var isExactLine = index >= 0 &&
                          index < document.Lines.Count &&
                          controller.IsExactAcquisitionLine(index);
        return MarketAcquisitionSelectedLinePresenter.Build(
            document,
            index,
            craftAppraisal.State,
            CanEdit(context),
            isAppraising,
            isExactLine,
            DateTimeOffset.UtcNow);
    }

    private void DrawRemovalUndo(MarketAcquisitionRequestBuilderContext context)
    {
        // An undo belongs to the Workbench it was made in.
        if (removalUndo.TryPeek(out var pending) &&
            !string.Equals(pending.After.LocalRequestId, document.LocalRequestId, StringComparison.Ordinal))
            removalUndo.Clear();
        var canEdit = CanEdit(context);
        ImGui.BeginDisabled(!canEdit);
        if (removalUndo.Draw(
                "AcquisitionWorkbenchRemovalUndo",
                removal => removal.Label,
                out var restored,
                result => RegisterRemovalUndoControl(result, canEdit)) && canEdit)
            controller.UndoRemoval(restored);
        ImGui.EndDisabled();
    }

    private void RegisterRemovalUndoControl(DalamudUndoStripResult result, bool canEdit)
    {
        if (result == DalamudUndoStripResult.Undo)
            RegisterLastControl(
                "acquisition.workbench.remove.undo",
                removalUndo.TryPeek(out var removal) ? $"Undo: {removal.Label}" : "Undo removal",
                canEdit,
                selected: false,
                value: null,
                () =>
                {
                    if (removalUndo.TryTake(out var taken))
                        controller.UndoRemoval(taken);
                });
        else if (result == DalamudUndoStripResult.Dismiss)
            RegisterLastControl(
                "acquisition.workbench.remove.dismiss",
                "Dismiss the removal undo",
                enabled: true,
                selected: false,
                value: null,
                removalUndo.Clear);
    }

    private void RemoveLinesWithUndo(IReadOnlyCollection<WorkbenchLineKey> lineKeys)
    {
        if (controller.RemoveLines(lineKeys) is not { } removal)
            return;
        removalUndo.Offer(removal);
        if (controller.Selection.Count != 1)
            selectedLineInspectorRequested = false;
    }

    private void DrawSelectionBar(
        MarketAcquisitionRequestBuilderContext context,
        MarketAcquisitionSelectedLinePresentation? single)
    {
        selectionBar.Draw(controller.Selection.Count, MainWindow.ColHeader, hasSelection =>
        {
            if (hasSelection)
                DrawBulkActions(context, single);
        });
    }

    private void DrawBulkActions(
        MarketAcquisitionRequestBuilderContext context,
        MarketAcquisitionSelectedLinePresentation? single)
    {
        var canEdit = CanEdit(context);
        var lineKeys = WorkbenchLineKey.For(document.Lines);
        var selected = document.Lines
            .Select((line, index) => (Line: line, Index: index, Key: lineKeys[index]))
            .Where(entry => controller.Selection.IsSelected(entry.Key))
            .ToList();
        var ids = selected.Select(entry => entry.Key).ToArray();
        var editable = selected.Where(entry => !controller.IsExactAcquisitionLine(entry.Index)).ToList();
        var fields = new List<BulkField>();
        if (editable.Count > 0)
            fields.AddRange([BulkField.UnitCeiling, BulkField.SpendCeiling]);
        if (editable.Any(entry => IsStockUpLine(entry.Line)))
            fields.AddRange([BulkField.TargetOnHand, BulkField.ExtraUnits]);
        if (editable.Count > 0)
            fields.AddRange([BulkField.Quality, BulkField.BuyingRule]);

        if (single is not null)
            DrawSingleSelectionSummary(single);

        ImGui.BeginDisabled(!canEdit);
        if (fields.Count > 0)
            DrawBulkFieldEditor(fields, ids, editable.Select(entry => entry.Line).ToList(), canEdit);

        var quoteLabel = selected.Count == 1 && single is not null
            ? single.Actions.First(action => action.Kind == MarketAcquisitionSelectedLineActionKind.RefreshQuote).Label
            : "Get quote";
        DalamudUiLayout.SameLineIfFits(quoteLabel);
        if (ImGuiUi.Button($"{quoteLabel}##BulkQuote", !isAppraising))
            _ = FetchCraftQuotesAsync(ids);
        RegisterLastControl(
            "acquisition.workbench.bulk.quote.refresh",
            $"{quoteLabel} for {DescribeSelection(selected.Count, single)}",
            canEdit && !isAppraising,
            selected: false,
            value: ids.Length.ToString(),
            () => _ = FetchCraftQuotesAsync(ids));

        var quoted = editable
            .Select(entry => (entry.Line, entry.Key, Threshold: QuoteThreshold(entry.Line)))
            .Where(entry => entry.Threshold is > 0 && entry.Threshold.Value != entry.Line.MaxUnitPrice)
            .ToDictionary(entry => entry.Key, entry => entry.Threshold!.Value);
        if (quoted.Count > 0)
        {
            DalamudUiLayout.SameLineIfFits("Use quote");
            if (ImGuiUi.PrimaryButton("Use quote##BulkUseQuote", true))
                UseQuotes(quoted);
            RegisterLastControl(
                "acquisition.workbench.bulk.quote.apply",
                $"Use Craft Architect quotes for {quoted.Count:N0} line{(quoted.Count == 1 ? string.Empty : "s")}",
                canEdit,
                selected: false,
                value: quoted.Count.ToString(),
                () => UseQuotes(quoted));
        }

        DalamudUiLayout.SameLineIfFits("Remove");
        if (ImGuiUi.Button("Remove##BulkRemove", true))
            RemoveLinesWithUndo(ids);
        RegisterLastControl(
            "acquisition.workbench.bulk.remove",
            $"Remove {DescribeSelection(selected.Count, single)}",
            canEdit,
            selected: false,
            value: ids.Length.ToString(),
            () => RemoveLinesWithUndo(ids));
        ImGui.EndDisabled();

        if (single is not null)
        {
            DalamudUiLayout.SameLineIfFits("Details");
            if (ImGuiUi.Button("Details", enabled: true))
                selectedLineInspectorRequested = true;
            RegisterLastControl(
                $"acquisition.workbench.line.{single.ItemId}.details",
                $"Show pricing evidence for {single.ItemName}",
                enabled: true,
                selected: false,
                value: single.ItemId.ToString(),
                () => selectedLineInspectorRequested = true);
            if (!string.IsNullOrWhiteSpace(single.PlanUrl))
            {
                DalamudUiLayout.SameLineIfFits("Open plan");
                if (ImGuiUi.Button("Open plan", enabled: true))
                    OpenCraftArchitectPlan(single.PlanUrl);
                RegisterLastControl(
                    $"acquisition.workbench.line.{single.ItemId}.quote.open-plan",
                    $"Open quoted Craft Architect plan for {single.ItemName}",
                    enabled: true,
                    selected: false,
                    value: single.PlanId,
                    () => OpenCraftArchitectPlan(single.PlanUrl));
            }
        }

        DalamudUiLayout.SameLineRight(DalamudUiLayout.ButtonWidth("Clear selection"));
        if (ImGuiUi.Button("Clear selection", enabled: true))
            ClearSelection();
        RegisterLastControl(
            "acquisition.workbench.bulk.clear",
            "Clear the buy-list selection",
            enabled: true,
            selected: false,
            value: ids.Length.ToString(),
            ClearSelection);
    }

    private void DrawSingleSelectionSummary(MarketAcquisitionSelectedLinePresentation single)
    {
        ImGui.SameLine();
        ImGui.TextColored(MainWindow.ColHeader, single.ItemName);
        var price = single.QuoteUnitPrice is > 0
            ? $"{single.QuoteUnitPrice.Value:N0} gil"
            : single.CurrentUnitPrice > 0
                ? $"{single.CurrentUnitPrice:N0} gil manual"
                : "No quote";
        DalamudUiLayout.SameLineIfFits(ImGui.CalcTextSize(price).X);
        ImGui.TextColored(single.QuoteUnitPrice is > 0 ? MainWindow.ColSuccess : MainWindow.ColMuted, price);
        if (single.Warnings.Count == 0)
            return;
        var warnings = $"{single.Warnings.Count:N0} warning{(single.Warnings.Count == 1 ? string.Empty : "s")}";
        DalamudUiLayout.SameLineIfFits(ImGui.CalcTextSize(warnings).X);
        ImGui.TextColored(MainWindow.ColWarning, warnings);
        DrawWarningsTooltip(single.Warnings);
    }

    /// <summary>Picks one field, then sets it on every selected line it applies to as a single edit.</summary>
    private void DrawBulkFieldEditor(
        IReadOnlyList<BulkField> fields,
        IReadOnlyCollection<WorkbenchLineKey> ids,
        IReadOnlyList<MarketAcquisitionRequestLineDocument> editable,
        bool canEdit)
    {
        if (!fields.Contains(bulkField))
            SelectBulkField(fields[0]);
        const float fieldWidth = 150f;
        const float valueWidth = 110f;
        DalamudUiLayout.SameLineIfFits(fieldWidth);
        ImGui.SetNextItemWidth(fieldWidth);
        if (ImGui.BeginCombo("##BulkField", BulkFieldLabel(bulkField)))
        {
            foreach (var field in fields)
            {
                if (ImGui.Selectable(BulkFieldLabel(field), field == bulkField) && field != bulkField)
                    SelectBulkField(field);
                if (field == bulkField)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        RegisterLastArgumentControl(
            "acquisition.workbench.bulk.field",
            $"Field to set on the selected lines: {string.Join(", ", fields.Select(BulkFieldLabel))}",
            AgentBridgeUiControlKind.Select,
            canEdit,
            BulkFieldLabel(bulkField),
            value =>
            {
                var match = fields.FirstOrDefault(field => string.Equals(BulkFieldLabel(field), value, StringComparison.OrdinalIgnoreCase));
                if (!string.Equals(BulkFieldLabel(match), value, StringComparison.OrdinalIgnoreCase))
                    return AgentBridgeUiActionResult.Fail($"Choose one of: {string.Join(", ", fields.Select(BulkFieldLabel))}.");
                SelectBulkField(match);
                return AgentBridgeUiActionResult.Ok($"{BulkFieldLabel(match)} selected.");
            });

        // Lines "Stock up to" would switch that have no target yet; they need one typed before it applies.
        var needTarget = editable.Count(line => !IsStockUpLine(line) && line.TargetQuantity == 0);
        if (bulkField is BulkField.Quality or BulkField.BuyingRule)
        {
            var options = bulkField == BulkField.Quality
                ? new[] { "Either", "HQOnly", "NQOnly" }
                : new[] { "TargetQuantity", "AllBelowThreshold" };
            var stockUpPending = bulkField == BulkField.BuyingRule && bulkStockUpPending;
            DalamudUiLayout.SameLineIfFits(valueWidth);
            ImGui.SetNextItemWidth(valueWidth);
            if (ImGui.BeginCombo("##BulkChoice", stockUpPending ? FormatEditorOption("TargetQuantity") : string.Empty, ImGuiComboFlags.HeightRegular))
            {
                foreach (var option in options)
                    if (ImGui.Selectable(FormatEditorOption(option)))
                        ChooseBulkOption(ids, option, needTarget);
                ImGui.EndCombo();
            }
            RegisterLastArgumentControl(
                "acquisition.workbench.bulk.choice",
                $"Set {BulkFieldLabel(bulkField)} on the selected lines: {string.Join(", ", options.Select(FormatEditorOption))}",
                AgentBridgeUiControlKind.Select,
                canEdit,
                null,
                value =>
                {
                    var option = options.FirstOrDefault(candidate =>
                        string.Equals(FormatEditorOption(candidate), value, StringComparison.OrdinalIgnoreCase));
                    if (option is null)
                        return AgentBridgeUiActionResult.Fail($"Choose one of: {string.Join(", ", options.Select(FormatEditorOption))}.");
                    if (!ChooseBulkOption(ids, option, needTarget))
                        return AgentBridgeUiActionResult.Ok($"Stock up to chosen; {needTarget:N0} line{(needTarget == 1 ? " has" : "s have")} no target on hand. Enter one in acquisition.workbench.bulk.value, then acquisition.workbench.bulk.apply.");
                    return AgentBridgeUiActionResult.Ok($"{BulkFieldLabel(bulkField)} set.");
                });
            if (!stockUpPending)
                return;
        }

        var stockingUp = bulkField == BulkField.BuyingRule;
        DalamudUiLayout.SameLineIfFits(valueWidth);
        ImGui.SetNextItemWidth(valueWidth);
        var submitted = ImGui.InputTextWithHint("##BulkValue", stockingUp ? "Target on hand" : string.Empty, ref bulkValueBuffer, 16,
            ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
        RegisterLastArgumentControl(
            "acquisition.workbench.bulk.value",
            stockingUp ? "Target on hand for the lines switched to Stock up to" : $"{BulkFieldLabel(bulkField)} for the selected lines",
            AgentBridgeUiControlKind.Input,
            canEdit,
            bulkValueBuffer,
            value =>
            {
                bulkValueBuffer = value.Trim();
                return AgentBridgeUiActionResult.Ok("Value entered.");
            });
        var typed = TryParseBulkValue(out var typedValue);
        // A switched line's target is at least 1; lines that already have one keep it unless a value is typed.
        var canApply = stockingUp ? needTarget == 0 || (typed && typedValue > 0) : typed;
        DalamudUiLayout.SameLineIfFits("Apply");
        if ((ImGuiUi.Button("Apply##BulkApply", canApply) || submitted) && canApply)
            ApplyBulkValue(ids);
        RegisterLastControl(
            "acquisition.workbench.bulk.apply",
            stockingUp ? "Switch the selected lines to Stock up to" : $"Set {BulkFieldLabel(bulkField)} on the selected lines",
            canEdit && canApply,
            selected: false,
            value: bulkValueBuffer,
            () => ApplyBulkValue(ids));
    }

    private void SelectBulkField(BulkField field)
    {
        bulkField = field;
        bulkValueBuffer = string.Empty;
        bulkStockUpPending = false;
    }

    /// <summary>
    /// Applies a Quality or Buying rule choice. "Stock up to" waits for a typed target when any line it would
    /// switch has none; returns false then.
    /// </summary>
    private bool ChooseBulkOption(IReadOnlyCollection<WorkbenchLineKey> ids, string option, int needTarget)
    {
        if (bulkField == BulkField.BuyingRule && option == "TargetQuantity" && needTarget > 0)
        {
            bulkStockUpPending = true;
            bulkValueBuffer = string.Empty;
            return false;
        }
        bulkStockUpPending = false;
        ApplyBulkChoice(ids, option);
        return true;
    }

    private bool TryParseBulkValue(out long value) =>
        GroupedIntegerParser.TryParse(bulkValueBuffer, out value) && value >= 0;

    private void ApplyBulkValue(IReadOnlyCollection<WorkbenchLineKey> ids)
    {
        if (bulkField == BulkField.BuyingRule)
        {
            uint? target = TryParseBulkValue(out var typedTarget) && typedTarget > 0 ? ClampToUInt(typedTarget) : null;
            if (controller.ApplyBulkLineEdit(ids, line =>
                    IsStockUpLine(line) || (target is null && line.TargetQuantity == 0)
                        ? null
                        : EditLine(line, quantityMode: "TargetQuantity", targetQuantity: target ?? line.TargetQuantity, maxQuantity: 0),
                    "Buying rule updated.") > 0)
            {
                bulkStockUpPending = false;
                bulkValueBuffer = string.Empty;
            }
            return;
        }
        if (!TryParseBulkValue(out var typed))
            return;
        var amount = ClampToUInt(typed);
        var reserveLevels = new List<(MarketAcquisitionRequestLineDocument Line, uint Level)>();
        var label = BulkFieldLabel(bulkField);
        var changed = controller.ApplyBulkLineEdit(ids, line =>
        {
            switch (bulkField)
            {
                case BulkField.UnitCeiling:
                    return EditLine(line, maxUnitPrice: amount);
                case BulkField.SpendCeiling:
                    return EditLine(line, gilCap: amount);
                case BulkField.TargetOnHand when IsStockUpLine(line):
                    var restock = IsRestockReserveLine(line);
                    var target = restock ? (uint)Math.Clamp(typed, 1, MaxReserveLevel) : Math.Max(1u, amount);
                    if (restock && target != line.TargetQuantity)
                        reserveLevels.Add((line, target));
                    return EditLine(line, targetQuantity: target, maxQuantity: 0);
                case BulkField.ExtraUnits when IsStockUpLine(line):
                    return EditLine(line, maximumOverage: amount);
                default:
                    return null;
            }
        }, $"{label} updated.");
        foreach (var (line, level) in reserveLevels)
            workbenchStock?.RequestReserveLevel(line, level);
        if (changed > 0)
            bulkValueBuffer = string.Empty;
    }

    /// <summary>Applies a choice that needs no value: a quality, Buy below ceiling, or Stock up to on lines that all have a target.</summary>
    private void ApplyBulkChoice(IReadOnlyCollection<WorkbenchLineKey> ids, string option)
    {
        if (bulkField == BulkField.Quality)
        {
            controller.ApplyBulkLineEdit(ids, line => EditLine(line, hqPolicy: option), "Quality updated.");
            return;
        }
        controller.ApplyBulkLineEdit(ids, line =>
            option == "TargetQuantity"
                ? IsStockUpLine(line) || line.TargetQuantity == 0 ? null : EditLine(line, quantityMode: option, maxQuantity: 0)
                : IsStockUpLine(line) ? EditLine(line, quantityMode: option, targetQuantity: 0, maxQuantity: 0) : null,
            "Buying rule updated.");
    }

    private void UseQuotes(IReadOnlyDictionary<WorkbenchLineKey, uint> thresholds)
    {
        // Thresholds belong to the lines as they were drawn; match each line by its key at edit time.
        var keys = WorkbenchLineKey.For(document.Lines);
        var byLine = new Dictionary<MarketAcquisitionRequestLineDocument, uint>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < keys.Count; index++)
            if (thresholds.TryGetValue(keys[index], out var threshold))
                byLine[document.Lines[index]] = threshold;
        controller.ApplyBulkLineEdit(
            thresholds.Keys,
            line => byLine.TryGetValue(line, out var threshold) ? EditLine(line, maxUnitPrice: threshold) : null,
            "Unit ceiling set from Craft Architect quote.");
    }

    private uint? QuoteThreshold(MarketAcquisitionRequestLineDocument line) =>
        craftAppraisal.State.TryGetLineQuoteThreshold(CraftAppraisalRequestMapper.BuildLineIdentity(document, line));

    private static bool IsStockUpLine(MarketAcquisitionRequestLineDocument line) =>
        line.QuantityMode.Equals("TargetQuantity", StringComparison.OrdinalIgnoreCase);

    private static MarketAcquisitionRequestLineDocument EditLine(
        MarketAcquisitionRequestLineDocument line,
        string? quantityMode = null,
        uint? targetQuantity = null,
        uint? maximumOverage = null,
        uint? maxQuantity = null,
        string? hqPolicy = null,
        uint? maxUnitPrice = null,
        uint? gilCap = null) =>
        RequestDocumentMutation.EditLine(
            line,
            quantityMode ?? line.QuantityMode,
            targetQuantity ?? line.TargetQuantity,
            maximumOverage ?? line.MaximumOverage,
            maxQuantity ?? line.MaxQuantity,
            hqPolicy ?? line.HqPolicy,
            maxUnitPrice ?? line.MaxUnitPrice,
            gilCap ?? line.GilCap);

    private static string BulkFieldLabel(BulkField field) =>
        field switch
        {
            BulkField.UnitCeiling => "Unit ceiling",
            BulkField.SpendCeiling => "Spend ceiling",
            BulkField.TargetOnHand => "Target on hand",
            BulkField.ExtraUnits => "Extra units allowed",
            BulkField.Quality => "Quality",
            BulkField.BuyingRule => "Buying rule",
            _ => field.ToString(),
        };

    private static string DescribeSelection(int count, MarketAcquisitionSelectedLinePresentation? single) =>
        count == 1 && single is not null ? single.ItemName : $"{count:N0} selected lines";

    private void ClearSelection()
    {
        controller.ClearSelection();
        selectedLineInspectorRequested = false;
    }

    private void DrawSelectedLineInspector(
        MarketAcquisitionRequestBuilderContext context,
        MarketAcquisitionSelectedLinePresentation selection)
    {
        ImGui.TextColored(MainWindow.ColHeader, selection.ItemName);
        var closeWidth = ImGui.CalcTextSize("Close details").X + (ImGui.GetStyle().FramePadding.X * 2f);
        ImGuiUi.SameLineRight(closeWidth);
        if (ImGuiUi.Button("Close details", enabled: true))
            selectedLineInspectorRequested = false;
        RegisterLastControl(
            $"acquisition.workbench.line.{selection.ItemId}.details.close",
            $"Close pricing evidence for {selection.ItemName}",
            enabled: true,
            selected: true,
            value: selection.ItemId.ToString(),
            () => selectedLineInspectorRequested = false);
        ImGui.Separator();

        ImGui.TextColored(MainWindow.ColMuted, "CRAFT ARCHITECT QUOTE");
        ImGui.TextColored(
            selection.QuoteUnitPrice is > 0 ? MainWindow.ColSuccess : MainWindow.ColMuted,
            selection.QuoteUnitPrice is > 0
                ? $"{selection.QuoteUnitPrice.Value:N0} gil"
                : "No complete quote");
        ImGui.TextColored(
            MainWindow.ColMuted,
            $"{selection.QuoteSource} · {selection.QuoteConfidence} confidence");
        ImGui.Spacing();

        ImGui.TextColored(MainWindow.ColMuted, "CURRENT UNIT CEILING");
        ImGui.TextUnformatted(
            selection.CurrentUnitPrice > 0
                ? $"{selection.CurrentUnitPrice:N0} gil"
                : "Unset");
        ImGui.Spacing();

        ImGui.TextColored(MainWindow.ColMuted, "QUOTE STATUS");
        ImGui.PushTextWrapPos();
        ImGui.TextWrapped(selection.QuoteStatus);
        ImGui.PopTextWrapPos();

        if (selection.Warnings.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(
                MainWindow.ColWarning,
                $"QUOTE WARNING{(selection.Warnings.Count == 1 ? string.Empty : "S")}");
            foreach (var warning in selection.Warnings)
            {
                ImGui.PushTextWrapPos();
                ImGui.TextWrapped(warning);
                ImGui.PopTextWrapPos();
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        DrawSelectedLineActions(context, selection, sameLine: false);
    }

    private void DrawSelectedLineActions(
        MarketAcquisitionRequestBuilderContext context,
        MarketAcquisitionSelectedLinePresentation selection,
        bool sameLine)
    {
        _ = context;
        foreach (var action in selection.Actions)
        {
            if (sameLine)
                ImGui.SameLine();

            var clicked = action.IsPrimary
                ? ImGuiUi.PrimaryButton(action.Label, action.Enabled)
                : ImGuiUi.Button(action.Label, action.Enabled);
            if (clicked)
                InvokeSelectedLineAction(selection, action.Kind);

            RegisterLastControl(
                BuildSelectedLineControlId(selection, action.Kind),
                BuildSelectedLineControlLabel(selection, action),
                action.Enabled,
                action.Kind == MarketAcquisitionSelectedLineActionKind.UseQuote &&
                selection.QuoteUnitPrice == selection.CurrentUnitPrice,
                action.Value,
                () => InvokeSelectedLineAction(selection, action.Kind));
        }
    }

    private void InvokeSelectedLineAction(
        MarketAcquisitionSelectedLinePresentation selection,
        MarketAcquisitionSelectedLineActionKind kind)
    {
        switch (kind)
        {
            case MarketAcquisitionSelectedLineActionKind.RefreshQuote:
                if (selection.LineIndex >= 0 && selection.LineIndex < document.Lines.Count)
                    _ = FetchCraftQuotesAsync([WorkbenchLineKey.For(document.Lines)[selection.LineIndex]]);
                break;
            case MarketAcquisitionSelectedLineActionKind.UseQuote:
                if (selection.QuoteUnitPrice is > 0)
                {
                    SetLineMaxUnitPrice(
                        selection.LineIndex,
                        selection.QuoteUnitPrice.Value,
                        "Unit ceiling set from Craft Architect quote.");
                }
                break;
            case MarketAcquisitionSelectedLineActionKind.OpenPlan:
                if (!string.IsNullOrWhiteSpace(selection.PlanUrl))
                    OpenCraftArchitectPlan(selection.PlanUrl);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown selected-line action.");
        }
    }

    private static string BuildSelectedLineControlId(
        MarketAcquisitionSelectedLinePresentation selection,
        MarketAcquisitionSelectedLineActionKind kind) =>
        kind switch
        {
            MarketAcquisitionSelectedLineActionKind.RefreshQuote =>
                $"acquisition.workbench.line.{selection.ItemId}.quote.refresh",
            MarketAcquisitionSelectedLineActionKind.UseQuote =>
                $"acquisition.workbench.line.{selection.ItemId}.quote.apply",
            MarketAcquisitionSelectedLineActionKind.OpenPlan =>
                $"acquisition.workbench.line.{selection.ItemId}.quote.open-plan",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown selected-line action."),
        };

    private static string BuildSelectedLineControlLabel(
        MarketAcquisitionSelectedLinePresentation selection,
        MarketAcquisitionSelectedLineActionPresentation action) =>
        action.Kind switch
        {
            MarketAcquisitionSelectedLineActionKind.RefreshQuote =>
                $"{action.Label} for {selection.ItemName}",
            MarketAcquisitionSelectedLineActionKind.UseQuote =>
                $"Use Craft Architect quote for {selection.ItemName}",
            MarketAcquisitionSelectedLineActionKind.OpenPlan =>
                $"Open quoted Craft Architect plan for {selection.ItemName}",
            _ => action.Label,
        };

    private static void DrawWarningsTooltip(IReadOnlyList<string> warnings)
    {
        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 34f);
        foreach (var warning in warnings)
            ImGui.TextWrapped(warning);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private void OpenCraftArchitectPlan(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            craftAppraisal.State.CraftQuoteStatus = "Opened the quoted Craft Architect plan.";
        }
        catch (Exception ex)
        {
            craftAppraisal.State.CraftQuoteStatus = $"Could not open the Craft Architect plan: {ex.Message}";
        }
    }

    private void DrawCompactAddRow(MarketAcquisitionRequestBuilderContext context, bool showHomes)
    {
        var canEdit = CanEdit(context);
        ImGui.PushID("AcquisitionWorkbenchAddRow");
        ImGui.TableNextRow();

        if (!canEdit)
            ImGui.BeginDisabled();

        ImGui.TableNextColumn();
        DalamudItemAutocompleteRenderer.DrawInline(
            "AcquisitionWorkbenchAdd",
            itemOptions,
            itemAutocomplete,
            MainWindow.ColMuted,
            MainWindow.ColSuccess,
            MainWindow.ColError);

        ImGui.TableNextColumn();
        DrawInlineCombo("##NewRule", ["AllBelowThreshold", "TargetQuantity"], ref quantityMode);

        ImGui.TableNextColumn();
        if (quantityMode == "TargetQuantity")
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##NewQuantity", "Required", ref targetQuantityBuffer, 32);
        }
        else
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(MainWindow.ColMuted, "-");
        }

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1);
        if (quantityMode == "TargetQuantity")
            ImGui.InputTextWithHint("##NewOverage", "0", ref maximumOverageBuffer, 32);
        else
            ImGui.TextColored(MainWindow.ColMuted, "-");

        ImGui.TableNextColumn();
        ImGui.TextColored(MainWindow.ColMuted, "-");
        ImGui.TableNextColumn();
        ImGui.TextColored(MainWindow.ColMuted, "-");
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##NewUnit", "Unset", ref maxUnitPriceBuffer, 32);

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##NewSpend", "Unset", ref gilCapBuffer, 10, ImGuiInputTextFlags.CharsDecimal);

        ImGui.TableNextColumn();
        DrawInlineCombo("##NewQuality", ["Either", "HQOnly", "NQOnly"], ref hqPolicy);

        ImGui.TableNextColumn();
        ImGui.TextColored(MainWindow.ColMuted, "After add");

        if (showHomes)
        {
            ImGui.TableNextColumn();
            ImGui.TextColored(MainWindow.ColMuted, "After add");
        }

        ImGui.TableNextColumn();
        var canAdd = canEdit && (quantityMode != "TargetQuantity" || string.IsNullOrWhiteSpace(maximumOverageBuffer) || uint.TryParse(maximumOverageBuffer.Trim(), out _)) && RequestLineInputValidator.CanAddIntentLine(
            itemAutocomplete.SelectedItem,
            quantityMode,
            targetQuantityBuffer,
            string.Empty,
            maxUnitPriceBuffer,
            gilCapBuffer);
        if (ImGuiUi.PrimaryButton("Add", canAdd))
        {
            AddEditorLine();
            ClearLineEditor();
        }
        RegisterLastControl(
            "acquisition.workbench.add",
            "Add the inline item to the Workbench",
            canAdd,
            false,
            itemAutocomplete.SelectedItem?.ItemId.ToString(),
            () =>
            {
                AddEditorLine();
                ClearLineEditor();
            });

        if (!canEdit)
            ImGui.EndDisabled();
        ImGui.PopID();
    }

    private bool CanEdit(MarketAcquisitionRequestBuilderContext context) =>
        !context.IsBusy &&
        !context.IsRouteActive &&
        !IsSynchronizing;

    private static void DrawInlineCombo(string id, IReadOnlyList<string> values, ref string current)
    {
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo(id, FormatEditorOption(current)))
            return;

        foreach (var value in values)
        {
            var selected = string.Equals(value, current, StringComparison.Ordinal);
            if (ImGui.Selectable(FormatEditorOption(value), selected))
                current = value;
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private bool IsPlanStale(MarketAcquisitionRequestBuilderContext context) =>
        context.CurrentPlan is not null &&
        !string.IsNullOrWhiteSpace(context.CurrentPlanHash) &&
        !string.Equals(context.CurrentPlanHash, CurrentIntentHash, StringComparison.Ordinal);

    private void DrawRouteScope(MarketAcquisitionRequestBuilderContext context)
    {
        var scope = RequestRouteScope.FromDocument(document);
        RequestRouteScopeSelector.DrawCompact(
            "AcquisitionRequestBuilder",
            scope,
            controller.UpdateRouteScope,
            MainWindow.ColMuted,
            MainWindow.ColError);
    }

    private static string FormatEditorOption(string value) =>
        value switch
        {
            "AllBelowThreshold" => "Buy below ceiling",
            "TargetQuantity" => "Stock up to",
            "Either" => "Any",
            "HQOnly" => "HQ only",
            "NQOnly" => "NQ only",
            _ => value,
        };

    private void SetLineMaxUnitPrice(int index, uint maxUnitPrice, string message)
    {
        controller.SetLineMaxUnitPrice(index, maxUnitPrice, message);
    }

    private void ApplyLineEdit(
        int index,
        MarketAcquisitionRequestLineDocument line,
        string? quantityMode = null,
        uint? targetQuantity = null,
        uint? maximumOverage = null,
        uint? maxQuantity = null,
        string? hqPolicy = null,
        uint? maxUnitPrice = null,
        uint? gilCap = null,
        string message = "Line updated.")
    {
        controller.ApplyLineEdit(
            index,
            quantityMode ?? line.QuantityMode,
            targetQuantity ?? line.TargetQuantity,
            maximumOverage ?? line.MaximumOverage,
            maxQuantity ?? line.MaxQuantity,
            hqPolicy ?? line.HqPolicy,
            maxUnitPrice ?? line.MaxUnitPrice,
            gilCap ?? line.GilCap,
            message);
    }

    /// <summary>
    /// Fetches a Craft Architect quote for each of these lines in turn; quotes are evidence, never applied.
    /// Requests are built from the document as it is now; quote state and status are updated on the framework
    /// thread, where the Workbench reads them.
    /// </summary>
    private async Task FetchCraftQuotesAsync(IReadOnlyList<WorkbenchLineKey> lineKeys)
    {
        if (isAppraising || lineKeys.Count == 0)
            return;

        var snapshot = document;
        var keys = WorkbenchLineKey.For(snapshot.Lines);
        var requests = lineKeys
            .Select(key => keys.ToList().IndexOf(key))
            .Where(index => index >= 0)
            .Select(index => snapshot.Lines[index])
            .Select(line => (
                Identity: CraftAppraisalRequestMapper.BuildLineIdentity(snapshot, line),
                Request: CraftAppraisalRequestMapper.Build(snapshot, line)))
            .ToList();
        if (requests.Count == 0)
            return;

        isAppraising = true;
        try
        {
            var ready = 0;
            string? readyName = null;
            foreach (var (identity, request) in requests)
            {
                await Plugin.Framework.RunOnTick(() => craftAppraisal.State.UpdateSelectedLine(identity)).ConfigureAwait(false);
                var quote = await craftAppraisal.FetchQuoteAsync(request).ConfigureAwait(false);
                await Plugin.Framework.RunOnTick(() =>
                {
                    craftAppraisal.State.RecordLineQuote(
                        identity,
                        quote,
                        craftAppraisal.State.LastCraftQuoteDiagnosticFilePath);
                    if (craftAppraisal.State.TryGetLineQuoteThreshold(identity) is > 0 &&
                        CraftAppraisalRequestMapper.FindMatchingLineIndex(document, identity) is var currentIndex and >= 0)
                    {
                        ready++;
                        readyName = FormatLineItemName(document.Lines[currentIndex]);
                    }
                }).ConfigureAwait(false);
            }

            var count = requests.Count;
            await Plugin.Framework.RunOnTick(() => controller.SetStatus(count == 1
                ? ready == 1
                    ? $"Craft Architect quote ready for {readyName}. Review the evidence, then use the quote to change the unit ceiling."
                    : "Craft Architect did not return a usable unit cost ceiling for this line."
                : $"Craft Architect quotes ready for {ready:N0} of {count:N0} lines.")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ = Plugin.Framework.RunOnTick(() => controller.SetStatus($"Craft Architect quote failed: {ex.Message}"));
        }
        finally
        {
            isAppraising = false;
        }
    }

    private void AddEditorLine()
    {
        if (itemAutocomplete.SelectedItem is not { } item)
            return;

        var line = new MarketAcquisitionRequestLineDocument
        {
            ItemId = item.ItemId,
            ItemName = item.Name,
            QuantityMode = quantityMode,
            TargetQuantity = quantityMode == "TargetQuantity" ? ParseUInt(targetQuantityBuffer) : 0,
            MaximumOverage = quantityMode == "TargetQuantity" ? ParseUInt(maximumOverageBuffer) : 0,
            MaxQuantity = 0,
            HqPolicy = hqPolicy,
            MaxUnitPrice = ParseUInt(maxUnitPriceBuffer),
            GilCap = ParseUInt(gilCapBuffer),
        };
        controller.AddEditorLine(line);
    }

    private void ClearDraft(MarketAcquisitionRequestBuilderContext context)
    {
        controller.ClearDraft(
            context.HasCharacterScope ? context.CharacterName : string.Empty,
            context.HasCharacterScope ? context.World : string.Empty);
        ClearLineEditor();
    }

    private void EnsureCharacterScope(MarketAcquisitionRequestBuilderContext context)
    {
        if (!context.HasCharacterScope)
            return;

        if (string.Equals(document.TargetCharacterName, context.CharacterName, StringComparison.Ordinal) &&
            string.Equals(document.TargetWorld, context.World, StringComparison.Ordinal))
        {
            return;
        }

        controller.EnsureCharacterScope(context.CharacterName, context.World);
    }

    private void ClearLineEditor()
    {
        ResetLineEditor();
        controller.ClearSelection();
        selectedLineInspectorRequested = false;
    }

    private void ResetLineEditor()
    {
        itemAutocomplete.SelectedItem = null;
        itemAutocomplete.SearchBuffer = string.Empty;
        quantityMode = "AllBelowThreshold";
        targetQuantityBuffer = string.Empty;
        maximumOverageBuffer = string.Empty;
        maxUnitPriceBuffer = string.Empty;
        gilCapBuffer = string.Empty;
        hqPolicy = "Either";
    }

    private static uint ParseUInt(string value) =>
        uint.TryParse(value?.Trim(), out var parsed) ? parsed : 0;

    private static uint ClampToUInt(long value) =>
        (uint)Math.Clamp(value, 0L, uint.MaxValue);

    private static string FormatLineItemName(MarketAcquisitionRequestLineDocument line) =>
        string.IsNullOrWhiteSpace(line.ItemName)
            ? $"Item {line.ItemId}"
            : line.ItemName;

    private void RegisterLastControl(
        string id,
        string label,
        bool enabled,
        bool selected,
        string? value,
        Action invoke) =>
        reviewRegistry.RegisterLastItem(
            id,
            label,
            AgentBridgeUiControlKind.Button,
            enabled,
            selected,
            value,
            invoke);

    private void RegisterLastToggle(
        string id,
        string label,
        bool enabled,
        bool selected,
        string? value,
        Action invoke) =>
        reviewRegistry.RegisterLastItem(
            id,
            label,
            AgentBridgeUiControlKind.Toggle,
            enabled,
            selected,
            value,
            invoke);

    /// <summary>Registers the last item for agents that pass a string <c>value</c> argument.</summary>
    private void RegisterLastArgumentControl(
        string id,
        string label,
        AgentBridgeUiControlKind kind,
        bool enabled,
        string? value,
        Func<string, AgentBridgeUiActionResult> apply) =>
        reviewRegistry.Register(
            id,
            label,
            kind,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            enabled,
            false,
            value,
            ValueSchema,
            arguments => arguments is { ValueKind: JsonValueKind.Object } argument &&
                         argument.TryGetProperty("value", out var text) &&
                         text.ValueKind == JsonValueKind.String
                ? apply(text.GetString() ?? string.Empty)
                : AgentBridgeUiActionResult.Fail("A string 'value' argument is required."));

}
