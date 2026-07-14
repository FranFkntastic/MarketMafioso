using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using MarketMafioso.CraftArchitectCompanion;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Windows;
using MarketMafioso.Windows.ItemAutocomplete;

namespace MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

public sealed class MarketAcquisitionRequestBuilderPanel
{
    private readonly IReadOnlyList<AcquisitionItemOption> itemOptions;
    private readonly CraftAppraisalRequestBuilderController craftAppraisal;
    private readonly MarketAcquisitionRequestBuilderController controller;
    private readonly ItemAutocompleteState itemAutocomplete = new();

    private string quantityMode = "AllBelowThreshold";
    private string targetQuantityBuffer = string.Empty;
    private string maxQuantityBuffer = string.Empty;
    private string maxUnitPriceBuffer = string.Empty;
    private string gilCapBuffer = string.Empty;
    private string hqPolicy = "Either";
    private bool isAppraising;

    private MarketAcquisitionRequestDocument document => controller.Document;
    private int selectedLineIndex => controller.SelectedLineIndex;
    private string status => controller.Status;

    public MarketAcquisitionRequestBuilderPanel(
        Configuration config,
        IDataManager dataManager,
        CraftAppraisalRequestBuilderController craftAppraisal,
        Func<MarketAcquisitionRequestDocument, Task<MarketAcquisitionRequestBuilderSyncOutcome>> syncRequest,
        Func<MarketAcquisitionRequestDocument, Task<MarketAcquisitionRequestBuilderRefreshOutcome>> refreshRequest,
        Action<MarketAcquisitionRequestDocument, MarketAcquisitionRequestView?> documentAdopted)
    {
        this.craftAppraisal = craftAppraisal;
        controller = new MarketAcquisitionRequestBuilderController(
            config,
            syncRequest,
            refreshRequest,
            documentAdopted);
        itemOptions = ItemAutocompleteControl.LoadItemOptions(dataManager);
    }

    public MarketAcquisitionRequestDocument CurrentDocument => document;

    public string CurrentIntentHash => controller.CurrentIntentHash;

    public int LineCount => document.Lines.Count;

    public void MarkPlanPrepared(string planHash) => controller.MarkPlanPrepared(planHash);

    public void AdoptRequest(MarketAcquisitionRequestView request) => controller.AdoptRequest(request);

    public bool AdoptRestoredRequestIfSafe(MarketAcquisitionRequestView request) =>
        controller.AdoptRestoredRequestIfSafe(request);

    public int StageLines(IEnumerable<MarketAcquisitionRequestLineDocument> lines) =>
        controller.AddLines(lines);

    public void Draw(MarketAcquisitionRequestBuilderContext context, bool showLifecycleSummary = true)
    {
        EnsureCharacterScope(context);
        controller.PumpAutomaticSynchronization(
            context.CharacterName,
            context.World,
            context.HasCharacterScope && !context.IsBusy && !context.IsRouteActive);

        ImGuiUi.SectionHeader("Local Request", MainWindow.ColHeader);
        if (showLifecycleSummary)
        {
            DrawStatusSummary(context);
            ImGui.Spacing();
        }
        else
        {
            ImGui.TextColored(GetSyncStatusColor(), FormatBuilderStatus(context));
        }
        DrawRouteScope(context);
        ImGui.Spacing();
        DrawLineEditor(context);
        ImGui.Spacing();
        DrawLineTable();
        ImGui.Spacing();
        DrawSelectedLineInspector(context);
        ImGui.Spacing();
        DrawActions(context);
    }

    private void DrawStatusSummary(MarketAcquisitionRequestBuilderContext context)
    {
        if (ImGui.BeginTable("AcquisitionRequestBuilderStatusStrip", 4, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Request", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Lines", ImGuiTableColumnFlags.WidthStretch, 0.75f);
            ImGui.TableSetupColumn("Plan", ImGuiTableColumnFlags.WidthStretch, 0.75f);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextColored(MainWindow.ColMuted, "Target");
            if (context.HasCharacterScope)
                ImGui.TextColored(MainWindow.ColSuccess, $"{context.CharacterName} @ {context.World}");
            else if (context.CharacterScopeTemporarilyUnavailable)
                ImGui.TextColored(MainWindow.ColMuted, "Temporarily unavailable");
            else
                ImGui.TextColored(MainWindow.ColError, "No character scope");

            ImGui.TableNextColumn();
            ImGui.TextColored(MainWindow.ColMuted, "Request");
            ImGui.TextColored(GetSyncStatusColor(), FormatRequestStatus());

            ImGui.TableNextColumn();
            ImGui.TextColored(MainWindow.ColMuted, "Lines");
            ImGui.TextColored(document.Lines.Count == 0 ? MainWindow.ColError : MainWindow.ColSuccess, $"{document.Lines.Count:N0} local");

            ImGui.TableNextColumn();
            ImGui.TextColored(MainWindow.ColMuted, "Plan");
            if (IsPlanStale(context))
                ImGui.TextColored(MainWindow.ColError, "Stale");
            else if (context.CurrentPlan is not null)
                ImGui.TextColored(MainWindow.ColSuccess, "Current");
            else
                ImGui.TextColored(MainWindow.ColMuted, "Not prepared");

            ImGui.EndTable();
        }

        ImGui.TextColored(GetSyncStatusColor(), FormatBuilderStatus(context));
        if (context.IsRouteActive)
            ImGui.TextColored(MainWindow.ColMuted, "Synchronization is paused while a guided route is active.");
    }

    private string FormatRequestStatus()
    {
        if (string.IsNullOrWhiteSpace(document.RemoteRequestId))
            return FormatSyncStatus(document.SyncStatus);

        return $"{FormatSyncStatus(document.SyncStatus)} r{document.RemoteRevision}";
    }

    private string FormatBuilderStatus(MarketAcquisitionRequestBuilderContext context)
    {
        var remote = string.IsNullOrWhiteSpace(document.RemoteRequestId)
            ? "not yet published"
            : $"server r{document.RemoteRevision}";
        var plan = IsPlanStale(context)
            ? ", plan stale"
            : context.CurrentPlan is null ? string.Empty : ", plan current";
        return $"{FormatSyncStatus(document.SyncStatus)}: {document.Lines.Count:N0} line(s), {remote}{plan}. {status}";
    }

    private static string FormatSyncStatus(string syncStatus) =>
        syncStatus switch
        {
            "NewDraft" => "Sync pending",
            "LocalEdits" => "Sync pending",
            "SyncedClean" => "Synced",
            "RemoteChanged" => "Synchronizing",
            "SyncFailed" => "Retrying sync",
            _ => string.IsNullOrWhiteSpace(syncStatus) ? "Draft" : syncStatus,
        };

    private Vector4 GetSyncStatusColor() =>
        document.SyncStatus switch
        {
            "SyncedClean" => MainWindow.ColSuccess,
            "RemoteChanged" or "SyncFailed" => MainWindow.ColError,
            _ => MainWindow.ColMuted,
        };

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

    private void DrawLineEditor(MarketAcquisitionRequestBuilderContext context)
    {
        var isEditing = selectedLineIndex >= 0;
        ImGui.TextColored(MainWindow.ColHeader, isEditing ? "Edit request item" : "Add an item");
        if (!isEditing)
            ImGui.TextColored(MainWindow.ColMuted, "Choose an item and its buying limits, then add it to the request.");

        if (ImGui.BeginTable("AcquisitionRequestBuilderLineEditorPrimary", 3, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Buying rule", ImGuiTableColumnFlags.WidthFixed, 180f);
            ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ItemAutocompleteControl.Draw(
                "AcquisitionRequestBuilder",
                itemOptions,
                itemAutocomplete,
                null,
                MainWindow.ColMuted,
                MainWindow.ColSuccess,
                MainWindow.ColError);

            ImGui.TableNextColumn();
            DrawCombo("Buying rule##AcquisitionRequestBuilderMode", ["AllBelowThreshold", "TargetQuantity"], ref quantityMode);
            ImGui.TableNextColumn();
            DrawCombo("Quality##AcquisitionRequestBuilderHq", ["Either", "HQOnly", "NQOnly"], ref hqPolicy);

            ImGui.EndTable();
        }

        if (ImGui.BeginTable("AcquisitionRequestBuilderLineEditorLimits", 3, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Quantity", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Unit Cost Ceiling", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Total Spend Ceiling", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            if (quantityMode == "TargetQuantity")
                DrawInput("Target Qty", "##AcquisitionRequestBuilderTargetQty", ref targetQuantityBuffer);
            else
                DrawInput("Max Qty", "##AcquisitionRequestBuilderMaxQty", ref maxQuantityBuffer);
            ImGui.TableNextColumn();
            DrawInput("Unit Cost Ceiling", "##AcquisitionRequestBuilderMaxUnit", ref maxUnitPriceBuffer);
            ImGui.TableNextColumn();
            DrawInput("Total Spend Ceiling", "##AcquisitionRequestBuilderGilCap", ref gilCapBuffer);

            ImGui.EndTable();
        }

        var canApply = !context.IsBusy &&
                       !context.IsRouteActive &&
                       RequestLineInputValidator.CanAddIntentLine(
                           itemAutocomplete.SelectedItem,
                           quantityMode,
                           targetQuantityBuffer,
                           maxQuantityBuffer,
                           maxUnitPriceBuffer,
                           gilCapBuffer);
        var actionLabel = isEditing ? "Save item changes" : "Add item to request";
        if (ImGuiUi.PrimaryButton($"{actionLabel}##AcquisitionRequestBuilderApplyLine", canApply))
        {
            ApplyEditorLine();
            ClearLineEditor();
        }

        if (isEditing)
        {
            ImGui.SameLine();
            if (ImGuiUi.Button("Cancel editing##AcquisitionRequestBuilderCancelEdit", true))
                ClearLineEditor();
        }
    }

    private static void DrawInput(string label, string id, ref string buffer)
    {
        ImGui.TextColored(MainWindow.ColMuted, label);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText(id, ref buffer, 32);
    }

    private static void DrawCombo(string label, IReadOnlyList<string> values, ref string current)
    {
        ImGui.TextColored(MainWindow.ColMuted, label.Split('#')[0]);
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo(label, FormatEditorOption(current)))
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

    private static string FormatEditorOption(string value) =>
        value switch
        {
            "AllBelowThreshold" => "Buy below ceiling",
            "TargetQuantity" => "Buy target quantity",
            "Either" => "Any",
            "HQOnly" => "HQ only",
            "NQOnly" => "NQ only",
            _ => value,
        };

    private void DrawLineTable()
    {
        var tableHeight = (ImGui.GetTextLineHeightWithSpacing() * 6.5f) + 8f;
        if (!ImGui.BeginTable("AcquisitionRequestBuilderLines", 7, AcquisitionRequestTableStyle.LineTableFlags, new Vector2(0, tableHeight)))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Mode", ImGuiTableColumnFlags.WidthFixed, 150);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 88);
        ImGui.TableSetupColumn("Unit Ceiling", ImGuiTableColumnFlags.WidthFixed, 112);
        ImGui.TableSetupColumn("Spend Ceiling", ImGuiTableColumnFlags.WidthFixed, 112);
        ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, 88);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 88);
        ImGui.TableHeadersRow();

        if (document.Lines.Count == 0)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(MainWindow.ColMuted, "No acquisition lines queued.");
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            ImGui.EndTable();
            return;
        }

        for (var index = 0; index < document.Lines.Count; index++)
        {
            var line = document.Lines[index];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.Selectable($"{FormatLineItem(line)}##AcquisitionRequestBuilderLine{index}", selectedLineIndex == index))
            {
                SelectLineForEditing(index, line);
            }

            ImGui.TableNextColumn();
            DrawLineModeCell(line, index);
            ImGui.TableNextColumn();
            DrawLineQuantityCell(line, index);
            ImGui.TableNextColumn();
            DrawMaxUnitCell(line, index);
            ImGui.TableNextColumn();
            DrawGilCapCell(line, index);
            ImGui.TableNextColumn();
            DrawLineHqCell(line, index);
            ImGui.TableNextColumn();
            if (ImGuiUi.Button($"Remove##AcquisitionRequestBuilderRemove{index}", true))
                RemoveLine(index);
        }

        ImGui.EndTable();
    }

    private void DrawMaxUnitCell(MarketAcquisitionRequestLineDocument line, int index)
    {
        var maxUnit = ClampToInt(line.MaxUnitPrice);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputInt($"##AcquisitionRequestBuilderMaxUnitCell{index}", ref maxUnit))
            ApplyLineEdit(
                index,
                line,
                maxUnitPrice: ClampToUInt(maxUnit),
                message: "Unit cost ceiling updated.");

        if (!ImGui.BeginPopupContextItem($"AcquisitionRequestBuilderMaxUnitMenu{index}"))
            return;

        SelectLineForEditing(index, line);
        var identity = CraftAppraisalRequestMapper.BuildLineIdentity(document, line);
        var threshold = craftAppraisal.State.TryGetLineQuoteThreshold(identity);
        if (threshold is > 0)
        {
            if (ImGuiUi.MenuItem("Use Craft Architect Quote", true))
                SetLineMaxUnitPrice(index, threshold.Value, "Unit cost ceiling set from Craft Architect quote.");
        }
        else
        {
            if (ImGuiUi.MenuItem(
                    "Get Craft Architect Quote",
                    craftAppraisal.State.WorkshopHostEnabled && !isAppraising && line.ItemId != 0))
            {
                _ = CalculateMaxUnitFromCraftAsync(index);
            }

            if (!craftAppraisal.State.WorkshopHostEnabled)
                ImGui.TextColored(MainWindow.ColMuted, "Craft Architect quotes are not available.");
        }

        if (ImGuiUi.MenuItem("Enter manually", true))
        {
            SelectLineForEditing(index, line);
            controller.SetStatus("Edit unit cost ceiling directly in the row.");
        }

        if (ImGuiUi.MenuItem("Clear unit cost ceiling", line.MaxUnitPrice > 0))
            SetLineMaxUnitPrice(index, 0, "Unit cost ceiling cleared.");

        ImGui.EndPopup();
    }

    private void DrawGilCapCell(MarketAcquisitionRequestLineDocument line, int index)
    {
        var gilCap = ClampToInt(line.GilCap);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputInt($"##AcquisitionRequestBuilderGilCapCell{index}", ref gilCap))
            ApplyLineEdit(
                index,
                line,
                gilCap: ClampToUInt(gilCap),
                message: "Total spend ceiling updated.");

        if (!ImGui.BeginPopupContextItem($"AcquisitionRequestBuilderGilCapMenu{index}"))
            return;

        SelectLineForEditing(index, line);
        var capQuantity = GetCapQuantity(line);
        var canCalculateCap = line.MaxUnitPrice > 0 && capQuantity > 0;
        if (ImGuiUi.MenuItem("Set from unit ceiling x quantity", canCalculateCap))
            SetLineGilCap(
                index,
                CalculateGilCap(line.MaxUnitPrice, capQuantity),
                "Total spend ceiling set from unit ceiling and quantity.");
        if (!canCalculateCap)
            ImGui.TextColored(MainWindow.ColMuted, "Set unit ceiling and a finite quantity before calculating a spend ceiling.");

        if (ImGuiUi.MenuItem("Enter manually", true))
        {
            SelectLineForEditing(index, line);
            controller.SetStatus("Edit total spend ceiling directly in the row.");
        }

        if (ImGuiUi.MenuItem("Clear total spend ceiling", line.GilCap > 0))
            SetLineGilCap(index, 0, "Total spend ceiling cleared.");

        ImGui.EndPopup();
    }

    private void DrawLineModeCell(MarketAcquisitionRequestLineDocument line, int index)
    {
        var current = string.IsNullOrWhiteSpace(line.QuantityMode) ? "AllBelowThreshold" : line.QuantityMode;
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo($"##AcquisitionRequestBuilderModeCell{index}", MarketAcquisitionQuantityModePresenter.FormatMode(current)))
            return;

        foreach (var mode in new[] { "AllBelowThreshold", "TargetQuantity" })
        {
            var selected = string.Equals(mode, current, StringComparison.Ordinal);
            if (ImGui.Selectable(MarketAcquisitionQuantityModePresenter.FormatMode(mode), selected))
            {
                ApplyLineEdit(
                    index,
                    line,
                    quantityMode: mode,
                    targetQuantity: mode == "TargetQuantity" ? Math.Max(1u, line.TargetQuantity) : line.TargetQuantity,
                    message: "Quantity mode updated.");
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawLineQuantityCell(MarketAcquisitionRequestLineDocument line, int index)
    {
        var isTargetQuantity = line.QuantityMode.Equals("TargetQuantity", StringComparison.OrdinalIgnoreCase);
        var quantity = ClampToInt(isTargetQuantity ? line.TargetQuantity : line.MaxQuantity);
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.InputInt($"##AcquisitionRequestBuilderQuantityCell{index}", ref quantity))
            return;

        var updated = ClampToUInt(quantity);
        ApplyLineEdit(
            index,
            line,
            targetQuantity: isTargetQuantity ? Math.Max(1u, updated) : line.TargetQuantity,
            maxQuantity: isTargetQuantity ? line.MaxQuantity : updated,
            message: isTargetQuantity ? "Target quantity updated." : "Maximum quantity updated.");
    }

    private void DrawLineHqCell(MarketAcquisitionRequestLineDocument line, int index)
    {
        var current = string.IsNullOrWhiteSpace(line.HqPolicy) ? "Either" : line.HqPolicy;
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo($"##AcquisitionRequestBuilderHqCell{index}", current))
            return;

        foreach (var hq in new[] { "Either", "HQOnly", "NQOnly" })
        {
            var selected = string.Equals(hq, current, StringComparison.Ordinal);
            if (ImGui.Selectable(hq, selected))
                ApplyLineEdit(index, line, hqPolicy: hq, message: "HQ policy updated.");

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawSelectedLineInspector(MarketAcquisitionRequestBuilderContext context)
    {
        ImGui.TextColored(MainWindow.ColHeader, "Selected Line");
        var selectedLine = selectedLineIndex >= 0 && selectedLineIndex < document.Lines.Count
            ? document.Lines[selectedLineIndex]
            : null;

        if (!ImGui.BeginTable("AcquisitionRequestBuilderSelectedLine", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            return;

        ImGui.TableSetupColumn("Evidence", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.TextColored(MainWindow.ColMuted, "Craft Architect quote");
        if (selectedLine is null)
        {
            ImGui.TextColored(MainWindow.ColMuted, document.Lines.Count == 0
                ? "Add an acquisition line to begin."
                : "Select a line to inspect pricing evidence.");
            ImGui.TableNextColumn();
            ImGui.TextColored(MainWindow.ColMuted, "Request state");
            ImGui.TextColored(MainWindow.ColMuted, "No line selected.");
            ImGui.EndTable();
            return;
        }

        var identity = CraftAppraisalRequestMapper.BuildLineIdentity(document, selectedLine);
        var threshold = craftAppraisal.State.TryGetLineQuoteThreshold(identity);
        if (threshold is > 0)
            ImGui.TextColored(MainWindow.ColSuccess, $"{threshold.Value:N0} gil recommended threshold");
        else
            ImGui.TextColored(MainWindow.ColMuted, "No Craft Architect quote yet.");
        ImGui.TextColored(MainWindow.ColMuted, craftAppraisal.State.CraftQuoteStatus);

        ImGui.TableNextColumn();
        ImGui.TextColored(MainWindow.ColMuted, "Request state");
        ImGui.TextUnformatted($"{FormatLineItem(selectedLine)}");
        ImGui.TextUnformatted($"{MarketAcquisitionQuantityModePresenter.FormatMode(selectedLine.QuantityMode)} / {FormatLineQuantity(selectedLine)}");
        ImGui.TextUnformatted($"Unit ceiling: {RequestPricingFormatter.FormatOptionalGil(selectedLine.MaxUnitPrice)}");
        ImGui.TextUnformatted($"Spend ceiling: {RequestPricingFormatter.FormatOptionalGil(selectedLine.GilCap)}");
        if (IsPlanStale(context))
            ImGui.TextColored(MainWindow.ColError, "Plan is stale until the request is updated and prepared again.");
        else if (context.CurrentPlan is not null)
            ImGui.TextColored(MainWindow.ColSuccess, "Plan matches current request.");
        else
            ImGui.TextColored(MainWindow.ColMuted, "No prepared plan yet.");

        ImGui.EndTable();
    }

    private void DrawActions(MarketAcquisitionRequestBuilderContext context)
    {
        var busy = context.IsBusy || controller.IsSyncing || controller.IsRefreshing || isAppraising;
        ImGui.TextColored(MainWindow.ColHeader, "Synchronization");
        ImGui.TextColored(
            MainWindow.ColMuted,
            context.IsRouteActive
                ? "Paused until the active route finishes. Local edits remain safe."
                : "Automatic. The most recently committed request change supersedes the prior version.");

        if (ImGui.TreeNode("Start over##AcquisitionRequestBuilderRecovery"))
        {
            if (ImGuiUi.Button("Clear local request##AcquisitionRequestBuilderClear", !busy && !context.IsRouteActive))
                ClearDraft(context);
            ImGui.TreePop();
        }

        ImGui.TextColored(MainWindow.ColMuted, "Right-click unit or spend ceiling cells for Craft Architect evidence and pricing shortcuts.");
    }

    private void SelectLineForEditing(int index, MarketAcquisitionRequestLineDocument line)
    {
        if (controller.SelectLine(index))
            LoadLineIntoEditor(line);
    }

    private void SetLineMaxUnitPrice(int index, uint maxUnitPrice, string message)
    {
        if (controller.SetLineMaxUnitPrice(index, maxUnitPrice, message))
            LoadLineIntoEditor(document.Lines[index]);
    }

    private void SetLineGilCap(int index, uint gilCap, string message)
    {
        if (controller.SetLineGilCap(index, gilCap, message))
            LoadLineIntoEditor(document.Lines[index]);
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
        if (controller.ApplyLineEdit(
                index,
                quantityMode ?? line.QuantityMode,
                targetQuantity ?? line.TargetQuantity,
                maxQuantity ?? line.MaxQuantity,
                hqPolicy ?? line.HqPolicy,
                maxUnitPrice ?? line.MaxUnitPrice,
                gilCap ?? line.GilCap,
                message))
        {
            LoadLineIntoEditor(document.Lines[index]);
        }
    }

    private static uint GetCapQuantity(MarketAcquisitionRequestLineDocument line) =>
        line.QuantityMode == "TargetQuantity"
            ? line.TargetQuantity
            : line.MaxQuantity;

    private static uint CalculateGilCap(uint maxUnitPrice, uint quantity)
    {
        var cap = (ulong)maxUnitPrice * quantity;
        return cap > uint.MaxValue ? uint.MaxValue : (uint)cap;
    }

    private async Task CalculateMaxUnitFromCraftAsync(int index)
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
                SetLineMaxUnitPrice(index, threshold.Value, "Unit cost ceiling set from Craft Architect quote.");
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

    private void ApplyEditorLine()
    {
        if (itemAutocomplete.SelectedItem is not { } item)
            return;

        var line = new MarketAcquisitionRequestLineDocument
        {
            ItemId = item.ItemId,
            ItemName = item.Name,
            QuantityMode = quantityMode,
            TargetQuantity = quantityMode == "TargetQuantity" ? ParseUInt(targetQuantityBuffer) : 0,
            MaxQuantity = quantityMode == "AllBelowThreshold" ? ParseUInt(maxQuantityBuffer) : 0,
            HqPolicy = hqPolicy,
            MaxUnitPrice = ParseUInt(maxUnitPriceBuffer),
            GilCap = ParseUInt(gilCapBuffer),
        };
        controller.ApplyEditorLine(line);
    }

    private void RemoveLine(int index)
    {
        if (controller.RemoveLine(index))
            ClearLineEditor();
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

    private void LoadLineIntoEditor(MarketAcquisitionRequestLineDocument line)
    {
        itemAutocomplete.SelectedItem = new AcquisitionItemOption(line.ItemId, line.ItemName);
        itemAutocomplete.SearchBuffer = line.ItemName;
        quantityMode = string.IsNullOrWhiteSpace(line.QuantityMode) ? "AllBelowThreshold" : line.QuantityMode;
        targetQuantityBuffer = line.TargetQuantity == 0 ? string.Empty : line.TargetQuantity.ToString();
        maxQuantityBuffer = line.MaxQuantity == 0 ? string.Empty : line.MaxQuantity.ToString();
        maxUnitPriceBuffer = line.MaxUnitPrice == 0 ? string.Empty : line.MaxUnitPrice.ToString();
        gilCapBuffer = line.GilCap == 0 ? string.Empty : line.GilCap.ToString();
        hqPolicy = string.IsNullOrWhiteSpace(line.HqPolicy) ? "Either" : line.HqPolicy;
    }

    private void ClearLineEditor()
    {
        itemAutocomplete.SelectedItem = null;
        itemAutocomplete.SearchBuffer = string.Empty;
        quantityMode = "AllBelowThreshold";
        targetQuantityBuffer = string.Empty;
        maxQuantityBuffer = string.Empty;
        maxUnitPriceBuffer = string.Empty;
        gilCapBuffer = string.Empty;
        hqPolicy = "Either";
        controller.ClearSelection();
    }

    private static uint ParseUInt(string value) =>
        uint.TryParse(value?.Trim(), out var parsed) ? parsed : 0;

    private static int ClampToInt(uint value) =>
        value > int.MaxValue ? int.MaxValue : (int)value;

    private static uint ClampToUInt(int value) =>
        value <= 0 ? 0u : (uint)value;

    private static string FormatLineItem(MarketAcquisitionRequestLineDocument line) =>
        string.IsNullOrWhiteSpace(line.ItemName)
            ? $"Item {line.ItemId}"
            : $"{line.ItemName} ({line.ItemId})";

    private static string FormatLineQuantity(MarketAcquisitionRequestLineDocument line) =>
        line.QuantityMode == "TargetQuantity"
            ? line.TargetQuantity.ToString("N0")
            : line.MaxQuantity == 0 ? "No cap" : line.MaxQuantity.ToString("N0");
}
