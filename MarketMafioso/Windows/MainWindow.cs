using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Configuration config;
    private readonly HttpReporter reporter;
    private readonly InventoryScanner scanner;
    private readonly AutoRetainerRefreshService autoRetainerRefresh;
    private readonly WorkshopProjectCatalog workshopCatalog;
    private readonly VIWIWorkshoppaIpc viwiWorkshoppaIpc;
    private readonly WorkshopRetainerRestockService workshopRetainerRestock;
    private readonly WorkshopAssemblyRunner workshopAssemblyRunner;
    private readonly WorkshopMaterialManifestExportService workshopMaterialManifestExport;
    private readonly IPluginLog log;

    private string urlBuffer = string.Empty;
    private string apiKeyBuffer = string.Empty;
    private string dashboardUrlBuffer = string.Empty;
    private string dashboardOpenStatus = "Dashboard link appears after a successful send.";
    private bool showApiKey = false;
    private bool showPreview = false;
    private readonly WorkshopProjectSelectionState workshopProjectSelection = new();
    private bool confirmViwiClear = false;
    private bool confirmNewWorkshopQueue = false;
    private bool confirmLoadFrozenQueue = false;
    private Guid? selectedFrozenQueueId;
    private string frozenQueueNameInput = string.Empty;
    private string workshopStatus = "Workshop prep queue is idle.";

    private const string ProductSummary = "Small, practical FFXIV improvements under one roof.";
    private const string InventoryModuleSummary = "Inventory Reporter exports character and retainer inventory snapshots as JSON.";
    private const string WorkshopLogisticsModuleSummary = "Workshop Logistics tracks company workshop jobs, materials, retainer restock, handoff, and assembly.";
    private const string LocalReceiverUrl = "http://localhost:8080/inventory";
    private const string DevReceiverUrl = "https://dev.xivcraftarchitect.com/api/marketmafioso/inventory";
    private const string ProductionReceiverUrl = "https://xivcraftarchitect.com/api/marketmafioso/inventory";

    private static readonly Vector4 ColHeader = new(0.38f, 0.73f, 1.00f, 1f);
    private static readonly Vector4 ColSuccess = new(0.45f, 0.90f, 0.55f, 1f);
    private static readonly Vector4 ColError = new(1.00f, 0.40f, 0.40f, 1f);
    private static readonly Vector4 ColMuted = new(0.60f, 0.60f, 0.60f, 1f);

    public MainWindow(
        Configuration config,
        HttpReporter reporter,
        InventoryScanner scanner,
        AutoRetainerRefreshService autoRetainerRefresh,
        WorkshopProjectCatalog workshopCatalog,
        VIWIWorkshoppaIpc viwiWorkshoppaIpc,
        WorkshopRetainerRestockService workshopRetainerRestock,
        WorkshopAssemblyRunner workshopAssemblyRunner,
        WorkshopMaterialManifestExportService workshopMaterialManifestExport,
        IPluginLog log)
        : base("MarketMafioso##MarketMafiosoMainWindow",
               ImGuiWindowFlags.None)
    {
        this.config = config;
        this.reporter = reporter;
        this.scanner = scanner;
        this.autoRetainerRefresh = autoRetainerRefresh;
        this.workshopCatalog = workshopCatalog;
        this.viwiWorkshoppaIpc = viwiWorkshoppaIpc;
        this.workshopRetainerRestock = workshopRetainerRestock;
        this.workshopAssemblyRunner = workshopAssemblyRunner;
        this.workshopMaterialManifestExport = workshopMaterialManifestExport;
        this.log = log;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(980, 560),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        urlBuffer = config.ServerUrl;
        apiKeyBuffer = config.ApiKey;
        ProjectBrowser = new WorkshopProjectBrowserWindow(
            config,
            workshopCatalog,
            workshopProjectSelection,
            AddWorkshopProject);
        FrozenQueueBrowser = new WorkshopFrozenQueueBrowserWindow(
            config,
            workshopCatalog,
            new WorkshopFrozenQueueBrowserActions(
                () => !workshopAssemblyRunner.HasActiveRun,
                LoadFrozenQueue,
                OverwriteFrozenQueueWithCurrent,
                RenameFrozenQueue,
                DuplicateFrozenQueue,
                DeleteFrozenQueue,
                SaveCurrentQueueAsNew));
    }

    public WorkshopProjectBrowserWindow ProjectBrowser { get; }
    public WorkshopFrozenQueueBrowserWindow FrozenQueueBrowser { get; }

    public override void Draw()
    {
        DrawHeader();
        ImGui.Spacing();

        if (ImGui.BeginTabBar("##MarketMafiosoTabs"))
        {
            if (ImGui.BeginTabItem("Overview"))
            {
                DrawOverviewTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Inventory Reporter"))
            {
                DrawInventoryReporterTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Workshop Logistics"))
            {
                DrawWorkshopPrepTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Status"))
            {
                DrawStatusTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawHeader()
    {
        ImGui.TextColored(ColHeader, "MarketMafioso");
        ImGui.TextWrapped(ProductSummary);
        ImGui.TextColored(ColMuted, "Current modules: Inventory Reporter, Workshop Logistics");
    }

    private void DrawOverviewTab()
    {
        ImGui.Spacing();
        ImGui.TextColored(ColHeader, "Modules");
        ImGui.Separator();

        DrawModuleSummary("Inventory Reporter", "Enabled", InventoryModuleSummary);
        DrawModuleSummary("Workshop Logistics", "Enabled", WorkshopLogisticsModuleSummary);
        DrawModuleSummary("Market Tools", "Planned", "Future market-board helpers will build on captured inventory and item data.");
        DrawModuleSummary("General Improvements", "Planned", "Small quality-of-life tools that are useful, but too narrow for their own plugin.");
    }

    private void DrawInventoryReporterTab()
    {
        ImGui.Spacing();
        ImGui.TextColored(ColHeader, "Inventory Reporter");
        ImGui.TextWrapped(InventoryModuleSummary);
        ImGui.Spacing();

        DrawServerSection();
        ImGui.Spacing();
        DrawInventoryOptionsSection();
        ImGui.Spacing();
        DrawBehaviourSection();
        ImGui.Spacing();
        DrawActionsSection();

        if (showPreview)
        {
            ImGui.Separator();
            DrawJsonPreview();
        }
    }

    private void DrawWorkshopPrepTab()
    {
        ImGui.Spacing();
        ImGui.TextColored(ColHeader, "Workshop Logistics");
        ImGui.TextWrapped(WorkshopLogisticsModuleSummary);
        ImGui.Spacing();

        var projects = workshopCatalog.GetProjects();

        DrawWorkshopPrepQueue(projects);
        ImGui.Spacing();
        DrawWorkshopMaterialSummary();
        ImGui.Spacing();
        DrawWorkshopAssemblyWorkflow();
    }

    private void DrawWorkshopPrepQueue(IReadOnlyList<WorkshopProjectDefinition> projects)
    {
        ImGuiUi.SectionHeaderWithActions("Prep Queue", ColHeader, DrawWorkshopQueueHeaderActions, 180);
        DrawFrozenQueueToolbar();
        ImGui.Spacing();

        if (projects.Count == 0)
        {
            ImGui.TextColored(ColMuted, "No company workshop projects were found.");
            return;
        }

        DrawWorkshopQueueTable(projects);
    }

    private void AddWorkshopProject(uint workshopItemId)
    {
        if (workshopAssemblyRunner.HasActiveRun)
        {
            workshopStatus = "Cannot edit prep queue while workshop assembly is active.";
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
        workshopStatus = "Added project to workshop prep queue.";
    }

    private void DrawWorkshopQueueHeaderActions()
    {
        var hasPrepQueue = config.WorkshopPrepQueue.Count > 0;
        var canEditQueue = !workshopAssemblyRunner.HasActiveRun;

        if (ImGuiUi.MenuButton("Handoff"))
            ImGui.OpenPopup("WorkshopQueueHandoffMenu");

        if (ImGui.BeginPopup("WorkshopQueueHandoffMenu"))
        {
            if (ImGuiUi.MenuItem("Send to VIWI", hasPrepQueue && canEditQueue))
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
        var canEditQueue = !workshopAssemblyRunner.HasActiveRun;
        var activeFrozenQueue = config.ActiveFrozenWorkshopQueueId == null
            ? null
            : config.FrozenWorkshopQueues.FirstOrDefault(x => x.Id == config.ActiveFrozenWorkshopQueueId.Value);

        var activeFrozenQueueLabel = activeFrozenQueue == null
            ? "Active queue: unsaved"
            : WorkshopQueueService.ActiveQueueMatchesFrozenQueue(config)
                ? $"Active saved job: {activeFrozenQueue.Name}"
                : $"Active saved job: {activeFrozenQueue.Name} (modified)";
        ImGui.TextColored(ColMuted, activeFrozenQueueLabel);

        var commandWidth = 720f;
        var nameWidth = Math.Max(220f, ImGui.GetContentRegionAvail().X - commandWidth);
        ImGui.SetNextItemWidth(nameWidth);
        ImGui.InputText("##workshopFrozenQueueName", ref frozenQueueNameInput, 128);

        ImGui.SameLine();
        if (ImGuiUi.Button("Save Queue", canEditQueue && config.WorkshopPrepQueue.Count > 0))
        {
            var createsFrozenQueue = config.ActiveFrozenWorkshopQueueId == null;
            ApplyFrozenQueueResult(
                WorkshopQueueService.SaveActiveQueue(config, frozenQueueNameInput, DateTime.UtcNow),
                clearName: createsFrozenQueue);
        }

        ImGui.SameLine();
        if (ImGuiUi.Button("Save As...", canEditQueue && config.WorkshopPrepQueue.Count > 0))
            ApplyFrozenQueueResult(WorkshopQueueService.FreezeCurrentQueue(config, frozenQueueNameInput, DateTime.UtcNow), clearName: true);

        ImGui.SameLine();
        if (ImGuiUi.Button("New Queue", canEditQueue))
        {
            if (config.WorkshopPrepQueue.Count > 0)
                confirmNewWorkshopQueue = true;
            else
                StartNewWorkshopQueue();
        }

        ImGui.SameLine();
        if (ImGuiUi.Button("Add Project...", canEditQueue))
            ProjectBrowser.IsOpen = true;

        ImGui.SameLine();
        DrawFrozenQueueLoadCombo(canEditQueue);

        ImGui.SameLine();
        if (ImGui.Button("Manage Saved Jobs"))
            FrozenQueueBrowser.IsOpen = true;

        ImGui.TextColored(ColMuted, "Handoff contains VIWI and future queue targets. Export contains Artisan JSON and Craft Architect .craftplan JSON.");

        DrawFrozenQueueConfirmations(canEditQueue);
    }

    private void DrawFrozenQueueLoadCombo(bool canEditQueue)
    {
        var canLoad = canEditQueue && config.FrozenWorkshopQueues.Count > 0;
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

    private void DrawFrozenQueueConfirmations(bool canEditQueue)
    {
        if (confirmNewWorkshopQueue)
        {
            ImGui.TextColored(ColMuted, "Start a new queue? Unsaved active queue changes will be discarded.");
            if (ImGuiUi.Button("Confirm New Queue", canEditQueue))
                StartNewWorkshopQueue();

            ImGui.SameLine();
            if (ImGui.Button("Cancel New Queue"))
                confirmNewWorkshopQueue = false;
        }

        if (confirmLoadFrozenQueue)
        {
            ImGui.TextColored(ColMuted, "Load saved job? Unsaved active queue changes will be discarded.");
            if (ImGuiUi.Button("Confirm Load Saved Job", canEditQueue && selectedFrozenQueueId != null))
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

    private void LoadFrozenQueue(Guid queueId)
    {
        selectedFrozenQueueId = queueId;
        ApplyFrozenQueueResult(WorkshopQueueService.LoadFrozenQueue(config, queueId));
    }

    private void DeleteFrozenQueue(Guid queueId)
    {
        var result = WorkshopQueueService.DeleteFrozenQueue(config, queueId);
        if (result.Success)
            selectedFrozenQueueId = config.FrozenWorkshopQueues.FirstOrDefault()?.Id;

        ApplyFrozenQueueResult(result);
    }

    private void OverwriteFrozenQueueWithCurrent(Guid queueId)
    {
        selectedFrozenQueueId = queueId;
        ApplyFrozenQueueResult(WorkshopQueueService.OverwriteFrozenQueue(config, queueId, DateTime.UtcNow));
    }

    private void RenameFrozenQueue(Guid queueId, string name)
    {
        selectedFrozenQueueId = queueId;
        ApplyFrozenQueueResult(WorkshopQueueService.RenameFrozenQueue(config, queueId, name, DateTime.UtcNow));
    }

    private void DuplicateFrozenQueue(Guid queueId, string name)
    {
        selectedFrozenQueueId = queueId;
        ApplyFrozenQueueResult(WorkshopQueueService.DuplicateFrozenQueue(config, queueId, name, DateTime.UtcNow));
    }

    private void SaveCurrentQueueAsNew(string name)
    {
        ApplyFrozenQueueResult(WorkshopQueueService.FreezeCurrentQueue(config, name, DateTime.UtcNow), clearName: false);
    }

    private void StartNewWorkshopQueue()
    {
        WorkshopQueueService.NewActiveQueue(config);
        config.Save();
        confirmNewWorkshopQueue = false;
        workshopStatus = "Started a new workshop prep queue.";
    }

    private void ApplyFrozenQueueResult(WorkshopQueueOperationResult result, bool clearName = false)
    {
        workshopStatus = result.Message;
        if (!result.Success)
            return;

        if (result.QueueId != null)
            selectedFrozenQueueId = result.QueueId;

        if (clearName)
            frozenQueueNameInput = string.Empty;

        config.Save();
    }

    private void DrawWorkshopQueueTable(IReadOnlyList<WorkshopProjectDefinition> projects)
    {
        var projectNames = projects.ToDictionary(x => x.WorkshopItemId, x => x.Name);
        var canEditQueue = !workshopAssemblyRunner.HasActiveRun;
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
                ImGui.TextColored(ColMuted, "No workshop projects queued.");
                ImGui.TableNextColumn();
                ImGui.TextColored(ColMuted, "-");
                ImGui.TableNextColumn();
                if (ImGuiUi.Button("Add##workshopQueueEmptyAdd", canEditQueue))
                    ProjectBrowser.IsOpen = true;
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
                if (!canEditQueue)
                    ImGui.BeginDisabled();

                if (ImGui.InputInt($"##workshopQueueQty{index}", ref quantity))
                {
                    item.Quantity = Math.Max(1, quantity);
                    SaveActiveQueueEdit();
                }

                if (!canEditQueue)
                    ImGui.EndDisabled();

                ImGui.TableNextColumn();
                if (ImGuiUi.Button($"Remove##workshopQueueRemove{index}", canEditQueue))
                {
                    config.WorkshopPrepQueue.RemoveAt(index);
                    SaveActiveQueueEdit();
                    workshopStatus = "Removed project from workshop prep queue.";
                    index--;
                }
            }

            ImGui.EndTable();
        }
    }

    private void DrawWorkshopMaterialSummary()
    {
        ImGuiUi.SectionHeaderWithActions("Materials", ColHeader, DrawWorkshopMaterialHeaderActions, 420);

        var availability = GetWorkshopAvailability();
        if (ImGui.BeginTable("WorkshopPrepMaterials", 7, ImGuiUi.InteractiveTableFlags))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Required", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Stock Differential", ImGuiTableColumnFlags.WidthFixed, 128);
            ImGui.TableSetupColumn("Inventory Missing", ImGuiTableColumnFlags.WidthFixed, 128);
            ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthFixed, 72);
            ImGui.TableSetupColumn("Retainers", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultHide, 88);
            ImGui.TableSetupColumn("Candidates", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultHide);
            ImGui.TableHeadersRow();

            if (availability.Count == 0)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(ColMuted, "No workshop materials yet. Add projects to the prep queue.");
                ImGui.TableNextColumn();
                ImGui.TextColored(ColMuted, "-");
                ImGui.TableNextColumn();
                ImGui.TextColored(ColMuted, "-");
                ImGui.TableNextColumn();
                ImGui.TextColored(ColMuted, "-");
                ImGui.TableNextColumn();
                ImGui.TextColored(ColMuted, "-");
                ImGui.TableNextColumn();
                ImGui.TextColored(ColMuted, "-");
                ImGui.TableNextColumn();
                ImGui.TextColored(ColMuted, "-");
            }

            foreach (var item in availability)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.ItemName);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.Required.ToString());
                ImGui.TableNextColumn();
                ImGui.TextColored(item.StockDifferential < 0 ? ColError : ColSuccess, FormatSignedQuantity(item.StockDifferential));
                ImGui.TableNextColumn();
                ImGui.TextColored(item.Shortage > 0 ? ColError : ColSuccess, item.Shortage.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.PlayerInventory.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.RetainerCache.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.Join(", ", item.CandidateRetainers.Select(x => x.RetainerName)));
            }

            ImGui.EndTable();
        }
    }

    private void DrawWorkshopMaterialHeaderActions()
    {
        var canRefreshRetainers = autoRetainerRefresh.CanStartRefresh &&
                                  !autoRetainerRefresh.IsRefreshing &&
                                  !autoRetainerRefresh.IsStartQueued;

        if (ImGuiUi.Button("Refresh Retainer Cache", canRefreshRetainers))
            autoRetainerRefresh.StartFullRefresh();

        ImGui.SameLine();
        if (ImGuiUi.Button("Restock From Retainers", !workshopRetainerRestock.IsRunning))
            _ = workshopRetainerRestock.StartAsync(GetWorkshopAvailability());

        ImGui.SameLine();
        if (ImGuiUi.MenuButton("Columns"))
            ImGui.OpenPopup("WorkshopMaterialColumnsMenu");

        if (ImGui.BeginPopup("WorkshopMaterialColumnsMenu"))
        {
            ImGui.TextColored(ColMuted, "Use table header context menu to hide columns.");
            ImGui.EndPopup();
        }
    }

    private void SaveActiveQueueEdit()
    {
        WorkshopQueueService.MarkActiveQueueEdited(config);
        config.Save();
    }

    private static string FormatSignedQuantity(int value)
    {
        return value > 0
            ? $"+{value}"
            : value.ToString();
    }

    private IReadOnlyList<WorkshopMaterialAvailability> GetWorkshopAvailability()
    {
        if (config.WorkshopPrepQueue.Count == 0)
            return [];

        var requirements = workshopCatalog.BuildRequirements(config.WorkshopPrepQueue);
        var playerInventory = scanner.CountPlayerInventory(config);
        return WorkshopMaterialAvailabilityService.BuildAvailability(requirements, playerInventory, config);
    }

    private void DrawWorkshopAssemblyWorkflow()
    {
        var hasPrepQueue = config.WorkshopPrepQueue.Count > 0;
        var actionWidth = workshopAssemblyRunner.HasActiveRun ? 280f : 140f;
        ImGuiUi.SectionHeaderWithActions(
            "Assembly Workflow",
            ColHeader,
            () => DrawWorkshopAssemblyActions(hasPrepQueue),
            actionWidth);

        ImGui.TextColored(GetWorkshopStatusColor(), workshopStatus);
        ImGui.TextColored(workshopRetainerRestock.IsRunning ? ColHeader : ColMuted, workshopRetainerRestock.LastStatus);

        var progress = workshopAssemblyRunner.Progress;
        ImGui.TextColored(workshopAssemblyRunner.HasActiveRun ? ColHeader : ColMuted, progress.Message);
        if (progress.TotalProjects > 0)
        {
            var completed = Math.Clamp(progress.CompletedProjects, 0, progress.TotalProjects);
            var fraction = completed / (float)progress.TotalProjects;
            ImGui.TextColored(ColMuted, $"Assembly progress: {completed}/{progress.TotalProjects}");
            ImGui.SameLine();
            ImGui.ProgressBar(fraction, new Vector2(210, 0), string.Empty);
        }

        DrawWorkshopQueueConfirmations();
    }

    private void DrawWorkshopAssemblyActions(bool hasPrepQueue)
    {
        if (workshopAssemblyRunner.IsPaused)
        {
            if (ImGui.Button("Resume"))
                workshopStatus = workshopAssemblyRunner.Resume().Message;

            ImGui.SameLine();
            if (ImGui.Button("Stop"))
            {
                workshopAssemblyRunner.Stop();
                workshopStatus = "Workshop assembly stopped.";
            }
        }
        else if (workshopAssemblyRunner.IsRunning)
        {
            if (ImGui.Button("Pause"))
                workshopStatus = workshopAssemblyRunner.Pause().Message;

            ImGui.SameLine();
            if (ImGui.Button("Stop"))
            {
                workshopAssemblyRunner.Stop();
                workshopStatus = "Workshop assembly stopped.";
            }
        }

        if (workshopAssemblyRunner.HasActiveRun)
            ImGui.SameLine();

        if (ImGuiUi.MenuButton("Start Options", !workshopAssemblyRunner.HasActiveRun && hasPrepQueue))
            ImGui.OpenPopup("WorkshopAssemblyStartMenu");

        if (ImGui.BeginPopup("WorkshopAssemblyStartMenu"))
        {
            if (ImGuiUi.MenuItem("Start Assembly", hasPrepQueue))
                StartWorkshopAssembly(enableDiagnostics: false);

            if (ImGuiUi.MenuItem("Start With Diagnostics", hasPrepQueue))
                StartWorkshopAssembly(enableDiagnostics: true);

            ImGui.EndPopup();
        }
    }

    private void DrawWorkshopQueueConfirmations()
    {
        var hasPrepQueue = config.WorkshopPrepQueue.Count > 0;
        var canEditQueue = !workshopAssemblyRunner.HasActiveRun;

        if (config.WorkshopPrepQueue.Count == 0)
            confirmViwiClear = false;

        if (!confirmViwiClear)
            return;

        ImGui.TextColored(ColMuted, "This will clear VIWI Workshoppa's queue and send the MarketMafioso prep queue.");

        if (ImGuiUi.Button("Confirm VIWI Queue Sync", hasPrepQueue && canEditQueue))
        {
            var result = viwiWorkshoppaIpc.SendQueue(config.WorkshopPrepQueue, clearExisting: true);
            workshopStatus = result.Message;
            confirmViwiClear = false;
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel VIWI Queue Sync"))
            confirmViwiClear = false;
    }

    private void StartWorkshopAssembly(bool enableDiagnostics)
    {
        try
        {
            var preflight = WorkshopAssemblyPreflightService.Check(
                config.WorkshopPrepQueue,
                workshopCatalog.GetProjects(),
                scanner.CountPlayerInventory(config));
            if (!preflight.CanStart || preflight.Plan == null)
            {
                workshopStatus = preflight.Message;
                return;
            }

            var result = workshopAssemblyRunner.Start(preflight.Plan, enableDiagnostics);
            workshopStatus = result.Message;
            if (enableDiagnostics && workshopAssemblyRunner.LastDiagnosticFilePath != null)
                workshopStatus = $"{workshopStatus} Diagnostics: {workshopAssemblyRunner.LastDiagnosticFilePath}";
        }
        catch (Exception ex)
        {
            workshopStatus = $"Unable to start workshop assembly. {ex.Message}";
            log.Warning(ex, "[MarketMafioso] Native workshop assembly preflight failed.");
        }
    }

    private Vector4 GetWorkshopStatusColor()
    {
        if (workshopStatus.Contains("unable", StringComparison.OrdinalIgnoreCase) ||
            workshopStatus.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            workshopStatus.Contains("not available", StringComparison.OrdinalIgnoreCase))
            return ColError;

        if (workshopStatus.Contains("copied", StringComparison.OrdinalIgnoreCase) ||
            workshopStatus.Contains("sent", StringComparison.OrdinalIgnoreCase) ||
            workshopStatus.Contains("added", StringComparison.OrdinalIgnoreCase) ||
            workshopStatus.Contains("cleared", StringComparison.OrdinalIgnoreCase) ||
            workshopStatus.Contains("removed", StringComparison.OrdinalIgnoreCase))
            return ColSuccess;

        return ColMuted;
    }

    private void CopyWorkshopArtisanManifest()
    {
        CopyWorkshopManifest(workshopMaterialManifestExport.ExportArtisanManifest(
            config.WorkshopPrepQueue,
            workshopCatalog.GetProjects(),
            GetWorkshopAvailability(),
            WorkshopMaterialManifestQuantityMode.InventoryMissing,
            DateTime.UtcNow));
    }

    private void CopyWorkshopCraftArchitectPlan()
    {
        CopyWorkshopManifest(WorkshopMaterialManifestExportService.ExportCraftArchitectPlan(
            config.WorkshopPrepQueue,
            workshopCatalog.GetProjects(),
            GetWorkshopAvailability(),
            WorkshopMaterialManifestQuantityMode.InventoryMissing,
            DateTime.UtcNow));
    }

    private void CopyWorkshopManifest(WorkshopMaterialManifestExportResult result)
    {
        if (result.Success && !string.IsNullOrWhiteSpace(result.Content))
            ImGui.SetClipboardText(result.Content);

        workshopStatus = result.Message;
        if (result.Severity is WorkshopMaterialManifestExportSeverity.Error or WorkshopMaterialManifestExportSeverity.Warning)
            log.Warning($"[MarketMafioso] {result.Message}");
    }

    private void DrawStatusTab()
    {
        ImGui.Spacing();
        DrawStatusSection();
        ImGui.Spacing();
        DrawRetainerCacheSection();
    }

    private void DrawModuleSummary(string name, string state, string description)
    {
        ImGui.BulletText(name);
        ImGui.SameLine();
        ImGui.TextColored(state == "Enabled" ? ColSuccess : ColMuted, $"({state})");
        ImGui.TextWrapped(description);
        ImGui.Spacing();
    }

    private void DrawServerSection()
    {
        ImGui.TextColored(ColHeader, "Export Endpoint");
        ImGui.Separator();

        ImGui.Text("Server URL:");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##url", ref urlBuffer, 512))
        {
            config.ServerUrl = urlBuffer;
            config.Save();
        }

        if (ImGui.Button("Local Receiver"))
            ApplyServerUrlPreset(LocalReceiverUrl);
        ImGui.SameLine();
        if (ImGui.Button("Dev VPS"))
            ApplyServerUrlPreset(DevReceiverUrl);
        ImGui.SameLine();
        ImGui.BeginDisabled();
        ImGui.Button("Production VPS (future)");
        ImGui.EndDisabled();

        var endpoint = ReceiverEndpointClassifier.Classify(urlBuffer);
        var requiresApiKey = endpoint.RequiresApiKey;
        ImGui.Text(requiresApiKey
            ? "API Key (required for this endpoint):"
            : "API Key (optional - sent as X-Api-Key header):");
        var keyWidth = ImGui.GetContentRegionAvail().X - 70;
        ImGui.SetNextItemWidth(keyWidth);
        var flags = showApiKey ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password;
        if (ImGui.InputText("##apikey", ref apiKeyBuffer, 256, flags))
        {
            config.ApiKey = apiKeyBuffer;
            config.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button(showApiKey ? "Hide##k" : "Show##k", new Vector2(60, 0)))
            showApiKey = !showApiKey;

        if (endpoint.Kind == ReceiverEndpointKind.Invalid)
            ImGui.TextColored(ColError, "Enter a valid HTTP or HTTPS receiver URL.");
        else if (requiresApiKey && string.IsNullOrWhiteSpace(apiKeyBuffer))
            ImGui.TextColored(ColError, "This endpoint requires an API key before reports can be sent.");
        else if (endpoint.Kind == ReceiverEndpointKind.CustomRemote)
            ImGui.TextColored(ColMuted, "Custom remote endpoint. API key is required by default.");

        ImGui.Spacing();
        DrawDashboardOpenSection();
    }

    private void DrawDashboardOpenSection()
    {
        var dashboardUrl = HttpReporter.ResolveDashboardUrlForDisplay(reporter.LastDashboardUrl, urlBuffer) ?? string.Empty;
        if (!string.Equals(dashboardUrlBuffer, dashboardUrl, StringComparison.Ordinal))
            dashboardUrlBuffer = dashboardUrl;

        ImGui.Text("Dashboard URL:");
        var buttonWidth = 128f;
        var inputWidth = Math.Max(120f, ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X);
        ImGui.SetNextItemWidth(inputWidth);
        ImGui.InputText("##dashboardUrl", ref dashboardUrlBuffer, 1024, ImGuiInputTextFlags.ReadOnly);
        ImGui.SameLine();
        if (ImGuiUi.Button("Open Dashboard", new Vector2(buttonWidth, 0), !string.IsNullOrWhiteSpace(dashboardUrl)))
            OpenDashboardUrl(dashboardUrl);

        var status = string.IsNullOrWhiteSpace(dashboardUrl)
            ? dashboardOpenStatus
            : string.IsNullOrWhiteSpace(reporter.LastDashboardUrl)
                ? "Dashboard link derived from endpoint."
                : dashboardOpenStatus;
        ImGui.TextColored(GetDashboardOpenStatusColor(status), status);
    }

    private void OpenDashboardUrl(string dashboardUrl)
    {
        if (!Uri.TryCreate(dashboardUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            dashboardOpenStatus = "Dashboard URL is not a valid HTTP or HTTPS link.";
            log.Warning($"[MarketMafioso] Refusing to open invalid dashboard URL: {dashboardUrl}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.ToString())
            {
                UseShellExecute = true,
            });
            dashboardOpenStatus = "Opened dashboard in external browser.";
        }
        catch (Exception ex)
        {
            dashboardOpenStatus = $"Unable to open dashboard. {ex.Message}";
            log.Error(ex, "[MarketMafioso] Unable to open dashboard URL.");
        }
    }

    private static Vector4 GetDashboardOpenStatusColor(string status) =>
        status.StartsWith("Unable", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("not a valid", StringComparison.OrdinalIgnoreCase)
            ? ColError
            : ColMuted;

    private void ApplyServerUrlPreset(string serverUrl)
    {
        urlBuffer = serverUrl;
        config.ServerUrl = serverUrl;
        config.Save();
    }

    private void DrawInventoryOptionsSection()
    {
        ImGui.TextColored(ColHeader, "Included Data");
        ImGui.Separator();

        ImGui.TextColored(ColMuted, "Player inventory (4 bags) is always included.");
        ImGui.Spacing();

        DrawCheckbox("Armoury Chest", v => config.IncludeArmoury = v, config.IncludeArmoury);
        DrawCheckbox("Crystal bag", v => config.IncludeCrystals = v, config.IncludeCrystals);
        DrawCheckbox("Equipped gear", v => config.IncludeEquipped = v, config.IncludeEquipped);
        DrawCheckbox("Saddlebag (if subscribed)", v => config.IncludeSaddlebag = v, config.IncludeSaddlebag);
        ImGui.Spacing();
        DrawCheckbox("Resolve item names via Lumina", v => config.IncludeItemNames = v, config.IncludeItemNames);
        DrawCheckbox("Include character name & world", v => config.IncludeCharacterInfo = v, config.IncludeCharacterInfo);
    }

    private void DrawBehaviourSection()
    {
        ImGui.TextColored(ColHeader, "Automation");
        ImGui.Separator();

        DrawCheckbox("Auto-send on retainer window close", v => config.AutoSendOnRetainerClose = v, config.AutoSendOnRetainerClose);
        ImGui.TextColored(ColMuted,
            "  Retainer data is cached each time you close a retainer window.\n" +
            "  Visit each retainer once per session to populate the cache.");

        ImGui.Spacing();

        DrawCheckbox("Enable automatic periodic sending", v =>
        {
            config.EnableAutoSendTimer = v;
            Plugin.Instance.RestartTimer();
        }, config.EnableAutoSendTimer);

        if (config.EnableAutoSendTimer)
        {
            var interval = config.AutoSendIntervalMinutes;
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputInt("Send Interval (minutes)##interval", ref interval, 1, 5))
            {
                if (interval < 1) interval = 1;
                if (interval != config.AutoSendIntervalMinutes)
                {
                    config.AutoSendIntervalMinutes = interval;
                    config.Save();
                    Plugin.Instance.RestartTimer();
                }
            }
        }
    }

    private void DrawRetainerCacheSection()
    {
        ImGui.TextColored(ColHeader, "Retainer Cache");
        ImGui.Separator();

        if (config.RetainerCache.Count == 0)
        {
            ImGui.TextColored(ColMuted, "No retainers cached. Open a retainer inventory to populate.");
        }
        else
        {
            foreach (var (_, cached) in config.RetainerCache)
            {
                var total = cached.Bags.Sum(b => b.Items.Count);
                ImGui.BulletText(
                    $"{cached.RetainerName}  -  {total} items  (last seen {cached.LastUpdated:HH:mm:ss UTC})");
            }

            ImGui.Spacing();
            if (ImGui.Button("Clear Retainer Cache"))
            {
                config.RetainerCache.Clear();
                config.Save();
            }
        }
    }

    private void DrawActionsSection()
    {
        ImGui.TextColored(ColHeader, "Inventory Reporter Actions");
        ImGui.Separator();

        var third = (ImGui.GetContentRegionAvail().X - 2 * ImGui.GetStyle().ItemSpacing.X) / 3f;

        if (ImGui.Button("Send Report Now", new Vector2(third, 0)))
            _ = reporter.SendReportAsync();

        ImGui.SameLine();

        var canRefreshRetainers = autoRetainerRefresh.CanStartRefresh &&
                                  !autoRetainerRefresh.IsRefreshing &&
                                  !autoRetainerRefresh.IsStartQueued;
        if (!canRefreshRetainers)
            ImGui.BeginDisabled();

        if (ImGui.Button("Refresh Retainer Cache", new Vector2(third, 0)))
            autoRetainerRefresh.StartFullRefresh();

        if (!canRefreshRetainers)
            ImGui.EndDisabled();

        ImGui.SameLine();

        var previewLabel = showPreview ? "Hide JSON Preview" : "Show JSON Preview";
        if (ImGui.Button(previewLabel, new Vector2(third, 0)))
            showPreview = !showPreview;

        ImGui.Spacing();
        ImGui.TextColored(GetRefreshStatusColor(), autoRetainerRefresh.LastStatus);
    }

    private Vector4 GetRefreshStatusColor()
    {
        if (autoRetainerRefresh.IsRefreshing)
            return ColHeader;

        if (autoRetainerRefresh.LastStatus.Contains("complete", StringComparison.OrdinalIgnoreCase))
            return ColSuccess;

        if (autoRetainerRefresh.LastStatus.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            autoRetainerRefresh.LastStatus.Contains("unable", StringComparison.OrdinalIgnoreCase) ||
            autoRetainerRefresh.LastStatus.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return ColError;

        return ColMuted;
    }

    private void DrawStatusSection()
    {
        ImGui.TextColored(ColHeader, "Module Status");
        ImGui.Separator();
        ImGui.TextColored(ColMuted, $"Build: {PluginBuildInfo.DisplayVersion}");
        ImGui.Spacing();

        if (reporter.LastSentAt.HasValue)
        {
            var statusOk = reporter.LastStatus.StartsWith("2");
            ImGui.TextColored(
                statusOk ? ColSuccess : ColError,
                $"Last sent: {reporter.LastSentAt:HH:mm:ss}  -  Status: {reporter.LastStatus}");
        }
        else
        {
            ImGui.TextColored(ColMuted, $"Status: {reporter.LastStatus}");
        }
    }

    private void DrawJsonPreview()
    {
        ImGui.TextColored(ColHeader, "JSON Preview (last payload)");
        ImGui.Separator();

        var json = reporter.LastPayload ?? "(No payload yet - press 'Send Report Now' first)";
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextMultiline(
            "##jsonPreview",
            ref json,
            Math.Max(json.Length + 1, 8192),
            new Vector2(-1, 240),
            ImGuiInputTextFlags.ReadOnly,
            (ImGui.ImGuiInputTextCallbackDelegate?)null);
    }


    private void DrawCheckbox(string label, Action<bool> setter, bool currentValue)
    {
        var v = currentValue;
        if (ImGui.Checkbox(label, ref v))
        {
            setter(v);
            config.Save();
        }
    }

    public void Dispose() { }
}
