using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.UI.Styling;
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

    private bool confirmViwiClear = false;
    private bool confirmNewWorkshopQueue = false;
    private bool confirmLoadFrozenQueue = false;
    private bool confirmTradeQueueReplace = false;
    private Guid? selectedFrozenQueueId;
    private Guid? frozenQueueNameContextId;
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
                Flags: ImGuiTableColumnFlags.WidthStretch),
            new(
                "Qty",
                96f,
                row => row.Item.Quantity.ToString("N0"),
                row => row.Item.Quantity,
                Draw: DrawQueueQuantity,
                Alignment: DalamudTableCellAlignment.Right),
            new(
                "Actions",
                104f,
                _ => "Remove",
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
        if (ImGuiUi.Button("Add Project...", CanEditQueue))
            openProjectBrowser();
    }

    public void DrawMaterialActions()
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
        if (ImGuiUi.MenuButton("Export", primary: true))
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
        if (frozenQueueNameContextId != config.ActiveFrozenWorkshopQueueId)
        {
            frozenQueueNameContextId = config.ActiveFrozenWorkshopQueueId;
            frozenQueueNameInput = activeFrozenQueue?.Name ?? string.Empty;
        }

        var nameChanged = activeFrozenQueue is not null &&
                          !string.Equals(
                              frozenQueueNameInput.Trim(),
                              activeFrozenQueue.Name,
                              StringComparison.Ordinal);
        var modified = activeFrozenQueue is not null &&
                       (!WorkshopQueueService.ActiveQueueMatchesFrozenQueue(config) || nameChanged);
        var queueStateLabel = activeFrozenQueue is null
            ? "Unsaved"
            : modified
                ? "Modified"
                : FormatSavedAge(activeFrozenQueue.UpdatedAt, DateTime.UtcNow);

        var nameWidth = Math.Max(240f, ImGui.GetContentRegionAvail().X - 360f);
        ImGui.SetNextItemWidth(nameWidth);
        using (DalamudUiChrome.PushInput(MarketMafiosoUiTheme.Palette))
        {
            ImGui.InputTextWithHint(
                "##workshopFrozenQueueName",
                "Name this queue...",
                ref frozenQueueNameInput,
                128);
        }

        ImGui.SameLine();
        DalamudUiChrome.DrawBadge(
            queueStateLabel,
            MarketMafiosoUiTheme.Palette,
            modified ? DalamudUiTone.Warning : DalamudUiTone.Neutral);

        ImGui.SameLine();
        if (ImGuiUi.PrimaryButton("Save", CanEditQueue && config.WorkshopPrepQueue.Count > 0))
        {
            SaveActiveQueue(activeFrozenQueue);
        }

        ImGui.SameLine();
        DrawSavedJobsMenu();

        DrawFrozenQueueConfirmations();
    }

    private void DrawSavedJobsMenu()
    {
        if (ImGuiUi.MenuButton("Saved Jobs"))
            ImGui.OpenPopup("WorkshopSavedJobsMenu");
        if (!ImGui.BeginPopup("WorkshopSavedJobsMenu"))
            return;

        var hasQueue = CanEditQueue && config.WorkshopPrepQueue.Count > 0;
        if (ImGuiUi.MenuItem("Save as new job...", hasQueue))
            ApplyFrozenQueueResult(
                WorkshopQueueService.FreezeCurrentQueue(
                    config,
                    frozenQueueNameInput,
                    DateTime.UtcNow));

        var canLoad = CanEditQueue && config.FrozenWorkshopQueues.Count > 0;
        ImGui.BeginDisabled(!canLoad);
        if (ImGui.BeginMenu("Load saved job..."))
        {
            foreach (var frozenQueue in config.FrozenWorkshopQueues.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (ImGui.MenuItem(
                        $"{frozenQueue.Name} ({frozenQueue.Items.Sum(x => x.Quantity):N0})##load{frozenQueue.Id}"))
                {
                    selectedFrozenQueueId = frozenQueue.Id;
                    RequestLoadFrozenQueue(frozenQueue.Id);
                }
            }

            ImGui.EndMenu();
        }
        ImGui.EndDisabled();

        if (ImGui.MenuItem("Manage saved jobs..."))
            openFrozenQueueBrowser();

        ImGui.Separator();
        if (ImGuiUi.MenuItem("Start new queue", CanEditQueue))
        {
            if (config.WorkshopPrepQueue.Count > 0)
                confirmNewWorkshopQueue = true;
            else
                StartNewWorkshopQueue();
        }

        ImGui.EndPopup();
    }

    private void SaveActiveQueue(WorkshopFrozenQueue? activeFrozenQueue)
    {
        var now = DateTime.UtcNow;
        if (activeFrozenQueue is not null &&
            !string.Equals(
                frozenQueueNameInput.Trim(),
                activeFrozenQueue.Name,
                StringComparison.Ordinal))
        {
            var rename = WorkshopQueueService.RenameFrozenQueue(
                config,
                activeFrozenQueue.Id,
                frozenQueueNameInput,
                now);
            if (!rename.Success)
            {
                ApplyFrozenQueueResult(rename);
                return;
            }
        }

        var createsFrozenQueue = activeFrozenQueue is null;
        ApplyFrozenQueueResult(
            WorkshopQueueService.SaveActiveQueue(config, frozenQueueNameInput, now),
            clearName: false);
        if (createsFrozenQueue)
            frozenQueueNameContextId = config.ActiveFrozenWorkshopQueueId;
    }

    private static string FormatSavedAge(DateTime updatedAtUtc, DateTime nowUtc)
    {
        var age = nowUtc - DateTime.SpecifyKind(updatedAtUtc, DateTimeKind.Utc);
        if (age < TimeSpan.FromMinutes(1))
            return "Saved just now";
        if (age < TimeSpan.FromHours(1))
            return $"Saved {(int)age.TotalMinutes:N0} min ago";
        if (age < TimeSpan.FromDays(1))
            return $"Saved {(int)age.TotalHours:N0} hr ago";
        return $"Saved {updatedAtUtc.ToLocalTime():g}";
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
        using var style = DalamudUiChrome.PushTable(MarketMafiosoUiTheme.Palette);
        if (!queueTable.Begin(
                "WorkshopPrepQueue",
                DalamudTableLayout.FitContent(ImGuiUi.InteractiveTableFlags)))
        {
            return;
        }

        if (rows.Length == 0)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "No workshop projects queued.");
            ImGui.TableNextColumn();
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "-");
            ImGui.TableNextColumn();
            if (ImGuiUi.Button("Add##workshopQueueEmptyAdd", CanEditQueue))
                openProjectBrowser();
            queueTable.End();
            return;
        }

        foreach (var row in queueTable.Apply(rows, ImGui.TableGetSortSpecs()))
        {
            ImGui.PushID(checked((int)row.Item.WorkshopItemId));
            queueTable.DrawRow(row);
            ImGui.PopID();
        }

        queueTable.End();
    }

    private void DrawQueueQuantity(WorkshopQueueTableRow row)
    {
        var quantity = row.Item.Quantity;
        ImGui.SetNextItemWidth(80);
        ImGui.BeginDisabled(!CanEditQueue);
        using (DalamudUiChrome.PushInput(MarketMafiosoUiTheme.Palette))
        {
            if (ImGui.InputInt("##workshopQueueQty", ref quantity))
            {
                row.Item.Quantity = Math.Max(1, quantity);
                SaveActiveQueueEdit();
            }
        }
        ImGui.EndDisabled();
    }

    private void DrawQueueActions(WorkshopQueueTableRow row)
    {
        if (!ImGuiUi.Button("Remove##workshopQueueRemove", CanEditQueue))
            return;

        config.WorkshopPrepQueue.Remove(row.Item);
        SaveActiveQueueEdit();
        setWorkshopStatus("Removed project from workshop prep queue.");
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

    private sealed record WorkshopQueueTableRow(
        WorkshopPrepQueueItem Item,
        string ProjectName);
}
