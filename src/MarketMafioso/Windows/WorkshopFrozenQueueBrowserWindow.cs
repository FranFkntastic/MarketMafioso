using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Franthropy.Dalamud.UI.Windows;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Windows;

public sealed class WorkshopFrozenQueueBrowserWindow : Window, IDisposable
{
    private readonly Configuration config;
    private readonly WorkshopProjectCatalog workshopCatalog;
    private readonly WorkshopFrozenQueueBrowserActions actions;
    private string search = string.Empty;
    private Guid? selectedQueueId;
    private string renameInput = string.Empty;
    private string duplicateNameInput = string.Empty;
    private string newQueueNameInput = string.Empty;
    private Guid? pendingLoadQueueId;
    private Guid? pendingDeleteQueueId;
    private readonly FileDialogManager fileDialogs = new();
    private string shareStatus = string.Empty;
    private bool shareStatusIsError;
    private readonly CompanionWindowActivationState activation = new();

    private static readonly Vector4 ColHeader = new(0.38f, 0.73f, 1.00f, 1f);
    private static readonly Vector4 ColSuccess = new(0.45f, 0.90f, 0.55f, 1f);
    private static readonly Vector4 ColWarning = new(1.00f, 0.75f, 0.35f, 1f);
    private static readonly Vector4 ColMuted = new(0.60f, 0.60f, 0.60f, 1f);

    public WorkshopFrozenQueueBrowserWindow(
        Configuration config,
        WorkshopProjectCatalog workshopCatalog,
        WorkshopFrozenQueueBrowserActions actions)
        : base("Saved Jobs##MarketMafiosoFrozenQueueBrowser", ImGuiWindowFlags.None)
    {
        this.config = config;
        this.workshopCatalog = workshopCatalog;
        this.actions = actions;

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

    public override void Draw()
    {
        DrawSearchAndCreate();

        var queues = BuildVisibleQueues();
        ImGui.Spacing();
        DrawBrowserLayout(queues);
        DrawConfirmations();
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

    private void DrawBrowserLayout(IReadOnlyList<WorkshopFrozenQueue> queues)
    {
        var flags = ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.SizingStretchProp |
                    ImGuiTableFlags.NoSavedSettings;
        if (!ImGui.BeginTable("WorkshopSavedJobsBrowserLayout", 2, flags, new Vector2(0, 0)))
            return;

        ImGui.TableSetupColumn("Saved Jobs", ImGuiTableColumnFlags.WidthStretch, 1.15f);
        ImGui.TableSetupColumn("Inspector", ImGuiTableColumnFlags.WidthStretch, 0.85f);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        DrawQueueTable(queues);
        ImGui.TableNextColumn();
        DrawSelectedQueue();
        ImGui.EndTable();
    }

    private void DrawQueueTable(IReadOnlyList<WorkshopFrozenQueue> queues)
    {
        if (queues.Count == 0)
        {
            ImGui.TextColored(ColMuted, "No saved jobs match this search.");
            return;
        }

        var nameWidth = CalculateNameColumnWidth(queues);
        var projectWidth = Math.Max(ImGui.CalcTextSize("Projects").X, ImGui.CalcTextSize("9999").X) + 24;
        var stateWidth = Math.Max(ImGui.CalcTextSize("Modified").X, ImGui.CalcTextSize("Not loaded").X) + 28;
        var etaWidth = Math.Max(ImGui.CalcTextSize("~1h 30m").X, ImGui.CalcTextSize("ETA").X) + 24;
        var updatedWidth = Math.Max(ImGui.CalcTextSize("Updated").X, ImGui.CalcTextSize(DateTime.Now.ToString("g")).X) + 24;
        var flags = ImGuiUi.InteractiveTableFlags |
                    ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.SizingFixedFit;

        if (ImGui.BeginTable("WorkshopFrozenQueueBrowserTable", 5, flags, new Vector2(0, 320)))
        {
            ImGui.TableSetupColumn("Saved Job", ImGuiTableColumnFlags.WidthFixed, nameWidth);
            ImGui.TableSetupColumn("Projects", ImGuiTableColumnFlags.WidthFixed, projectWidth);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, stateWidth);
            ImGui.TableSetupColumn("ETA", ImGuiTableColumnFlags.WidthFixed, etaWidth);
            ImGui.TableSetupColumn("Updated", ImGuiTableColumnFlags.WidthFixed, updatedWidth);
            ImGui.TableHeadersRow();

            foreach (var queue in queues)
            {
                var isSelected = selectedQueueId == queue.Id;
                var state = WorkshopQueueService.GetFrozenQueueState(config, queue.Id);

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (ImGui.Selectable($"{queue.Name}##workshopFrozenQueue{queue.Id}", isSelected, ImGuiSelectableFlags.SpanAllColumns))
                    SelectQueue(queue);

                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    SelectQueue(queue);
                    RequestLoad(queue.Id);
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(queue.Items.Sum(x => x.Quantity).ToString());

                ImGui.TableNextColumn();
                ImGui.TextColored(GetStateColor(state), FormatState(state));

                ImGui.TableNextColumn();
                var estimate = TryEstimateQueue(queue);
                ImGui.TextUnformatted(estimate == null
                    ? "-"
                    : WorkshopAssemblyEstimator.FormatDuration(estimate.Duration));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(queue.UpdatedAt.ToLocalTime().ToString("g"));
            }

            ImGui.EndTable();
        }
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
        ImGui.TextColored(GetStateColor(WorkshopQueueService.GetFrozenQueueState(config, queue.Id)), FormatState(WorkshopQueueService.GetFrozenQueueState(config, queue.Id)));

        var estimate = TryEstimateQueue(queue);
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

        var projectNames = workshopCatalog.GetProjects().ToDictionary(x => x.WorkshopItemId, x => x.Name);
        if (ImGui.BeginTable("WorkshopFrozenQueuePreview", 2, ImGuiUi.InteractiveTableFlags))
        {
            ImGui.TableSetupColumn("Project", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 72);
            ImGui.TableHeadersRow();

            foreach (var item in queue.Items)
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

        DrawQueueActions(queue);
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

        if (ImGuiUi.Button("Load", actions.CanEditQueue))
            RequestLoad(queue.Id);

        ImGui.SameLine();
        if (ImGuiUi.Button("Overwrite With Current", actions.CanEditQueue && config.WorkshopPrepQueue.Count > 0))
            actions.Overwrite(queue.Id);

        ImGui.SameLine();
        if (ImGuiUi.Button("Delete", actions.CanEditQueue))
            pendingDeleteQueueId = queue.Id;

        ImGui.SameLine();
        if (ImGui.Button("Close"))
            IsOpen = false;
    }

    private void SelectQueue(WorkshopFrozenQueue queue)
    {
        selectedQueueId = queue.Id;
        renameInput = queue.Name;
        duplicateNameInput = $"{queue.Name} copy";
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

    private void DrawConfirmations()
    {
        if (pendingLoadQueueId != null)
        {
            ImGui.TextColored(ColMuted, "Load saved job? Unsaved active queue changes will be discarded.");
            if (ImGuiUi.Button("Confirm Load", actions.CanEditQueue))
            {
                actions.Load(pendingLoadQueueId.Value);
                pendingLoadQueueId = null;
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel Load"))
                pendingLoadQueueId = null;
        }

        if (pendingDeleteQueueId != null)
        {
            ImGui.TextColored(ColMuted, "Delete selected saved job?");
            if (ImGuiUi.Button("Confirm Delete", actions.CanEditQueue))
            {
                actions.Delete(pendingDeleteQueueId.Value);
                if (selectedQueueId == pendingDeleteQueueId)
                    selectedQueueId = null;

                pendingDeleteQueueId = null;
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel Delete"))
                pendingDeleteQueueId = null;
        }
    }

    private float CalculateNameColumnWidth(IReadOnlyList<WorkshopFrozenQueue> queues)
    {
        var width = ImGui.CalcTextSize("Queue").X;
        foreach (var queue in queues)
            width = Math.Max(width, ImGui.CalcTextSize(queue.Name).X);

        return Math.Clamp(width + 28, 220, Math.Max(260, ImGui.GetContentRegionAvail().X - 280));
    }

    private WorkshopAssemblyEstimate? TryEstimateQueue(WorkshopFrozenQueue queue)
    {
        try
        {
            var plan = WorkshopAssemblyPlanBuilder.Build(queue.Items, workshopCatalog.GetProjects());
            return WorkshopAssemblyEstimator.Estimate(plan);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

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
    }
}

public sealed record WorkshopFrozenQueueBrowserActions(
    Func<bool> CanEditQueueProvider,
    Action<Guid> Load,
    Action<Guid> Overwrite,
    Action<Guid, string> Rename,
    Action<Guid, string> Duplicate,
    Action<Guid> Delete,
    Action<string> NewFromCurrent,
    Func<string?, WorkshopQueueOperationResult> Import)
{
    public bool CanEditQueue => CanEditQueueProvider();
}
