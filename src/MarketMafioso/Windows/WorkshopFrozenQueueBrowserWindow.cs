using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Feedback;
using Franthropy.Dalamud.UI.Tables;
using Franthropy.Dalamud.UI.Windows;
using MarketMafioso.TradeQueue;
using MarketMafioso.Windows.Main;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Windows;

public sealed partial class WorkshopFrozenQueueBrowserWindow : Window, IDisposable
{
    private const string SurfaceId = "workshop-saved-jobs";
    private const string ArmedDeleteLabel = "Delete?";

    private readonly Configuration config;
    private readonly WorkshopProjectCatalog workshopCatalog;
    private readonly WorkshopFrozenQueueBrowserActions actions;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private string search = string.Empty;
    private Guid? selectedQueueId;
    private Guid? selectedIncomingId;
    private string renameInput = string.Empty;
    private string duplicateNameInput = string.Empty;
    private string newQueueNameInput = string.Empty;
    private Guid? pendingLoadQueueId;
    private Guid? pendingTradeReplaceQueueId;

    // A share that still owes materials needs a second click; the first arms it in the row or inspector.
    private Guid? armedDeleteQueueId;
    private bool armedDeleteInRow;

    // One level of undo: the last delete or cleanup sweep.
    private WorkshopSavedJobRemoval? undoRemoval;
    private string undoLabel = string.Empty;
    private readonly FileDialogManager fileDialogs = new();
    private string shareStatus = string.Empty;
    private bool shareStatusIsError;
    private readonly CompanionWindowActivationState activation = new();
    private WorkshopJobSplitPanel? split;

    // Handoff calls run off the draw thread; their results are applied here at the next Draw.
    private readonly ConcurrentQueue<Action> completedHandoffCalls = new();
    private readonly HashSet<Guid> busyHandoffs = [];
    private readonly HashSet<Guid> delivering = [];
    private readonly HashSet<Guid> resolvedIncoming = [];
    private readonly CancellationTokenSource lifetime = new();

    private static readonly Vector4 ColHeader = new(0.38f, 0.73f, 1.00f, 1f);
    private static readonly Vector4 ColSuccess = new(0.45f, 0.90f, 0.55f, 1f);
    private static readonly Vector4 ColWarning = new(1.00f, 0.75f, 0.35f, 1f);
    private static readonly Vector4 ColError = new(1.00f, 0.40f, 0.40f, 1f);
    private static readonly Vector4 ColMuted = new(0.60f, 0.60f, 0.60f, 1f);

    public WorkshopFrozenQueueBrowserWindow(
        Configuration config,
        WorkshopProjectCatalog workshopCatalog,
        WorkshopFrozenQueueBrowserActions actions,
        AgentBridgeUiReviewRegistry reviewRegistry)
        : base("Saved Jobs##MarketMafiosoFrozenQueueBrowser", ImGuiWindowFlags.None)
    {
        this.config = config;
        this.workshopCatalog = workshopCatalog;
        this.actions = actions;
        this.reviewRegistry = reviewRegistry;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(900, 560),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void OpenAndFocus()
    {
        IsOpen = true;
        activation.RequestOpen();
    }

    public override void PreDraw()
    {
        if (activation.ConsumeFocusRequest())
            ImGui.SetNextWindowFocus();
    }

    public override void OnOpen()
    {
        actions.Handoff().RequestRefresh();
        SweepFinishedMarketRunJobs();
    }

    public override void OnClose()
    {
        DropUndo();
        DisarmDelete();
        pendingLoadQueueId = null;
    }

    /// <summary>
    /// Shows a job that arrived over IPC: opens this window on it and loads it when the active queue is empty
    /// and editable. Returns the result with a message for the sending plugin's user.
    /// </summary>
    public WorkshopQueueOperationResult PresentSentJob(Guid queueId, WorkshopQueueOperationResult imported)
    {
        var queue = config.FrozenWorkshopQueues.FirstOrDefault(x => x.Id == queueId);
        if (queue == null)
            return imported;

        // Opening sweeps finished market-run jobs (OnOpen); an already open window is never swept under the reader.
        OpenAndFocus();

        search = string.Empty;
        SelectQueue(queue);

        string message;
        if (!actions.CanEditQueue)
        {
            message = $"Saved {queue.Name}; a workshop assembly run is active.";
        }
        else if (config.WorkshopPrepQueue.Count > 0)
        {
            message = $"Saved {queue.Name}; the active queue is in use.";
        }
        else
        {
            actions.Load(queue.Id);
            message = WorkshopQueueService.GetFrozenQueueState(config, queue.Id) == WorkshopFrozenQueueState.Loaded
                ? $"Sent and loaded {queue.Name}."
                : $"Saved {queue.Name}; it could not be loaded.";
        }

        SetShareStatus(message, isError: false);
        return imported with { Message = message };
    }

    /// <summary>
    /// Applies finished Workshop Host calls and remembers host state. Called every frame by the
    /// plugin, so results land even while this window is closed.
    /// </summary>
    public void PumpHandoffResults()
    {
        while (completedHandoffCalls.TryDequeue(out var apply))
            apply();
        SyncHandoffStates();
        actions.Handover.Pump();
    }

    public override void Draw()
    {

        if (split != null)
        {
            DrawSplit(split);
            fileDialogs.Draw();
            return;
        }

        DrawSearchAndCreate();

        var queues = BuildVisibleQueues();
        var incoming = BuildVisibleIncoming();
        ImGui.Spacing();
        DrawBrowserLayout(queues, incoming);
        fileDialogs.Draw();
    }

    private void DrawSearchAndCreate()
    {
        ImGuiUi.SectionHeaderWithActions(
            "Workshop Saved Jobs",
            ColHeader,
            () =>
            {
                if (ImGui.Button("Close"))
                    IsOpen = false;
            },
            70);

        var saveWidth = 160f;
        var nameWidth = Math.Max(220f, (ImGui.GetContentRegionAvail().X - saveWidth) * 0.45f);
        ImGui.SetNextItemWidth(nameWidth);
        ImGui.InputText("New saved job name##workshopSavedJobNewName", ref newQueueNameInput, 128);
        ImGui.SameLine();
        if (ImGuiUi.Button("Save Current As New", actions.CanEditQueue && config.WorkshopPrepQueue.Count > 0))
            actions.NewFromCurrent(newQueueNameInput);

        if (ImGuiUi.Button("Import from Clipboard", actions.CanEditQueue))
            ImportFromClipboard();

        ImGui.SameLine();
        if (ImGuiUi.Button("Import from File...", actions.CanEditQueue))
            fileDialogs.OpenFileDialog("Import Workshop Saved Job", WorkshopSavedJobShare.FileExtension, ImportFromFile);

        if (shareStatus.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(shareStatusIsError ? ColWarning : ColSuccess, shareStatus);
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("Search##workshopSavedJobSearch", ref search, 256);
    }

    private IReadOnlyList<WorkshopFrozenQueue> BuildVisibleQueues()
    {
        var query = search.Trim();
        var queues = config.FrozenWorkshopQueues.AsEnumerable();
        if (query.Length > 0)
            queues = queues.Where(x => x.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

        return queues
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<WorkshopHandoffView> BuildVisibleIncoming()
    {
        var query = search.Trim();
        return actions.Handoff().Incoming
            .Where(view => view.State == WorkshopHandoffState.Offered && !resolvedIncoming.Contains(view.HandoffId))
            .Where(view => query.Length == 0 ||
                           view.JobName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                           view.Sender.CharacterName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void DrawBrowserLayout(IReadOnlyList<WorkshopFrozenQueue> queues, IReadOnlyList<WorkshopHandoffView> incoming)
    {
        var flags = ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.SizingStretchProp |
                    ImGuiTableFlags.NoSavedSettings;
        // A sender's share needs the wider inspector for its handover table; a separate table id keeps
        // each layout's own column weights.
        var handover = selectedIncomingId == null && SelectedSenderShare() != null;
        if (!ImGui.BeginTable(handover ? "WorkshopSavedJobsBrowserLayoutHandover" : "WorkshopSavedJobsBrowserLayout", 2, flags, new Vector2(0, 0)))
            return;

        ImGui.TableSetupColumn("Saved Jobs", ImGuiTableColumnFlags.WidthStretch, handover ? 0.8f : 1.15f);
        ImGui.TableSetupColumn("Inspector", ImGuiTableColumnFlags.WidthStretch, handover ? 1.2f : 0.85f);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        DrawQueueTable(queues, incoming);
        DrawUndoStrip();
        ImGui.TableNextColumn();
        var selectedIncoming = selectedIncomingId is { } incomingId
            ? incoming.FirstOrDefault(view => view.HandoffId == incomingId)
            : null;
        if (selectedIncoming != null)
            DrawSelectedIncoming(selectedIncoming);
        else
            DrawSelectedQueue();
        ImGui.EndTable();
    }

    private void DrawQueueTable(IReadOnlyList<WorkshopFrozenQueue> queues, IReadOnlyList<WorkshopHandoffView> incoming)
    {
        if (queues.Count == 0 && incoming.Count == 0)
        {
            ImGui.TextColored(ColMuted, "No saved jobs match this search.");
            return;
        }

        var outgoing = actions.Handoff().Outgoing;
        var statusLabels = queues.ToDictionary(queue => queue.Id, queue => FormatStatus(queue, outgoing));
        var incomingLabels = incoming.ToDictionary(view => view.HandoffId, view => $"Incoming · {view.Sender.CharacterName}");

        var projectWidth = Math.Max(ImGui.CalcTextSize("Projects").X, ImGui.CalcTextSize("9999").X) + 24;
        var stateWidth = statusLabels.Values.Select(entry => entry.Label)
            .Concat(incomingLabels.Values)
            .Append("Not loaded")
            .Max(label => ImGui.CalcTextSize(label).X) + 28;
        var etaWidth = Math.Max(ImGui.CalcTextSize("~1h 30m").X, ImGui.CalcTextSize("ETA").X) + 24;
        var updatedWidth = Math.Max(ImGui.CalcTextSize("Updated").X, ImGui.CalcTextSize(DateTime.Now.ToString("g")).X) + 24;
        // The trailing column fits the × and widens only while a row's delete is armed.
        var removeWidth = armedDeleteInRow && queues.Any(queue => queue.Id == armedDeleteQueueId)
            ? Math.Max(DalamudRowRemoveButton.Width, ImGui.CalcTextSize(ArmedDeleteLabel).X + (ImGui.GetStyle().FramePadding.X * 2f))
            : DalamudRowRemoveButton.Width;
        var nameWidth = CalculateNameColumnWidth(
            queues.Select(queue => queue.Name).Concat(incoming.Select(view => view.JobName)),
            projectWidth + stateWidth + etaWidth + updatedWidth + removeWidth);
        var flags = ImGuiUi.InteractiveTableFlags |
                    ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.SizingFixedFit;

        if (ImGui.BeginTable("WorkshopFrozenQueueBrowserTable", 6, flags, new Vector2(0, 320)))
        {
            ImGui.TableSetupColumn("Saved Job", ImGuiTableColumnFlags.WidthFixed, nameWidth);
            ImGui.TableSetupColumn("Projects", ImGuiTableColumnFlags.WidthFixed, projectWidth);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, stateWidth);
            ImGui.TableSetupColumn("ETA", ImGuiTableColumnFlags.WidthFixed, etaWidth);
            ImGui.TableSetupColumn("Updated", ImGuiTableColumnFlags.WidthFixed, updatedWidth);
            ImGui.TableSetupColumn("##remove", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, removeWidth);
            ImGui.TableHeadersRow();

            foreach (var view in incoming)
                DrawIncomingRow(view, incomingLabels[view.HandoffId]);

            foreach (var queue in queues)
            {
                var isSelected = selectedIncomingId == null && selectedQueueId == queue.Id;
                var (statusLabel, statusColor) = statusLabels[queue.Id];

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                // AllowItemOverlap lets the row's × take the click instead of the spanning selectable.
                if (ImGui.Selectable(
                        $"{queue.Name}##workshopFrozenQueue{queue.Id}",
                        isSelected,
                        ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap))
                    SelectQueue(queue);

                var rowHovered = DalamudRowRemoveButton.IsLastItemRowHovered();
                if (!WorkshopSavedJobHandoff.IsHandoff(queue) &&
                    ImGui.IsItemHovered() &&
                    ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    SelectQueue(queue);
                    RequestLoad(queue.Id);
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(queue.Items.Sum(x => x.Quantity).ToString());

                ImGui.TableNextColumn();
                ImGui.TextColored(statusColor, statusLabel);

                ImGui.TableNextColumn();
                var estimate = TryEstimate(queue.Items);
                ImGui.TextUnformatted(estimate == null
                    ? "-"
                    : WorkshopAssemblyEstimator.FormatDuration(estimate.Duration));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(queue.UpdatedAt.ToLocalTime().ToString("g"));

                ImGui.TableNextColumn();
                DrawRowDelete(queue, rowHovered);
            }

            ImGui.EndTable();
        }
    }

    private void DrawRowDelete(WorkshopFrozenQueue queue, bool rowHovered)
    {
        var canDelete = CanDelete(queue);
        var armed = armedDeleteInRow && armedDeleteQueueId == queue.Id;
        if (armed && !rowHovered)
        {
            DisarmDelete();
            armed = false;
        }

        bool clicked;
        if (armed)
        {
            ImGui.BeginDisabled(!canDelete);
            ImGui.PushStyleColor(ImGuiCol.Text, ColWarning);
            clicked = ImGui.SmallButton($"{ArmedDeleteLabel}##workshopSavedJobDelete{queue.Id}") && canDelete;
            ImGui.PopStyleColor();
            ImGui.EndDisabled();
        }
        else
        {
            clicked = DalamudRowRemoveButton.Draw($"workshopSavedJobDelete{queue.Id}", rowHovered, canDelete);
        }

        if (clicked)
            RequestDelete(queue, fromRow: true);

        RegisterLastAction(
            $"{SurfaceId}.row.{queue.Id}.delete",
            armed ? $"Confirm deleting {queue.Name}" : $"Delete {queue.Name}",
            canDelete,
            queue.Id.ToString(),
            // An agent has no pointer on the row, so a row-armed delete would disarm before it could confirm.
            _ => InvokeDelete(queue.Id, fromRow: false));
    }

    private void DrawUndoStrip()
    {
        if (undoRemoval is not { } removal)
            return;

        switch (DalamudUndoStrip.Draw("workshopSavedJobsUndo", undoLabel))
        {
            case DalamudUndoStripResult.Undo:
                UndoRemoval(removal);
                break;
            case DalamudUndoStripResult.Dismiss:
                DropUndo();
                break;
        }
    }

    // ---- delete and undo -----------------------------------------------------------------

    /// <summary>Same rule as the inspector's Delete buttons: editable, no host call in flight, and for shares only settled states.</summary>
    private bool CanDelete(WorkshopFrozenQueue queue)
    {
        if (!actions.CanEditQueue)
            return false;
        if (!WorkshopSavedJobHandoff.IsHandoff(queue))
            return true;
        var status = WorkshopSavedJobHandoff.Resolve(queue, actions.Handoff().Outgoing, delivering.Contains(queue.Id));
        return !busyHandoffs.Contains(queue.Id) &&
               status is WorkshopSavedJobHandoffStatus.NotDelivered or WorkshopSavedJobHandoffStatus.Declined
                   or WorkshopSavedJobHandoffStatus.Withdrawn or WorkshopSavedJobHandoffStatus.Accepted;
    }

    /// <summary>
    /// Materials this character still owes the share's assembler. 0 for anything but a sent share that the
    /// assembler accepted or that already received materials; an unanswered or refused offer owes nothing.
    /// </summary>
    private int OwedUnits(WorkshopFrozenQueue queue)
    {
        if (!WorkshopSavedJobHandoff.IsHandoff(queue) || !IsSender(queue))
            return 0;
        var status = WorkshopSavedJobHandoff.Resolve(queue, actions.Handoff().Outgoing, delivering.Contains(queue.Id));
        var committed = status == WorkshopSavedJobHandoffStatus.Accepted || queue.HandedOver.Any(entry => entry.Quantity > 0);
        return committed ? BuildHandoverView(queue).Owed : 0;
    }

    /// <summary>Deletes at once, except that a share still owing materials arms on the first request.</summary>
    private (bool Success, string Message) RequestDelete(WorkshopFrozenQueue queue, bool fromRow)
    {
        if (!CanDelete(queue))
            return (false, $"{queue.Name} cannot be deleted right now.");

        if (armedDeleteQueueId != queue.Id && OwedUnits(queue) is var owed and > 0)
        {
            armedDeleteQueueId = queue.Id;
            armedDeleteInRow = fromRow;
            return (true, $"{queue.Name} still owes {owed:N0} unit(s) to {queue.HandoffTargetName}; delete again to confirm.");
        }

        return DeleteNow(queue);
    }

    private AgentBridgeUiActionResult InvokeDelete(Guid queueId, bool fromRow)
    {
        var queue = config.FrozenWorkshopQueues.FirstOrDefault(x => x.Id == queueId);
        return queue == null
            ? AgentBridgeUiActionResult.Fail("That saved job no longer exists.")
            : ToActionResult(RequestDelete(queue, fromRow));
    }

    private (bool Success, string Message) DeleteNow(WorkshopFrozenQueue queue)
    {
        var removal = actions.Delete([queue.Id]);
        if (removal == null)
            return (false, "That saved job no longer exists.");

        ForgetRemoved(removal);
        OfferUndo(removal, $"Deleted {queue.Name}");
        return (true, $"Deleted {queue.Name}.");
    }

    private void ForgetRemoved(WorkshopSavedJobRemoval removal)
    {
        foreach (var entry in removal.Jobs)
        {
            var id = entry.Job.Id;
            if (selectedQueueId == id)
                selectedQueueId = null;
            if (pendingLoadQueueId == id)
                pendingLoadQueueId = null;
            if (pendingTradeReplaceQueueId == id)
                pendingTradeReplaceQueueId = null;
            if (armedDeleteQueueId == id)
                DisarmDelete();
        }
    }

    private void OfferUndo(WorkshopSavedJobRemoval removal, string label)
    {
        undoRemoval = removal;
        undoLabel = label;
    }

    private void DropUndo()
    {
        undoRemoval = null;
        undoLabel = string.Empty;
    }

    private void DisarmDelete()
    {
        armedDeleteQueueId = null;
        armedDeleteInRow = false;
    }

    private void UndoRemoval(WorkshopSavedJobRemoval removal)
    {
        var result = actions.Restore(removal);
        if (!result.Success)
        {
            SetShareStatus(result.Message, isError: true);
            if (actions.CanEditQueue)
                DropUndo();
            return;
        }

        DropUndo();
        if (selectedQueueId == null && result.QueueId is { } restoredId &&
            config.FrozenWorkshopQueues.FirstOrDefault(x => x.Id == restoredId) is { } restored)
            SelectQueue(restored);
    }

    /// <summary>
    /// Removes market-run jobs whose work for this character is proven finished, as one undoable step.
    /// Runs only as the window opens, so rows never disappear while it is being read.
    /// </summary>
    private void SweepFinishedMarketRunJobs()
    {
        if (!actions.CanEditQueue)
            return;

        var outgoing = actions.Handoff().Outgoing;
        var finished = new List<(WorkshopFrozenQueue Job, string Reason)>();
        foreach (var queue in config.FrozenWorkshopQueues)
        {
            if (queue.Origin != WorkshopSavedJobOrigin.MarketRun)
                continue;
            var reason = WorkshopMarketRunCleanup.FinishedReason(new WorkshopMarketRunJobFacts(
                queue,
                WorkshopSavedJobHandoff.Resolve(queue, outgoing, delivering.Contains(queue.Id)),
                IsSender(queue),
                Requirements(queue),
                config.WorkshopHandoverCountTheirInventory,
                config.WorkshopHandoverIncludeTheirRetainers,
                config.ActiveFrozenWorkshopQueueId,
                config.WorkshopPrepQueue.Count,
                config.TradeQueueBinding?.ShareJobId,
                busyHandoffs.Contains(queue.Id) || delivering.Contains(queue.Id)));
            if (reason != null)
                finished.Add((queue, reason));
        }

        if (finished.Count == 0)
            return;

        var removal = actions.Delete(finished.Select(entry => entry.Job.Id).ToList());
        if (removal == null)
            return;

        foreach (var (job, reason) in finished)
            actions.LogInformation($"[MarketMafioso] Removed finished market-run saved job {job.Name}: {reason}.");
        ForgetRemoved(removal);
        OfferUndo(
            removal,
            removal.Jobs.Count == 1
                ? "Cleared 1 finished market-run job"
                : $"Cleared {removal.Jobs.Count:N0} finished market-run jobs");
    }

    /// <summary>The inspector's Delete: same path as the row ×, with an owing share armed right here.</summary>
    private void DrawInspectorDelete(WorkshopFrozenQueue queue, bool enabled)
    {
        if (armedDeleteQueueId == queue.Id && !armedDeleteInRow)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ColWarning);
            var confirmed = ImGuiUi.Button($"{ArmedDeleteLabel}##workshopSavedJobInspectorDelete", enabled);
            ImGui.PopStyleColor();
            if (confirmed)
                RequestDelete(queue, fromRow: false);
            ImGui.SameLine();
            if (ImGui.Button("Cancel##workshopSavedJobInspectorDelete"))
                DisarmDelete();
            return;
        }

        if (ImGuiUi.Button("Delete##workshopSavedJobInspectorDelete", enabled))
            RequestDelete(queue, fromRow: false);
    }

    private void DrawIncomingRow(WorkshopHandoffView view, string statusLabel)
    {
        ImGui.TableNextRow();
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(new Vector4(ColHeader.X, ColHeader.Y, ColHeader.Z, 0.10f)));
        ImGui.TableNextColumn();
        ImGui.PushStyleColor(ImGuiCol.Text, ColHeader);
        if (ImGui.Selectable($"{view.JobName}##workshopIncoming{view.HandoffId}", selectedIncomingId == view.HandoffId, ImGuiSelectableFlags.SpanAllColumns))
        {
            selectedIncomingId = view.HandoffId;
            pendingTradeReplaceQueueId = null;
            pendingLoadQueueId = null;
            DisarmDelete();
        }

        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(view.Sender.Key);

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(view.Items.Sum(item => item.Quantity).ToString());
        ImGui.TableNextColumn();
        ImGui.TextColored(ColHeader, statusLabel);
        ImGui.TableNextColumn();
        var estimate = TryEstimate(view.Items);
        ImGui.TextUnformatted(estimate == null ? "-" : WorkshopAssemblyEstimator.FormatDuration(estimate.Duration));
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(view.UpdatedAt.ToLocalTime().ToString("g"));
    }

    private void DrawSelectedQueue()
    {
        var queue = selectedQueueId is { } id
            ? config.FrozenWorkshopQueues.FirstOrDefault(x => x.Id == id)
            : null;
        if (queue == null)
        {
            ImGui.TextColored(ColMuted, "Select a saved job to preview its projects.");
            DrawQueueActions(null);
            return;
        }

        ImGui.TextColored(ColHeader, queue.Name);
        ImGui.SameLine();
        var (statusLabel, statusColor) = FormatStatus(queue, actions.Handoff().Outgoing);
        ImGui.TextColored(statusColor, statusLabel);

        if (WorkshopSavedJobHandoff.IsHandoff(queue) && IsSender(queue))
        {
            DrawProjectSummary(queue.Items);
            if (!string.IsNullOrWhiteSpace(queue.HandoffError) && !delivering.Contains(queue.Id))
                ImGui.TextColored(ColWarning, queue.HandoffError);
            DrawHandover(queue);
            DrawHandoffActions(queue);
            return;
        }

        if (WorkshopSavedJobHandoff.IsReceived(queue))
            DrawReceivedProgress(queue);

        if (WorkshopSavedJobHandoff.Target(queue) is { } target)
        {
            ImGui.TextColored(ColMuted, "Assembler:");
            ImGui.SameLine();
            ImGui.TextUnformatted(target.Key);
            if (!string.IsNullOrWhiteSpace(queue.HandoffError) && !delivering.Contains(queue.Id))
                ImGui.TextColored(ColWarning, queue.HandoffError);
        }

        DrawEstimate(queue.Items);
        DrawProjectPreview("WorkshopFrozenQueuePreview", queue.Items);

        if (WorkshopSavedJobHandoff.IsHandoff(queue))
            DrawHandoffActions(queue);
        else
            DrawQueueActions(queue);
    }

    private void DrawSelectedIncoming(WorkshopHandoffView view)
    {
        ImGui.TextColored(ColHeader, view.JobName);
        ImGui.SameLine();
        ImGui.TextColored(ColHeader, "Incoming");
        ImGui.TextColored(ColMuted, "From:");
        ImGui.SameLine();
        ImGui.TextUnformatted(view.Sender.Key);

        DrawEstimate(view.Items);
        DrawProjectPreview("WorkshopIncomingPreview", view.Items);

        var busy = busyHandoffs.Contains(view.HandoffId);
        var canAccept = !busy && actions.CanEditQueue;
        if (ImGuiUi.PrimaryButton("Accept", canAccept))
            AcceptIncoming(view);
        RegisterLastAction(
            $"{SurfaceId}.incoming.accept",
            $"Accept {view.JobName} from {view.Sender.Key} as a saved job",
            canAccept,
            view.HandoffId.ToString(),
            _ =>
            {
                if (!actions.CanEditQueue)
                    return AgentBridgeUiActionResult.Fail("Saved jobs cannot change while an assembly run is active.");
                if (busyHandoffs.Contains(view.HandoffId))
                    return AgentBridgeUiActionResult.Fail("This share already has a Workshop Host request in flight.");
                AcceptIncoming(view);
                return AgentBridgeUiActionResult.Ok($"Accepting {view.JobName} from {view.Sender.Key}.");
            });

        ImGui.SameLine();
        if (ImGuiUi.Button("Decline", !busy))
            DeclineIncoming(view);
        RegisterLastAction(
            $"{SurfaceId}.incoming.decline",
            $"Decline {view.JobName} from {view.Sender.Key}",
            !busy,
            view.HandoffId.ToString(),
            _ =>
            {
                if (busyHandoffs.Contains(view.HandoffId))
                    return AgentBridgeUiActionResult.Fail("This share already has a Workshop Host request in flight.");
                DeclineIncoming(view);
                return AgentBridgeUiActionResult.Ok($"Declining {view.JobName} from {view.Sender.Key}.");
            });

        ImGui.SameLine();
        if (ImGui.Button("Close"))
            IsOpen = false;
    }

    private void DrawEstimate(IReadOnlyList<WorkshopPrepQueueItem> items)
    {
        var estimate = TryEstimate(items);
        if (estimate != null)
        {
            ImGui.TextColored(ColMuted, $"Estimated time: {WorkshopAssemblyEstimator.FormatDuration(estimate.Duration)}");
            ImGui.TextColored(ColMuted, $"Projects: {estimate.TotalProjects}");
            ImGui.SameLine();
            ImGui.TextColored(ColMuted, $"Contribution steps: {estimate.ContributionSteps}");
        }
        else
        {
            ImGui.TextColored(ColMuted, "Estimated time: unavailable");
        }
    }

    private void DrawProjectPreview(string id, IReadOnlyList<WorkshopPrepQueueItem> items)
    {
        var projectNames = ProjectNames();
        if (!ImGui.BeginTable(id, 2, ImGuiUi.InteractiveTableFlags))
            return;

        ImGui.TableSetupColumn("Project", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 72);
        ImGui.TableHeadersRow();

        foreach (var item in items)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(projectNames.TryGetValue(item.WorkshopItemId, out var name)
                ? name
                : $"Unknown project {item.WorkshopItemId}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.Quantity.ToString());
        }

        ImGui.EndTable();
    }

    private void DrawQueueActions(WorkshopFrozenQueue? queue)
    {
        if (queue == null)
        {
            if (ImGui.Button("Close"))
                IsOpen = false;
            return;
        }

        ImGui.SetNextItemWidth(260);
        ImGui.InputText("Name##workshopSavedJobRename", ref renameInput, 128);
        ImGui.SameLine();
        if (ImGuiUi.Button("Rename", actions.CanEditQueue))
            actions.Rename(queue.Id, renameInput);

        ImGui.SetNextItemWidth(260);
        ImGui.InputText("Duplicate as##workshopSavedJobDuplicate", ref duplicateNameInput, 128);
        ImGui.SameLine();
        if (ImGuiUi.Button("Duplicate", actions.CanEditQueue))
            actions.Duplicate(queue.Id, duplicateNameInput);

        DrawShareButtons(queue);
        DrawSplitButton(queue);

        if (ImGuiUi.Button("Load", actions.CanEditQueue))
            RequestLoad(queue.Id);

        ImGui.SameLine();
        if (ImGuiUi.Button("Overwrite With Current", actions.CanEditQueue && config.WorkshopPrepQueue.Count > 0))
            actions.Overwrite(queue.Id);

        ImGui.SameLine();
        DrawInspectorDelete(queue, CanDelete(queue));

        ImGui.SameLine();
        if (ImGui.Button("Close"))
            IsOpen = false;

        if (pendingLoadQueueId == queue.Id)
        {
            ImGui.TextColored(ColMuted, "Load saved job? Unsaved active queue changes will be discarded.");
            if (ImGuiUi.PrimaryButton("Confirm Load", actions.CanEditQueue))
            {
                pendingLoadQueueId = null;
                actions.Load(queue.Id);
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel##workshopSavedJobLoad"))
                pendingLoadQueueId = null;
        }
    }

    private void DrawSplitButton(WorkshopFrozenQueue queue)
    {
        var currentCharacter = actions.CurrentCharacter();
        var canSplit = actions.CanEditQueue && currentCharacter != null && queue.Items.Sum(item => item.Quantity) >= 2;
        if (canSplit)
        {
            ImGui.SameLine();
            if (ImGui.Button("Split..."))
                StartSplit(queue, currentCharacter!);
            RegisterLastAction(
                $"{SurfaceId}.split",
                $"Split {queue.Name} across assemblers",
                enabled: true,
                queue.Id.ToString(),
                _ =>
                {
                    var character = actions.CurrentCharacter();
                    if (character == null)
                        return AgentBridgeUiActionResult.Fail("No character is logged in to split from.");
                    StartSplit(queue, character);
                    return AgentBridgeUiActionResult.Ok($"Opened split mode for {queue.Name}.");
                });
        }
    }

    private void DrawHandoffActions(WorkshopFrozenQueue queue)
    {
        var service = actions.Handoff();
        var status = WorkshopSavedJobHandoff.Resolve(queue, service.Outgoing, delivering.Contains(queue.Id));
        var busy = busyHandoffs.Contains(queue.Id);
        var isSender = IsSender(queue);

        // Receiver copies never show handover controls; only the sender hands materials over.
        if (isSender)
            DrawHandOverButton(queue);

        if (status == WorkshopSavedJobHandoffStatus.Offered && isSender)
        {
            ImGui.SameLine();
            if (ImGuiUi.Button("Withdraw", !busy))
                Withdraw(queue);
            RegisterLastAction(
                $"{SurfaceId}.handoff.withdraw",
                $"Withdraw {queue.Name} from {queue.HandoffTargetName}",
                !busy,
                queue.HandoffId?.ToString(),
                _ =>
                {
                    if (busyHandoffs.Contains(queue.Id))
                        return AgentBridgeUiActionResult.Fail("This share already has a Workshop Host request in flight.");
                    Withdraw(queue);
                    return AgentBridgeUiActionResult.Ok($"Withdrawing {queue.Name}.");
                });
        }

        // Declined and withdrawn shares can be offered again; the host revives them under the same id.
        if (status is WorkshopSavedJobHandoffStatus.NotDelivered or WorkshopSavedJobHandoffStatus.Declined or WorkshopSavedJobHandoffStatus.Withdrawn &&
            isSender && service.IsAvailable)
        {
            ImGui.SameLine();
            if (ImGuiUi.Button("Deliver", !busy))
                Deliver(queue);
            RegisterLastAction(
                $"{SurfaceId}.handoff.deliver",
                $"Offer {queue.Name} to {queue.HandoffTargetName} through the Workshop Host",
                !busy,
                queue.HandoffError,
                _ =>
                {
                    Deliver(queue);
                    return AgentBridgeUiActionResult.Ok($"Offering {queue.Name}.");
                });
        }

        if (status is WorkshopSavedJobHandoffStatus.NotDelivered or WorkshopSavedJobHandoffStatus.Declined or WorkshopSavedJobHandoffStatus.Withdrawn or WorkshopSavedJobHandoffStatus.Accepted)
        {
            if (isSender)
                ImGui.SameLine();
            DrawInspectorDelete(queue, actions.CanEditQueue && !busy);
        }

        DrawShareButtons(queue);
        ImGui.SameLine();
        if (ImGui.Button("Close"))
            IsOpen = false;

        if (isSender && pendingTradeReplaceQueueId == queue.Id)
        {
            ImGui.TextColored(ColMuted, "Replace the current Trade Queue with this share's owed materials?");
            if (ImGuiUi.PrimaryButton("Replace Trade Queue", true))
            {
                pendingTradeReplaceQueueId = null;
                HandOver(queue, confirmed: true);
            }

            RegisterLastAction(
                $"{SurfaceId}.handoff.hand-over.confirm",
                $"Replace the Trade Queue with {queue.Name}'s owed materials",
                enabled: true,
                queue.Id.ToString(),
                _ =>
                {
                    pendingTradeReplaceQueueId = null;
                    return ToActionResult(HandOver(queue, confirmed: true));
                });

            ImGui.SameLine();
            if (ImGui.Button("Cancel##workshopTradeReplace"))
                pendingTradeReplaceQueueId = null;
        }
    }

    private void DrawShareButtons(WorkshopFrozenQueue queue)
    {
        if (ImGui.Button("Copy to Clipboard"))
            CopyToClipboard(queue);

        ImGui.SameLine();
        if (ImGui.Button("Export to File..."))
        {
            var queueId = queue.Id;
            fileDialogs.SaveFileDialog(
                "Export Workshop Saved Job",
                WorkshopSavedJobShare.FileExtension,
                WorkshopSavedJobShare.BuildFileName(queue.Name),
                WorkshopSavedJobShare.FileExtension,
                (confirmed, path) => ExportToFile(queueId, confirmed, path));
        }
    }

    // ---- split ---------------------------------------------------------------------------

    private void StartSplit(WorkshopFrozenQueue queue, WorkshopAssembler currentCharacter)
    {
        pendingLoadQueueId = null;
        DisarmDelete();
        pendingTradeReplaceQueueId = null;
        var projectNames = ProjectNames();
        split = new WorkshopJobSplitPanel(
            new WorkshopJobSplitPlan(queue, currentCharacter),
            id => projectNames.TryGetValue(id, out var name) ? name : $"Unknown project {id}",
            () => actions.Handoff().KnownAssemblers,
            reviewRegistry,
            () => actions.Availability(queue.Items));
        actions.Handoff().RequestRefresh();
    }

    private void DrawSplit(WorkshopJobSplitPanel panel)
    {
        var source = config.FrozenWorkshopQueues.FirstOrDefault(queue => queue.Id == panel.Plan.SourceJobId);
        if (source == null)
        {
            split = null;
            SetShareStatus($"Saved job {panel.Plan.JobName} no longer exists.", isError: true);
            return;
        }

        ImGuiUi.SectionHeader(source.Name, ColHeader);
        panel.Draw(actions.CanEditQueue, CommitSplit, () => split = null);
    }

    private string? CommitSplit()
    {
        if (split == null)
            return "Split mode is not open.";
        if (!actions.CanEditQueue)
            return "Saved jobs cannot change while an assembly run is active.";

        WorkshopJobSplitApplyResult result;
        try
        {
            result = WorkshopJobSplitService.Apply(config, split.Plan.BuildCommit(), DateTime.UtcNow);
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }

        var sender = split.Plan.CurrentCharacter;
        foreach (var job in result.Delivered)
        {
            job.HandoffSenderName = sender.CharacterName;
            job.HandoffSenderWorld = sender.World;
            Deliver(job, save: false);
        }

        config.Save();
        split = null;
        search = string.Empty;
        var focus = result.Retained ?? result.Delivered.FirstOrDefault();
        if (focus != null)
            SelectQueue(focus);
        else
            selectedQueueId = null;

        SetShareStatus(result.Message, isError: false);
        return null;
    }

    // ---- handoff calls -------------------------------------------------------------------

    private void Deliver(WorkshopFrozenQueue job, bool save = true)
    {
        var service = actions.Handoff();
        if (WorkshopSavedJobHandoff.Target(job) is not { } target)
            return;

        if (!service.IsAvailable)
        {
            job.HandoffError = service.UnavailableReason ?? "Workshop Host delivery is unavailable.";
            if (save)
                config.Save();
            return;
        }

        job.HandoffError = null;
        delivering.Add(job.Id);
        var jobId = job.Id;
        var jobName = job.Name;
        var items = WorkshopQueueService.CloneItems(job.Items);
        RunHandoff(
            jobId,
            token => service.OfferAsync(jobId, jobName, target, items, token),
            result =>
            {
                delivering.Remove(jobId);
                var current = config.FrozenWorkshopQueues.FirstOrDefault(queue => queue.Id == jobId);
                if (current == null)
                    return;

                if (result.Success && result.Handoff != null)
                {
                    current.HandoffId = result.Handoff.HandoffId;
                    current.HandoffState = result.Handoff.State;
                    current.HandoffError = null;
                    // A revived share accepts handover again; relay the kept ledger.
                    if (current.HandoverReportRefusal != null || current.HandedOver.Count > 0 || current.HandoverTotal > 0)
                    {
                        current.HandoverReportRefusal = null;
                        actions.Handover.MarkForReport(current);
                    }
                }
                else
                {
                    current.HandoffError = result.Success
                        ? "The Workshop Host accepted the offer but returned no handoff."
                        : result.Message;
                }

                config.Save();
            });
    }

    /// <summary>Only the character that split a share off may deliver or withdraw it.</summary>
    private bool IsSender(WorkshopFrozenQueue job)
    {
        if (actions.CurrentCharacter() is not { } current)
            return false;
        if (job.HandoffSenderName is null || job.HandoffSenderWorld is null)
            return job.HandoffId is { } id && actions.Handoff().Outgoing.Any(view => view.HandoffId == id);
        return string.Equals(job.HandoffSenderName, current.CharacterName, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(job.HandoffSenderWorld, current.World, StringComparison.OrdinalIgnoreCase);
    }

    private void Withdraw(WorkshopFrozenQueue job)
    {
        if (job.HandoffId is not { } handoffId)
            return;

        var service = actions.Handoff();
        var jobId = job.Id;
        var expected = service.Outgoing.FirstOrDefault(view => view.HandoffId == handoffId)?.UpdatedAt;
        RunHandoff(
            jobId,
            token => service.WithdrawAsync(handoffId, expected, token),
            result =>
            {
                var current = config.FrozenWorkshopQueues.FirstOrDefault(queue => queue.Id == jobId);
                if (current == null)
                    return;

                if (result.Success)
                {
                    current.HandoffState = result.Handoff?.State ?? WorkshopHandoffState.Withdrawn;
                    current.HandoffError = null;
                    SetShareStatus($"Withdrew {current.Name}.", isError: false);
                }
                else
                {
                    current.HandoffError = result.Message;
                    SetShareStatus(result.Message, isError: true);
                }

                config.Save();
                service.RequestRefresh();
            });
    }

    private void AcceptIncoming(WorkshopHandoffView view)
    {
        var service = actions.Handoff();
        RunHandoff(
            view.HandoffId,
            token => service.AcceptAsync(view.HandoffId, view.UpdatedAt, token),
            result =>
            {
                service.RequestRefresh();
                if (!result.Success)
                {
                    SetShareStatus(result.Message, isError: true);
                    return;
                }

                resolvedIncoming.Add(view.HandoffId);
                if (selectedIncomingId == view.HandoffId)
                    selectedIncomingId = null;

                // Import what the host accepted, not what this frame captured.
                var accepted = result.Handoff ?? view;
                var imported = WorkshopQueueService.ImportFrozenQueue(config, accepted.JobName, accepted.Items, DateTime.UtcNow);
                SetShareStatus(
                    imported.Success
                        ? $"Accepted {imported.Name} from {view.Sender.CharacterName}."
                        : $"Accepted on the Workshop Host, but the saved job could not be created: {imported.Message}",
                    isError: !imported.Success);
                if (!imported.Success || imported.QueueId is not { } queueId)
                    return;

                var created = config.FrozenWorkshopQueues.FirstOrDefault(queue => queue.Id == queueId);
                if (created != null)
                {
                    // The receiver's copy keeps the share's lineage (never a target), so it can show
                    // material-received progress relayed by the host.
                    created.HandoffId = accepted.HandoffId;
                    created.SplitSourceJobId = accepted.SourceJobId;
                    created.HandoffSenderName = accepted.Sender.CharacterName;
                    created.HandoffSenderWorld = accepted.Sender.World;
                    created.HandoffState = WorkshopHandoffState.Accepted;
                }

                config.Save();
                if (created != null)
                    SelectQueue(created);
            });
    }

    private void DeclineIncoming(WorkshopHandoffView view)
    {
        var service = actions.Handoff();
        RunHandoff(
            view.HandoffId,
            token => service.DeclineAsync(view.HandoffId, view.UpdatedAt, token),
            result =>
            {
                service.RequestRefresh();
                if (!result.Success)
                {
                    SetShareStatus(result.Message, isError: true);
                    return;
                }

                resolvedIncoming.Add(view.HandoffId);
                if (selectedIncomingId == view.HandoffId)
                    selectedIncomingId = null;
                SetShareStatus($"Declined {view.JobName} from {view.Sender.CharacterName}.", isError: false);
            });
    }

    private void RunHandoff(
        Guid key,
        Func<CancellationToken, Task<WorkshopHandoffResult>> call,
        Action<WorkshopHandoffResult> apply)
    {
        if (!busyHandoffs.Add(key))
            return;

        var token = lifetime.Token;
        _ = Task.Run(async () =>
        {
            WorkshopHandoffResult result;
            try
            {
                result = await call(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                result = WorkshopHandoffResult.Fail($"Workshop Host request failed: {ex.Message}");
            }

            completedHandoffCalls.Enqueue(() =>
            {
                busyHandoffs.Remove(key);
                apply(result);
            });
        });
    }

    /// <summary>Remembers the latest host state so a share's status survives a host outage.</summary>
    private void SyncHandoffStates()
    {
        var outgoing = actions.Handoff().Outgoing;
        if (outgoing.Count == 0)
            return;

        var changed = false;
        foreach (var queue in config.FrozenWorkshopQueues)
        {
            if (queue.HandoffId is not { } handoffId)
                continue;
            var view = outgoing.FirstOrDefault(handoff => handoff.HandoffId == handoffId);
            if (view == null || view.State == queue.HandoffState)
                continue;
            queue.HandoffState = view.State;
            changed = true;
        }

        if (changed)
            config.Save();
    }

    private (string Label, Vector4 Color) FormatStatus(WorkshopFrozenQueue queue, IReadOnlyList<WorkshopHandoffView> outgoing)
    {
        var status = WorkshopSavedJobHandoff.Resolve(queue, outgoing, delivering.Contains(queue.Id));
        if (status == WorkshopSavedJobHandoffStatus.None)
        {
            var state = WorkshopQueueService.GetFrozenQueueState(config, queue.Id);
            if (WorkshopSavedJobHandoff.IsReceived(queue) && WorkshopSavedJobHandoff.Sender(queue) is { } sender)
            {
                return state is WorkshopFrozenQueueState.Loaded or WorkshopFrozenQueueState.Modified
                    ? ($"{FormatState(state)} · from {sender.CharacterName}", GetStateColor(state))
                    : ($"From {sender.CharacterName} @{sender.World}", ColSuccess);
            }

            return (FormatState(state), GetStateColor(state));
        }

        var color = status switch
        {
            WorkshopSavedJobHandoffStatus.Offered => ColHeader,
            WorkshopSavedJobHandoffStatus.Accepted => ColSuccess,
            WorkshopSavedJobHandoffStatus.Declined => ColError,
            WorkshopSavedJobHandoffStatus.NotDelivered => ColWarning,
            _ => ColMuted,
        };
        var label = $"{WorkshopSavedJobHandoff.Label(status)} · {queue.HandoffTargetName}";
        if (queue.HandoverTotal > 0 || queue.HandoverDue != null)
        {
            // Per-item capped: surplus of one material never reads as another material handed over.
            var view = BuildHandoverView(queue);
            if (view.Due > 0)
                label += view.Owed == 0 ? " · handed over" : $" · {view.Handed:N0}/{view.Due:N0} handed";
        }

        return (label, color);
    }

    // ---- existing browser behavior ------------------------------------------------------

    private void SelectQueue(WorkshopFrozenQueue queue)
    {
        if (selectedQueueId != queue.Id)
        {
            pendingLoadQueueId = null;
            DisarmDelete();
        }

        selectedQueueId = queue.Id;
        selectedIncomingId = null;
        pendingTradeReplaceQueueId = null;
        renameInput = queue.Name;
        duplicateNameInput = $"{queue.Name} copy";
        if (WorkshopSavedJobHandoff.IsHandoff(queue) && IsSender(queue))
        {
            actions.Handover.RequestHoldings(queue, force: false);
            actions.Handover.RequestReport(queue);
        }
    }

    private void CopyToClipboard(WorkshopFrozenQueue queue)
    {
        ImGui.SetClipboardText(WorkshopSavedJobShare.Export(queue, workshopCatalog.GetProjects(), DateTime.UtcNow));
        SetShareStatus($"Copied saved job {queue.Name}.", isError: false);
    }

    private void ExportToFile(Guid queueId, bool confirmed, string path)
    {
        if (!confirmed || string.IsNullOrWhiteSpace(path))
            return;

        var queue = config.FrozenWorkshopQueues.FirstOrDefault(x => x.Id == queueId);
        if (queue == null)
        {
            SetShareStatus("Saved job no longer exists.", isError: true);
            return;
        }

        try
        {
            File.WriteAllText(path, WorkshopSavedJobShare.Export(queue, workshopCatalog.GetProjects(), DateTime.UtcNow));
            SetShareStatus($"Exported saved job {queue.Name}.", isError: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            SetShareStatus($"Export failed: {ex.Message}", isError: true);
        }
    }

    private void ImportFromClipboard()
    {
        string? text;
        try
        {
            text = ImGui.GetClipboardText();
        }
        catch (Exception)
        {
            text = null;
        }

        Import(text);
    }

    private void ImportFromFile(bool confirmed, string path)
    {
        if (!confirmed || string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (new FileInfo(path).Length > WorkshopSavedJobShare.MaxTextLength * 4L)
            {
                SetShareStatus("That file is too large to be a workshop saved job.", isError: true);
                return;
            }

            Import(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            SetShareStatus($"Import failed: {ex.Message}", isError: true);
        }
    }

    private void Import(string? text)
    {
        if (!actions.CanEditQueue)
        {
            SetShareStatus("Saved jobs cannot change while an assembly run is active.", isError: true);
            return;
        }

        var result = actions.Import(text);
        SetShareStatus(result.Message, isError: !result.Success);
        if (!result.Success || result.QueueId is not { } importedId)
            return;

        search = string.Empty;
        var imported = config.FrozenWorkshopQueues.FirstOrDefault(x => x.Id == importedId);
        if (imported != null)
            SelectQueue(imported);
    }

    private void SetShareStatus(string message, bool isError)
    {
        shareStatus = message;
        shareStatusIsError = isError;
    }

    private void RequestLoad(Guid queueId)
    {
        if (config.WorkshopPrepQueue.Count > 0 && config.ActiveFrozenWorkshopQueueId != queueId)
        {
            pendingLoadQueueId = queueId;
            return;
        }

        actions.Load(queueId);
    }

    private static float CalculateNameColumnWidth(IEnumerable<string> names, float otherColumnsWidth)
    {
        var width = ImGui.CalcTextSize("Queue").X;
        foreach (var name in names)
            width = Math.Max(width, ImGui.CalcTextSize(name).X);

        return Math.Clamp(width + 28, 220, Math.Max(260, ImGui.GetContentRegionAvail().X - otherColumnsWidth - 24));
    }

    private Dictionary<uint, string> ProjectNames()
    {
        var names = new Dictionary<uint, string>();
        foreach (var project in workshopCatalog.GetProjects())
            names.TryAdd(project.WorkshopItemId, project.Name);
        return names;
    }

    private WorkshopAssemblyEstimate? TryEstimate(IReadOnlyList<WorkshopPrepQueueItem> items)
    {
        try
        {
            var plan = WorkshopAssemblyPlanBuilder.Build(items, workshopCatalog.GetProjects());
            return WorkshopAssemblyEstimator.Estimate(plan);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void RegisterLastAction(
        string id,
        string label,
        bool enabled,
        string? value,
        Func<System.Text.Json.JsonElement?, AgentBridgeUiActionResult> invoke) =>
        reviewRegistry.Register(
            id,
            label,
            AgentBridgeUiControlKind.Button,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            enabled,
            selected: false,
            value,
            arguments: null,
            surfaceId: SurfaceId,
            mutating: true,
            completionOperationKind: null,
            invoke);

    private static string FormatState(WorkshopFrozenQueueState state)
    {
        return state switch
        {
            WorkshopFrozenQueueState.Loaded => "Loaded",
            WorkshopFrozenQueueState.Modified => "Modified",
            _ => "Not loaded",
        };
    }

    private static Vector4 GetStateColor(WorkshopFrozenQueueState state)
    {
        return state switch
        {
            WorkshopFrozenQueueState.Loaded => ColSuccess,
            WorkshopFrozenQueueState.Modified => ColWarning,
            _ => ColMuted,
        };
    }

    public void Dispose()
    {
        lifetime.Cancel();
        lifetime.Dispose();
    }
}

public delegate bool WorkshopHandOverHandler(IReadOnlyList<TradeQueueItem> items, TradeQueueRecipientBinding binding, out string message);

public sealed record WorkshopFrozenQueueBrowserActions(
    Func<bool> CanEditQueueProvider,
    Action<Guid> Load,
    Action<Guid> Overwrite,
    Action<Guid, string> Rename,
    Action<Guid, string> Duplicate,
    Func<IReadOnlyCollection<Guid>, WorkshopSavedJobRemoval?> Delete,
    Func<WorkshopSavedJobRemoval, WorkshopQueueOperationResult> Restore,
    Action<string> NewFromCurrent,
    Func<string?, WorkshopQueueOperationResult> Import,
    Func<WorkshopAssembler?> CurrentCharacter,
    Func<IWorkshopHandoffService> Handoff,
    Func<bool> TradeQueueHasItems,
    WorkshopHandoverTracker Handover,
    Func<IReadOnlyDictionary<uint, int>?> TradeableBags,
    WorkshopHandOverHandler HandOver,
    Func<Guid?> BoundShareId,
    Action ShowTradeQueue,
    Func<IReadOnlyList<WorkshopPrepQueueItem>, IReadOnlyList<WorkshopMaterialAvailability>> Availability,
    WorkshopShareRestockActions ShareRestock,
    Action<string> LogInformation)
{
    public bool CanEditQueue => CanEditQueueProvider();
}

/// <summary>What the split-share view needs to plan and run a restock for a share.</summary>
public sealed record WorkshopShareRestockActions(
    WorkshopVendorRestockRunner Runner,
    WorkshopVendorProcurementPlanner Planner,
    Func<MarketMafioso.Quartermaster.QuartermasterOwnerScope> OwnerScope,
    Func<IReadOnlyDictionary<uint, int>> UsableBags,
    Func<IReadOnlyList<WorkshopMaterialRequirement>, IReadOnlyDictionary<uint, int>?, IReadOnlyList<WorkshopMaterialAvailability>> Availability,
    Func<bool> CanStageMarketAcquisition,
    Func<WorkshopMaterialProcurement, string> StageMarketAcquisition,
    Action<string> LogWarning);
