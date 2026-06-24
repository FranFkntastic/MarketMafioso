using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
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

    private static readonly Vector4 ColHeader = new(0.38f, 0.73f, 1.00f, 1f);
    private static readonly Vector4 ColSuccess = new(0.45f, 0.90f, 0.55f, 1f);
    private static readonly Vector4 ColWarning = new(1.00f, 0.75f, 0.35f, 1f);
    private static readonly Vector4 ColMuted = new(0.60f, 0.60f, 0.60f, 1f);

    public WorkshopFrozenQueueBrowserWindow(
        Configuration config,
        WorkshopProjectCatalog workshopCatalog,
        WorkshopFrozenQueueBrowserActions actions)
        : base("Frozen Queue Browser##MarketMafiosoFrozenQueueBrowser", ImGuiWindowFlags.None)
    {
        this.config = config;
        this.workshopCatalog = workshopCatalog;
        this.actions = actions;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 500),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        DrawSearchAndCreate();

        var queues = BuildVisibleQueues();
        ImGui.Spacing();
        DrawQueueTable(queues);
        ImGui.Spacing();
        DrawSelectedQueue();
        DrawConfirmations();
    }

    private void DrawSearchAndCreate()
    {
        ImGui.Text("Search:");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##workshopFrozenQueueSearch", ref search, 256);

        ImGui.SetNextItemWidth(260);
        ImGui.InputText("New From Current##workshopFrozenQueueNewName", ref newQueueNameInput, 128);
        ImGui.SameLine();
        if (ImGuiUi.Button("Save Current As New", actions.CanEditQueue && config.WorkshopPrepQueue.Count > 0))
            actions.NewFromCurrent(newQueueNameInput);
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

    private void DrawQueueTable(IReadOnlyList<WorkshopFrozenQueue> queues)
    {
        if (queues.Count == 0)
        {
            ImGui.TextColored(ColMuted, "No frozen queues match this search.");
            return;
        }

        var nameWidth = CalculateNameColumnWidth(queues);
        var projectWidth = Math.Max(ImGui.CalcTextSize("Projects").X, ImGui.CalcTextSize("9999").X) + 24;
        var stateWidth = Math.Max(ImGui.CalcTextSize("Modified").X, ImGui.CalcTextSize("Not loaded").X) + 28;
        var updatedWidth = Math.Max(ImGui.CalcTextSize("Updated").X, ImGui.CalcTextSize(DateTime.Now.ToString("g")).X) + 24;
        var flags = ImGuiUi.InteractiveTableFlags |
                    ImGuiTableFlags.ScrollY |
                    ImGuiTableFlags.SizingFixedFit;

        if (ImGui.BeginTable("WorkshopFrozenQueueBrowserTable", 4, flags, new Vector2(0, 220)))
        {
            ImGui.TableSetupColumn("Queue", ImGuiTableColumnFlags.WidthFixed, nameWidth);
            ImGui.TableSetupColumn("Projects", ImGuiTableColumnFlags.WidthFixed, projectWidth);
            ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, stateWidth);
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
            ImGui.TextColored(ColMuted, "Select a frozen queue to preview its projects.");
            DrawQueueActions(null);
            return;
        }

        ImGui.TextColored(ColHeader, queue.Name);
        ImGui.SameLine();
        ImGui.TextColored(GetStateColor(WorkshopQueueService.GetFrozenQueueState(config, queue.Id)), FormatState(WorkshopQueueService.GetFrozenQueueState(config, queue.Id)));

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
        ImGui.InputText("Rename##workshopFrozenQueueBrowserRename", ref renameInput, 128);
        ImGui.SameLine();
        if (ImGuiUi.Button("Rename", actions.CanEditQueue))
            actions.Rename(queue.Id, renameInput);

        ImGui.SetNextItemWidth(260);
        ImGui.InputText("Duplicate As##workshopFrozenQueueBrowserDuplicate", ref duplicateNameInput, 128);
        ImGui.SameLine();
        if (ImGuiUi.Button("Duplicate", actions.CanEditQueue))
            actions.Duplicate(queue.Id, duplicateNameInput);

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
            ImGui.TextColored(ColMuted, "Load frozen queue? Unsaved active queue changes will be discarded.");
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
            ImGui.TextColored(ColMuted, "Delete selected frozen queue?");
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
    Action<string> NewFromCurrent)
{
    public bool CanEditQueue => CanEditQueueProvider();
}
