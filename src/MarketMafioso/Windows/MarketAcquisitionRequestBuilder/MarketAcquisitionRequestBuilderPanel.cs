using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Items;
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

    private string quantityMode = "AllBelowThreshold";
    private string targetQuantityBuffer = string.Empty;
    private string maxUnitPriceBuffer = string.Empty;
    private string gilCapBuffer = string.Empty;
    private string hqPolicy = "Either";
    private bool isAppraising;
    private bool selectedLineInspectorRequested;

    private MarketAcquisitionRequestDocument document => controller.Document;
    private string status => controller.Status;

    public MarketAcquisitionRequestBuilderPanel(
        Configuration config,
        IDataManager dataManager,
        CraftAppraisalRequestBuilderController craftAppraisal,
        Func<MarketAcquisitionRequestDocument, Task<MarketAcquisitionRequestBuilderSyncOutcome>> syncRequest,
        Func<MarketAcquisitionRequestDocument, Task<MarketAcquisitionRequestBuilderRefreshOutcome>> refreshRequest,
        Action<MarketAcquisitionRequestDocument, MarketAcquisitionRequestView?> documentAdopted,
        AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
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

    public void MarkPlanPrepared(string planHash) => controller.MarkPlanPrepared(planHash);

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
            context.HasCharacterScope && !context.IsBusy && !context.IsRouteActive);

        DrawExactAcquisitionAuthority(context);
        DrawRouteScope(context);
        ImGui.Spacing();
        DrawExceptionalStatus(context);
        ImGuiUi.SectionHeader("Buy list", MainWindow.ColHeader);
        DrawSelectedLineWorkspace(context, reservedFooterHeight);
    }

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
        if (ImGui.InputInt("Price flexibility %##ExternalExactAcquisitionFlex", ref flex, 1, 5))
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
            ImGui.TextColored(MainWindow.ColError, status);
            ImGui.Spacing();
            return;
        }

        if (IsPlanStale(context))
        {
            ImGui.TextColored(MainWindow.ColWarning, "The finalized plan is stale because the buy list changed.");
            ImGui.Spacing();
        }
    }

    private void DrawSelectedLineWorkspace(
        MarketAcquisitionRequestBuilderContext context,
        float reservedFooterHeight)
    {
        var selection = BuildSelectedLinePresentation(context);
        var surface = MarketAcquisitionSelectedLinePresenter.ResolveSurface(
            selectedLineInspectorRequested,
            selection);
        if (surface == MarketAcquisitionSelectedLineSurface.CommandBar)
            DrawSelectedLineCommandBar(context, selection);

        var tableHeight = Math.Max(150f, ImGui.GetContentRegionAvail().Y - Math.Max(0, reservedFooterHeight));
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
        if (!ImGui.BeginTable("AcquisitionWorkbenchLinesV3", 8, flags, new Vector2(0, tableHeight)))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 2.4f);
        ImGui.TableSetupColumn("Buying rule", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn("Quantity", ImGuiTableColumnFlags.WidthStretch, 0.9f);
        ImGui.TableSetupColumn("Unit ceiling", ImGuiTableColumnFlags.WidthStretch, 1.1f);
        ImGui.TableSetupColumn("Spend ceiling", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn("Evidence", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn(string.Empty, ImGuiTableColumnFlags.WidthFixed, 72f);
        ImGui.TableHeadersRow();

        for (var index = 0; index < document.Lines.Count; index++)
        {
            var line = document.Lines[index];
            DrawCompactLineRow(context, line, index);
        }

        DrawCompactAddRow(context);
        ImGui.EndTable();
    }

    private void DrawCompactLineRow(
        MarketAcquisitionRequestBuilderContext context,
        MarketAcquisitionRequestLineDocument line,
        int index)
    {
        var canEdit = !context.IsBusy && !context.IsRouteActive && !IsSynchronizing;
        var isExactAcquisitionLine = controller.IsExactAcquisitionLine(index);
        ImGui.PushID($"AcquisitionWorkbenchLine{line.ItemId}_{index}");
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        var isSelected = controller.SelectedLineIndex == index;
        var selectionCursor = ImGui.GetCursorPos();
        var selection = DalamudTableSelectionRenderer.DrawRow(
            "##SelectLine",
            isSelected,
            new Vector2(0f, ImGui.GetTextLineHeightWithSpacing()));
        if (selection.Activated)
        {
            controller.SelectLine(index);
            selectedLineInspectorRequested = false;
        }
        RegisterLastControl(
            $"acquisition.workbench.line.{line.ItemId}.select",
            $"Select {FormatLineItemName(line)} in the Workbench",
            enabled: true,
            selected: isSelected,
            value: line.ItemId.ToString(),
            () =>
            {
                controller.SelectLine(index);
                selectedLineInspectorRequested = false;
            });
        ImGui.SetCursorPos(selectionCursor);
        ImGui.TextUnformatted(FormatLineItemName(line));

        if (!canEdit || isExactAcquisitionLine)
            ImGui.BeginDisabled();

        ImGui.TableNextColumn();
        DrawCompactModeCell(line, index);

        ImGui.TableNextColumn();
        DrawCompactQuantityCell(line, index);

        ImGui.TableNextColumn();
        DrawCompactUnitCell(line, index);
        DrawUnitPriceContextMenu(line, index, canEdit);

        ImGui.TableNextColumn();
        DrawCompactSpendCell(line, index);

        ImGui.TableNextColumn();
        DrawCompactHqCell(line, index);

        ImGui.TableNextColumn();
        DrawCompactEvidenceState(line);

        if (!canEdit || isExactAcquisitionLine)
            ImGui.EndDisabled();

        ImGui.PopID();
    }

    private void DrawCompactModeCell(MarketAcquisitionRequestLineDocument line, int index)
    {
        var current = string.IsNullOrWhiteSpace(line.QuantityMode) ? "AllBelowThreshold" : line.QuantityMode;
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##Mode", FormatEditorOption(current)))
            return;

        foreach (var mode in new[] { "AllBelowThreshold", "TargetQuantity" })
        {
            var selected = string.Equals(mode, current, StringComparison.Ordinal);
            if (ImGui.Selectable(FormatEditorOption(mode), selected))
            {
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

    private void DrawCompactQuantityCell(MarketAcquisitionRequestLineDocument line, int index)
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

        var quantity = ClampToInt(line.TargetQuantity);
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.InputInt("##Quantity", ref quantity))
            return;

        ApplyLineEdit(
            index,
            line,
            targetQuantity: Math.Max(1u, ClampToUInt(quantity)),
            maxQuantity: 0,
            message: "Target quantity updated.");
    }

    private void DrawCompactUnitCell(MarketAcquisitionRequestLineDocument line, int index)
    {
        var maxUnit = ClampToInt(line.MaxUnitPrice);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputInt("##Unit", ref maxUnit))
            ApplyLineEdit(index, line, maxUnitPrice: ClampToUInt(maxUnit), message: "Unit ceiling updated.");
    }

    private void DrawUnitPriceContextMenu(
        MarketAcquisitionRequestLineDocument line,
        int index,
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
            _ = FetchCraftQuoteEvidenceAsync(index);
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

    private void DrawCompactSpendCell(MarketAcquisitionRequestLineDocument line, int index)
    {
        var gilCap = ClampToInt(line.GilCap);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputInt("##Spend", ref gilCap))
            ApplyLineEdit(index, line, gilCap: ClampToUInt(gilCap), message: "Spend ceiling updated.");
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
        var index = controller.SelectedLineIndex;
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

    private void DrawSelectedLineCommandBar(
        MarketAcquisitionRequestBuilderContext context,
        MarketAcquisitionSelectedLinePresentation? selection)
    {
        var height = ImGui.GetFrameHeightWithSpacing() + 10f;
        if (!ImGui.BeginChild("AcquisitionWorkbenchSelectedLineCommandBar", new Vector2(0, height), true))
        {
            ImGui.EndChild();
            return;
        }

        ImGui.AlignTextToFramePadding();
        if (selection is null)
        {
            ImGui.TextColored(MainWindow.ColMuted, "Select a buy-list line to work with it.");
            DrawDisabledSelectionActions();
        }
        else
        {
            ImGui.TextColored(MainWindow.ColHeader, selection.ItemName);
            ImGui.SameLine();
            ImGui.TextColored(
                selection.QuoteUnitPrice is > 0 ? MainWindow.ColSuccess : MainWindow.ColMuted,
                selection.QuoteUnitPrice is > 0
                    ? $"{selection.QuoteUnitPrice.Value:N0} gil"
                    : selection.CurrentUnitPrice > 0
                        ? $"{selection.CurrentUnitPrice:N0} gil manual"
                        : "No quote");
            if (selection.Warnings.Count > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(
                    MainWindow.ColWarning,
                    $"{selection.Warnings.Count:N0} warning{(selection.Warnings.Count == 1 ? string.Empty : "s")}");
                DrawWarningsTooltip(selection.Warnings);
            }

            var actionWidth = CalculateSelectionActionWidth(selection, includeDetails: true);
            ImGuiUi.SameLineRight(actionWidth);
            if (ImGuiUi.Button("Details", enabled: true))
                selectedLineInspectorRequested = true;
            RegisterLastControl(
                $"acquisition.workbench.line.{selection.ItemId}.details",
                $"Show pricing evidence for {selection.ItemName}",
                enabled: true,
                selected: false,
                value: selection.ItemId.ToString(),
                () => selectedLineInspectorRequested = true);
            DrawSelectedLineActions(context, selection, sameLine: true);
        }

        ImGui.EndChild();
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
                _ = FetchCraftQuoteEvidenceAsync(selection.LineIndex);
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
            case MarketAcquisitionSelectedLineActionKind.RemoveLine:
                RemoveLine(selection.LineIndex);
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
            MarketAcquisitionSelectedLineActionKind.RemoveLine =>
                $"acquisition.workbench.line.{selection.ItemId}.remove",
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
            MarketAcquisitionSelectedLineActionKind.RemoveLine =>
                $"Remove {selection.ItemName} from the Workbench",
            _ => action.Label,
        };

    private static float CalculateSelectionActionWidth(
        MarketAcquisitionSelectedLinePresentation selection,
        bool includeDetails)
    {
        var style = ImGui.GetStyle();
        var labels = selection.Actions.Select(action => action.Label).ToList();
        if (includeDetails)
            labels.Insert(0, "Details");
        return labels.Sum(label => ImGui.CalcTextSize(label).X + (style.FramePadding.X * 2f)) +
               (Math.Max(0, labels.Count - 1) * style.ItemSpacing.X);
    }

    private static void DrawDisabledSelectionActions()
    {
        var labels = new[] { "Details", "Get quote", "Use quote", "Open plan", "Remove line" };
        var style = ImGui.GetStyle();
        var width = labels.Sum(label => ImGui.CalcTextSize(label).X + (style.FramePadding.X * 2f)) +
                    ((labels.Length - 1) * style.ItemSpacing.X);
        ImGuiUi.SameLineRight(width);
        foreach (var label in labels)
        {
            if (!string.Equals(label, labels[0], StringComparison.Ordinal))
                ImGui.SameLine();
            ImGuiUi.Button(label, enabled: false);
        }
    }

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

    private void DrawCompactAddRow(MarketAcquisitionRequestBuilderContext context)
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
        ImGui.InputTextWithHint("##NewUnit", "Unset", ref maxUnitPriceBuffer, 32);

        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##NewSpend", "Optional", ref gilCapBuffer, 32);

        ImGui.TableNextColumn();
        DrawInlineCombo("##NewQuality", ["Either", "HQOnly", "NQOnly"], ref hqPolicy);

        ImGui.TableNextColumn();
        ImGui.TextColored(MainWindow.ColMuted, "After add");

        ImGui.TableNextColumn();
        var canAdd = canEdit && RequestLineInputValidator.CanAddIntentLine(
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
            "TargetQuantity" => "Target quantity",
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
            maxQuantity ?? line.MaxQuantity,
            hqPolicy ?? line.HqPolicy,
            maxUnitPrice ?? line.MaxUnitPrice,
            gilCap ?? line.GilCap,
            message);
    }

    private async Task FetchCraftQuoteEvidenceAsync(int index)
    {
        if (isAppraising || index < 0 || index >= document.Lines.Count)
            return;

        isAppraising = true;
        try
        {
            var line = document.Lines[index];
            var identity = CraftAppraisalRequestMapper.BuildLineIdentity(document, line);
            craftAppraisal.State.UpdateSelectedLine(identity);
            var quote = await craftAppraisal.FetchQuoteAsync(
                CraftAppraisalRequestMapper.Build(document, line)).ConfigureAwait(false);
            craftAppraisal.State.RecordLineQuote(
                identity,
                quote,
                craftAppraisal.State.LastCraftQuoteDiagnosticFilePath);
            var threshold = craftAppraisal.State.TryGetLineQuoteThreshold(identity);
            if (threshold is > 0)
            {
                var currentIndex = CraftAppraisalRequestMapper.FindMatchingLineIndex(document, identity);
                if (currentIndex < 0)
                {
                    controller.SetStatus("Craft Architect quote was kept as evidence but not applied because the Workbench line changed.");
                    return;
                }

                controller.SetStatus(
                    $"Craft Architect quote ready for {FormatLineItemName(document.Lines[currentIndex])}. Review the evidence, then use the quote to change the unit ceiling.");
                return;
            }

            controller.SetStatus("Craft Architect did not return a usable unit cost ceiling for this line.");
        }
        catch (Exception ex)
        {
            controller.SetStatus($"Craft Architect quote failed: {ex.Message}");
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
            MaxQuantity = 0,
            HqPolicy = hqPolicy,
            MaxUnitPrice = ParseUInt(maxUnitPriceBuffer),
            GilCap = ParseUInt(gilCapBuffer),
        };
        controller.AddEditorLine(line);
    }

    private void RemoveLine(int index)
    {
        if (controller.RemoveLine(index))
        {
            ResetLineEditor();
            if (controller.SelectedLineIndex < 0)
                selectedLineInspectorRequested = false;
        }
    }

    private void RemoveLineByItemId(uint itemId)
    {
        var index = document.Lines.FindIndex(line => line.ItemId == itemId);
        if (index >= 0)
            RemoveLine(index);
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
        maxUnitPriceBuffer = string.Empty;
        gilCapBuffer = string.Empty;
        hqPolicy = "Either";
    }

    private static uint ParseUInt(string value) =>
        uint.TryParse(value?.Trim(), out var parsed) ? parsed : 0;

    private static int ClampToInt(uint value) =>
        value > int.MaxValue ? int.MaxValue : (int)value;

    private static uint ClampToUInt(int value) =>
        value <= 0 ? 0u : (uint)value;

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

}
