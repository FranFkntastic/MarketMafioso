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
        DrawActions(context);
    }

    private void DrawStatusSummary(MarketAcquisitionRequestBuilderContext context)
    {
        if (context.HasCharacterScope)
            ImGui.TextColored(MainWindow.ColMuted, $"Target: {context.CharacterName} @ {context.World}");
        else if (context.CharacterScopeTemporarilyUnavailable)
            ImGui.TextColored(MainWindow.ColMuted, "Target temporarily unavailable during route travel.");
        else
            ImGui.TextColored(MainWindow.ColError, "Log into a character before syncing acquisition requests.");

        ImGui.TextColored(GetSyncStatusColor(), FormatBuilderStatus(context));
        if (pendingRemoteDocument is not null)
            ImGui.TextColored(MainWindow.ColHeader, "Remote request changed. Review local lines, then adopt remote or update request.");
        if (context.IsRouteActive)
            ImGui.TextColored(MainWindow.ColMuted, "Request replacement is disabled while a guided route is active.");
    }

    private string FormatBuilderStatus(MarketAcquisitionRequestBuilderContext context)
    {
        var remote = string.IsNullOrWhiteSpace(document.RemoteRequestId)
            ? "local draft"
            : $"remote {document.RemoteRequestId} r{document.RemoteRevision}";
        var plan = IsPlanStale(context)
            ? ", plan stale"
            : context.CurrentPlan is null ? string.Empty : ", plan current";
        return $"{document.SyncStatus}: {document.Lines.Count:N0} line(s), {remote}{plan}. {status}";
    }

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
        RequestRouteScopeSelector.Draw(
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
        ItemAutocompleteControl.Draw(
            "AcquisitionRequestBuilder",
            itemOptions,
            itemAutocomplete,
            null,
            MainWindow.ColMuted,
            MainWindow.ColSuccess,
            MainWindow.ColError);

        DrawCombo("Mode##AcquisitionRequestBuilderMode", ["AllBelowThreshold", "TargetQuantity"], ref quantityMode);
        DrawCombo("HQ##AcquisitionRequestBuilderHq", ["Either", "HQOnly", "NQOnly"], ref hqPolicy);

        if (quantityMode == "TargetQuantity")
        {
            DrawInput("Target Quantity", "##AcquisitionRequestBuilderTargetQty", ref targetQuantityBuffer);
        }
        else
        {
            DrawInput("Max Quantity", "##AcquisitionRequestBuilderMaxQty", ref maxQuantityBuffer);
        }

        DrawInput("Max Unit Price", "##AcquisitionRequestBuilderMaxUnit", ref maxUnitPriceBuffer);
        DrawInput("Gil Cap", "##AcquisitionRequestBuilderGilCap", ref gilCapBuffer);

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
        ImGui.SetNextItemWidth(180);
        ImGui.InputText(id, ref buffer, 32);
    }

    private static void DrawCombo(string label, IReadOnlyList<string> values, ref string current)
    {
        ImGui.TextColored(MainWindow.ColMuted, label.Split('#')[0]);
        ImGui.SetNextItemWidth(180);
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
        if (document.Lines.Count == 0)
        {
            ImGui.TextColored(MainWindow.ColMuted, "No acquisition lines queued.");
            return;
        }

        const ImGuiTableFlags Flags =
            ImGuiTableFlags.Borders |
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.Resizable |
            ImGuiTableFlags.ScrollX;

        if (!ImGui.BeginTable("AcquisitionRequestBuilderLines", 7, Flags))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Mode", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 88);
        ImGui.TableSetupColumn("Max Unit", ImGuiTableColumnFlags.WidthFixed, 88);
        ImGui.TableSetupColumn("Gil Cap", ImGuiTableColumnFlags.WidthFixed, 88);
        ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, 64);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 88);
        ImGui.TableHeadersRow();

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
            ImGui.TextUnformatted(MarketAcquisitionQuantityModePresenter.FormatMode(line.QuantityMode));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatLineQuantity(line));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(RequestPricingFormatter.FormatOptionalGil(line.MaxUnitPrice));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(RequestPricingFormatter.FormatOptionalGil(line.GilCap));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.HqPolicy);
            ImGui.TableNextColumn();
            if (ImGuiUi.Button($"Remove##AcquisitionRequestBuilderRemove{index}", true))
                RemoveLine(index);
        }

        ImGui.EndTable();
    }

    private void DrawActions(MarketAcquisitionRequestBuilderContext context)
    {
        var busy = context.IsBusy || isSyncing || isRefreshing || isAppraising;
        var canSync = !busy &&
                      !context.IsRouteActive &&
                      context.HasCharacterScope &&
                      document.Lines.Count > 0;
        var syncLabel = string.IsNullOrWhiteSpace(document.RemoteRequestId) ? "Sync Request" : "Update Request";
        if (ImGuiUi.Button($"{syncLabel}##AcquisitionRequestBuilderSync", canSync))
            _ = SyncAsync(context);

        ImGui.SameLine();
        if (ImGuiUi.Button("Refresh Request##AcquisitionRequestBuilderRefresh", !busy && !string.IsNullOrWhiteSpace(document.RemoteRequestId)))
            _ = RefreshAsync();

        ImGui.SameLine();
        if (ImGuiUi.Button("Adopt Remote##AcquisitionRequestBuilderAdoptRemote", !busy && pendingRemoteDocument is not null))
            AdoptRemote();

        ImGui.SameLine();
        if (ImGuiUi.Button("Clear Local Draft##AcquisitionRequestBuilderClear", !busy && !context.IsRouteActive))
            ClearDraft(context);

        if (craftAppraisal.State.WorkshopHostEnabled)
        {
            if (ImGuiUi.Button("Appraise Missing##AcquisitionRequestBuilderAppraiseMissing", !busy && document.Lines.Any(line => line.MaxUnitPrice == 0)))
                _ = AppraiseMissingAsync();

            ImGui.SameLine();
            if (ImGuiUi.Button("Apply Quotes##AcquisitionRequestBuilderApplyQuotes", !busy && document.Lines.Count > 0))
                ApplyStoredQuotes();

            ImGui.TextColored(MainWindow.ColMuted, craftAppraisal.State.CraftQuoteStatus);
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
        status = "Adopted remote request.";
        SaveDocument();
        documentAdopted(document, remote);
    }

    private async Task AppraiseMissingAsync()
    {
        if (isAppraising)
            return;

        isAppraising = true;
        var updated = document.Lines.ToList();
        var applied = 0;
        try
        {
            for (var index = 0; index < updated.Count; index++)
            {
                var line = updated[index];
                if (line.MaxUnitPrice > 0 || line.ItemId == 0)
                    continue;

                var identity = CraftAppraisalRequestMapper.BuildLineIdentity(document, line);
                craftAppraisal.State.UpdateSelectedLine(identity);
                var quote = await craftAppraisal.FetchQuoteAsync(
                    CraftAppraisalRequestMapper.Build(document, line)).ConfigureAwait(false);
                craftAppraisal.State.RecordLineQuote(
                    identity,
                    quote,
                    craftAppraisal.State.LastCraftQuoteDiagnosticFilePath);
                var threshold = craftAppraisal.State.TryGetLineQuoteThreshold(identity);
                if (threshold is not > 0)
                    continue;

                updated[index] = line with { MaxUnitPrice = threshold.Value };
                applied++;
            }

            if (applied > 0)
            {
                document = MarkEdited(document with { Lines = updated });
                status = $"Applied {applied:N0} craft quote(s).";
                SaveDocument();
            }
            else
            {
                status = "No missing max prices could be appraised.";
            }
        }
        finally
        {
            isAppraising = false;
        }
    }

    private void ApplyStoredQuotes()
    {
        var updated = document.Lines.ToList();
        var applied = 0;
        for (var index = 0; index < updated.Count; index++)
        {
            var line = updated[index];
            if (line.ItemId == 0)
                continue;

            var identity = CraftAppraisalRequestMapper.BuildLineIdentity(document, line);
            var threshold = craftAppraisal.State.TryGetLineQuoteThreshold(identity);
            if (threshold is not > 0)
                continue;

            updated[index] = line with { MaxUnitPrice = threshold.Value };
            applied++;
        }

        if (applied == 0)
        {
            status = "No stored craft quotes are available for current lines.";
            return;
        }

        document = MarkEdited(document with { Lines = updated });
        status = $"Applied {applied:N0} stored quote(s).";
        SaveDocument();
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

    private static string FormatLineItem(MarketAcquisitionRequestLineDocument line) =>
        string.IsNullOrWhiteSpace(line.ItemName)
            ? $"Item {line.ItemId}"
            : $"{line.ItemName} ({line.ItemId})";

    private static string FormatLineQuantity(MarketAcquisitionRequestLineDocument line) =>
        line.QuantityMode == "TargetQuantity"
            ? line.TargetQuantity.ToString("N0")
            : line.MaxQuantity == 0 ? "No cap" : line.MaxQuantity.ToString("N0");
}
