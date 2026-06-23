using System;
using System.Collections.Generic;
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
    private readonly IPluginLog log;

    private string urlBuffer = string.Empty;
    private string apiKeyBuffer = string.Empty;
    private bool showApiKey = false;
    private bool showPreview = false;
    private readonly WorkshopProjectSelectionState workshopProjectSelection = new();
    private bool confirmViwiClear = false;
    private string workshopStatus = "Workshop prep queue is idle.";

    private const string ProductSummary = "Small, practical FFXIV improvements under one roof.";
    private const string InventoryModuleSummary = "Inventory Reporter exports character and retainer inventory snapshots as JSON.";
    private const string WorkshopPrepModuleSummary = "Workshop Prep tracks company workshop projects and their direct material needs.";
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
        this.log = log;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 520),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        urlBuffer = config.ServerUrl;
        apiKeyBuffer = config.ApiKey;
        ProjectBrowser = new WorkshopProjectBrowserWindow(
            config,
            workshopCatalog,
            workshopProjectSelection,
            AddWorkshopProject);
    }

    public WorkshopProjectBrowserWindow ProjectBrowser { get; }

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

            if (ImGui.BeginTabItem("Workshop Prep"))
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
        ImGui.TextColored(ColMuted, "Current modules: Inventory Reporter, Workshop Prep");
    }

    private void DrawOverviewTab()
    {
        ImGui.Spacing();
        ImGui.TextColored(ColHeader, "Modules");
        ImGui.Separator();

        DrawModuleSummary("Inventory Reporter", "Enabled", InventoryModuleSummary);
        DrawModuleSummary("Workshop Prep", "Enabled", WorkshopPrepModuleSummary);
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
        ImGui.TextColored(ColHeader, "Workshop Prep");
        ImGui.TextWrapped(WorkshopPrepModuleSummary);
        ImGui.Spacing();

        var projects = workshopCatalog.GetProjects();

        DrawWorkshopPrepQueue(projects);
        ImGui.Spacing();
        DrawWorkshopMaterialSummary();
        ImGui.Spacing();
        DrawWorkshopPrepActions();
    }

    private void DrawWorkshopPrepQueue(IReadOnlyList<WorkshopProjectDefinition> projects)
    {
        ImGuiUi.SectionHeader("Prep Queue", ColHeader);

        if (projects.Count == 0)
        {
            ImGui.TextColored(ColMuted, "No company workshop projects were found.");
            return;
        }

        var selectedProject = projects.FirstOrDefault(x => x.WorkshopItemId == workshopProjectSelection.SelectedWorkshopItemId);
        ImGui.TextColored(ColMuted, selectedProject == null
            ? "No workshop project selected."
            : $"Selected: {selectedProject.Name}");

        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Quantity##workshopQuantity", ref workshopProjectSelection.Quantity))
        {
            if (workshopProjectSelection.Quantity < 1)
                workshopProjectSelection.Quantity = 1;
        }

        ImGui.SameLine();
        if (ImGui.Button("Browse Projects..."))
            ProjectBrowser.IsOpen = true;

        ImGui.SameLine();
        if (ImGuiUi.Button("Add Selected", selectedProject != null))
            AddWorkshopProject(workshopProjectSelection.SelectedWorkshopItemId);

        ImGui.Spacing();
        DrawWorkshopQueueTable(projects);
    }

    private void AddWorkshopProject(uint workshopItemId)
    {
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

        config.Save();
        workshopStatus = "Added project to workshop prep queue.";
    }

    private void DrawWorkshopQueueTable(IReadOnlyList<WorkshopProjectDefinition> projects)
    {
        if (config.WorkshopPrepQueue.Count == 0)
        {
            ImGui.TextColored(ColMuted, "No workshop projects queued.");
            return;
        }

        var projectNames = projects.ToDictionary(x => x.WorkshopItemId, x => x.Name);
        if (ImGui.BeginTable("WorkshopPrepQueue", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Project");
            ImGui.TableSetupColumn("Qty");
            ImGui.TableSetupColumn("");
            ImGui.TableHeadersRow();

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
                if (ImGui.InputInt($"##workshopQueueQty{index}", ref quantity))
                {
                    item.Quantity = Math.Max(1, quantity);
                    config.Save();
                }

                ImGui.TableNextColumn();
                if (ImGui.Button($"Remove##workshopQueueRemove{index}"))
                {
                    config.WorkshopPrepQueue.RemoveAt(index);
                    config.Save();
                    workshopStatus = "Removed project from workshop prep queue.";
                    index--;
                }
            }

            ImGui.EndTable();
        }
    }

    private void DrawWorkshopMaterialSummary()
    {
        ImGuiUi.SectionHeader("Materials", ColHeader);

        var availability = GetWorkshopAvailability();
        if (availability.Count == 0)
        {
            ImGui.TextColored(ColMuted, "No workshop materials yet. Add projects to the prep queue.");
            return;
        }

        if (ImGui.BeginTable("WorkshopPrepMaterials", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Required");
            ImGui.TableSetupColumn("Player");
            ImGui.TableSetupColumn("Retainers");
            ImGui.TableSetupColumn("Shortage");
            ImGui.TableSetupColumn("Candidates");
            ImGui.TableHeadersRow();

            foreach (var item in availability)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.ItemName);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.Required.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.PlayerInventory.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.RetainerCache.ToString());
                ImGui.TableNextColumn();
                ImGui.TextColored(item.Shortage > 0 ? ColError : ColSuccess, item.Shortage.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.Join(", ", item.CandidateRetainers.Select(x => x.RetainerName)));
            }

            ImGui.EndTable();
        }
    }

    private IReadOnlyList<WorkshopMaterialAvailability> GetWorkshopAvailability()
    {
        if (config.WorkshopPrepQueue.Count == 0)
            return [];

        var requirements = workshopCatalog.BuildRequirements(config.WorkshopPrepQueue);
        var playerInventory = scanner.CountPlayerInventory(config);
        return WorkshopMaterialAvailabilityService.BuildAvailability(requirements, playerInventory, config);
    }

    private void DrawWorkshopPrepActions()
    {
        ImGuiUi.SectionHeader("Actions", ColHeader);

        if (config.WorkshopPrepQueue.Count == 0)
            confirmViwiClear = false;

        var hasPrepQueue = config.WorkshopPrepQueue.Count > 0;
        var canRefreshRetainers = autoRetainerRefresh.CanStartRefresh &&
                                  !autoRetainerRefresh.IsRefreshing &&
                                  !autoRetainerRefresh.IsStartQueued;
        if (ImGuiUi.Button("Refresh Retainer Cache", canRefreshRetainers))
            autoRetainerRefresh.StartFullRefresh();

        ImGui.SameLine();
        if (ImGuiUi.Button("Restock Materials From Retainers", !workshopRetainerRestock.IsRunning))
            _ = workshopRetainerRestock.StartAsync(GetWorkshopAvailability());

        ImGui.SameLine();
        if (workshopAssemblyRunner.IsRunning)
        {
            if (ImGui.Button("Stop Assembly"))
                workshopAssemblyRunner.Stop();
        }
        else if (ImGuiUi.Button("Start Native Assembly", hasPrepQueue))
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
                }
                else
                {
                    var result = workshopAssemblyRunner.Start(preflight.Plan);
                    workshopStatus = result.Message;
                }
            }
            catch (Exception ex)
            {
                workshopStatus = $"Unable to start workshop assembly. {ex.Message}";
                log.Warning(ex, "[MarketMafioso] Native workshop assembly preflight failed.");
            }
        }

        ImGui.SameLine();
        if (ImGuiUi.Button("Send Queue To VIWI", hasPrepQueue))
            confirmViwiClear = true;

        if (confirmViwiClear)
        {
            ImGui.TextColored(ColMuted, "This will clear VIWI Workshoppa's queue and send the MarketMafioso prep queue.");

            if (ImGuiUi.Button("Confirm VIWI Queue Sync", hasPrepQueue))
            {
                var result = viwiWorkshoppaIpc.SendQueue(config.WorkshopPrepQueue, clearExisting: true);
                workshopStatus = result.Message;
                confirmViwiClear = false;
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel VIWI Queue Sync"))
                confirmViwiClear = false;
        }

        if (ImGuiUi.Button("Clear Prep Queue", hasPrepQueue))
        {
            config.WorkshopPrepQueue.Clear();
            config.Save();
            workshopStatus = "Cleared prep queue.";
        }

        ImGui.Spacing();
        ImGui.TextColored(GetWorkshopStatusColor(), workshopStatus);
        ImGui.TextColored(workshopRetainerRestock.IsRunning ? ColHeader : ColMuted, workshopRetainerRestock.LastStatus);
        var progress = workshopAssemblyRunner.Progress;
        ImGui.TextColored(workshopAssemblyRunner.IsRunning ? ColHeader : ColMuted, progress.Message);
    }

    private Vector4 GetWorkshopStatusColor()
    {
        if (workshopStatus.Contains("unable", StringComparison.OrdinalIgnoreCase) ||
            workshopStatus.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            workshopStatus.Contains("not available", StringComparison.OrdinalIgnoreCase))
            return ColError;

        if (workshopStatus.Contains("sent", StringComparison.OrdinalIgnoreCase) ||
            workshopStatus.Contains("added", StringComparison.OrdinalIgnoreCase) ||
            workshopStatus.Contains("cleared", StringComparison.OrdinalIgnoreCase) ||
            workshopStatus.Contains("removed", StringComparison.OrdinalIgnoreCase))
            return ColSuccess;

        return ColMuted;
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
    }

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
