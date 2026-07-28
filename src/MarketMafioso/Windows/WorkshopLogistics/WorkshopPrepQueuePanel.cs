using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.UI.Items;
using Franthropy.Dalamud.UI.Tables;
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
    private readonly Func<bool> tradeQueueHasItems;
    private readonly Func<IReadOnlyList<WorkshopMaterialAvailability>, string> replaceTradeQueue;
    private readonly IPluginLog log;
    private readonly DalamudTableProjection<WorkshopQueueTableRow> queueTable;
    private readonly DalamudItemAutocompleteState projectAutocomplete = new();
    private IReadOnlyList<DalamudItemOption> projectOptions = [];
    private int projectAddQuantity = 1;

    private bool confirmViwiClear = false;
    private bool confirmNewWorkshopQueue = false;
    private bool confirmLoadFrozenQueue = false;
    private bool confirmTradeQueueReplace = false;
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
        Func<bool> tradeQueueHasItems,
        Func<IReadOnlyList<WorkshopMaterialAvailability>, string> replaceTradeQueue,
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
        this.tradeQueueHasItems = tradeQueueHasItems ?? throw new ArgumentNullException(nameof(tradeQueueHasItems));
        this.replaceTradeQueue = replaceTradeQueue ?? throw new ArgumentNullException(nameof(replaceTradeQueue));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        queueTable = new DalamudTableProjection<WorkshopQueueTableRow>(
        [
            new(
                "Project",
                1f,
                row => row.ProjectName,
                Flags: ImGuiTableColumnFlags.WidthStretch,
                Draw: DrawQueueProject),
            new(
                "Qty",
                96f,
                row => row.Item?.Quantity.ToString("N0") ?? projectAddQuantity.ToString("N0"),
                row => row.Item?.Quantity ?? projectAddQuantity,
                Draw: DrawQueueQuantity,
                Alignment: DalamudTableCellAlignment.Right),
            new(
                "Actions",
                180f,
                row => row.IsEditor ? "Add or browse" : "Remove",
                Flags: ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort,
                Draw: DrawQueueActions),
        ]);
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

        projectOptions = projects
            .Select(project => new DalamudItemOption(project.WorkshopItemId, project.Name))
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(project => project.ItemId)
            .ToArray();
        DrawQueueTable(projects);
    }

    public void DrawConfirmations()
    {
        var hasPrepQueue = config.WorkshopPrepQueue.Count > 0;

        if (config.WorkshopPrepQueue.Count == 0)
        {
            confirmViwiClear = false;
            confirmTradeQueueReplace = false;
        }

        if (!confirmViwiClear)
        {
            if (confirmTradeQueueReplace)
            {
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Replace every existing Trade Queue item with available workshop materials?");
                if (ImGuiUi.Button("Confirm Trade Queue Replace", hasPrepQueue))
                {
                    setWorkshopStatus(replaceTradeQueue(getWorkshopAvailability()));
                    confirmTradeQueueReplace = false;
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel Trade Queue Replace"))
                    confirmTradeQueueReplace = false;
            }
            return;
        }

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

    public void AddWorkshopProject(uint workshopItemId) =>
        AddWorkshopProject(workshopItemId, workshopProjectSelection.Quantity);

    private void AddWorkshopProject(uint workshopItemId, int requestedQuantity)
    {
        if (workshopAssemblyRunner.HasActiveRun)
        {
            setWorkshopStatus("Cannot edit prep queue while workshop assembly is active.");
            return;
        }

        var existing = config.WorkshopPrepQueue.FirstOrDefault(x => x.WorkshopItemId == workshopItemId);
        var quantity = Math.Max(1, requestedQuantity);
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

            if (ImGuiUi.MenuItem("Replace Trade Queue with Available Materials", hasPrepQueue))
            {
                if (tradeQueueHasItems())
                    confirmTradeQueueReplace = true;
                else
                    setWorkshopStatus(replaceTradeQueue(getWorkshopAvailability()));
            }

            ImGui.EndPopup();
        }

        ImGui.SameLine();
        if (ImGuiUi.MenuButton("Export"))
            ImGui.OpenPopup("WorkshopQueueExportMenu");

        if (ImGui.BeginPopup("WorkshopQueueExportMenu"))
        {
            if (ImGuiUi.MenuItem("Copy Artisan Manifest", hasPrepQueue))
                CopyWorkshopArtisanManifest();

            if (ImGuiUi.MenuItem("Copy Artisan Manifest with Subcrafts", hasPrepQueue))
                CopyWorkshopArtisanManifestWithSubcrafts();

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

    private unsafe void DrawQueueTable(IReadOnlyList<WorkshopProjectDefinition> projects)
    {
        var projectNames = projects.ToDictionary(x => x.WorkshopItemId, x => x.Name);
        var rows = config.WorkshopPrepQueue
            .Select(item => new WorkshopQueueTableRow(
                item,
                projectNames.TryGetValue(item.WorkshopItemId, out var name)
                    ? name
                    : $"Unknown project {item.WorkshopItemId}"))
            .ToArray();
        if (!queueTable.Begin(
                "WorkshopPrepQueue",
                DalamudTableLayout.FitContent(ImGuiUi.InteractiveTableFlags)))
        {
            return;
        }

        foreach (var row in queueTable.Apply(rows, ImGui.TableGetSortSpecs()))
        {
            ImGui.PushID(checked((int)row.Item!.WorkshopItemId));
            queueTable.DrawRow(row);
            ImGui.PopID();
        }

        ImGui.PushID("WorkshopQueueAddRow");
        var accent = MarketMafiosoUiTheme.Header;
        queueTable.DrawRow(
            WorkshopQueueTableRow.Editor,
            new(accent.X, accent.Y, accent.Z, 0.10f),
            ImGui.GetFrameHeight() + 8f);
        ImGui.PopID();
        queueTable.End();
    }

    private void DrawQueueProject(WorkshopQueueTableRow row)
    {
        if (!row.IsEditor)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(row.ProjectName);
            return;
        }

        ImGui.BeginDisabled(!CanEditQueue);
        DalamudItemAutocompleteRenderer.DrawInline(
            "WorkshopProjectAdd",
            projectOptions,
            projectAutocomplete,
            MarketMafiosoUiTheme.Muted,
            MarketMafiosoUiTheme.Success,
            MarketMafiosoUiTheme.Error,
            "Search project...");
        ImGui.EndDisabled();
    }

    private void DrawQueueQuantity(WorkshopQueueTableRow row)
    {
        var quantity = row.Item?.Quantity ?? projectAddQuantity;
        ImGui.SetNextItemWidth(80);
        ImGui.BeginDisabled(!CanEditQueue);
        if (ImGui.InputInt(
                row.IsEditor ? "##workshopQueueAddQty" : "##workshopQueueQty",
                ref quantity))
        {
            quantity = Math.Max(1, quantity);
            if (row.IsEditor)
            {
                projectAddQuantity = quantity;
            }
            else
            {
                row.Item!.Quantity = quantity;
                SaveActiveQueueEdit();
            }
        }
        ImGui.EndDisabled();
    }

    private void DrawQueueActions(WorkshopQueueTableRow row)
    {
        if (row.IsEditor)
        {
            var canAdd = CanEditQueue && projectAutocomplete.SelectedItem is not null;
            if (ImGuiUi.PrimaryButton("Add", canAdd) &&
                projectAutocomplete.SelectedItem is { } selected)
            {
                AddWorkshopProject(selected.ItemId, projectAddQuantity);
                ClearProjectAddRow();
            }

            ImGui.SameLine();
            if (ImGuiUi.Button("Browse...", CanEditQueue))
                openProjectBrowser();
            return;
        }

        if (!ImGuiUi.Button("Remove##workshopQueueRemove", CanEditQueue))
            return;

        config.WorkshopPrepQueue.Remove(row.Item!);
        SaveActiveQueueEdit();
        setWorkshopStatus("Removed project from workshop prep queue.");
    }

    private void ClearProjectAddRow()
    {
        projectAutocomplete.SelectedItem = null;
        projectAutocomplete.SearchBuffer = string.Empty;
        projectAutocomplete.ResetSelection();
        projectAddQuantity = 1;
    }

    private sealed record WorkshopQueueTableRow(
        WorkshopPrepQueueItem? Item,
        string ProjectName,
        bool IsEditor = false)
    {
        public static WorkshopQueueTableRow Editor { get; } =
            new(null, "Add project", IsEditor: true);
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

    private void CopyWorkshopArtisanManifestWithSubcrafts()
    {
        CopyWorkshopManifest(workshopMaterialManifestExport.ExportArtisanManifestWithSubcrafts(
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
