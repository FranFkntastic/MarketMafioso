using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly Configuration config;
    private readonly IReadOnlyList<AcquisitionItemOption> itemOptions;
    private readonly CraftAppraisalRequestBuilderController craftAppraisal;
    private readonly Func<MarketAcquisitionRequestDocument, Task<MarketAcquisitionRequestBuilderSyncOutcome>> syncRequest;
    private readonly Func<MarketAcquisitionRequestDocument, Task<MarketAcquisitionRequestBuilderRefreshOutcome>> refreshRequest;
    private readonly Action<MarketAcquisitionRequestDocument, MarketAcquisitionRequestView?> documentAdopted;
    private readonly ItemAutocompleteState itemAutocomplete = new();

    private MarketAcquisitionRequestDocument document;
    private MarketAcquisitionRequestDocument? pendingRemoteDocument;
    private MarketAcquisitionRequestView? pendingRemoteRequest;
    private int selectedLineIndex = -1;
    private string quantityMode = "AllBelowThreshold";
    private string targetQuantityBuffer = string.Empty;
    private string maxQuantityBuffer = string.Empty;
    private string maxUnitPriceBuffer = string.Empty;
    private string gilCapBuffer = string.Empty;
    private string hqPolicy = "Either";
    private string status = "Request builder ready.";
    private bool isSyncing;
    private bool isRefreshing;
    private bool isAppraising;

    public MarketAcquisitionRequestBuilderPanel(
        Configuration config,
        IDataManager dataManager,
        CraftAppraisalRequestBuilderController craftAppraisal,
        Func<MarketAcquisitionRequestDocument, Task<MarketAcquisitionRequestBuilderSyncOutcome>> syncRequest,
        Func<MarketAcquisitionRequestDocument, Task<MarketAcquisitionRequestBuilderRefreshOutcome>> refreshRequest,
        Action<MarketAcquisitionRequestDocument, MarketAcquisitionRequestView?> documentAdopted)
    {
        this.config = config;
        this.craftAppraisal = craftAppraisal;
        this.syncRequest = syncRequest;
        this.refreshRequest = refreshRequest;
        this.documentAdopted = documentAdopted;
        itemOptions = ItemAutocompleteControl.LoadItemOptions(dataManager);
        document = MarketAcquisitionRequestDocumentPersistence.Restore(config);
    }

    public MarketAcquisitionRequestDocument CurrentDocument => document;

    public string CurrentIntentHash => MarketAcquisitionRequestDocumentHasher.ComputeIntentHash(document);

    public int LineCount => document.Lines.Count;

    public void MarkPlanPrepared(string planHash)
    {
        document = document with { LastPlanHash = planHash, UpdatedAtUtc = DateTimeOffset.UtcNow };
        SaveDocument();
    }

    public void AdoptRequest(MarketAcquisitionRequestView request)
    {
        document = MarketAcquisitionRequestDocumentMapper.FromRequestView(request);
        pendingRemoteDocument = null;
        pendingRemoteRequest = null;
        selectedLineIndex = -1;
        status = "Loaded request into builder.";
        SaveDocument();
        documentAdopted(document, request);
    }

    public bool AdoptRestoredRequestIfSafe(MarketAcquisitionRequestView request)
    {
        if (!ShouldAdoptRestoredRequest(request))
            return false;

        document = MarketAcquisitionRequestDocumentMapper.FromRequestView(request);
        pendingRemoteDocument = null;
        pendingRemoteRequest = null;
        selectedLineIndex = -1;
        status = "Loaded restored request into builder.";
        SaveDocument();
        return true;
    }

    public void Draw(MarketAcquisitionRequestBuilderContext context)
    {
        EnsureCharacterScope(context);

        ImGuiUi.SectionHeader("Request Builder", MainWindow.ColHeader);
        DrawStatusSummary(context);
        ImGui.Spacing();
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
        if (pendingRemoteDocument is not null)
            ImGui.TextColored(MainWindow.ColHeader, "Remote request changed. Review local lines, then adopt remote or update request.");
        if (context.IsRouteActive)
            ImGui.TextColored(MainWindow.ColMuted, "Request replacement is disabled while a guided route is active.");
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
            ? "not saved"
            : $"saved r{document.RemoteRevision}";
        var plan = IsPlanStale(context)
            ? ", plan stale"
            : context.CurrentPlan is null ? string.Empty : ", plan current";
        return $"{FormatSyncStatus(document.SyncStatus)}: {document.Lines.Count:N0} line(s), {remote}{plan}. {status}";
    }

    private static string FormatSyncStatus(string syncStatus) =>
        syncStatus switch
        {
            "NewDraft" => "Draft",
            "LocalEdits" => "Edited locally",
            "SyncedClean" => "Saved",
            "RemoteChanged" => "Server changed",
            "SyncFailed" => "Save failed",
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

    private bool ShouldAdoptRestoredRequest(MarketAcquisitionRequestView request)
    {
        if (string.IsNullOrWhiteSpace(request.Id))
            return false;

        if (string.IsNullOrWhiteSpace(document.RemoteRequestId))
            return true;

        if (!string.Equals(document.RemoteRequestId, request.Id, StringComparison.Ordinal))
            return true;

        if (document.SyncStatus.Equals("LocalEdits", StringComparison.OrdinalIgnoreCase) ||
            document.SyncStatus.Equals("RemoteChanged", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return document.Lines.Count == 0 ||
               document.RemoteRevision != request.Revision ||
               !document.SyncStatus.Equals("SyncedClean", StringComparison.OrdinalIgnoreCase);
    }

    private void DrawRouteScope(MarketAcquisitionRequestBuilderContext context)
    {
        var scope = RequestRouteScope.FromDocument(document);
        RequestRouteScopeSelector.DrawCompact(
            "AcquisitionRequestBuilder",
            scope,
            updated =>
            {
                document = MarkEdited(document with
                {
                    Region = updated.Region,
                    WorldMode = updated.WorldMode,
                    SweepScope = updated.SweepScope,
                    SweepDataCenters = updated.SweepDataCenters.ToList(),
                });
                pendingRemoteDocument = null;
                pendingRemoteRequest = null;
            },
            MainWindow.ColMuted,
            MainWindow.ColError);
    }

    private void DrawLineEditor(MarketAcquisitionRequestBuilderContext context)
    {
        ImGui.TextColored(MainWindow.ColHeader, selectedLineIndex >= 0 ? "Edit Line" : "Add Line");

        if (ImGui.BeginTable("AcquisitionRequestBuilderLineEditor", 6, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 2.3f);
            ImGui.TableSetupColumn("Mode", ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthStretch, 0.8f);
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthStretch, 0.9f);
            ImGui.TableSetupColumn("Unit Cost Ceiling", ImGuiTableColumnFlags.WidthStretch, 0.9f);
            ImGui.TableSetupColumn("Total Spend Ceiling", ImGuiTableColumnFlags.WidthStretch, 0.9f);
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
            DrawCombo("Mode##AcquisitionRequestBuilderMode", ["AllBelowThreshold", "TargetQuantity"], ref quantityMode);
            ImGui.TableNextColumn();
            DrawCombo("HQ##AcquisitionRequestBuilderHq", ["Either", "HQOnly", "NQOnly"], ref hqPolicy);
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
        var actionLabel = selectedLineIndex >= 0 ? "Update Line" : "Add Line";
        if (ImGuiUi.Button($"{actionLabel}##AcquisitionRequestBuilderApplyLine", canApply))
            ApplyEditorLine();

        ImGui.SameLine();
        if (ImGuiUi.Button("New Line##AcquisitionRequestBuilderNewLine", true))
            ClearLineEditor();
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
        if (!ImGui.BeginCombo(label, current))
            return;

        foreach (var value in values)
        {
            var selected = string.Equals(value, current, StringComparison.Ordinal);
            if (ImGui.Selectable(value, selected))
                current = value;
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

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
                selectedLineIndex = index;
                LoadLineIntoEditor(line);
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
            status = "Edit unit cost ceiling directly in the row.";
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
            status = "Edit total spend ceiling directly in the row.";
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
        var busy = context.IsBusy || isSyncing || isRefreshing || isAppraising;
        var canSync = !busy &&
                      !context.IsRouteActive &&
                      context.HasCharacterScope &&
                      document.Lines.Count > 0;
        var syncLabel = string.IsNullOrWhiteSpace(document.RemoteRequestId) ? "Save Request" : "Update Request";

        ImGui.TextColored(MainWindow.ColHeader, "Request");
        if (ImGuiUi.Button($"{syncLabel}##AcquisitionRequestBuilderSync", canSync))
            _ = SyncAsync(context);

        ImGui.SameLine();
        if (ImGuiUi.Button("Reload Saved##AcquisitionRequestBuilderRefresh", !busy && !string.IsNullOrWhiteSpace(document.RemoteRequestId)))
            _ = RefreshAsync();

        ImGui.SameLine();
        if (ImGuiUi.Button("Use Server Copy##AcquisitionRequestBuilderAdoptRemote", !busy && pendingRemoteDocument is not null))
            AdoptRemote();

        ImGui.SameLine();
        if (ImGuiUi.Button("Clear Draft##AcquisitionRequestBuilderClear", !busy && !context.IsRouteActive))
            ClearDraft(context);

        ImGui.TextColored(MainWindow.ColMuted, "Right-click unit or spend ceiling cells for Craft Architect evidence and pricing shortcuts.");
    }

    private void SelectLineForEditing(int index, MarketAcquisitionRequestLineDocument line)
    {
        selectedLineIndex = index;
        LoadLineIntoEditor(line);
    }

    private void SetLineMaxUnitPrice(int index, uint maxUnitPrice, string message)
    {
        UpdateLine(
            index,
            line => line with { MaxUnitPrice = maxUnitPrice },
            message);
    }

    private void SetLineGilCap(int index, uint gilCap, string message)
    {
        UpdateLine(
            index,
            line => line with { GilCap = gilCap },
            message);
    }

    private void UpdateLine(
        int index,
        Func<MarketAcquisitionRequestLineDocument, MarketAcquisitionRequestLineDocument> update,
        string message)
    {
        if (index < 0 || index >= document.Lines.Count)
            return;

        var lines = document.Lines.ToList();
        lines[index] = update(lines[index]);
        document = MarkEdited(document with { Lines = lines });
        pendingRemoteDocument = null;
        pendingRemoteRequest = null;
        selectedLineIndex = index;
        LoadLineIntoEditor(lines[index]);
        status = message;
        SaveDocument();
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
        if (index < 0 || index >= document.Lines.Count)
            return;

        document = RequestDocumentMutation.ApplyLineEdit(
            document,
            index,
            quantityMode ?? line.QuantityMode,
            targetQuantity ?? line.TargetQuantity,
            maxQuantity ?? line.MaxQuantity,
            hqPolicy ?? line.HqPolicy,
            maxUnitPrice ?? line.MaxUnitPrice,
            gilCap ?? line.GilCap);
        pendingRemoteDocument = null;
        pendingRemoteRequest = null;
        selectedLineIndex = index;
        LoadLineIntoEditor(document.Lines[index]);
        status = message;
        SaveDocument();
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

            status = "Craft Architect did not return a usable unit cost ceiling for this line.";
        }
        catch (Exception ex)
        {
            status = $"Craft Architect quote failed: {ex.Message}";
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
        var lines = document.Lines.ToList();
        if (selectedLineIndex >= 0 && selectedLineIndex < lines.Count)
        {
            lines[selectedLineIndex] = line;
        }
        else
        {
            lines.Add(line);
            selectedLineIndex = lines.Count - 1;
        }

        document = MarkEdited(document with { Lines = lines });
        pendingRemoteDocument = null;
        pendingRemoteRequest = null;
        status = "Local request updated.";
        SaveDocument();
    }

    private void RemoveLine(int index)
    {
        if (index < 0 || index >= document.Lines.Count)
            return;

        var lines = document.Lines.ToList();
        lines.RemoveAt(index);
        selectedLineIndex = -1;
        ClearLineEditor();
        document = MarkEdited(document with { Lines = lines });
        pendingRemoteDocument = null;
        pendingRemoteRequest = null;
        status = "Line removed.";
        SaveDocument();
    }

    private async Task SyncAsync(MarketAcquisitionRequestBuilderContext context)
    {
        if (isSyncing)
            return;

        isSyncing = true;
        try
        {
            var scopedDocument = document with
            {
                TargetCharacterName = context.CharacterName,
                TargetWorld = context.World,
            };
            var outcome = await syncRequest(scopedDocument).ConfigureAwait(false);
            document = outcome.Document;
            pendingRemoteDocument = null;
            pendingRemoteRequest = null;
            selectedLineIndex = -1;
            status = outcome.StatusMessage;
            SaveDocument();
        }
        catch (Exception ex)
        {
            document = document with { SyncStatus = "SyncFailed", UpdatedAtUtc = DateTimeOffset.UtcNow };
            status = $"Sync failed: {ex.Message}";
            SaveDocument();
        }
        finally
        {
            isSyncing = false;
        }
    }

    private async Task RefreshAsync()
    {
        if (isRefreshing)
            return;

        isRefreshing = true;
        try
        {
            var outcome = await refreshRequest(document).ConfigureAwait(false);
            document = outcome.Document;
            pendingRemoteDocument = outcome.RemoteDocument;
            pendingRemoteRequest = outcome.RemoteRequest;
            status = outcome.StatusMessage;
            SaveDocument();
        }
        catch (Exception ex)
        {
            status = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            isRefreshing = false;
        }
    }

    private void AdoptRemote()
    {
        if (pendingRemoteDocument is null)
            return;

        document = pendingRemoteDocument;
        var remote = pendingRemoteRequest;
        pendingRemoteDocument = null;
        pendingRemoteRequest = null;
        selectedLineIndex = -1;
        status = "Using saved server copy.";
        SaveDocument();
        documentAdopted(document, remote);
    }

    private void ClearDraft(MarketAcquisitionRequestBuilderContext context)
    {
        document = MarketAcquisitionRequestDocument.CreateDefault(
            context.HasCharacterScope ? context.CharacterName : string.Empty,
            context.HasCharacterScope ? context.World : string.Empty);
        pendingRemoteDocument = null;
        pendingRemoteRequest = null;
        selectedLineIndex = -1;
        ClearLineEditor();
        status = "Local draft cleared.";
        SaveDocument();
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

        document = document with
        {
            TargetCharacterName = context.CharacterName,
            TargetWorld = context.World,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        SaveDocument();
    }

    private MarketAcquisitionRequestDocument MarkEdited(MarketAcquisitionRequestDocument next)
    {
        var statusName = string.IsNullOrWhiteSpace(document.RemoteRequestId) ? "NewDraft" : "LocalEdits";
        return next with
        {
            LocalRevision = document.LocalRevision + 1,
            SyncStatus = statusName,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private void SaveDocument()
    {
        MarketAcquisitionRequestDocumentPersistence.Save(config, document);
        config.Save();
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
        selectedLineIndex = -1;
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
