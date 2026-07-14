using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using MarketMafioso.Windows.Main;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Windows.WorkshopLogistics;

internal sealed class WorkshopPrepQueuePanel
{
    private readonly Configuration config;
    private readonly WorkshopProjectCatalog workshopCatalog;
    private readonly VIWIWorkshoppaIpc viwiWorkshoppaIpc;
    private readonly WorkshopAssemblyRunner workshopAssemblyRunner;
    private readonly WorkshopProjectSelectionState workshopProjectSelection;
    private readonly WorkshopMaterialManifestExportService workshopMaterialManifestExport;
    private readonly Func<IReadOnlyList<WorkshopMaterialAvailability>> getWorkshopAvailability;
    private readonly Action<string> setWorkshopStatus;
    private readonly Action openProjectBrowser;
    private readonly Action openFrozenQueueBrowser;
    private readonly IPluginLog log;

    private bool confirmViwiClear = false;
    private bool confirmNewWorkshopQueue = false;
    private bool confirmLoadFrozenQueue = false;
    private Guid? selectedFrozenQueueId;
    private string frozenQueueNameInput = string.Empty;

    public WorkshopPrepQueuePanel(
        Configuration config,
        WorkshopProjectCatalog workshopCatalog,
        VIWIWorkshoppaIpc viwiWorkshoppaIpc,
        WorkshopAssemblyRunner workshopAssemblyRunner,
        WorkshopProjectSelectionState workshopProjectSelection,
        WorkshopMaterialManifestExportService workshopMaterialManifestExport,
        Func<IReadOnlyList<WorkshopMaterialAvailability>> getWorkshopAvailability,
        Action<string> setWorkshopStatus,
        Action openProjectBrowser,
        Action openFrozenQueueBrowser,
        IPluginLog log)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.workshopCatalog = workshopCatalog ?? throw new ArgumentNullException(nameof(workshopCatalog));
        this.viwiWorkshoppaIpc = viwiWorkshoppaIpc ?? throw new ArgumentNullException(nameof(viwiWorkshoppaIpc));
        this.workshopAssemblyRunner = workshopAssemblyRunner ?? throw new ArgumentNullException(nameof(workshopAssemblyRunner));
        this.workshopProjectSelection = workshopProjectSelection ?? throw new ArgumentNullException(nameof(workshopProjectSelection));
        this.workshopMaterialManifestExport = workshopMaterialManifestExport ?? throw new ArgumentNullException(nameof(workshopMaterialManifestExport));
        this.getWorkshopAvailability = getWorkshopAvailability ?? throw new ArgumentNullException(nameof(getWorkshopAvailability));
        this.setWorkshopStatus = setWorkshopStatus ?? throw new ArgumentNullException(nameof(setWorkshopStatus));
        this.openProjectBrowser = openProjectBrowser ?? throw new ArgumentNullException(nameof(openProjectBrowser));
        this.openFrozenQueueBrowser = openFrozenQueueBrowser ?? throw new ArgumentNullException(nameof(openFrozenQueueBrowser));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public bool CanEditQueue => !workshopAssemblyRunner.HasActiveRun;

    public void Draw(IReadOnlyList<WorkshopProjectDefinition> projects)
    {
        ImGuiUi.SectionHeaderWithActions("Prep Queue", MarketMafiosoUiTheme.Header, DrawHeaderActions, 180);
        DrawFrozenQueueToolbar();
        ImGui.Spacing();

        if (projects.Count == 0)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "No company workshop projects were found.");
            return;
        }

        DrawQueueTable(projects);
    }

    public void DrawConfirmations()
    {
        var hasPrepQueue = config.WorkshopPrepQueue.Count > 0;

        if (config.WorkshopPrepQueue.Count == 0)
            confirmViwiClear = false;

        if (!confirmViwiClear)
            return;

        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "This will clear VIWI Workshoppa's queue and send the MarketMafioso prep queue.");

        if (ImGuiUi.Button("Confirm VIWI Queue Sync", hasPrepQueue && CanEditQueue))
        {
            var result = viwiWorkshoppaIpc.SendQueue(config.WorkshopPrepQueue, clearExisting: true);
            setWorkshopStatus(result.Message);
            confirmViwiClear = false;
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel VIWI Queue Sync"))
            confirmViwiClear = false;
    }

    public void AddWorkshopProject(uint workshopItemId)
    {
        if (workshopAssemblyRunner.HasActiveRun)
        {
            setWorkshopStatus("Cannot edit prep queue while workshop assembly is active.");
            return;
        }

        var existing = config.WorkshopPrepQueue.FirstOrDefault(x => x.WorkshopItemId == workshopItemId);
        var quantity = Math.Max(1, workshopProjectSelection.Quantity);
        if (existing != null)
        {
            existing.Quantity += quantity;
        }
        else
        {
            config.WorkshopPrepQueue.Add(new WorkshopPrepQueueItem
            {
                WorkshopItemId = workshopItemId,
                Quantity = quantity,
            });
        }

        SaveActiveQueueEdit();
        setWorkshopStatus("Added project to workshop prep queue.");
    }

    public void LoadFrozenQueue(Guid queueId)
    {
        selectedFrozenQueueId = queueId;
        ApplyFrozenQueueResult(WorkshopQueueService.LoadFrozenQueue(config, queueId));
    }

    public void DeleteFrozenQueue(Guid queueId)
    {
        var result = WorkshopQueueService.DeleteFrozenQueue(config, queueId);
        if (result.Success)
            selectedFrozenQueueId = config.FrozenWorkshopQueues.FirstOrDefault()?.Id;

        ApplyFrozenQueueResult(result);
    }

    public void OverwriteFrozenQueueWithCurrent(Guid queueId)
    {
        selectedFrozenQueueId = queueId;
        ApplyFrozenQueueResult(WorkshopQueueService.OverwriteFrozenQueue(config, queueId, DateTime.UtcNow));
    }

    public void RenameFrozenQueue(Guid queueId, string name)
    {
        selectedFrozenQueueId = queueId;
        ApplyFrozenQueueResult(WorkshopQueueService.RenameFrozenQueue(config, queueId, name, DateTime.UtcNow));
    }

    public void DuplicateFrozenQueue(Guid queueId, string name)
    {
        selectedFrozenQueueId = queueId;
        ApplyFrozenQueueResult(WorkshopQueueService.DuplicateFrozenQueue(config, queueId, name, DateTime.UtcNow));
    }

    public void SaveCurrentQueueAsNew(string name)
    {
        ApplyFrozenQueueResult(WorkshopQueueService.FreezeCurrentQueue(config, name, DateTime.UtcNow), clearName: false);
    }

    private void DrawHeaderActions()
    {
        var hasPrepQueue = config.WorkshopPrepQueue.Count > 0;

        if (ImGuiUi.MenuButton("Handoff"))
            ImGui.OpenPopup("WorkshopQueueHandoffMenu");

        if (ImGui.BeginPopup("WorkshopQueueHandoffMenu"))
        {
            if (ImGuiUi.MenuItem("Send to VIWI", hasPrepQueue && CanEditQueue))
                confirmViwiClear = true;

            ImGui.EndPopup();
        }

        ImGui.SameLine();
        if (ImGuiUi.MenuButton("Export"))
            ImGui.OpenPopup("WorkshopQueueExportMenu");

        if (ImGui.BeginPopup("WorkshopQueueExportMenu"))
        {
            if (ImGuiUi.MenuItem("Copy Artisan Manifest", hasPrepQueue))
                CopyWorkshopArtisanManifest();

            if (ImGuiUi.MenuItem("Copy Craft Architect Plan", hasPrepQueue))
                CopyWorkshopCraftArchitectPlan();

            ImGui.EndPopup();
        }
    }

    private void DrawFrozenQueueToolbar()
    {
        var activeFrozenQueue = config.ActiveFrozenWorkshopQueueId == null
            ? null
            : config.FrozenWorkshopQueues.FirstOrDefault(x => x.Id == config.ActiveFrozenWorkshopQueueId.Value);

        var activeFrozenQueueLabel = activeFrozenQueue == null
            ? "Active queue: unsaved"
            : WorkshopQueueService.ActiveQueueMatchesFrozenQueue(config)
                ? $"Active saved job: {activeFrozenQueue.Name}"
                : $"Active saved job: {activeFrozenQueue.Name} (modified)";
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, activeFrozenQueueLabel);

        var saveWidth = ImGui.CalcTextSize("Save As...").X + ImGui.GetStyle().FramePadding.X * 2f;
        var nameWidth = Math.Max(180f, ImGui.GetContentRegionAvail().X - saveWidth - 110f - ImGui.GetStyle().ItemSpacing.X * 2f);
        ImGui.SetNextItemWidth(nameWidth);
        ImGui.InputText("##workshopFrozenQueueName", ref frozenQueueNameInput, 128);

        ImGui.SameLine();
        if (ImGuiUi.Button("Save Queue", CanEditQueue && config.WorkshopPrepQueue.Count > 0))
        {
            var createsFrozenQueue = config.ActiveFrozenWorkshopQueueId == null;
            ApplyFrozenQueueResult(
                WorkshopQueueService.SaveActiveQueue(config, frozenQueueNameInput, DateTime.UtcNow),
                clearName: createsFrozenQueue);
        }

        ImGui.SameLine();
        if (ImGuiUi.Button("Save As...", CanEditQueue && config.WorkshopPrepQueue.Count > 0))
            ApplyFrozenQueueResult(WorkshopQueueService.FreezeCurrentQueue(config, frozenQueueNameInput, DateTime.UtcNow), clearName: true);

        if (ImGuiUi.Button("New Queue", CanEditQueue))
        {
            if (config.WorkshopPrepQueue.Count > 0)
                confirmNewWorkshopQueue = true;
            else
                StartNewWorkshopQueue();
        }

        ImGui.SameLine();
        if (ImGuiUi.Button("Add Project...", CanEditQueue))
            openProjectBrowser();

        ImGui.SameLine();
        DrawFrozenQueueLoadCombo();

        ImGui.SameLine();
        if (ImGui.Button("Manage Saved Jobs"))
            openFrozenQueueBrowser();

        DrawFrozenQueueConfirmations();
    }

    private void DrawFrozenQueueLoadCombo()
    {
        var canLoad = CanEditQueue && config.FrozenWorkshopQueues.Count > 0;
        if (!canLoad)
            ImGui.BeginDisabled();

        var preview = selectedFrozenQueueId is { } id
            ? config.FrozenWorkshopQueues.FirstOrDefault(x => x.Id == id)?.Name ?? "Load saved job..."
            : "Load saved job...";
        ImGui.SetNextItemWidth(220);
        if (ImGui.BeginCombo("##workshopFrozenQueueLoad", preview))
        {
            foreach (var frozenQueue in config.FrozenWorkshopQueues.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                var isSelected = selectedFrozenQueueId == frozenQueue.Id;
                if (ImGui.Selectable($"{frozenQueue.Name} ({frozenQueue.Items.Sum(x => x.Quantity)})##load{frozenQueue.Id}", isSelected))
                {
                    selectedFrozenQueueId = frozenQueue.Id;
                    RequestLoadFrozenQueue(frozenQueue.Id);
                }
            }

            ImGui.EndCombo();
        }

        if (!canLoad)
            ImGui.EndDisabled();
    }

    private void DrawFrozenQueueConfirmations()
    {
        if (confirmNewWorkshopQueue)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Start a new queue? Unsaved active queue changes will be discarded.");
            if (ImGuiUi.Button("Confirm New Queue", CanEditQueue))
                StartNewWorkshopQueue();

            ImGui.SameLine();
            if (ImGui.Button("Cancel New Queue"))
                confirmNewWorkshopQueue = false;
        }

        if (confirmLoadFrozenQueue)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Load saved job? Unsaved active queue changes will be discarded.");
            if (ImGuiUi.Button("Confirm Load Saved Job", CanEditQueue && selectedFrozenQueueId != null))
                LoadSelectedFrozenQueue();

            ImGui.SameLine();
            if (ImGui.Button("Cancel Load Saved Job"))
                confirmLoadFrozenQueue = false;
        }
    }

    private void RequestLoadFrozenQueue(Guid queueId)
    {
        selectedFrozenQueueId = queueId;
        if (config.WorkshopPrepQueue.Count > 0 && config.ActiveFrozenWorkshopQueueId != queueId)
        {
            confirmLoadFrozenQueue = true;
            return;
        }

        LoadSelectedFrozenQueue();
    }

    private void LoadSelectedFrozenQueue()
    {
        if (selectedFrozenQueueId == null)
            return;

        LoadFrozenQueue(selectedFrozenQueueId.Value);
        confirmLoadFrozenQueue = false;
    }

    private void StartNewWorkshopQueue()
    {
        WorkshopQueueService.NewActiveQueue(config);
        config.Save();
        confirmNewWorkshopQueue = false;
        setWorkshopStatus("Started a new workshop prep queue.");
    }

    private void ApplyFrozenQueueResult(WorkshopQueueOperationResult result, bool clearName = false)
    {
        setWorkshopStatus(result.Message);
        if (!result.Success)
            return;

        if (result.QueueId != null)
            selectedFrozenQueueId = result.QueueId;

        if (clearName)
            frozenQueueNameInput = string.Empty;

        config.Save();
    }

    private void DrawQueueTable(IReadOnlyList<WorkshopProjectDefinition> projects)
    {
        var projectNames = projects.ToDictionary(x => x.WorkshopItemId, x => x.Name);
        if (ImGui.BeginTable("WorkshopPrepQueue", 3, ImGuiUi.InteractiveTableFlags))
        {
            ImGui.TableSetupColumn("Project", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 96);
            ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 104);
            ImGui.TableHeadersRow();

            if (config.WorkshopPrepQueue.Count == 0)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, "No workshop projects queued.");
                ImGui.TableNextColumn();
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, "-");
                ImGui.TableNextColumn();
                if (ImGuiUi.Button("Add##workshopQueueEmptyAdd", CanEditQueue))
                    openProjectBrowser();
            }

            for (var index = 0; index < config.WorkshopPrepQueue.Count; index++)
            {
                var item = config.WorkshopPrepQueue[index];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(projectNames.TryGetValue(item.WorkshopItemId, out var name)
                    ? name
                    : $"Unknown project {item.WorkshopItemId}");

                ImGui.TableNextColumn();
                var quantity = item.Quantity;
                ImGui.SetNextItemWidth(80);
                if (!CanEditQueue)
                    ImGui.BeginDisabled();

                if (ImGui.InputInt($"##workshopQueueQty{index}", ref quantity))
                {
                    item.Quantity = Math.Max(1, quantity);
                    SaveActiveQueueEdit();
                }

                if (!CanEditQueue)
                    ImGui.EndDisabled();

                ImGui.TableNextColumn();
                if (ImGuiUi.Button($"Remove##workshopQueueRemove{index}", CanEditQueue))
                {
                    config.WorkshopPrepQueue.RemoveAt(index);
                    SaveActiveQueueEdit();
                    setWorkshopStatus("Removed project from workshop prep queue.");
                    index--;
                }
            }

            ImGui.EndTable();
        }
    }

    private void SaveActiveQueueEdit()
    {
        WorkshopQueueService.MarkActiveQueueEdited(config);
        config.Save();
    }

    private void CopyWorkshopArtisanManifest()
    {
        CopyWorkshopManifest(workshopMaterialManifestExport.ExportArtisanManifest(
            config.WorkshopPrepQueue,
            workshopCatalog.GetProjects(),
            getWorkshopAvailability(),
            WorkshopMaterialManifestQuantityMode.InventoryMissing,
            DateTime.UtcNow));
    }

    private void CopyWorkshopCraftArchitectPlan()
    {
        CopyWorkshopManifest(WorkshopMaterialManifestExportService.ExportCraftArchitectPlan(
            config.WorkshopPrepQueue,
            workshopCatalog.GetProjects(),
            getWorkshopAvailability(),
            WorkshopMaterialManifestQuantityMode.InventoryMissing,
            DateTime.UtcNow));
    }

    private void CopyWorkshopManifest(WorkshopMaterialManifestExportResult result)
    {
        if (result.Success && !string.IsNullOrWhiteSpace(result.Content))
            ImGui.SetClipboardText(result.Content);

        setWorkshopStatus(result.Message);
        if (result.Severity is WorkshopMaterialManifestExportSeverity.Error or WorkshopMaterialManifestExportSeverity.Warning)
            log.Warning($"[MarketMafioso] {result.Message}");
    }
}
