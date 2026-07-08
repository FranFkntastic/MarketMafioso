using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MarketMafioso.Automation.Diagnostics;
using MarketMafioso.Automation.Retainers;
using MarketMafioso.Automation.Travel;
using MarketMafioso.CraftArchitectCompanion;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.RetainerRestock;
using MarketMafioso.Windows.ItemAutocomplete;
using MarketMafioso.Windows.MarketAcquisitionRequestBuilder;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Windows;

public class MainWindow : Window, IDisposable
{
    private static readonly TimeSpan UniversalisFreshnessVerificationDelay = TimeSpan.FromSeconds(10);

    private readonly Configuration config;
    private readonly HttpReporter reporter;
    private readonly InventoryScanner scanner;
    private readonly AutoRetainerRefreshService autoRetainerRefresh;
    private readonly WorkshopProjectCatalog workshopCatalog;
    private readonly VIWIWorkshoppaIpc viwiWorkshoppaIpc;
    private readonly WorkshopRetainerRestockService workshopRetainerRestock;
    private readonly WorkshopAssemblyRunner workshopAssemblyRunner;
    private readonly WorkshopMaterialManifestExportService workshopMaterialManifestExport;
    private readonly RetainerCacheFileStore? retainerCacheStore;
    private readonly IPlayerState playerState;
    private readonly IPluginLog log;
    private readonly HttpClient acquisitionHttpClient = new();
    private readonly HttpClient craftQuoteHttpClient = new();
    private readonly MarketAcquisitionRequestClient acquisitionClient;
    private readonly UniversalisMarketAcquisitionPlanSource acquisitionPlanSource;
    private readonly MarketAcquisitionWorldVisitCatalog marketAcquisitionWorldVisitCatalog;
    private readonly MarketAcquisitionPlanPreparationService marketAcquisitionPlanPreparationService;
    private readonly MarketBoardListingReader marketBoardListingReader;
    private readonly MarketBoardItemSearchDriver marketBoardItemSearchDriver;
    private readonly MarketBoardInputCaptureReader marketBoardInputCaptureReader;
    private readonly DalamudMarketBoardPurchaseAdapter marketBoardPurchaseAdapter;
    private readonly MarketBoardPurchaseExecutor marketBoardPurchaseExecutor;
    private readonly MarketBoardApproachService marketBoardApproachService;
    private readonly MarketAcquisitionRouteRunner marketAcquisitionRouteRunner;
    private readonly MarketBoardAutomationController marketBoardAutomationController = new();
    private readonly string marketAcquisitionRouteDiagnosticsDirectory;
    private readonly MarketAcquisitionRequestBuilderPanel acquisitionRequestBuilder;

    private string urlBuffer = string.Empty;
    private string apiKeyBuffer = string.Empty;
    private string dashboardUrlBuffer = string.Empty;
    private string dashboardOpenStatus = "Dashboard link appears after a successful send.";
    private string diagnosticsFolderStatus = "Route diagnostics folder opens in Explorer.";
    private string marketAcquisitionUnlockKeyBuffer = string.Empty;
    private string marketAcquisitionUnlockStatus = "Private module is hidden until unlocked.";
    private bool showApiKey = false;
    private bool showMarketAcquisitionUnlockKey = false;
    private bool showPreview = false;
    private readonly WorkshopProjectSelectionState workshopProjectSelection = new();
    private IReadOnlyList<MarketAcquisitionRequestView> pendingAcquisitionRequests = [];
    private MarketAcquisitionClaimView? claimedAcquisitionRequest;
    private string? claimedAcceptIdempotencyKey;
    private string? claimedRejectIdempotencyKey;
    private MarketAcquisitionPlan? acquisitionPlan;
    private string? currentAcquisitionPlanHash;
    private MarketBoardReadResult? marketBoardReadResult;
    private MarketBoardListingReconciliation? marketBoardReconciliation;
    private MarketAcquisitionLiveCandidatePlan? marketAcquisitionLiveCandidatePlan;
    private uint activeWorldPurchasedQuantity;
    private uint activeWorldSpentGil;
    private string? activeWorldPurchaseBatchWorld;
    private string? activePurchaseLineId;
    private uint activeLinePurchasedQuantity;
    private uint activeLineSpentGil;
    private DateTimeOffset nextGuidedRouteMonitorUtc = DateTimeOffset.MinValue;
    private int marketInputCaptureIndex;
    private bool guidedRouteProbeRunning = false;
    private long guidedRouteProgressReportSequence;
    private long guidedRouteProgressSessionVersion;
    private string guidedRouteProgressNonce = Guid.NewGuid().ToString("N");
    private string? lastGuidedRouteProgressReportKey;
    private readonly HashSet<string> expandedGuidedRouteStops = new(StringComparer.OrdinalIgnoreCase);
    private bool acquisitionRequestBusy = false;
    private string acquisitionStatus = "No dashboard request has been fetched this session.";
    private CancellationTokenSource? acquisitionRequestCancellation;
    private bool confirmViwiClear = false;
    private bool confirmNewWorkshopQueue = false;
    private bool confirmLoadFrozenQueue = false;
    private Guid? selectedFrozenQueueId;
    private string frozenQueueNameInput = string.Empty;
    private string workshopStatus = "Workshop prep queue is idle.";
    private readonly IReadOnlyList<AcquisitionItemOption> restockItemOptions;
    private readonly ItemAutocompleteState restockItemAutocomplete = new();
    private int restockDesiredQuantity = 1;
    private bool confirmClearRetainerRestockPlan = false;

    private const string ProductSummary = "Workshop logistics and self-hosted inventory history.";
    private const string InventoryModuleSummary = "Inventory Reporter exports character and retainer inventory snapshots as JSON.";
    private const string WorkshopLogisticsModuleSummary = "Workshop Logistics tracks company workshop jobs, materials, retainer restock, handoff, and assembly.";
    private const string MarketAcquisitionModuleSummary = "Build, sync, and monitor acquisition requests from one persistent board.";
    private const string LocalReceiverUrl = "http://localhost:8080/inventory";
    private const string DevReceiverUrl = "https://dev.xivcraftarchitect.com/marketmafioso/api/inventory";
    private const string ProductionReceiverUrl = "https://xivcraftarchitect.com/marketmafioso/api/inventory";
    private static readonly TimeSpan MarketBoardPurchaseConfirmationWatchdog = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MarketBoardPurchaseListingRemovalWatchdog = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MarketBoardPurchaseInitialMonitorDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MarketBoardPurchaseMonitorInterval = TimeSpan.FromMilliseconds(500);
    private const int MarketAcquisitionWorldVisitCatalogMaxRecords = 2_000;

    internal static readonly Vector4 ColHeader = new(0.38f, 0.73f, 1.00f, 1f);
    internal static readonly Vector4 ColSuccess = new(0.45f, 0.90f, 0.55f, 1f);
    internal static readonly Vector4 ColError = new(1.00f, 0.40f, 0.40f, 1f);
    internal static readonly Vector4 ColMuted = new(0.60f, 0.60f, 0.60f, 1f);

    private sealed record AdvisoryPlanRow(
        int RouteOrdinal,
        string Item,
        string World,
        string DataCenter,
        uint Quantity,
        uint Gil,
        uint Unit,
        bool IsHq,
        string Listing,
        bool ExceedsRequestedQuantity);

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
        IDataManager dataManager,
        IPlayerState playerState,
        MarketBoardApproachService marketBoardApproachService,
        string marketAcquisitionRouteDiagnosticsDirectory,
        RetainerCacheFileStore? retainerCacheStore,
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
        this.retainerCacheStore = retainerCacheStore;
        this.playerState = playerState;
        this.log = log;
        acquisitionClient = new MarketAcquisitionRequestClient(acquisitionHttpClient);
        acquisitionPlanSource = new UniversalisMarketAcquisitionPlanSource(acquisitionHttpClient);
        marketAcquisitionWorldVisitCatalog = new MarketAcquisitionWorldVisitCatalog(config);
        marketAcquisitionPlanPreparationService = new MarketAcquisitionPlanPreparationService(
            acquisitionPlanSource,
            marketAcquisitionWorldVisitCatalog,
            (ex, worldName, itemId) => log.Warning(
                ex,
                "[MarketMafioso] Unable to refresh Universalis evidence for {World} item {ItemId}.",
                worldName,
                itemId));
        var universalisFreshnessVerifier = new UniversalisMarketFreshnessVerifier(acquisitionHttpClient);
        marketBoardListingReader = new MarketBoardListingReader(Plugin.GameGui);
        marketBoardItemSearchDriver = new MarketBoardItemSearchDriver(Plugin.GameGui);
        marketBoardInputCaptureReader = new MarketBoardInputCaptureReader(Plugin.GameGui);
        marketBoardPurchaseAdapter = new DalamudMarketBoardPurchaseAdapter(Plugin.GameGui, log);
        marketBoardPurchaseExecutor = new MarketBoardPurchaseExecutor(marketBoardPurchaseAdapter);
        this.marketBoardApproachService = marketBoardApproachService;
        this.marketAcquisitionRouteDiagnosticsDirectory = marketAcquisitionRouteDiagnosticsDirectory;
        marketAcquisitionRouteRunner = new MarketAcquisitionRouteRunner(
            marketAcquisitionRouteDiagnosticsDirectory,
            universalisFreshnessVerifier.VerifyAsync);

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(980, 560),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        if (string.IsNullOrWhiteSpace(config.ApiKey) &&
            !string.IsNullOrWhiteSpace(config.CommandPickupApiKey))
        {
            config.ApiKey = config.CommandPickupApiKey;
            config.Save();
        }

        urlBuffer = config.ServerUrl;
        apiKeyBuffer = config.ApiKey;
        restockItemOptions = ItemAutocompleteControl.LoadItemOptions(dataManager);
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
        var acquisitionRequestBuilderCraftAppraisal = CreateAcquisitionRequestBuilderCraftAppraisalController();
        acquisitionRequestBuilder = new MarketAcquisitionRequestBuilderPanel(
            config,
            dataManager,
            acquisitionRequestBuilderCraftAppraisal,
            SyncAcquisitionRequestBuilderAsync,
            RefreshAcquisitionRequestBuilderRemoteAsync,
            OnAcquisitionRequestBuilderDocumentAdopted);
        AcquisitionDiagnostics = new MarketAcquisitionDiagnosticsWindow(
            () => marketBoardReadResult,
            () => marketBoardReconciliation,
            () => marketAcquisitionLiveCandidatePlan,
            () => acquisitionPlan,
            CanProbeLiveMarketBoard,
            () => _ = ProbeLiveMarketBoardAsync(),
            CaptureMarketBoardInputState,
            () => marketAcquisitionRouteRunner.CanFinalizeInputCaptureLog,
            FinalizeMarketBoardInputCaptureLog,
            () => marketAcquisitionRouteRunner.LastDiagnosticFilePath,
            () => acquisitionRequestBuilderCraftAppraisal.State.CreateDiagnosticsSnapshot());
        AutomationDiagnostics = new AutomationDiagnosticsWindow(CreateAutomationDiagnosticProbes(), IsMarketAcquisitionUnlocked);

        var restoredAcquisitionClaim = MarketAcquisitionClaimPersistence.Restore(config);
        if (restoredAcquisitionClaim != null)
        {
            claimedAcquisitionRequest = restoredAcquisitionClaim.Value.Claim;
            claimedAcceptIdempotencyKey = restoredAcquisitionClaim.Value.AcceptIdempotencyKey;
            claimedRejectIdempotencyKey = restoredAcquisitionClaim.Value.RejectIdempotencyKey;
            var adopted = acquisitionRequestBuilder.AdoptRestoredRequestIfSafe(claimedAcquisitionRequest);
            acquisitionStatus = adopted
                ? "Restored previously claimed dashboard request into the builder."
                : "Restored previously claimed dashboard request; preserving local builder edits.";
        }
    }

    public WorkshopProjectBrowserWindow ProjectBrowser { get; }
    public WorkshopFrozenQueueBrowserWindow FrozenQueueBrowser { get; }
    public MarketAcquisitionDiagnosticsWindow AcquisitionDiagnostics { get; }
    public AutomationDiagnosticsWindow AutomationDiagnostics { get; }

    public void OnFrameworkUpdate(IFramework _)
    {
        if (!IsMarketAcquisitionUnlocked())
            return;

        MonitorMarketBoardPurchase();
        MonitorGuidedRoute();
    }

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

            if (ImGui.BeginTabItem("Restock"))
            {
                DrawRetainerRestockTab();
                ImGui.EndTabItem();
            }

            if (IsMarketAcquisitionUnlocked() && ImGui.BeginTabItem("Market Acquisition"))
            {
                DrawMarketAcquisitionTab();
                ImGui.EndTabItem();
            }

            if (IsMarketAcquisitionUnlocked() && ImGui.BeginTabItem("Diagnostics"))
            {
                DrawDiagnosticsTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Settings"))
            {
                DrawSettingsTab();
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
        ImGui.TextColored(
            ColMuted,
            IsMarketAcquisitionUnlocked()
                ? "Current modules: Inventory Reporter, Workshop Logistics, Market Acquisition"
                : "Current modules: Inventory Reporter, Workshop Logistics");
    }

    private void DrawOverviewTab()
    {
        ImGui.Spacing();
        ImGui.TextColored(ColHeader, "Modules");
        ImGui.Separator();

        DrawModuleSummary("Inventory Reporter", "Enabled", InventoryModuleSummary);
        DrawModuleSummary("Workshop Logistics", "Enabled", WorkshopLogisticsModuleSummary);
        if (IsMarketAcquisitionUnlocked())
            DrawModuleSummary("Market Acquisition", "Internal", MarketAcquisitionModuleSummary);
        DrawModuleSummary("General Improvements", "Planned", "Small quality-of-life tools that are useful, but too narrow for their own plugin.");
    }

    private void DrawInventoryReporterTab()
    {
        ImGui.Spacing();
        ImGui.TextColored(ColHeader, "Inventory Reporter");
        ImGui.TextWrapped(InventoryModuleSummary);
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

    private void DrawRetainerRestockTab()
    {
        ImGui.Spacing();
        ImGui.TextColored(ColHeader, "Restock");
        ImGui.TextWrapped("Build a local plan and pull matching items from cached retainers.");
        ImGui.Spacing();

        var plan = GetRetainerRestockPlan();
        DrawRetainerRestockEditor();
        ImGui.Spacing();
        DrawRetainerRestockPreview(plan);
        ImGui.Spacing();
        DrawRetainerRestockControls(plan);
    }

    private void DrawRetainerRestockEditor()
    {
        ImGuiUi.SectionHeaderWithActions("Plan Editor", ColHeader, DrawRetainerRestockEditorActions, 150);

        if (config.RetainerRestockPlanItems.Count == 0)
            ImGui.TextColored(ColMuted, "No restock rows yet.");

        if (ImGui.BeginTable("RetainerRestockRows", 5, ImGuiUi.InteractiveTableFlags))
        {
            ImGui.TableSetupColumn("Enabled", ImGuiTableColumnFlags.WidthFixed, 72);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Desired", ImGuiTableColumnFlags.WidthFixed, 88);
            ImGui.TableSetupColumn("Note", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 84);
            ImGui.TableHeadersRow();

            for (var index = 0; index < config.RetainerRestockPlanItems.Count; index++)
            {
                var row = config.RetainerRestockPlanItems[index];
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                var enabled = row.Enabled;
                if (ImGui.Checkbox($"##retainerRestockEnabled{row.Id}", ref enabled))
                {
                    row.Enabled = enabled;
                    config.Save();
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(row.ItemName) ? $"Item {row.ItemId}" : row.ItemName);

                ImGui.TableNextColumn();
                var desired = row.DesiredPlayerQuantity;
                ImGui.SetNextItemWidth(72);
                if (ImGui.InputInt($"##retainerRestockDesired{row.Id}", ref desired))
                {
                    row.DesiredPlayerQuantity = Math.Max(1, desired);
                    config.Save();
                }

                ImGui.TableNextColumn();
                var note = row.Note ?? string.Empty;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText($"##retainerRestockNote{row.Id}", ref note, 160))
                {
                    row.Note = note;
                    config.Save();
                }

                ImGui.TableNextColumn();
                if (ImGuiUi.Button($"Remove##retainerRestockRemove{row.Id}", true))
                {
                    config.RetainerRestockPlanItems.RemoveAt(index);
                    config.Save();
                    index--;
                }
            }

            ImGui.EndTable();
        }
    }

    private void DrawRetainerRestockEditorActions()
    {
        if (ImGuiUi.Button("Add Item", true))
            ImGui.OpenPopup("RetainerRestockAddItemPopup");

        ImGui.SameLine();
        if (ImGuiUi.Button("Clear Plan", config.RetainerRestockPlanItems.Count > 0))
            confirmClearRetainerRestockPlan = true;

        if (confirmClearRetainerRestockPlan)
        {
            ImGui.SameLine();
            if (ImGuiUi.Button("Confirm Clear", config.RetainerRestockPlanItems.Count > 0))
            {
                config.RetainerRestockPlanItems.Clear();
                config.Save();
                confirmClearRetainerRestockPlan = false;
            }
        }

        if (ImGui.BeginPopup("RetainerRestockAddItemPopup"))
        {
            ItemAutocompleteControl.Draw(
                "RetainerRestock",
                restockItemOptions,
                restockItemAutocomplete,
                null,
                ColMuted,
                ColSuccess,
                ColError);

            ImGui.TextColored(ColMuted, "Desired player quantity");
            ImGui.SetNextItemWidth(120);
            ImGui.InputInt("##retainerRestockNewDesired", ref restockDesiredQuantity);
            restockDesiredQuantity = Math.Max(1, restockDesiredQuantity);

            var selected = ItemAutocompletePresenter.ResolveSelectedItem(
                restockItemOptions,
                restockItemAutocomplete.SearchBuffer,
                restockItemAutocomplete.SelectedItem);
            var canAdd = selected is not null && restockDesiredQuantity > 0;
            if (ImGuiUi.Button("Add To Plan", canAdd))
            {
                AddRetainerRestockPlanItem(selected!, restockDesiredQuantity);
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private void AddRetainerRestockPlanItem(AcquisitionItemOption selected, int desiredQuantity)
    {
        var existing = config.RetainerRestockPlanItems.FirstOrDefault(x => x.ItemId == selected.ItemId);
        if (existing is not null)
        {
            existing.ItemName = selected.Name;
            existing.DesiredPlayerQuantity = desiredQuantity;
            existing.Enabled = true;
        }
        else
        {
            config.RetainerRestockPlanItems.Add(new RetainerRestockPlanItem
            {
                ItemId = selected.ItemId,
                ItemName = selected.Name,
                DesiredPlayerQuantity = desiredQuantity,
                Enabled = true,
            });
        }

        config.Save();
        restockItemAutocomplete.SearchBuffer = string.Empty;
        restockItemAutocomplete.SelectedItem = null;
        restockDesiredQuantity = 1;
    }

    private void DrawRetainerRestockPreview(RetainerRestockPlan plan)
    {
        ImGui.TextColored(ColHeader, "Preview");
        ImGui.Separator();

        if (plan.Lines.Count == 0)
        {
            ImGui.TextColored(ColMuted, "Add enabled item rows to preview retainer coverage.");
            return;
        }

        if (ImGui.BeginTable("RetainerRestockPreview", 9, ImGuiUi.InteractiveTableFlags))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Desired", ImGuiTableColumnFlags.WidthFixed, 72);
            ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthFixed, 72);
            ImGui.TableSetupColumn("Need", ImGuiTableColumnFlags.WidthFixed, 72);
            ImGui.TableSetupColumn("Retainers", ImGuiTableColumnFlags.WidthFixed, 88);
            ImGui.TableSetupColumn("Missing", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Cache Age", ImGuiTableColumnFlags.WidthFixed, 96);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 112);
            ImGui.TableSetupColumn("Candidates", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            foreach (var line in plan.Lines)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(line.ItemName);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(line.DesiredPlayerQuantity.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(line.PlayerQuantity.ToString());
                ImGui.TableNextColumn();
                ImGui.TextColored(line.NeededQuantity > 0 ? ColError : ColSuccess, line.NeededQuantity.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(line.CachedRetainerQuantity.ToString());
                ImGui.TableNextColumn();
                ImGui.TextColored(line.MissingQuantity > 0 ? ColError : ColSuccess, line.MissingQuantity.ToString());
                ImGui.TableNextColumn();
                ImGui.TextColored(line.OldestRelevantCacheAge is null ? ColMuted : ColHeader, FormatRetainerRestockCacheAge(line.OldestRelevantCacheAge));
                ImGui.TableNextColumn();
                ImGui.TextColored(GetRetainerRestockStatusColor(line.Status), FormatRetainerRestockStatus(line.Status));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatRetainerRestockCandidates(line.Candidates));
            }

            ImGui.EndTable();
        }
    }

    private void DrawRetainerRestockControls(RetainerRestockPlan plan)
    {
        var canRefreshRetainers = autoRetainerRefresh.CanStartRefresh &&
                                  !autoRetainerRefresh.IsRefreshing &&
                                  !autoRetainerRefresh.IsStartQueued;
        var canRun = !workshopRetainerRestock.IsRunning &&
                     plan.Lines.Any(line => line.NeededQuantity > 0 && line.Candidates.Count > 0);

        ImGui.TextColored(ColHeader, "Run");
        ImGui.Separator();
        if (ImGuiUi.Button("Refresh Retainer Cache", canRefreshRetainers))
            autoRetainerRefresh.StartFullRefresh();

        ImGui.SameLine();
        if (ImGuiUi.Button("Restock From Retainers", canRun))
            _ = workshopRetainerRestock.StartRestockAsync(plan.Lines);

        ImGui.Spacing();
        ImGui.TextColored(workshopRetainerRestock.IsRunning ? ColHeader : ColMuted, workshopRetainerRestock.LastStatus);

        var ownerScope = GetCurrentRetainerOwnerScope();
        if (!ownerScope.IsAvailable)
        {
            ImGui.TextColored(ColError, "Current character and home world are unavailable; retainer restock cannot use cached retainers.");
            return;
        }

        var scopedRetainers = config.RetainerCache.Values
            .Where(retainer => ownerScope.Matches(retainer.OwnerCharacterName, retainer.OwnerHomeWorld))
            .ToList();

        if (scopedRetainers.Count == 0)
        {
            ImGui.TextColored(ColMuted, $"No retainers cached for {ownerScope.CharacterName} @ {ownerScope.HomeWorld} yet.");
            return;
        }

        var newest = scopedRetainers.Max(x => x.LastUpdated);
        var oldest = scopedRetainers.Min(x => x.LastUpdated);
        ImGui.TextColored(
            ColMuted,
            $"Cached retainers for {ownerScope.CharacterName} @ {ownerScope.HomeWorld}: {scopedRetainers.Count}; newest {newest:HH:mm:ss UTC}; oldest {oldest:HH:mm:ss UTC}.");
    }

    private RetainerRestockPlan GetRetainerRestockPlan()
    {
        var playerInventory = scanner.CountPlayerInventory(config);
        return RetainerRestockPlanner.BuildPlan(
            config.RetainerRestockPlanItems,
            playerInventory,
            config,
            DateTime.UtcNow,
            GetCurrentRetainerOwnerScope());
    }

    private static string FormatRetainerRestockStatus(RetainerRestockPlanLineStatus status)
    {
        return status switch
        {
            RetainerRestockPlanLineStatus.NoNeed => "No need",
            RetainerRestockPlanLineStatus.Ready => "Ready",
            RetainerRestockPlanLineStatus.Partial => "Partial",
            RetainerRestockPlanLineStatus.NoCachedStock => "No cached stock",
            _ => status.ToString(),
        };
    }

    private static string FormatRetainerRestockCacheAge(TimeSpan? age)
    {
        if (age is null)
            return "-";

        if (age.Value.TotalHours >= 1)
            return $"{age.Value.TotalHours:0.0}h";

        return $"{Math.Max(0, age.Value.TotalMinutes):0}m";
    }

    private static string FormatRetainerRestockCandidates(IReadOnlyList<RetainerRestockCandidate> candidates)
    {
        return candidates.Count == 0
            ? "-"
            : string.Join(", ", candidates.Select(x => $"{x.RetainerName} x{x.CachedQuantity}"));
    }

    private static Vector4 GetRetainerRestockStatusColor(RetainerRestockPlanLineStatus status)
    {
        return status switch
        {
            RetainerRestockPlanLineStatus.NoNeed or RetainerRestockPlanLineStatus.Ready => ColSuccess,
            RetainerRestockPlanLineStatus.Partial => ColHeader,
            RetainerRestockPlanLineStatus.NoCachedStock => ColError,
            _ => ColMuted,
        };
    }

    private void DrawMarketAcquisitionTab()
    {
        ImGui.Spacing();
        ImGui.TextColored(ColHeader, "Market Acquisition");
        ImGui.TextWrapped(MarketAcquisitionModuleSummary);
        ImGui.Spacing();

        const ImGuiTableFlags BoardFlags =
            ImGuiTableFlags.BordersInnerV |
            ImGuiTableFlags.Resizable |
            ImGuiTableFlags.SizingStretchProp;

        if (!ImGui.BeginTable("MarketAcquisitionBoard", 2, BoardFlags))
            return;

        ImGui.TableSetupColumn("Request", ImGuiTableColumnFlags.WidthStretch, 0.85f);
        ImGui.TableSetupColumn("Execution", ImGuiTableColumnFlags.WidthStretch, 1.15f);
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        DrawMarketAcquisitionRequestBuilder();
        ImGui.Spacing();
        DrawMarketAcquisitionPickupSection(compactWhenClaimed: true);

        ImGui.TableNextColumn();
        DrawMarketAcquisitionExecutionPane();

        ImGui.EndTable();
    }

    private void DrawMarketAcquisitionRequestBuilder()
    {
        var hasScope = TryGetAcquisitionScope(out var characterName, out var world);
        acquisitionRequestBuilder.Draw(new MarketAcquisitionRequestBuilderContext(
            characterName,
            world,
            hasScope,
            IsExpectedCharacterScopeGap(),
            acquisitionRequestBusy,
            IsMarketAcquisitionRouteActive(),
            claimedAcquisitionRequest,
            acquisitionPlan,
            currentAcquisitionPlanHash));
    }

    private void DrawMarketAcquisitionExecutionPane()
    {
        ImGuiUi.SectionHeader("Accepted Request, Plan, and Guided Route", ColHeader);
        DrawClaimedAcquisitionRequest();
        ImGui.Spacing();
        DrawMarketAcquisitionPlan();
        ImGui.Spacing();
        DrawMarketAcquisitionGuidedRoute();
    }

    private void DrawMarketAcquisitionPickupSection(bool compactWhenClaimed = false)
    {
        ImGuiUi.SectionHeader("Request Pickup", ColHeader);

        var routeOwnsUi = IsMarketAcquisitionRouteActive();
        if (routeOwnsUi)
        {
            ImGui.TextColored(ColMuted, "Request pickup is hidden while a guided route is active.");
            if (claimedAcquisitionRequest != null)
                ImGui.TextColored(ColMuted, $"Active request: {FormatAcquisitionItem(claimedAcquisitionRequest)}");
            ImGui.SameLine();
            var visibleStatus = GetVisibleAcquisitionStatus();
            ImGui.TextColored(GetAcquisitionStatusColor(visibleStatus), visibleStatus);
            return;
        }

        if (compactWhenClaimed && claimedAcquisitionRequest is not null && pendingAcquisitionRequests.Count == 0)
        {
            var canFetchCompact = !acquisitionRequestBusy &&
                                  !string.IsNullOrWhiteSpace(apiKeyBuffer) &&
                                  TryGetAcquisitionScope(out _, out _);
            if (ImGuiUi.Button("Fetch Dashboard Requests", canFetchCompact))
                _ = FetchDashboardRequestsAsync();

            ImGui.SameLine();
            ImGui.TextColored(GetAcquisitionStatusColor(GetVisibleAcquisitionStatus()), GetVisibleAcquisitionStatus());
            return;
        }

        if (TryGetAcquisitionScope(out var characterName, out var world))
        {
            ImGui.TextColored(ColMuted, $"Character scope: {characterName} @ {world}");
        }
        else if (IsExpectedCharacterScopeGap())
        {
            ImGui.TextColored(ColMuted, "Character scope temporarily unavailable during route travel.");
        }
        else
        {
            ImGui.TextColored(ColError, "Character scope unavailable. Log into a character before fetching requests.");
        }

        var canFetch = !acquisitionRequestBusy &&
                       !string.IsNullOrWhiteSpace(apiKeyBuffer) &&
                       TryGetAcquisitionScope(out _, out _);
        if (ImGuiUi.Button("Fetch Dashboard Requests", canFetch))
            _ = FetchDashboardRequestsAsync();

        ImGui.SameLine();
        var visibleAcquisitionStatus = GetVisibleAcquisitionStatus();
        ImGui.TextColored(GetAcquisitionStatusColor(visibleAcquisitionStatus), visibleAcquisitionStatus);

        if (pendingAcquisitionRequests.Count == 0)
        {
            ImGui.TextColored(
                string.IsNullOrWhiteSpace(apiKeyBuffer) ? ColError : ColMuted,
                string.IsNullOrWhiteSpace(apiKeyBuffer)
                    ? "Set the client API key in Settings before fetching dashboard requests."
                    : "No pending dashboard requests are loaded.");
            return;
        }

        var tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable;
        if (ImGui.BeginTable("MarketAcquisitionPendingRequests", 6, tableFlags))
        {
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Qty");
            ImGui.TableSetupColumn("HQ");
            ImGui.TableSetupColumn("Max Unit");
            ImGui.TableSetupColumn("Mode");
            ImGui.TableSetupColumn("");
            ImGui.TableHeadersRow();

            foreach (var request in pendingAcquisitionRequests)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatAcquisitionItem(request));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(MarketAcquisitionQuantityModePresenter.FormatQuantity(request.QuantityMode, request.Quantity));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(request.HqPolicy);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatGil(request.MaxUnitPrice));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(MarketAcquisitionQuantityModePresenter.FormatMode(request.QuantityMode));
                ImGui.TableNextColumn();
                if (ImGuiUi.Button($"Claim##marketAcquisitionClaim{request.Id}", !acquisitionRequestBusy))
                    _ = ClaimAcquisitionRequestAsync(request.Id);
            }

            ImGui.EndTable();
        }
    }

    private void DrawClaimedAcquisitionRequest()
    {
        ImGuiUi.SectionHeader("Claimed Batch", ColHeader);

        if (claimedAcquisitionRequest == null)
        {
            ImGui.TextColored(ColMuted, "No batch is claimed by this plugin session.");
            return;
        }

        DrawClaimedBatchSummary(claimedAcquisitionRequest);
        ImGui.Spacing();
        DrawClaimedBatchLines(claimedAcquisitionRequest);
        ImGui.Spacing();
        DrawClaimedBatchActions(claimedAcquisitionRequest);
    }

    private void DrawClaimedBatchActions(MarketAcquisitionClaimView claimed)
    {
        var canMutateClaim = !acquisitionRequestBusy &&
                             string.Equals(claimed.Status, "Claimed", StringComparison.OrdinalIgnoreCase);
        if (ImGuiUi.Button("Accept Locally", canMutateClaim))
            _ = AcceptClaimedAcquisitionRequestAsync();

        ImGui.SameLine();
        if (ImGuiUi.Button("Reject", canMutateClaim))
            _ = RejectClaimedAcquisitionRequestAsync();

        ImGui.SameLine();
        if (ImGuiUi.Button("Forget Local", !acquisitionRequestBusy))
            ForgetLocalAcquisitionRequest();

        ImGui.SameLine();
        var canPrepare = !acquisitionRequestBusy &&
                         CanPrepareAcquisitionPlanForStatus(claimed.Status);
        if (ImGuiUi.Button("Prepare Plan", canPrepare))
            _ = PrepareMarketAcquisitionPlanAsync();

        ImGui.TextColored(ColMuted, "Preparing a plan reads remote market data. Guided routes validate live rows before purchasing.");
    }

    private static void DrawClaimedBatchSummary(MarketAcquisitionClaimView claimed)
    {
        if (!ImGui.BeginTable("MarketAcquisitionClaimedBatchSummary", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            return;

        DrawClaimedRequestRow("Status", claimed.Status);
        DrawClaimedRequestRow("Target", $"{claimed.TargetCharacterName} @ {claimed.TargetWorld}");
        DrawClaimedRequestRow("Lines", FormatAcquisitionLineCount(claimed));
        DrawClaimedRequestRow("Routing", FormatClaimedBatchRouting(claimed));
        DrawClaimedRequestRow("Latest", FormatClaimedBatchLatest(claimed));
        ImGui.EndTable();
    }

    private static void DrawClaimedBatchLines(MarketAcquisitionClaimView claimed)
    {
        const ImGuiTableFlags Flags =
            ImGuiTableFlags.Borders |
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.Resizable |
            ImGuiTableFlags.ScrollX;

        if (!ImGui.BeginTable("MarketAcquisitionClaimedBatchLines", 9, Flags))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Mode", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("Max Unit", ImGuiTableColumnFlags.WidthFixed, 88);
        ImGui.TableSetupColumn("Max Qty", ImGuiTableColumnFlags.WidthFixed, 88);
        ImGui.TableSetupColumn("Gil Cap", ImGuiTableColumnFlags.WidthFixed, 88);
        ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, 64);
        ImGui.TableSetupColumn("Bought", ImGuiTableColumnFlags.WidthFixed, 88);
        ImGui.TableSetupColumn("Spent", ImGuiTableColumnFlags.WidthFixed, 88);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 128);
        ImGui.TableHeadersRow();

        foreach (var line in GetAcquisitionPlanLines(claimed).OrderBy(line => line.Ordinal))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatLineItem(line));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(MarketAcquisitionQuantityModePresenter.FormatMode(line.QuantityMode));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatGil(line.MaxUnitPrice));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatLineMaxQuantity(line));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatGilCap(line.GilCap));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.HqPolicy);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.PurchasedQuantity == 0 ? "-" : line.PurchasedQuantity.ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.SpentGil == 0 ? "-" : FormatGil(line.SpentGil));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(line.Status) ? "-" : line.Status);
        }

        ImGui.EndTable();
    }

    private void DrawMarketAcquisitionPlan()
    {
        ImGuiUi.SectionHeader("Advisory Plan", ColHeader);

        if (acquisitionPlan == null)
        {
            ImGui.TextColored(ColMuted, "No market plan prepared.");
            return;
        }

        ImGui.TextColored(
            acquisitionPlan.Status == "Ready" ? ColSuccess : ColMuted,
            $"Status: {acquisitionPlan.Status}  -  Mode: {FormatWorldMode(acquisitionPlan.WorldMode)}  -  Planned {acquisitionPlan.PlannedQuantity:N0}/{acquisitionPlan.RequestedQuantity:N0} item(s), {FormatGil(acquisitionPlan.PlannedGil)}");

        if (IsAcquisitionPlanStale())
            ImGui.TextColored(ColError, "Request changed after this plan was prepared. Update the request and prepare a fresh plan before starting.");

        if (acquisitionPlan.WorldBatches.Count == 0)
            return;

        if (!ImGui.CollapsingHeader("World Listings##MarketAcquisitionPlanWorldListings"))
        {
            ImGui.TextColored(ColMuted, "Expand to inspect advisory listings. Live market-board rows remain authoritative.");
            return;
        }

        var tableFlags =
            ImGuiTableFlags.Borders |
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.Resizable |
            ImGuiTableFlags.ScrollX |
            ImGuiTableFlags.Sortable;
        if (ImGui.BeginTable("MarketAcquisitionPlanBatches", 8, tableFlags))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort);
            ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableSetupColumn("Data Center", ImGuiTableColumnFlags.WidthFixed, 96);
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 72);
            ImGui.TableSetupColumn("Gil", ImGuiTableColumnFlags.WidthFixed, 96);
            ImGui.TableSetupColumn("Unit", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, 48);
            ImGui.TableSetupColumn("Listing", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            var rows = SortAdvisoryPlanRows(BuildAdvisoryPlanRows(acquisitionPlan), ImGui.TableGetSortSpecs());
            foreach (var row in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Item);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.World);
                if (row.ExceedsRequestedQuantity)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(ColMuted, "(over)");
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatRouteDataCenter(row.DataCenter));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Quantity.ToString("N0"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatGil(row.Gil));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatGil(row.Unit));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.IsHq ? "HQ" : "NQ");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Listing);
            }

            ImGui.EndTable();
        }
    }

    private static string FormatPlannedListingItem(MarketAcquisitionPlannedListing listing) =>
        string.IsNullOrWhiteSpace(listing.ItemName)
            ? $"Item {listing.ItemId}"
            : $"{listing.ItemName} ({listing.ItemId})";

    private static IReadOnlyList<AdvisoryPlanRow> BuildAdvisoryPlanRows(MarketAcquisitionPlan plan)
    {
        var rows = new List<AdvisoryPlanRow>();
        var ordinal = 0;
        foreach (var batch in plan.WorldBatches)
        {
            foreach (var listing in batch.Listings)
            {
                rows.Add(new AdvisoryPlanRow(
                    ordinal++,
                    FormatPlannedListingItem(listing),
                    batch.WorldName,
                    batch.DataCenter,
                    listing.Quantity,
                    listing.TotalGil,
                    listing.UnitPrice,
                    listing.IsHq,
                    $"{listing.RetainerName} / {listing.ListingId}",
                    batch.ExceedsRequestedQuantity));
            }
        }

        return rows;
    }

    private static IReadOnlyList<AdvisoryPlanRow> SortAdvisoryPlanRows(
        IReadOnlyList<AdvisoryPlanRow> rows,
        ImGuiTableSortSpecsPtr sortSpecs)
    {
        if (sortSpecs.SpecsCount == 0)
            return rows.OrderBy(row => row.RouteOrdinal).ToList();

        var spec = sortSpecs.Specs;
        return spec.ColumnIndex switch
        {
            0 => SortAdvisoryPlanRowsBy(rows, row => row.Item, spec.SortDirection),
            1 => SortAdvisoryPlanRowsBy(rows, row => row.World, spec.SortDirection),
            2 => SortAdvisoryPlanRowsBy(rows, row => row.DataCenter, spec.SortDirection),
            3 => SortAdvisoryPlanRowsBy(rows, row => row.Quantity, spec.SortDirection),
            4 => SortAdvisoryPlanRowsBy(rows, row => row.Gil, spec.SortDirection),
            5 => SortAdvisoryPlanRowsBy(rows, row => row.Unit, spec.SortDirection),
            6 => SortAdvisoryPlanRowsBy(rows, row => row.IsHq, spec.SortDirection),
            7 => SortAdvisoryPlanRowsBy(rows, row => row.Listing, spec.SortDirection),
            _ => rows.OrderBy(row => row.RouteOrdinal).ToList(),
        };
    }

    private static IReadOnlyList<AdvisoryPlanRow> SortAdvisoryPlanRowsBy<TKey>(
        IReadOnlyList<AdvisoryPlanRow> rows,
        Func<AdvisoryPlanRow, TKey> keySelector,
        ImGuiSortDirection direction)
    {
        var ordered = direction == ImGuiSortDirection.Descending
            ? rows.OrderByDescending(keySelector).ThenBy(row => row.RouteOrdinal)
            : rows.OrderBy(keySelector).ThenBy(row => row.RouteOrdinal);

        return ordered.ToList();
    }

    private bool CanProbeLiveMarketBoard()
    {
        return !acquisitionRequestBusy &&
               acquisitionPlan is { Status: "Ready" } &&
               !IsAcquisitionPlanStale() &&
               acquisitionPlan.WorldBatches.Count > 0;
    }

    private bool IsAcquisitionPlanStale() =>
        acquisitionPlan is not null &&
        !string.IsNullOrWhiteSpace(currentAcquisitionPlanHash) &&
        !string.Equals(currentAcquisitionPlanHash, acquisitionRequestBuilder.CurrentIntentHash, StringComparison.Ordinal);

    private void DrawMarketBoardProbeStatus()
    {
        DrawLiveCandidatePlanResult();

        if (ImGuiUi.Button("Open Diagnostics", true))
            AcquisitionDiagnostics.IsOpen = true;
    }

    private void MonitorGuidedRoute()
    {
        if (acquisitionRequestBusy || guidedRouteProbeRunning)
            return;

        var route = marketAcquisitionRouteRunner;
        if (!route.IsRunning)
            return;

        var now = DateTimeOffset.UtcNow;
        if (now < nextGuidedRouteMonitorUtc)
            return;

        nextGuidedRouteMonitorUtc = now.AddMilliseconds(500);

        try
        {
            var activeStop = route.ActiveStop;
            if (activeStop == null)
                return;

            if (string.Equals(activeStop.Status, "Pending", StringComparison.OrdinalIgnoreCase))
            {
                if (route.MarketBoardCloseRequiredBeforeTravel)
                {
                    if (TryCloseMarketBoardWindows())
                    {
                        nextGuidedRouteMonitorUtc = DateTimeOffset.UtcNow.AddMilliseconds(250);
                        return;
                    }

                    route.RecordMarketBoardClosedBeforeTravel();
                    nextGuidedRouteMonitorUtc = DateTimeOffset.UtcNow.AddMilliseconds(500);
                    return;
                }

                var pendingCurrentWorld = playerState.CurrentWorld.IsValid ? GetCurrentWorldName() : null;
                if (playerState.CurrentWorld.IsValid &&
                    !activeStop.WorldName.Equals(pendingCurrentWorld, StringComparison.OrdinalIgnoreCase) &&
                    !EnsureRouteTravelUiIsClear(route))
                {
                    nextGuidedRouteMonitorUtc = DateTimeOffset.UtcNow.AddMilliseconds(500);
                    return;
                }

                route.PreparePendingStopForCurrentWorld(
                    playerState.CurrentWorld.IsValid,
                    pendingCurrentWorld,
                    Plugin.CommandManager.ProcessCommand);
                nextGuidedRouteMonitorUtc = DateTimeOffset.UtcNow.AddSeconds(2);
                return;
            }

            if (!playerState.CurrentWorld.IsValid)
            {
                route.RecordCurrentWorldUnavailable();
                return;
            }

            var currentWorld = GetCurrentWorldName();
            if (!activeStop.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
            {
                route.RecordCurrentWorld(currentWorld);
                return;
            }

            if (string.Equals(activeStop.Status, "TravelCommandSent", StringComparison.OrdinalIgnoreCase))
            {
                route.RecordCurrentWorld(currentWorld);
            }

            if (route.ActiveStop?.Status == "Arrived")
            {
                var claimed = claimedAcquisitionRequest ??
                              throw new InvalidOperationException("No dashboard request is accepted.");
                if (!route.SearchSubmitted)
                {
                    var approachResult = marketBoardApproachService.OpenOrApproach();
                    route.RecordMarketBoardApproach(approachResult);
                    if (approachResult.MarketBoardTravelNeeded)
                    {
                        if (!EnsureRouteTravelUiIsClear(route))
                        {
                            nextGuidedRouteMonitorUtc = DateTimeOffset.UtcNow.AddMilliseconds(500);
                            return;
                        }

                        route.ExecuteMarketBoardTravelCommand(Plugin.CommandManager.ProcessCommand);
                        nextGuidedRouteMonitorUtc = DateTimeOffset.UtcNow.AddMilliseconds(750);
                        return;
                    }

                    if (!approachResult.ReadyToSearch)
                    {
                        nextGuidedRouteMonitorUtc = DateTimeOffset.UtcNow.AddMilliseconds(250);
                        return;
                    }

                    var activeLine = GetActiveRouteLine(claimed);
                    var searchResult = marketBoardItemSearchDriver.Search(activeLine.ItemId, activeLine.ItemName);
                    route.RecordSearchResult(searchResult);

                    if (!searchResult.ReadyForListings)
                    {
                        nextGuidedRouteMonitorUtc = DateTimeOffset.UtcNow.AddMilliseconds(500);
                        return;
                    }

                    nextGuidedRouteMonitorUtc = DateTimeOffset.UtcNow;
                }

                route.BeginProbe($"Arrived on {currentWorld}. Reading live listings for {FormatAcquisitionItem(GetActiveRouteLine(claimed))}.");
                guidedRouteProbeRunning = true;
                _ = ProbeGuidedRouteMarketBoardAsync();
            }

            if (route.ActiveStop is { Status: "Purchasing" } &&
                marketBoardAutomationController.PurchaseSession?.IsActive != true)
            {
                BeginNextWorldPurchase();
                nextGuidedRouteMonitorUtc = DateTimeOffset.UtcNow.AddMilliseconds(500);
            }
        }
        catch (Exception ex)
        {
            route.FailRoute($"Unable to monitor guided route. {ex.Message}", ex);
            log.Warning(ex, "[MarketMafioso] Unable to monitor guided market acquisition route.");
        }
        finally
        {
            ReportGuidedRouteProgress();
        }
    }

    private static bool EnsureRouteTravelUiIsClear(MarketAcquisitionRouteRunner route)
    {
        var preflight = AutomationTravelPreflight.Check(GetOpenRouteTravelBlockingAddons());
        if (preflight.CanSendCommand)
            return true;

        route.RecordTravelBlockedByUi(preflight);
        return false;
    }

    private static IReadOnlyList<string> GetOpenRouteTravelBlockingAddons()
    {
        return AutomationTravelPreflight.BlockingAddonNames
            .Where(IsAddonOpen)
            .ToArray();
    }

    private static unsafe bool TryCloseMarketBoardWindows()
    {
        var closeRequested = false;
        closeRequested |= TryCloseAddon("ItemSearchResult");
        closeRequested |= TryCloseAddon("ItemSearch");
        return closeRequested || IsAddonOpen("ItemSearchResult") || IsAddonOpen("ItemSearch");
    }

    private static unsafe bool TryCloseAddon(string addonName)
    {
        var addon = Plugin.GameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
        if (!IsAddonOpen(addon))
            return false;

        addon->Close(true);
        return true;
    }

    private static unsafe bool IsAddonOpen(string addonName)
    {
        return IsAddonOpen(Plugin.GameGui.GetAddonByName<AtkUnitBase>(addonName, 1));
    }

    private static unsafe bool IsAddonOpen(AtkUnitBase* addon)
    {
        return addon != null && addon->IsReady && addon->IsVisible;
    }

    private Task ProbeGuidedRouteMarketBoardAsync()
    {
        try
        {
            ProbeLiveMarketBoardCore();

            var activeStop = marketAcquisitionRouteRunner.ActiveStop;
            if (activeStop is { Status: "Arrived" } &&
                !string.Equals(marketBoardReadResult?.Status, "Ready", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(marketBoardReadResult?.Status, "NoSearchItem", StringComparison.OrdinalIgnoreCase))
                    marketAcquisitionRouteRunner.ClearSearchSubmission("Market board results did not expose a searched item id.");

                marketAcquisitionRouteRunner.BeginProbe(
                    $"Arrived on {activeStop.WorldName}; waiting for live listings. {marketBoardReadResult?.Message ?? "Market board read has not completed."}");
            }
        }
        catch (Exception ex)
        {
            var activeStop = marketAcquisitionRouteRunner.ActiveStop;
            var activeLine = claimedAcquisitionRequest == null ? null : GetActiveRouteLine(claimedAcquisitionRequest);
            var itemLabel = activeLine == null ? "active item" : FormatAcquisitionItem(activeLine);
            var worldLabel = activeStop?.WorldName ?? GetCurrentWorldName();
            var message = $"Live market board probe failed for {itemLabel} on {worldLabel}. {ex.Message}";
            marketAcquisitionRouteRunner.FailRoute(message, ex);
            acquisitionStatus = message;
            log.Warning(ex, "[MarketMafioso] Market acquisition route probe failed.");
        }
        finally
        {
            if (marketAcquisitionRouteRunner.ActiveStop?.Status != "Arrived")
                marketAcquisitionRouteRunner.ClearSearchSubmission("Route advanced or stopped before the next live listing read.");

            guidedRouteProbeRunning = false;
            nextGuidedRouteMonitorUtc = DateTimeOffset.UtcNow.AddMilliseconds(500);
            ReportGuidedRouteProgress();
        }

        return Task.CompletedTask;
    }

    private void DrawLiveCandidatePlanResult()
    {
        if (marketAcquisitionLiveCandidatePlan == null)
            return;

        ImGui.Spacing();
        ImGui.TextColored(
            marketAcquisitionLiveCandidatePlan.Status == "Ready" ? ColSuccess : ColHeader,
            $"Live candidates: {marketAcquisitionLiveCandidatePlan.Status}  -  Would buy {marketAcquisitionLiveCandidatePlan.WouldBuyQuantity:N0}/{marketAcquisitionLiveCandidatePlan.RequestedQuantity:N0}, spend {FormatGil(marketAcquisitionLiveCandidatePlan.WouldSpendGil)}");
        ImGui.TextWrapped(marketAcquisitionLiveCandidatePlan.Message);

        var summary = MarketAcquisitionLiveCandidatePresenter.BuildSummary(marketAcquisitionLiveCandidatePlan);
        ImGui.TextColored(ColMuted, $"{summary.WouldBuyRows:N0} buy row(s), {summary.SkippedRows:N0} skipped row(s).");

        var purchaseActive = marketBoardAutomationController.PurchaseSession?.IsActive == true;
        var activeStop = marketAcquisitionRouteRunner.ActiveStop;
        var purchasingWorld = activeStop is { Status: "Purchasing" };

        var firstCandidate = MarketBoardPurchasePlanner.SelectFirstCandidate(marketAcquisitionLiveCandidatePlan);
        if (firstCandidate != null && purchasingWorld)
        {
            ImGui.TextColored(
                ColMuted,
                $"Next safe listing: {firstCandidate.Quantity:N0} @ {FormatGil(firstCandidate.UnitPrice)} ({FormatGil(firstCandidate.TotalGil)})");
        }

        if (purchasingWorld)
            ImGui.TextColored(ColHeader, $"World batch running: purchased {activeWorldPurchasedQuantity:N0}, spent {FormatGil(activeWorldSpentGil)}.");

        var purchaseSession = marketBoardAutomationController.PurchaseSession;
        var purchaseResult = marketBoardAutomationController.LastPurchaseResult;
        if (purchaseSession != null)
        {
            ImGui.TextColored(
                purchaseSession.Status is "Completed" ? ColSuccess :
                purchaseSession.IsActive ? ColHeader : ColError,
                $"Purchase status: {purchaseSession.Status} - {purchaseSession.Message}");
        }
        else if (purchaseResult != null)
        {
            ImGui.TextColored(
                purchaseResult.Status is "PurchaseSelectionSent" or "ConfirmationSubmitted" ? ColHeader : ColError,
                $"Purchase status: {purchaseResult.Status} - {purchaseResult.Message}");
        }
    }

    private void BeginNextWorldPurchase()
    {
        var activeStop = marketAcquisitionRouteRunner.ActiveStop;
        if (activeStop is not { Status: "Purchasing" })
            return;

        var claimed = claimedAcquisitionRequest ??
                      throw new InvalidOperationException("No dashboard request is accepted.");
        var activeLine = GetActiveRouteLine(claimed);
        var plan = acquisitionPlan ??
                   throw new InvalidOperationException("No market acquisition plan is prepared.");
        var currentWorld = GetCurrentWorldName();
        if (!activeStop.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Cannot purchase on {currentWorld}; active route stop is {activeStop.WorldName}.");

        if (!string.Equals(activeWorldPurchaseBatchWorld, activeStop.WorldName, StringComparison.OrdinalIgnoreCase))
        {
            activeWorldPurchaseBatchWorld = activeStop.WorldName;
            activeWorldPurchasedQuantity = 0;
            activeWorldSpentGil = 0;
        }

        var activeLineId = GetActiveRouteLineId(claimed);
        if (!string.Equals(activePurchaseLineId, activeLineId, StringComparison.Ordinal))
        {
            activePurchaseLineId = activeLineId;
            activeLinePurchasedQuantity = 0;
            activeLineSpentGil = 0;
            if (activeStop.ActiveItemSubtask != null)
            {
                ReportAcquisitionLineProgress(
                    activeStop.ActiveItemSubtask,
                    "Running",
                    activeLinePurchasedQuantity,
                    activeLineSpentGil,
                    $"Started purchasing {FormatAcquisitionItem(activeLine)} on {activeStop.WorldName}.");
            }
        }

        var freshRead = marketBoardListingReader.ReadCurrentListings(currentWorld);
        marketBoardReadResult = freshRead;
        if (!freshRead.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase))
        {
            if (!freshRead.IsFresh)
            {
                marketAcquisitionRouteRunner.RecordListingReadPending(currentWorld, freshRead);
                acquisitionStatus = $"Waiting for fresh market listings. {freshRead.Message}";
                nextGuidedRouteMonitorUtc = DateTimeOffset.UtcNow.AddMilliseconds(500);
                return;
            }

            throw new InvalidOperationException(freshRead.Message);
        }

        var candidatePurchaseTotals = ResolveActiveRouteLinePurchaseTotals(activeStop.ActiveItemSubtask);
        marketAcquisitionLiveCandidatePlan = activeStop.ActiveItemSubtask == null
            ? MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(
                activeLine,
                plan,
                currentWorld,
                freshRead,
                candidatePurchaseTotals.PurchasedQuantity,
                candidatePurchaseTotals.SpentGil)
            : MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(
                activeLine,
                plan,
                activeStop.ActiveItemSubtask,
                currentWorld,
                freshRead,
                candidatePurchaseTotals.PurchasedQuantity,
                candidatePurchaseTotals.SpentGil);
        var purchaseResult = marketBoardPurchaseExecutor.ExecuteFirstCandidate(
            marketAcquisitionLiveCandidatePlan,
            freshRead);
        var now = DateTimeOffset.UtcNow;
        marketBoardAutomationController.RecordPurchaseSelection(
            purchaseResult,
            now,
            MarketBoardPurchaseConfirmationWatchdog);
        marketAcquisitionRouteRunner.RecordAutomationSnapshot(CreatePurchaseSelectionSnapshot(purchaseResult));

        if (purchaseResult.Status.Equals("NoCandidate", StringComparison.OrdinalIgnoreCase))
        {
            if (ShouldFailWorldPurchaseBatchOnNoCandidate(marketAcquisitionLiveCandidatePlan))
            {
                marketAcquisitionRouteRunner.FailRoute(marketAcquisitionLiveCandidatePlan.Message);
                acquisitionStatus = marketAcquisitionRouteRunner.StatusMessage;
                ReportGuidedRouteProgress();
                return;
            }

            CompleteActiveWorldPurchaseBatch(currentWorld);
            return;
        }

        if (ClassifyPurchaseSelectionOutcome(purchaseResult.Status) == MarketBoardAutomationOutcome.Recoverable)
        {
            acquisitionStatus = $"Purchase: {purchaseResult.Status}. {purchaseResult.Message}";
            nextGuidedRouteMonitorUtc = DateTimeOffset.UtcNow.AddMilliseconds(250);
            return;
        }

        if (!purchaseResult.Status.Equals("PurchaseSelectionSent", StringComparison.OrdinalIgnoreCase) ||
            purchaseResult.Candidate == null)
        {
            marketAcquisitionRouteRunner.FailRoute($"World purchase batch stopped: {purchaseResult.Message}");
            acquisitionStatus = marketAcquisitionRouteRunner.StatusMessage;
            ReportGuidedRouteProgress();
            return;
        }

        marketBoardAutomationController.ScheduleNextMonitor(now, MarketBoardPurchaseInitialMonitorDelay);
        acquisitionStatus = $"Purchase: {purchaseResult.Status}. {purchaseResult.Message}";
    }

    private void CompleteActiveWorldPurchaseBatch(string currentWorld)
    {
        var activeSubtask = marketAcquisitionRouteRunner.ActiveStop?.ActiveItemSubtask;
        if (activeSubtask != null && claimedAcquisitionRequest != null)
        {
            var lineStatus = ResolveZeroPurchaseLineStatus(marketAcquisitionLiveCandidatePlan, activeLinePurchasedQuantity, activeLineSpentGil);
            ReportAcquisitionLineProgress(
                activeSubtask,
                lineStatus,
                activeLinePurchasedQuantity,
                activeLineSpentGil,
                $"Completed {FormatAcquisitionItem(GetActiveRouteLine(claimedAcquisitionRequest))} on {currentWorld}: purchased {activeLinePurchasedQuantity:N0}, spent {FormatGil(activeLineSpentGil)}.");
        }

        var result = marketAcquisitionRouteRunner.RecordWorldPurchaseBatchComplete(
            currentWorld,
            activeSubtask == null ? activeWorldPurchasedQuantity : activeLinePurchasedQuantity,
            activeSubtask == null ? activeWorldSpentGil : activeLineSpentGil,
            activeLinePurchasedQuantity == 0 && activeLineSpentGil == 0
                ? ResolveZeroPurchaseLineStatus(marketAcquisitionLiveCandidatePlan, activeLinePurchasedQuantity, activeLineSpentGil)
                : null,
            activeLinePurchasedQuantity == 0 && activeLineSpentGil == 0
                ? marketAcquisitionLiveCandidatePlan?.Message
                : null);
        acquisitionStatus = result.Message;
        ClearMarketBoardAutomationState();
        var nextStop = marketAcquisitionRouteRunner.ActiveStop;
        if (nextStop == null ||
            !nextStop.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
        {
            activeWorldPurchasedQuantity = 0;
            activeWorldSpentGil = 0;
            activeWorldPurchaseBatchWorld = null;
            activePurchaseLineId = null;
            activeLinePurchasedQuantity = 0;
            activeLineSpentGil = 0;
        }
        else
        {
            var nextSubtask = nextStop.ActiveItemSubtask;
            if (nextSubtask == null)
            {
                activePurchaseLineId = null;
                activeLinePurchasedQuantity = 0;
                activeLineSpentGil = 0;
            }
            else if (activeSubtask != null &&
                     !string.Equals(activeSubtask.LineId, nextSubtask.LineId, StringComparison.Ordinal))
            {
                ResetMarketBoardStateForNextRouteItem(
                    $"Advancing from {activeSubtask.ItemName ?? activeSubtask.LineId} to {nextSubtask.ItemName ?? nextSubtask.LineId} on {currentWorld}.");
            }

            activeWorldPurchaseBatchWorld = nextStop.WorldName;
        }

        ReportGuidedRouteProgress();
        if (result.Success &&
            marketAcquisitionRouteRunner.LatestWorldCompletionSummary?.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase) == true)
        {
            ReportUniversalisFreshnessAsync();
        }
    }

    private void ResetMarketBoardStateForNextRouteItem(string reason)
    {
        marketBoardReadResult = null;
        marketBoardReconciliation = null;
        marketAcquisitionLiveCandidatePlan = null;
        ClearMarketBoardAutomationState();
        marketAcquisitionRouteRunner.ClearSearchSubmission(reason);
        TryCloseMarketBoardWindows();
        nextGuidedRouteMonitorUtc = DateTimeOffset.UtcNow.AddMilliseconds(250);
    }

    private void ClearMarketBoardAutomationState()
    {
        marketBoardAutomationController.Clear();
    }

    private void ReportUniversalisFreshnessAsync()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(UniversalisFreshnessVerificationDelay)
                    .ConfigureAwait(false);
                await marketAcquisitionRouteRunner.VerifyLatestWorldFreshnessAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[MarketMafioso] Unable to record Universalis freshness diagnostics.");
            }
        });
    }

    private void MonitorMarketBoardPurchase()
    {
        if (acquisitionRequestBusy)
            return;

        var session = marketBoardAutomationController.PurchaseSession;
        if (session?.IsActive != true)
            return;

        var now = DateTimeOffset.UtcNow;
        if (!marketBoardAutomationController.IsMonitorDue(now))
            return;

        try
        {
            var tick = marketBoardAutomationController.MonitorPurchase(
                now,
                MarketBoardPurchaseMonitorInterval,
                MarketBoardPurchaseListingRemovalWatchdog,
                candidate => marketBoardPurchaseAdapter.TryConfirmPendingPurchase(candidate),
                () => marketBoardListingReader.ReadCurrentListings(GetCurrentWorldName()));

            if (!tick.DidWork)
                return;

            if (tick.ConfirmationResult != null)
            {
                var candidate = tick.ConfirmationResult.Candidate ?? session.Candidate;
                marketAcquisitionRouteRunner.RecordAutomationSnapshot(CreatePurchaseConfirmationSnapshot(tick.ConfirmationResult, candidate));
            }

            if (tick.FreshRead != null)
            {
                marketBoardReadResult = tick.FreshRead;
                if (tick.FreshReadSession != null)
                    marketAcquisitionRouteRunner.RecordAutomationSnapshot(tick.FreshReadSession.CreateFreshReadSnapshot(tick.FreshRead));
            }

            session = tick.Session ?? session;
            acquisitionStatus = $"Purchase: {session.Status}. {session.Message}";
            if (session.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase))
            {
                var completedCandidate = session.Candidate;
                activeWorldPurchasedQuantity = checked(activeWorldPurchasedQuantity + session.Candidate.Quantity);
                activeWorldSpentGil = checked(activeWorldSpentGil + session.Candidate.TotalGil);
                activeLinePurchasedQuantity = checked(activeLinePurchasedQuantity + completedCandidate.Quantity);
                activeLineSpentGil = checked(activeLineSpentGil + completedCandidate.TotalGil);
                ReportConfirmedPurchase(completedCandidate, activeLinePurchasedQuantity, activeLineSpentGil);
                ClearMarketBoardAutomationState();
                if (marketBoardReadResult?.Status is "MarketBoardNotOpen" or "NoListings")
                    CompleteActiveWorldPurchaseBatch(GetCurrentWorldName());
                else
                    BeginNextWorldPurchase();
            }
            else if (!session.IsActive)
            {
                marketAcquisitionRouteRunner.FailRoute($"World purchase batch stopped: {session.Message}");
                acquisitionStatus = marketAcquisitionRouteRunner.StatusMessage;
                ReportGuidedRouteProgress();
            }
        }
        catch (Exception ex)
        {
            marketBoardAutomationController.RecordMonitorFailure("PurchaseMonitorFailed", ex.Message);
            acquisitionStatus = $"Purchase monitor failed: {ex.Message}";
            log.Warning(ex, "[MarketMafioso] Unable to monitor guarded market-board purchase.");
        }
    }

    private static MarketBoardAutomationSnapshot CreatePurchaseConfirmationSnapshot(
        MarketBoardPurchaseResult result,
        MarketBoardPurchaseCandidate candidate)
    {
        return MarketBoardAutomationSnapshot.Create(
            "BuyListing",
            "Confirmation",
            "PurchasePrompt",
            result.Status,
            ClassifyPurchaseConfirmationOutcome(result.Status),
            ChoosePurchaseConfirmationNextAction(result.Status),
            new Dictionary<string, string?>
            {
                ["candidateItemId"] = candidate.ItemId.ToString(),
                ["candidateWorld"] = candidate.WorldName,
                ["candidateListingId"] = candidate.ListingId,
                ["candidateRetainerId"] = candidate.RetainerId,
                ["candidateQuantity"] = candidate.Quantity.ToString(),
                ["candidateUnitPrice"] = candidate.UnitPrice.ToString(),
                ["candidateTotalGil"] = candidate.TotalGil.ToString(),
                ["confirmationAddon"] = result.ConfirmationAddonName,
                ["confirmationPromptText"] = result.ConfirmationPromptText,
            });
    }

    private static MarketBoardAutomationSnapshot CreatePurchaseSelectionSnapshot(MarketBoardPurchaseResult result)
    {
        var details = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["resultMessage"] = result.Message,
        };

        if (result.Candidate != null)
        {
            details["candidateItemId"] = result.Candidate.ItemId.ToString();
            details["candidateWorld"] = result.Candidate.WorldName;
            details["candidateListingId"] = result.Candidate.ListingId;
            details["candidateRetainerId"] = result.Candidate.RetainerId;
            details["candidateQuantity"] = result.Candidate.Quantity.ToString();
            details["candidateUnitPrice"] = result.Candidate.UnitPrice.ToString();
            details["candidateTotalGil"] = result.Candidate.TotalGil.ToString();
        }

        foreach (var pair in result.Diagnostics)
            details[pair.Key] = pair.Value;

        return MarketBoardAutomationSnapshot.Create(
            "BuyListing",
            "Selection",
            "ClickableMarketBoardListing",
            result.Status,
            ClassifyPurchaseSelectionOutcome(result.Status),
            ChoosePurchaseSelectionNextAction(result.Status),
            details);
    }

    private static MarketBoardAutomationOutcome ClassifyPurchaseSelectionOutcome(string status)
    {
        return status switch
        {
            "PurchaseSelectionSent" => MarketBoardAutomationOutcome.InProgress,
            "NoCandidate" => MarketBoardAutomationOutcome.ExpectedAlternate,
            "MarketBoardNotOpen" => MarketBoardAutomationOutcome.Recoverable,
            "InfoProxyUnavailable" => MarketBoardAutomationOutcome.Recoverable,
            "ListingListUnavailable" => MarketBoardAutomationOutcome.Recoverable,
            "ListingListNotReady" => MarketBoardAutomationOutcome.Recoverable,
            _ => MarketBoardAutomationOutcome.Fatal,
        };
    }

    private static string ChoosePurchaseSelectionNextAction(string status)
    {
        return status switch
        {
            "PurchaseSelectionSent" => "WaitForConfirmation",
            "NoCandidate" => "CompleteWorldBatch",
            "MarketBoardNotOpen" => "ReopenMarketBoard",
            "InfoProxyUnavailable" => "RetryPurchaseSelection",
            "ListingListUnavailable" => "InspectListComponentState",
            "ListingListNotReady" => "RetryPurchaseSelection",
            _ => "StopRoute",
        };
    }

    private static MarketBoardAutomationOutcome ClassifyPurchaseConfirmationOutcome(string status)
    {
        return status switch
        {
            "ConfirmationSubmitted" => MarketBoardAutomationOutcome.InProgress,
            "ConfirmationPending" => MarketBoardAutomationOutcome.InProgress,
            "UnexpectedConfirmation" => MarketBoardAutomationOutcome.Fatal,
            _ => MarketBoardAutomationOutcome.Recoverable,
        };
    }

    private static string ChoosePurchaseConfirmationNextAction(string status)
    {
        return status switch
        {
            "ConfirmationSubmitted" => "VerifyListingRemoval",
            "ConfirmationPending" => "ContinueMonitoring",
            "UnexpectedConfirmation" => "CaptureInputState",
            _ => "ContinueMonitoring",
        };
    }

    private static void DrawClaimedRequestRow(string label, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextColored(ColMuted, label);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(value);
    }

    private async Task FetchDashboardRequestsAsync()
    {
        await RunAcquisitionRequestAsync(async token =>
        {
            if (!TryGetAcquisitionScope(out var characterName, out var world))
                throw new InvalidOperationException("Character scope is unavailable.");

            pendingAcquisitionRequests = await acquisitionClient.FetchPendingAsync(
                config.ServerUrl,
                config.ApiKey,
                characterName,
                world,
                token).ConfigureAwait(false);

            acquisitionStatus = pendingAcquisitionRequests.Count == 0
                ? "No matching dashboard requests."
                : $"Loaded {pendingAcquisitionRequests.Count} dashboard batch(es).";
        }).ConfigureAwait(false);
    }

    private async Task<MarketAcquisitionRequestBuilderSyncOutcome> SyncAcquisitionRequestBuilderAsync(
        MarketAcquisitionRequestDocument document)
    {
        if (IsMarketAcquisitionRouteActive())
            throw new InvalidOperationException("Stop the guided route before replacing request intent.");
        if (!TryGetAcquisitionScope(out var characterName, out var world))
            throw new InvalidOperationException("Character scope is unavailable.");

        MarketAcquisitionRequestSyncResult? syncResult = null;
        await RunAcquisitionRequestAsync(async token =>
        {
            var service = new MarketAcquisitionRequestSyncService(acquisitionClient);
            syncResult = await service.SyncAsync(
                new MarketAcquisitionRequestSyncRequest(
                    config.ServerUrl,
                    config.ApiKey,
                    characterName,
                    world,
                    config.PluginInstanceId,
                    document,
                    claimedAcquisitionRequest),
                token).ConfigureAwait(false);

            claimedAcquisitionRequest = syncResult.Claim;
            if (!string.IsNullOrWhiteSpace(syncResult.AcceptIdempotencyKey))
                claimedAcceptIdempotencyKey = syncResult.AcceptIdempotencyKey;
            claimedRejectIdempotencyKey ??= Guid.NewGuid().ToString("N");
            MarketAcquisitionClaimPersistence.Save(
                config,
                claimedAcquisitionRequest,
                claimedAcceptIdempotencyKey,
                claimedRejectIdempotencyKey);
            config.Save();
            ClearPreparedAcquisitionPlan();
            pendingAcquisitionRequests = pendingAcquisitionRequests
                .Where(request => !string.Equals(request.Id, claimedAcquisitionRequest.Id, StringComparison.Ordinal))
                .ToList();
            acquisitionStatus = syncResult.WasReplacement
                ? "Request updated. Prepare a fresh advisory plan when ready."
                : "Request synced, claimed, and accepted. Prepare an advisory plan when ready.";
        }).ConfigureAwait(false);

        if (syncResult is null)
            throw new InvalidOperationException("Request sync did not complete.");

        return new MarketAcquisitionRequestBuilderSyncOutcome(syncResult.Document, acquisitionStatus);
    }

    private async Task<MarketAcquisitionRequestBuilderRefreshOutcome> RefreshAcquisitionRequestBuilderRemoteAsync(
        MarketAcquisitionRequestDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.RemoteRequestId))
            throw new InvalidOperationException("Sync the request before refreshing remote state.");

        MarketAcquisitionRequestView? remote = null;
        await RunAcquisitionRequestAsync(async token =>
        {
            remote = await acquisitionClient.GetBatchAsync(
                config.ServerUrl,
                config.ApiKey,
                document.RemoteRequestId,
                token).ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (remote is null)
            throw new InvalidOperationException("Remote request refresh did not complete.");

        var remoteDocument = MarketAcquisitionRequestDocumentMapper.FromRequestView(remote);
        var currentHash = MarketAcquisitionRequestDocumentHasher.ComputeIntentHash(document);
        var hasLocalEdits = !string.Equals(currentHash, document.LastSyncedHash, StringComparison.Ordinal);
        var remoteHash = MarketAcquisitionRequestDocumentHasher.ComputeIntentHash(remoteDocument);
        if (!hasLocalEdits)
        {
            OnAcquisitionRequestBuilderDocumentAdopted(remoteDocument, remote);
            acquisitionStatus = "Remote request refreshed and adopted.";
            return new MarketAcquisitionRequestBuilderRefreshOutcome(
                remoteDocument,
                RemoteDocument: null,
                RemoteRequest: null,
                acquisitionStatus);
        }

        var marked = document with
        {
            RemoteRevision = remote.Revision,
            RemoteHash = remoteHash,
            SyncStatus = "RemoteChanged",
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        acquisitionStatus = "Remote request changed while local edits are present.";
        return new MarketAcquisitionRequestBuilderRefreshOutcome(
            marked,
            remoteDocument,
            remote,
            acquisitionStatus);
    }

    private void OnAcquisitionRequestBuilderDocumentAdopted(
        MarketAcquisitionRequestDocument document,
        MarketAcquisitionRequestView? remoteRequest)
    {
        if (remoteRequest is not null &&
            claimedAcquisitionRequest is not null &&
            string.Equals(remoteRequest.Id, claimedAcquisitionRequest.Id, StringComparison.Ordinal))
        {
            claimedAcquisitionRequest = MarketAcquisitionRequestDocumentMapper.MergeClaimWithRequest(
                claimedAcquisitionRequest,
                remoteRequest);
            MarketAcquisitionClaimPersistence.Save(
                config,
                claimedAcquisitionRequest,
                claimedAcceptIdempotencyKey,
                claimedRejectIdempotencyKey);
            config.Save();
            ClearPreparedAcquisitionPlan();
        }
    }

    private async Task ClaimAcquisitionRequestAsync(string requestId)
    {
        await RunAcquisitionRequestAsync(async token =>
        {
            if (!TryGetAcquisitionScope(out var characterName, out var world))
                throw new InvalidOperationException("Character scope is unavailable.");

            claimedAcquisitionRequest = await acquisitionClient.ClaimAsync(
                config.ServerUrl,
                config.ApiKey,
                requestId,
                characterName,
                world,
                config.PluginInstanceId,
                token).ConfigureAwait(false);

            claimedAcceptIdempotencyKey = Guid.NewGuid().ToString("N");
            claimedRejectIdempotencyKey = Guid.NewGuid().ToString("N");
            MarketAcquisitionClaimPersistence.Save(
                config,
                claimedAcquisitionRequest,
                claimedAcceptIdempotencyKey,
                claimedRejectIdempotencyKey);
            config.Save();
            acquisitionRequestBuilder.AdoptRequest(claimedAcquisitionRequest);
            ClearPreparedAcquisitionPlan();
            pendingAcquisitionRequests = pendingAcquisitionRequests
                .Where(request => !string.Equals(request.Id, requestId, StringComparison.Ordinal))
                .ToList();
            acquisitionStatus = "Dashboard batch claimed. Review it before accepting.";
        }).ConfigureAwait(false);
    }

    private async Task AcceptClaimedAcquisitionRequestAsync()
    {
        await RunAcquisitionRequestAsync(async token =>
        {
            var claimed = claimedAcquisitionRequest ??
                          throw new InvalidOperationException("No dashboard request is claimed.");
            claimedAcceptIdempotencyKey ??= Guid.NewGuid().ToString("N");

            var accepted = await acquisitionClient.AcceptAsync(
                config.ServerUrl,
                config.ApiKey,
                claimed.Id,
                claimed.ClaimToken,
                claimedAcceptIdempotencyKey,
                token).ConfigureAwait(false);

            claimedAcquisitionRequest = MarketAcquisitionRequestDocumentMapper.MergeClaimWithRequest(claimed, accepted);
            MarketAcquisitionClaimPersistence.Save(
                config,
                claimedAcquisitionRequest,
                claimedAcceptIdempotencyKey,
                claimedRejectIdempotencyKey);
            config.Save();
            acquisitionRequestBuilder.AdoptRequest(claimedAcquisitionRequest);
            ClearPreparedAcquisitionPlan();
            acquisitionStatus = "Request accepted locally. Prepare an advisory plan when ready.";
        }).ConfigureAwait(false);
    }

    private async Task RejectClaimedAcquisitionRequestAsync()
    {
        await RunAcquisitionRequestAsync(async token =>
        {
            var claimed = claimedAcquisitionRequest ??
                          throw new InvalidOperationException("No dashboard request is claimed.");
            claimedRejectIdempotencyKey ??= Guid.NewGuid().ToString("N");

            var rejected = await acquisitionClient.RejectAsync(
                config.ServerUrl,
                config.ApiKey,
                claimed.Id,
                claimed.ClaimToken,
                claimedRejectIdempotencyKey,
                "Rejected in the MarketMafioso plugin.",
                token).ConfigureAwait(false);

            claimedAcquisitionRequest = claimed with { Status = rejected.Status };
            MarketAcquisitionClaimPersistence.Clear(config);
            config.Save();
            claimedAcquisitionRequest = null;
            ClearPreparedAcquisitionPlan();
            acquisitionStatus = "Request rejected.";
        }).ConfigureAwait(false);
    }

    private void ForgetLocalAcquisitionRequest()
    {
        MarketAcquisitionClaimPersistence.Clear(config);
        config.Save();
        claimedAcquisitionRequest = null;
        claimedAcceptIdempotencyKey = null;
        claimedRejectIdempotencyKey = null;
        ClearPreparedAcquisitionPlan();
        acquisitionStatus = "Forgot local acquisition claim. Fetch dashboard requests to pick up a pending request.";
    }

    private void ClearPreparedAcquisitionPlan()
    {
        acquisitionPlan = null;
        currentAcquisitionPlanHash = null;
        marketBoardReadResult = null;
        marketBoardReconciliation = null;
        marketAcquisitionLiveCandidatePlan = null;
        ResetGuidedRoute("No guided route has started.");
    }

    private async Task PrepareMarketAcquisitionPlanAsync()
    {
        await RunAcquisitionRequestAsync(async token =>
        {
            var claimed = claimedAcquisitionRequest ??
                          throw new InvalidOperationException("No dashboard request is accepted.");
            claimed = await EnsureAcquisitionClaimReadyForPlanningAsync(claimed, token).ConfigureAwait(false);
            var currentWorld = playerState.CurrentWorld.IsValid ? GetCurrentWorldName() : string.Empty;
            var result = await marketAcquisitionPlanPreparationService.PrepareAsync(
                new MarketAcquisitionPlanPreparationRequest
                {
                    Claim = claimed,
                    CurrentWorld = currentWorld,
                    PreparedAtUtc = DateTimeOffset.UtcNow,
                    RecentWorldTtl = GetMarketAcquisitionRecentWorldTtl(),
                    IgnoreRecentWorldVisitsForSweep = config.MarketAcquisitionIgnoreRecentWorldVisitsForSweep,
                },
                token).ConfigureAwait(false);

            acquisitionPlan = result.Plan;
            currentAcquisitionPlanHash = acquisitionRequestBuilder.CurrentIntentHash;
            acquisitionRequestBuilder.MarkPlanPrepared(currentAcquisitionPlanHash);
            marketBoardReadResult = null;
            marketBoardReconciliation = null;
            marketAcquisitionLiveCandidatePlan = null;
            ResetGuidedRoute("No route has started.");
            acquisitionStatus = result.StatusMessage;
        }).ConfigureAwait(false);
    }

    private static IReadOnlyList<MarketAcquisitionBatchLineView> GetAcquisitionPlanLines(MarketAcquisitionClaimView claimed) =>
        MarketAcquisitionPlanPreparationService.GetPlanLines(claimed);

    private TimeSpan GetMarketAcquisitionRecentWorldTtl()
    {
        var ttlHours = Math.Clamp(config.MarketAcquisitionRecentWorldTtlHours, 1, 168);
        if (ttlHours != config.MarketAcquisitionRecentWorldTtlHours)
        {
            config.MarketAcquisitionRecentWorldTtlHours = ttlHours;
            config.Save();
        }

        return TimeSpan.FromHours(ttlHours);
    }

    private MarketAcquisitionRequestView GetActiveRouteLine(MarketAcquisitionRequestView claimed)
    {
        var activeStop = marketAcquisitionRouteRunner.ActiveStop;
        var activeSubtask = activeStop?.ActiveItemSubtask;
        if (activeSubtask == null)
            return claimed;

        return claimed with
        {
            ItemId = activeSubtask.ItemId,
            ItemName = activeSubtask.ItemName,
            QuantityMode = activeSubtask.QuantityMode,
            Quantity = activeSubtask.RequestedQuantity,
            HqPolicy = activeSubtask.HqPolicy,
            MaxUnitPrice = activeSubtask.MaxUnitPrice,
            MaxTotalGil = activeSubtask.GilCap,
        };
    }

    private string GetActiveRouteLineId(MarketAcquisitionClaimView claimed)
    {
        var activeSubtask = marketAcquisitionRouteRunner.ActiveStop?.ActiveItemSubtask;
        if (!string.IsNullOrWhiteSpace(activeSubtask?.LineId))
            return activeSubtask.LineId;

        var firstLine = GetAcquisitionPlanLines(claimed).FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstLine?.LineId)
            ? claimed.Id
            : firstLine.LineId;
    }

    private async Task<MarketAcquisitionClaimView> EnsureAcquisitionClaimReadyForPlanningAsync(
        MarketAcquisitionClaimView claimed,
        CancellationToken token)
    {
        if (!IsFailedAcquisitionStatus(claimed.Status))
            return claimed;

        await acquisitionClient.ResendAsync(
            config.ServerUrl,
            config.ApiKey,
            claimed.Id,
            token).ConfigureAwait(false);

        var reclaimed = await acquisitionClient.ClaimAsync(
            config.ServerUrl,
            config.ApiKey,
            claimed.Id,
            claimed.TargetCharacterName,
            claimed.TargetWorld,
            config.PluginInstanceId,
            token).ConfigureAwait(false);

        claimedAcceptIdempotencyKey = Guid.NewGuid().ToString("N");
        claimedRejectIdempotencyKey = Guid.NewGuid().ToString("N");
        var accepted = await acquisitionClient.AcceptAsync(
            config.ServerUrl,
            config.ApiKey,
            reclaimed.Id,
            reclaimed.ClaimToken,
            claimedAcceptIdempotencyKey,
            token).ConfigureAwait(false);

        claimedAcquisitionRequest = reclaimed with { Status = accepted.Status };
        MarketAcquisitionClaimPersistence.Save(
            config,
            claimedAcquisitionRequest,
            claimedAcceptIdempotencyKey,
            claimedRejectIdempotencyKey);
        config.Save();
        pendingAcquisitionRequests = pendingAcquisitionRequests
            .Where(request => !string.Equals(request.Id, reclaimed.Id, StringComparison.Ordinal))
            .ToList();
        acquisitionStatus = "Failed request was reopened and accepted locally. Preparing a fresh plan.";
        return claimedAcquisitionRequest;
    }

    private static string ResolveZeroPurchaseLineStatus(
        MarketAcquisitionLiveCandidatePlan? candidatePlan,
        uint purchasedQuantity,
        uint spentGil)
    {
        if (purchasedQuantity > 0 || spentGil > 0)
            return "Complete";

        return candidatePlan?.Status.Equals("VisibleCacheExhausted", StringComparison.OrdinalIgnoreCase) == true
            ? "SkippedVisibleCacheExhausted"
            : "SkippedNoLiveStock";
    }

    internal static bool ShouldFailWorldPurchaseBatchOnNoCandidate(MarketAcquisitionLiveCandidatePlan? candidatePlan) =>
        candidatePlan?.Status.Equals("VisibleCacheExhausted", StringComparison.OrdinalIgnoreCase) == true;

    private Task ProbeLiveMarketBoardAsync()
    {
        return RunAcquisitionRequestAsync(_ =>
        {
            ProbeLiveMarketBoardCore();
            return Task.CompletedTask;
        });
    }

    private void ProbeLiveMarketBoardCore()
    {
        var plan = acquisitionPlan ??
                   throw new InvalidOperationException("Prepare a live candidate plan before probing live market board listings.");
        var claimed = claimedAcquisitionRequest ??
                      throw new InvalidOperationException("No dashboard request is accepted.");
        var activeLine = GetActiveRouteLine(claimed);
        var activeSubtask = marketAcquisitionRouteRunner.ActiveStop?.ActiveItemSubtask;
        var currentWorld = GetCurrentWorldName();
        marketBoardReconciliation = null;
        marketAcquisitionLiveCandidatePlan = null;
        marketBoardReadResult = marketBoardListingReader.ReadCurrentListings(currentWorld);

        var canBuildLiveCandidatePlan = marketBoardReadResult.Status is "Ready" or "NoListings";
        marketBoardReconciliation = marketBoardReadResult.Status == "Ready"
            ? activeSubtask == null
                ? MarketBoardListingReconciler.Reconcile(
                    plan,
                    currentWorld,
                    marketBoardReadResult.ItemId,
                    marketBoardReadResult.Listings)
                : MarketBoardListingReconciler.Reconcile(
                    plan,
                    activeSubtask,
                    currentWorld,
                    marketBoardReadResult.ItemId,
                    marketBoardReadResult.Listings)
            : null;
        if (!marketBoardReadResult.IsFresh)
        {
            if (marketAcquisitionRouteRunner.IsRunning)
                marketAcquisitionRouteRunner.RecordListingReadPending(currentWorld, marketBoardReadResult);

            acquisitionStatus = marketBoardReadResult.Message;
            return;
        }

        var liveCandidatePurchaseTotals = ResolveActiveRouteLinePurchaseTotals(activeSubtask);
        marketAcquisitionLiveCandidatePlan = canBuildLiveCandidatePlan
            ? activeSubtask == null
                ? MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(
                    activeLine,
                    plan,
                    currentWorld,
                    marketBoardReadResult,
                    liveCandidatePurchaseTotals.PurchasedQuantity,
                    liveCandidatePurchaseTotals.SpentGil)
                : MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(
                    activeLine,
                    plan,
                    activeSubtask,
                    currentWorld,
                    marketBoardReadResult,
                    liveCandidatePurchaseTotals.PurchasedQuantity,
                    liveCandidatePurchaseTotals.SpentGil)
            : null;
        var guidedRouteResult = marketAcquisitionRouteRunner.IsRunning &&
                                marketAcquisitionRouteRunner.ActiveStop is { Status: "Arrived" } &&
                                marketAcquisitionLiveCandidatePlan != null
            ? marketAcquisitionRouteRunner.RecordProbe(currentWorld, marketAcquisitionLiveCandidatePlan)
            : null;
        if (guidedRouteResult?.Success == true && marketAcquisitionLiveCandidatePlan != null)
            RecordMarketAcquisitionProbeVisit(currentWorld, activeLine, activeSubtask, marketAcquisitionLiveCandidatePlan);

        acquisitionStatus = marketBoardReconciliation == null
            ? marketBoardReadResult.Message
            : $"Live listing reconciliation {marketBoardReconciliation.Status}; live candidates {marketAcquisitionLiveCandidatePlan?.Status ?? "Unavailable"}.";
        if (guidedRouteResult != null)
        {
            acquisitionStatus = $"{acquisitionStatus} Route: {guidedRouteResult.Message}";
        }
    }

    private void RecordMarketAcquisitionProbeVisit(
        string currentWorld,
        MarketAcquisitionRequestView activeLine,
        MarketAcquisitionWorldItemSubtask? activeSubtask,
        MarketAcquisitionLiveCandidatePlan candidatePlan)
    {
        var legalRows = candidatePlan.Rows
            .Where(row => row.Decision.Equals("WouldBuy", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var observedLegalQuantity = (uint)legalRows.Sum(row => row.LiveListing.Quantity);
        var observedLegalGil = legalRows.Aggregate(
            0ul,
            (total, row) => checked(total + ((ulong)row.LiveListing.UnitPrice * row.LiveListing.Quantity)));
        var hqPolicy = MarketAcquisitionPolicy.NormalizeHqPolicy(activeSubtask?.HqPolicy ?? activeLine.HqPolicy);
        var maxUnitPrice = activeSubtask?.MaxUnitPrice ?? activeLine.MaxUnitPrice;
        var dataCenter = ResolveCatalogDataCenter(currentWorld, activeSubtask?.DataCenter);

        marketAcquisitionWorldVisitCatalog.RecordProbe(new MarketAcquisitionWorldVisitRecord
        {
            WorldName = currentWorld,
            DataCenter = dataCenter,
            ItemId = activeSubtask?.ItemId ?? activeLine.ItemId,
            ItemName = activeSubtask?.ItemName ?? activeLine.ItemName,
            HqPolicy = hqPolicy,
            MaxUnitPrice = maxUnitPrice,
            CheckedAtUtc = DateTimeOffset.UtcNow,
            Result = candidatePlan.WouldBuyQuantity > 0 ? "LegalStockObserved" : candidatePlan.Status,
            ObservedLegalListingCount = legalRows.Length,
            ObservedLegalQuantity = observedLegalQuantity,
            ObservedLegalGil = observedLegalGil,
            Source = "LiveMarketBoardProbe",
            RequestId = claimedAcquisitionRequest?.Id,
            RouteRunId = guidedRouteProgressNonce,
            RouteStopId = $"{dataCenter}:{currentWorld}",
        });
        marketAcquisitionWorldVisitCatalog.Prune(MarketAcquisitionWorldVisitCatalogMaxRecords);
        config.Save();
    }

    private void RecordMarketAcquisitionPurchaseVisit(
        MarketBoardPurchaseCandidate candidate,
        MarketAcquisitionWorldItemSubtask activeSubtask,
        string worldName)
    {
        var hqPolicy = MarketAcquisitionPolicy.NormalizeHqPolicy(activeSubtask.HqPolicy);
        var dataCenter = ResolveCatalogDataCenter(worldName, activeSubtask.DataCenter);
        marketAcquisitionWorldVisitCatalog.RecordProbe(new MarketAcquisitionWorldVisitRecord
        {
            WorldName = worldName,
            DataCenter = dataCenter,
            ItemId = activeSubtask.ItemId,
            ItemName = activeSubtask.ItemName,
            HqPolicy = hqPolicy,
            MaxUnitPrice = activeSubtask.MaxUnitPrice,
            CheckedAtUtc = DateTimeOffset.UtcNow,
            Result = "Purchased",
            PurchasedQuantity = candidate.Quantity,
            SpentGil = candidate.TotalGil,
            ObservedLegalListingCount = 1,
            ObservedLegalQuantity = candidate.Quantity,
            ObservedLegalGil = candidate.TotalGil,
            Source = "PurchaseAudit",
            RequestId = claimedAcquisitionRequest?.Id,
            RouteRunId = guidedRouteProgressNonce,
            RouteStopId = $"{dataCenter}:{worldName}",
        });
        marketAcquisitionWorldVisitCatalog.Prune(MarketAcquisitionWorldVisitCatalogMaxRecords);
        config.Save();
    }

    private static string ResolveCatalogDataCenter(string worldName, string? plannedDataCenter)
    {
        if (!string.IsNullOrWhiteSpace(plannedDataCenter))
            return plannedDataCenter;

        return MarketAcquisitionWorldCatalog.ResolveDataCenter(worldName);
    }

    private void DrawMarketAcquisitionGuidedRoute()
    {
        ImGuiUi.SectionHeader("Guided World Route", ColHeader);

        var canStart = acquisitionPlan is { Status: "Ready" } &&
                       !IsAcquisitionPlanStale() &&
                       acquisitionPlan.WorldBatches.Count > 0 &&
                       !marketAcquisitionRouteRunner.IsRunning &&
                       !marketAcquisitionRouteRunner.IsPaused;
        var canReprepare = canStart &&
                           marketAcquisitionRouteRunner.CanRestart &&
                           marketAcquisitionRouteRunner.CompletedOrProbedStops.Count > 0;
        if (ImGuiUi.Button("Start Route", canStart))
            _ = StartGuidedRouteAsync(enableDiagnostics: false);

        ImGui.SameLine();
        if (ImGuiUi.Button("Start With Diagnostics", canStart))
            _ = StartGuidedRouteAsync(enableDiagnostics: true);

        ImGui.SameLine();
        if (marketAcquisitionRouteRunner.IsPaused)
        {
            if (ImGuiUi.Button("Resume", true))
                _ = ResumeGuidedRouteAsync();
        }
        else
        {
            if (ImGuiUi.Button("Pause", marketAcquisitionRouteRunner.IsRunning))
                _ = PauseGuidedRouteAsync();
        }

        ImGui.SameLine();
        if (ImGuiUi.Button("Stop", marketAcquisitionRouteRunner.IsRunning || marketAcquisitionRouteRunner.IsPaused))
            _ = StopGuidedRouteAsync();

        ImGui.SameLine();
        if (ImGuiUi.Button("Restart", canStart && marketAcquisitionRouteRunner.CanRestart))
            _ = RestartGuidedRouteAsync();

        ImGui.SameLine();
        if (ImGuiUi.Button("Re-prepare Route", canReprepare))
            _ = ReprepareGuidedRouteAsync();

        ImGui.TextColored(GetGuidedRouteStatusColor(), marketAcquisitionRouteRunner.StatusMessage);
        if (IsAcquisitionPlanStale())
            ImGui.TextColored(ColError, "Request changed after this plan was prepared. Prepare a fresh plan before starting.");
        DrawPostRunDiagnosticSummary();
        DrawLatestWorldCompletionSummary();

        if (marketAcquisitionRouteRunner.Stops.Count == 0)
        {
            ImGui.TextColored(ColMuted, "Start after preparing a plan. Routes travel, validate live listings, and purchase safe rows automatically.");
            return;
        }

        if (marketAcquisitionRouteRunner.LastDiagnosticFilePath != null)
            ImGui.TextColored(ColMuted, $"Diagnostics: {marketAcquisitionRouteRunner.LastDiagnosticFilePath}");

        var activeStop = marketAcquisitionRouteRunner.ActiveStop;
        if (activeStop == null)
        {
            ImGui.TextColored(ColSuccess, "Route is not actively executing.");
        }
        else
        {
            ImGui.TextColored(ColHeader, $"Next stop: {activeStop.WorldName}");
            ImGui.SameLine();
            ImGui.TextColored(ColMuted, $"Planned {activeStop.PlannedQuantity:N0} item(s), {FormatGil(activeStop.PlannedGil)}");
        }

        DrawGuidedRouteStops(marketAcquisitionRouteRunner.Stops);
        DrawMarketBoardProbeStatus();
    }

    private void DrawDiagnosticsTab()
    {
        ImGui.Spacing();
        ImGuiUi.SectionHeader("Diagnostics", ColHeader);

        if (ImGuiUi.Button("Open Route Diagnostics Folder", true))
            OpenDiagnosticsFolder(marketAcquisitionRouteDiagnosticsDirectory);

        ImGui.SameLine();
        if (ImGuiUi.Button("Market Acquisition Diagnostics", true))
            AcquisitionDiagnostics.IsOpen = true;

        ImGui.SameLine();
        if (ImGuiUi.Button("Automation Diagnostics", true))
            AutomationDiagnostics.IsOpen = true;

        ImGui.TextColored(GetDiagnosticsFolderStatusColor(), diagnosticsFolderStatus);
        ImGui.TextColored(ColMuted, marketAcquisitionRouteDiagnosticsDirectory);

        if (marketAcquisitionRouteRunner.LastDiagnosticFilePath != null)
            ImGui.TextColored(ColMuted, $"Latest report: {marketAcquisitionRouteRunner.LastDiagnosticFilePath}");

        DrawPostRunDiagnosticSummary();
        DrawMarketBoardInputCapture();
    }

    private void DrawLatestWorldCompletionSummary()
    {
        var summary = marketAcquisitionRouteRunner.LatestWorldCompletionSummary;
        if (summary == null)
            return;

        ImGui.TextColored(
            ColMuted,
            $"Latest world: {summary.WorldName} ({FormatRouteDataCenter(summary.DataCenter)}) bought {summary.PurchasedQuantity:N0}, spent {FormatGil(summary.SpentGil)}; {summary.CompletedLineCount:N0} complete / {summary.SkippedLineCount:N0} skipped.");
    }

    private void DrawPostRunDiagnosticSummary()
    {
        var runSummary = marketAcquisitionRouteRunner.LastRunSummary;
        if (runSummary != null)
        {
            ImGui.TextColored(
                runSummary.FailedWorldCount > 0 || runSummary.Warnings.Count > 0 ? ColHeader : ColSuccess,
                $"Run rollup: purchased {runSummary.PurchasedQuantity:N0}, spent {FormatGil(runSummary.SpentGil)}; {runSummary.CompletedWorldCount:N0} complete / {runSummary.PartialWorldCount:N0} partial / {runSummary.FailedWorldCount:N0} failed world(s).");

            if (runSummary.OpportunisticPurchasedQuantity > 0 || runSummary.PlannedPurchasedQuantity > 0)
            {
                ImGui.TextColored(
                    ColMuted,
                    $"Planned buys: {runSummary.PlannedPurchasedQuantity:N0} / {FormatGil(runSummary.PlannedSpentGil)}. Opportunistic buys: {runSummary.OpportunisticPurchasedQuantity:N0} / {FormatGil(runSummary.OpportunisticSpentGil)}.");
            }

            if (runSummary.TopItemsBySpentGil.Count > 0)
            {
                var topItems = string.Join(
                    "; ",
                    runSummary.TopItemsBySpentGil
                        .Take(3)
                        .Select(item => $"{item.ItemName} {item.PurchasedQuantity:N0} / {FormatGil(item.SpentGil)}"));
                ImGui.TextColored(ColMuted, $"Top buys: {topItems}");
            }

            if (runSummary.Warnings.Count > 0)
            {
                ImGui.TextColored(
                    ColError,
                    $"Post-run diagnostics: {runSummary.Warnings.Count:N0} warning(s). Open Diagnostics for details.");
            }

            return;
        }

        var summary = marketAcquisitionRouteRunner.LastRunDiagnosticSummary;
        if (summary.Warnings.Count > 0)
        {
            ImGui.TextColored(
                ColError,
                $"Post-run diagnostics: {summary.Warnings.Count:N0} warning(s). Open Diagnostics for details.");
        }
    }

    private void DrawMarketBoardInputCapture()
    {
        ImGui.Spacing();
        ImGuiUi.SectionHeader("Input Capture", ColHeader);
        ImGui.TextColored(ColMuted, "Capture current market-board UI/input state before and after manual purchase clicks or pagination attempts.");

        if (ImGuiUi.Button("Capture Input State", true))
            CaptureMarketBoardInputState();

        ImGui.SameLine();
        if (ImGuiUi.Button("Finish Capture Log", marketAcquisitionRouteRunner.CanFinalizeInputCaptureLog))
            FinalizeMarketBoardInputCaptureLog();

        if (marketAcquisitionRouteRunner.LastDiagnosticFilePath != null)
            ImGui.TextColored(ColMuted, $"Capture log: {marketAcquisitionRouteRunner.LastDiagnosticFilePath}");
    }

    private void DrawGuidedRouteStops(IReadOnlyList<MarketAcquisitionGuidedRouteStop> stops)
    {
        var rows = MarketAcquisitionRouteTablePresenter.BuildRows(stops);
        if (ImGui.BeginTable("MarketAcquisitionGuidedRouteStops", 7, ImGuiUi.InteractiveTableFlags))
        {
            ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, 140);
            ImGui.TableSetupColumn("Data Center");
            ImGui.TableSetupColumn("Route Lines");
            ImGui.TableSetupColumn("State");
            ImGui.TableSetupColumn("Intent");
            ImGui.TableSetupColumn("Result");
            ImGui.TableSetupColumn("Notes");
            ImGui.TableHeadersRow();

            foreach (var row in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                DrawGuidedRouteStopExpander(row);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatRouteDataCenter(row.DataCenter));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.RouteLines);
                if (!string.IsNullOrWhiteSpace(row.LineMix) && !string.Equals(row.LineMix, "No route lines", StringComparison.Ordinal))
                {
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(row.LineMix);
                }

                ImGui.TableNextColumn();
                ImGui.TextColored(GetGuidedRouteStopColor(row.State), row.State);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Intent);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Result);
                ImGui.TableNextColumn();
                ImGui.TextColored(row.Aggregate.FailedLineCount > 0 ? ColError : ColMuted, row.Notes);

                if (expandedGuidedRouteStops.Contains(GetGuidedRouteStopKey(row)))
                    DrawGuidedRouteStopLineRows(row);
            }

            ImGui.EndTable();
        }
    }

    private void DrawGuidedRouteStopExpander(MarketAcquisitionRouteStopRow row)
    {
        var key = GetGuidedRouteStopKey(row);
        var expanded = expandedGuidedRouteStops.Contains(key);
        var buttonLabel = expanded ? $"v##route-stop-{key}" : $">##route-stop-{key}";
        if (ImGui.SmallButton(buttonLabel))
        {
            if (expanded)
                expandedGuidedRouteStops.Remove(key);
            else
                expandedGuidedRouteStops.Add(key);
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(row.WorldName);
    }

    private void DrawGuidedRouteStopLineRows(MarketAcquisitionRouteStopRow stop)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextColored(ColMuted, "  Item");
        ImGui.TableNextColumn();
        ImGui.TextColored(ColMuted, "Source");
        ImGui.TableNextColumn();
        ImGui.TextColored(ColMuted, "State");
        ImGui.TableNextColumn();
        ImGui.TextColored(ColMuted, "Planned");
        ImGui.TableNextColumn();
        ImGui.TextColored(ColMuted, "Discovered");
        ImGui.TableNextColumn();
        ImGui.TextColored(ColMuted, "Bought");
        ImGui.TableNextColumn();
        ImGui.TextColored(ColMuted, "Notes");

        foreach (var line in stop.Lines)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(ColMuted, $"  {line.Item}");
            ImGui.TableNextColumn();
            ImGui.TextColored(ColMuted, line.Source);
            ImGui.TableNextColumn();
            ImGui.TextColored(GetRouteLineStateColor(line.State), line.State);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.Planned);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.Discovered);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.Bought);
            ImGui.TableNextColumn();
            ImGui.TextColored(line.State.Equals("Blocked", StringComparison.OrdinalIgnoreCase) ? ColError : ColMuted, line.Notes);
        }
    }

    private static string GetGuidedRouteStopKey(MarketAcquisitionRouteStopRow row) =>
        $"{row.WorldName}|{row.DataCenter}";

    private Task StartGuidedRouteAsync(bool enableDiagnostics)
    {
        return RunAcquisitionRequestAsync(async token =>
        {
            var plan = acquisitionPlan ??
                       throw new InvalidOperationException("Prepare a plan before starting a guided route.");
            var claimed = claimedAcquisitionRequest ??
                          throw new InvalidOperationException("No dashboard request is accepted.");
            await EnsureRouteReportableClaimAsync(claimed, token).ConfigureAwait(false);
            marketAcquisitionRouteRunner.Start(
                plan,
                enableDiagnostics,
                config.EnableOpportunisticWorldChecks);
            marketBoardReadResult = null;
            marketBoardReconciliation = null;
            marketAcquisitionLiveCandidatePlan = null;
            ClearMarketBoardAutomationState();
            activeWorldPurchasedQuantity = 0;
            activeWorldSpentGil = 0;
            activeWorldPurchaseBatchWorld = null;
            activePurchaseLineId = null;
            activeLinePurchasedQuantity = 0;
            activeLineSpentGil = 0;
            guidedRouteProbeRunning = false;
            nextGuidedRouteMonitorUtc = DateTimeOffset.MinValue;
            Interlocked.Increment(ref guidedRouteProgressSessionVersion);
            guidedRouteProgressNonce = Guid.NewGuid().ToString("N");
            Interlocked.Exchange(ref guidedRouteProgressReportSequence, 0);
            lastGuidedRouteProgressReportKey = null;
            ReportGuidedRouteProgress();
        });
    }

    private Task PauseGuidedRouteAsync()
    {
        marketBoardApproachService.StopNavigation();
        marketAcquisitionRouteRunner.Pause();
        ReportGuidedRouteProgress();
        return Task.CompletedTask;
    }

    private Task ResumeGuidedRouteAsync()
    {
        marketAcquisitionRouteRunner.Resume();
        ReportGuidedRouteProgress();
        return Task.CompletedTask;
    }

    private Task StopGuidedRouteAsync()
    {
        marketBoardApproachService.StopNavigation();
        marketAcquisitionRouteRunner.Stop();
        ClearMarketBoardAutomationState();
        ReportGuidedRouteProgress();
        return Task.CompletedTask;
    }

    private Task RestartGuidedRouteAsync()
    {
        return RunAcquisitionRequestAsync(async token =>
        {
            var plan = acquisitionPlan ??
                       throw new InvalidOperationException("Prepare a plan before restarting a guided route.");
            var claimed = claimedAcquisitionRequest ??
                          throw new InvalidOperationException("No dashboard request is accepted.");
            await EnsureRouteReportableClaimAsync(claimed, token).ConfigureAwait(false);
            marketBoardApproachService.StopNavigation();
            marketAcquisitionRouteRunner.Restart(plan);
            marketBoardReadResult = null;
            marketBoardReconciliation = null;
            marketAcquisitionLiveCandidatePlan = null;
            ClearMarketBoardAutomationState();
            activeWorldPurchasedQuantity = 0;
            activeWorldSpentGil = 0;
            activeWorldPurchaseBatchWorld = null;
            activePurchaseLineId = null;
            activeLinePurchasedQuantity = 0;
            activeLineSpentGil = 0;
            guidedRouteProbeRunning = false;
            nextGuidedRouteMonitorUtc = DateTimeOffset.MinValue;
            Interlocked.Increment(ref guidedRouteProgressSessionVersion);
            guidedRouteProgressNonce = Guid.NewGuid().ToString("N");
            Interlocked.Exchange(ref guidedRouteProgressReportSequence, 0);
            lastGuidedRouteProgressReportKey = null;
            ReportGuidedRouteProgress();
        });
    }

    private Task ReprepareGuidedRouteAsync()
    {
        return RunAcquisitionRequestAsync(async token =>
        {
            var plan = acquisitionPlan ??
                       throw new InvalidOperationException("Prepare a plan before re-preparing a guided route.");
            var claimed = claimedAcquisitionRequest ??
                          throw new InvalidOperationException("No dashboard request is accepted.");
            await EnsureRouteReportableClaimAsync(claimed, token).ConfigureAwait(false);
            marketBoardApproachService.StopNavigation();
            var result = marketAcquisitionRouteRunner.ReprepareAndRestart(plan, DateTimeOffset.UtcNow);
            if (marketAcquisitionRouteRunner.ActivePlan != null)
                acquisitionPlan = marketAcquisitionRouteRunner.ActivePlan;
            acquisitionStatus = result.Message;
            marketBoardReadResult = null;
            marketBoardReconciliation = null;
            marketAcquisitionLiveCandidatePlan = null;
            ClearMarketBoardAutomationState();
            activeWorldPurchasedQuantity = 0;
            activeWorldSpentGil = 0;
            activeWorldPurchaseBatchWorld = null;
            activePurchaseLineId = null;
            activeLinePurchasedQuantity = 0;
            activeLineSpentGil = 0;
            guidedRouteProbeRunning = false;
            nextGuidedRouteMonitorUtc = DateTimeOffset.MinValue;
            Interlocked.Increment(ref guidedRouteProgressSessionVersion);
            guidedRouteProgressNonce = Guid.NewGuid().ToString("N");
            Interlocked.Exchange(ref guidedRouteProgressReportSequence, 0);
            lastGuidedRouteProgressReportKey = null;
            ReportGuidedRouteProgress();
        });
    }

    private void CaptureMarketBoardInputState()
    {
        try
        {
            var label = $"input-capture-{++marketInputCaptureIndex}";
            var capture = marketBoardInputCaptureReader.Capture();
            var result = marketAcquisitionRouteRunner.RecordInputCapture(label, capture);
            acquisitionStatus = result.Success
                ? $"{result.Message} {marketAcquisitionRouteRunner.LastDiagnosticFilePath}"
                : result.Message;
        }
        catch (Exception ex)
        {
            acquisitionStatus = $"Unable to capture market board input state. {ex.Message}";
            log.Warning(ex, "[MarketMafioso] Unable to capture market board input state.");
        }
    }

    private void FinalizeMarketBoardInputCaptureLog()
    {
        try
        {
            var result = marketAcquisitionRouteRunner.FinalizeInputCaptureLog();
            acquisitionStatus = result.Message;
        }
        catch (Exception ex)
        {
            acquisitionStatus = $"Unable to finalize market board input capture log. {ex.Message}";
            log.Warning(ex, "[MarketMafioso] Unable to finalize market board input capture log.");
        }
    }

    private void ResetGuidedRoute(string status)
    {
        marketBoardApproachService.StopNavigation();
        marketAcquisitionRouteRunner.Reset(status);
        ClearMarketBoardAutomationState();
        activeWorldPurchasedQuantity = 0;
        activeWorldSpentGil = 0;
        activeWorldPurchaseBatchWorld = null;
        activePurchaseLineId = null;
        activeLinePurchasedQuantity = 0;
        activeLineSpentGil = 0;
        guidedRouteProbeRunning = false;
        Interlocked.Increment(ref guidedRouteProgressSessionVersion);
        guidedRouteProgressNonce = Guid.NewGuid().ToString("N");
        Interlocked.Exchange(ref guidedRouteProgressReportSequence, 0);
        lastGuidedRouteProgressReportKey = null;
        nextGuidedRouteMonitorUtc = DateTimeOffset.MinValue;
    }

    private void ReportGuidedRouteProgress()
    {
        var claimed = claimedAcquisitionRequest;
        if (claimed == null ||
            string.IsNullOrWhiteSpace(claimed.ClaimToken) ||
            string.IsNullOrWhiteSpace(config.ApiKey) ||
            string.IsNullOrWhiteSpace(config.ServerUrl) ||
            string.Equals(marketAcquisitionRouteRunner.State, "Idle", StringComparison.OrdinalIgnoreCase))
            return;

        var runnerState = marketAcquisitionRouteRunner.State;
        if (!MarketAcquisitionRouteProgressReporter.CanReportForRouteState(runnerState))
        {
            log.Verbose(
                "[MarketMafioso] Skipping route progress report for local route state {RouteState}.",
                runnerState);
            return;
        }

        if (!MarketAcquisitionRouteProgressReporter.CanReportForRequestStatus(claimed.Status))
        {
            log.Verbose(
                "[MarketMafioso] Skipping route progress report for request {RequestId} in server status {Status}.",
                claimed.Id,
                claimed.Status);
            return;
        }

        var message = marketAcquisitionRouteRunner.StatusMessage;
        var reportKey = $"{claimed.Id}|{runnerState}|{message}";
        if (string.Equals(lastGuidedRouteProgressReportKey, reportKey, StringComparison.Ordinal))
            return;

        lastGuidedRouteProgressReportKey = reportKey;
        var reportSessionVersion = Interlocked.Read(ref guidedRouteProgressSessionVersion);
        var eventSequence = Interlocked.Increment(ref guidedRouteProgressReportSequence);
        var attemptId = guidedRouteProgressNonce;
        var activeStop = marketAcquisitionRouteRunner.ActiveStop;
        var routeStopId = activeStop == null
            ? null
            : $"{activeStop.DataCenter}:{activeStop.WorldName}";
        var activeWorld = activeStop?.WorldName;
        var phase = activeStop?.Status ?? runnerState;
        _ = Task.Run(async () =>
        {
            try
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var action = MarketAcquisitionRouteProgressReporter.ResolveAction(runnerState);
                var attemptResult = action switch
                {
                    MarketAcquisitionRouteProgressReporter.FailAction => await acquisitionClient.FailAttemptAsync(
                        config.ServerUrl,
                        config.ApiKey,
                        claimed.Id,
                        claimed.ClaimToken,
                        config.PluginInstanceId,
                        attemptId,
                        eventSequence,
                        routeStopId,
                        activeWorld,
                        phase,
                        message,
                        PluginBuildInfo.DisplayVersion,
                        cancellation.Token).ConfigureAwait(false),
                    MarketAcquisitionRouteProgressReporter.CompleteAction => await acquisitionClient.CompleteAttemptAsync(
                        config.ServerUrl,
                        config.ApiKey,
                        claimed.Id,
                        claimed.ClaimToken,
                        config.PluginInstanceId,
                        attemptId,
                        eventSequence,
                        routeStopId,
                        activeWorld,
                        phase,
                        message,
                        PluginBuildInfo.DisplayVersion,
                        cancellation.Token).ConfigureAwait(false),
                    _ => await acquisitionClient.ReportAttemptProgressAsync(
                        config.ServerUrl,
                        config.ApiKey,
                        claimed.Id,
                        claimed.ClaimToken,
                        config.PluginInstanceId,
                        attemptId,
                        eventSequence,
                        routeStopId,
                        activeWorld,
                        phase,
                        message,
                        PluginBuildInfo.DisplayVersion,
                        cancellation.Token).ConfigureAwait(false),
                };
                var updated = attemptResult.Request;

                if (Interlocked.Read(ref guidedRouteProgressSessionVersion) == reportSessionVersion &&
                    claimedAcquisitionRequest?.Id == claimed.Id)
                {
                    if (action.Equals(MarketAcquisitionRouteProgressReporter.CompleteAction, StringComparison.Ordinal))
                    {
                        MarketAcquisitionClaimPersistence.Clear(config);
                        claimedAcquisitionRequest = null;
                        claimedAcceptIdempotencyKey = null;
                        claimedRejectIdempotencyKey = null;
                        acquisitionStatus = $"Route complete: {message}";
                    }
                    else
                    {
                        claimedAcquisitionRequest = claimed with { Status = updated.Status };
                        MarketAcquisitionClaimPersistence.Save(
                            config,
                            claimedAcquisitionRequest,
                            claimedAcceptIdempotencyKey,
                            claimedRejectIdempotencyKey);
                    }

                    config.Save();
                }
            }
            catch (Exception ex)
            {
                if (!TryHandleRouteProgressConflict(ex, claimed, reportSessionVersion))
                {
                    acquisitionStatus = $"Route progress report failed: {ex.Message}";
                    log.Warning(ex, "[MarketMafioso] Unable to report market acquisition route progress.");
                }
            }
        });
    }

    private void ReportConfirmedPurchase(
        MarketBoardPurchaseCandidate candidate,
        uint linePurchasedQuantity,
        uint lineSpentGil)
    {
        var claimed = claimedAcquisitionRequest;
        var activeSubtask = marketAcquisitionRouteRunner.ActiveStop?.ActiveItemSubtask;
        if (claimed == null ||
            activeSubtask == null ||
            string.IsNullOrWhiteSpace(claimed.ClaimToken))
            return;

        var lineId = string.IsNullOrWhiteSpace(activeSubtask.LineId)
            ? GetActiveRouteLineId(claimed)
            : activeSubtask.LineId;
        var itemName = activeSubtask.ItemName;
        var worldName = string.IsNullOrWhiteSpace(candidate.WorldName)
            ? GetCurrentWorldName()
            : candidate.WorldName;
        var message = $"Purchased {candidate.Quantity:N0} {FormatAcquisitionItem(GetActiveRouteLine(claimed))} on {worldName} for {FormatGil(candidate.TotalGil)}.";

        marketAcquisitionRouteRunner.RecordPurchaseAudit(
            lineId,
            itemName,
            worldName,
            candidate.ListingId,
            candidate.RetainerId,
            candidate.Quantity,
            candidate.TotalGil,
            "Purchased",
            activeSubtask.Source);
        marketAcquisitionRouteRunner.RecordLineProgress(
            lineId,
            itemName,
            "Running",
            linePurchasedQuantity,
            lineSpentGil,
            message,
            activeSubtask.Source);
        RecordMarketAcquisitionPurchaseVisit(candidate, activeSubtask, worldName);

        ReportPurchaseAuditAsync(
            claimed,
            lineId,
            itemName,
            candidate,
            worldName,
            message);
        ReportLineProgressAsync(
            claimed,
            lineId,
            itemName,
            "Running",
            linePurchasedQuantity,
            lineSpentGil,
            message,
            reason: null);
    }

    private void ReportAcquisitionLineProgress(
        MarketAcquisitionWorldItemSubtask subtask,
        string status,
        uint purchasedQuantity,
        uint spentGil,
        string message)
    {
        var claimed = claimedAcquisitionRequest;
        if (claimed == null ||
            string.IsNullOrWhiteSpace(claimed.ClaimToken))
            return;

        var lineId = string.IsNullOrWhiteSpace(subtask.LineId)
            ? GetActiveRouteLineId(claimed)
            : subtask.LineId;
        marketAcquisitionRouteRunner.RecordLineProgress(
            lineId,
            subtask.ItemName,
            status,
            purchasedQuantity,
            spentGil,
            message,
            subtask.Source);
        ReportLineProgressAsync(
            claimed,
            lineId,
            subtask.ItemName,
            status,
            purchasedQuantity,
            spentGil,
            message,
            reason: null);
    }

    private void ReportPurchaseAuditAsync(
        MarketAcquisitionClaimView claimed,
        string lineId,
        string? itemName,
        MarketBoardPurchaseCandidate candidate,
        string worldName,
        string message)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey) ||
            string.IsNullOrWhiteSpace(config.ServerUrl))
            return;

        var eventSequence = Interlocked.Increment(ref guidedRouteProgressReportSequence);
        var attemptId = guidedRouteProgressNonce;
        var idempotencyKey = $"{config.PluginInstanceId}:{attemptId}:purchase:{eventSequence}";
        _ = Task.Run(async () =>
        {
            try
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await acquisitionClient.PostPurchaseAuditAsync(
                    config.ServerUrl,
                    config.ApiKey,
                    claimed.Id,
                    new MarketAcquisitionPurchaseAuditRequest
                    {
                        ClaimToken = claimed.ClaimToken,
                        IdempotencyKey = idempotencyKey,
                        AttemptId = attemptId,
                        Sequence = eventSequence,
                        LineId = lineId,
                        WorldName = worldName,
                        ItemId = candidate.ItemId,
                        ItemName = itemName,
                        ListingId = candidate.ListingId,
                        RetainerName = candidate.RetainerName,
                        RetainerId = candidate.RetainerId,
                        Quantity = candidate.Quantity,
                        UnitPrice = candidate.UnitPrice,
                        TotalGil = candidate.TotalGil,
                        IsHq = candidate.IsHq,
                        Result = "Purchased",
                        Message = message,
                    },
                    cancellation.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[MarketMafioso] Unable to report market acquisition purchase audit.");
            }
        });
    }

    private void ReportLineProgressAsync(
        MarketAcquisitionClaimView claimed,
        string lineId,
        string? itemName,
        string status,
        uint purchasedQuantity,
        uint spentGil,
        string message,
        string? reason)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey) ||
            string.IsNullOrWhiteSpace(config.ServerUrl))
            return;

        var eventSequence = Interlocked.Increment(ref guidedRouteProgressReportSequence);
        var attemptId = guidedRouteProgressNonce;
        var idempotencyKey = $"{config.PluginInstanceId}:{attemptId}:line:{lineId}:{eventSequence}";
        _ = Task.Run(async () =>
        {
            try
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await acquisitionClient.PostLineProgressAsync(
                    config.ServerUrl,
                    config.ApiKey,
                    claimed.Id,
                    lineId,
                    new MarketAcquisitionLineProgressRequest
                    {
                        ClaimToken = claimed.ClaimToken,
                        IdempotencyKey = idempotencyKey,
                        AttemptId = attemptId,
                        Sequence = eventSequence,
                        Status = status,
                        PurchasedQuantity = purchasedQuantity,
                        SpentGil = spentGil,
                        Message = message,
                        Reason = reason,
                    },
                    cancellation.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.Warning(
                    ex,
                    "[MarketMafioso] Unable to report market acquisition line progress for {LineId} ({ItemName}).",
                    lineId,
                    itemName ?? "unknown item");
            }
        });
    }

    private bool TryHandleRouteProgressConflict(Exception exception, MarketAcquisitionClaimView claimed, long reportSessionVersion)
    {
        if (exception is not MarketAcquisitionLifecycleHttpException { StatusCode: System.Net.HttpStatusCode.Conflict } conflict ||
            !TryExtractInvalidTransitionSourceStatus(conflict.Error, out var sourceStatus))
            return false;

        if (Interlocked.Read(ref guidedRouteProgressSessionVersion) != reportSessionVersion ||
            claimedAcquisitionRequest?.Id != claimed.Id)
            return true;

        if (sourceStatus.Equals("Complete", StringComparison.OrdinalIgnoreCase))
        {
            MarketAcquisitionClaimPersistence.Clear(config);
            claimedAcquisitionRequest = null;
            claimedAcceptIdempotencyKey = null;
            claimedRejectIdempotencyKey = null;
            acquisitionStatus = "Server already marked this route complete.";
        }
        else if (IsFailedAcquisitionStatus(sourceStatus))
        {
            claimedAcquisitionRequest = claimed with { Status = sourceStatus };
            MarketAcquisitionClaimPersistence.Save(
                config,
                claimedAcquisitionRequest,
                claimedAcceptIdempotencyKey,
                claimedRejectIdempotencyKey);
            acquisitionStatus = "Server already marked this route failed. Restart to reopen the request.";
        }
        else if (!MarketAcquisitionRouteProgressReporter.CanReportForRequestStatus(sourceStatus))
        {
            MarketAcquisitionClaimPersistence.Clear(config);
            claimedAcquisitionRequest = null;
            claimedAcceptIdempotencyKey = null;
            claimedRejectIdempotencyKey = null;
            acquisitionStatus = $"Server request moved to {sourceStatus}; fetch dashboard requests to continue.";
        }
        else
        {
            claimedAcquisitionRequest = claimed with { Status = sourceStatus };
            MarketAcquisitionClaimPersistence.Save(
                config,
                claimedAcquisitionRequest,
                claimedAcceptIdempotencyKey,
                claimedRejectIdempotencyKey);
            acquisitionStatus = marketAcquisitionRouteRunner.StatusMessage;
        }

        config.Save();
        log.Verbose(
            "[MarketMafioso] Reconciled stale route progress conflict for request {RequestId}: {Error}",
            claimed.Id,
            conflict.Error ?? string.Empty);
        return true;
    }

    private static bool TryExtractInvalidTransitionSourceStatus(string? error, out string sourceStatus)
    {
        const string prefix = "Cannot move acquisition request from ";
        const string separator = " to ";
        sourceStatus = string.Empty;
        if (string.IsNullOrWhiteSpace(error) ||
            !error.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var start = prefix.Length;
        var end = error.IndexOf(separator, start, StringComparison.OrdinalIgnoreCase);
        if (end <= start)
            return false;

        sourceStatus = error[start..end].Trim();
        return !string.IsNullOrWhiteSpace(sourceStatus);
    }

    private async Task EnsureRouteReportableClaimAsync(MarketAcquisitionClaimView claimed, CancellationToken token)
    {
        claimed = await EnsureAcquisitionClaimReadyForPlanningAsync(claimed, token).ConfigureAwait(false);
        if (!MarketAcquisitionRouteProgressReporter.CanReportForRequestStatus(claimed.Status))
            throw new InvalidOperationException($"Request status {claimed.Status} cannot start a route. Fetch or accept a dashboard request first.");
    }

    private bool IsMarketAcquisitionRouteActive() =>
        marketAcquisitionRouteRunner.IsRunning ||
        marketAcquisitionRouteRunner.IsPaused ||
        guidedRouteProbeRunning ||
        marketBoardAutomationController.PurchaseSession?.IsActive == true;

    private bool IsExpectedCharacterScopeGap() =>
        claimedAcquisitionRequest != null &&
        marketAcquisitionRouteRunner.ActiveStop?.Status == "TravelCommandSent";

    private string GetVisibleAcquisitionStatus()
    {
        if (acquisitionStatus.StartsWith("Route progress report failed:", StringComparison.OrdinalIgnoreCase) &&
            IsMarketAcquisitionRouteActive())
            return marketAcquisitionRouteRunner.StatusMessage;

        return acquisitionStatus;
    }

    private Vector4 GetGuidedRouteStatusColor()
    {
        var status = marketAcquisitionRouteRunner.StatusMessage;
        if (status.StartsWith("Unable", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("Cannot", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("Failed", StringComparison.OrdinalIgnoreCase))
            return ColError;

        if (status.Contains("Waiting", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("Approve", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("Purchasing", StringComparison.OrdinalIgnoreCase) ||
            marketAcquisitionRouteRunner.IsPaused)
            return ColHeader;

        if (status.Contains("Arrived", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("Recorded", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("complete", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("started", StringComparison.OrdinalIgnoreCase))
            return ColSuccess;

        return ColMuted;
    }

    private static Vector4 GetGuidedRouteStopColor(string state) =>
        state switch
        {
            "Complete" => ColSuccess,
            "Partial" or "Buying" or "Traveling" or "Arrived" => ColHeader,
            "Blocked" or "Failed" => ColError,
            _ => ColMuted,
        };

    private static Vector4 GetRouteLineStateColor(string state) =>
        state switch
        {
            "Complete" or "Purchasing" or "Buying" => ColSuccess,
            "Pending" => ColMuted,
            _ when state.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase) => ColMuted,
            _ when state.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
                   state.Equals("Blocked", StringComparison.OrdinalIgnoreCase) => ColError,
            _ => ColHeader,
        };

    internal static bool CanPrepareAcquisitionPlanForStatus(string status) =>
        string.Equals(status, "AcceptedInPlugin", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase) ||
        IsFailedAcquisitionStatus(status);

    private static bool IsFailedAcquisitionStatus(string status) =>
        string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase);

    private async Task RunAcquisitionRequestAsync(Func<CancellationToken, Task> action)
    {
        if (acquisitionRequestBusy)
            return;

        acquisitionRequestBusy = true;
        acquisitionRequestCancellation?.Dispose();
        acquisitionRequestCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            await action(acquisitionRequestCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            acquisitionStatus = $"Request failed: {ex.Message}";
            log.Warning(ex, "[MarketMafioso] Market acquisition request action failed.");
        }
        finally
        {
            acquisitionRequestCancellation?.Dispose();
            acquisitionRequestCancellation = null;
            acquisitionRequestBusy = false;
        }
    }

    private bool TryGetAcquisitionScope(out string characterName, out string world)
    {
        characterName = playerState.CharacterName ?? string.Empty;
        world = playerState.HomeWorld.IsValid ? playerState.HomeWorld.Value.Name.ToString() : string.Empty;
        return !string.IsNullOrWhiteSpace(characterName) && !string.IsNullOrWhiteSpace(world);
    }

    private RetainerOwnerScope GetCurrentRetainerOwnerScope() =>
        new(
            playerState.CharacterName,
            playerState.HomeWorld.IsValid ? playerState.HomeWorld.Value.Name.ToString() : null);

    private string GetCurrentWorldName()
    {
        if (!playerState.CurrentWorld.IsValid)
            throw new InvalidOperationException("Current world is unavailable.");

        return playerState.CurrentWorld.Value.Name.ToString();
    }

    private static string FormatAcquisitionItem(MarketAcquisitionRequestView request)
    {
        if (request.Lines.Count > 1)
            return $"{request.Lines.Count:N0} item batch ({FormatPrimaryAcquisitionItem(request)})";

        return FormatPrimaryAcquisitionItem(request);
    }

    private static string FormatPrimaryAcquisitionItem(MarketAcquisitionRequestView request)
    {
        var name = string.IsNullOrWhiteSpace(request.ItemName)
            ? $"Item {request.ItemId}"
            : request.ItemName;
        return $"{name} ({request.ItemId})";
    }

    private static string FormatAcquisitionLineCount(MarketAcquisitionRequestView request) =>
        request.Lines.Count == 0 ? "1 line" : $"{request.Lines.Count:N0} line(s)";

    private static string FormatClaimedBatchRouting(MarketAcquisitionClaimView claimed) =>
        $"{FormatWorldMode(claimed.WorldMode)} / {claimed.Region}";

    private static string FormatClaimedBatchLatest(MarketAcquisitionClaimView claimed)
    {
        var latestLineMessage = claimed.Lines
            .OrderByDescending(line => line.Ordinal)
            .Select(line => line.LatestMessage)
            .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message));
        if (!string.IsNullOrWhiteSpace(latestLineMessage))
            return latestLineMessage;

        if (!string.IsNullOrWhiteSpace(claimed.LatestAttemptResult))
            return claimed.LatestAttemptResult;

        return "-";
    }

    private static string FormatLineItem(MarketAcquisitionBatchLineView line)
    {
        var name = string.IsNullOrWhiteSpace(line.ItemName)
            ? $"Item {line.ItemId}"
            : line.ItemName;
        return $"{name} ({line.ItemId})";
    }

    private static string FormatLineMaxQuantity(MarketAcquisitionBatchLineView line)
    {
        if (line.MaxQuantity == 0)
            return "No cap";

        return line.MaxQuantity.ToString("N0");
    }

    private static string FormatGil(uint gil) => $"{gil:N0} gil";

    private static string FormatGilCap(uint gil) => gil == 0 ? "No cap" : FormatGil(gil);

    private static string FormatRouteDataCenter(string dataCenter) =>
        string.IsNullOrWhiteSpace(dataCenter) ? "-" : dataCenter;

    private static string FormatWorldMode(string worldMode) =>
        worldMode switch
        {
            "AllWorldSweep" => "All-world sweep",
            "CurrentWorldOnly" => "Current world only",
            _ => worldMode,
        };

    private Vector4 GetAcquisitionStatusColor(string? visibleStatus = null)
    {
        if (acquisitionRequestBusy)
            return ColHeader;

        var status = visibleStatus ?? acquisitionStatus;
        if (status.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("required", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
            return ColError;

        if (status.Contains("Loaded", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("claimed", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("accepted", StringComparison.OrdinalIgnoreCase))
            return ColSuccess;

        return ColMuted;
    }

    private void OpenExternalUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            acquisitionStatus = $"Unable to open dashboard: {ex.Message}";
            log.Warning(ex, "[MarketMafioso] Unable to open market acquisition dashboard.");
        }
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
        return WorkshopMaterialAvailabilityService.BuildAvailability(
            requirements,
            playerInventory,
            config,
            GetCurrentRetainerOwnerScope());
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

    private void OpenDiagnosticsFolder(string folderPath)
    {
        try
        {
            Directory.CreateDirectory(folderPath);
            Process.Start(new ProcessStartInfo(folderPath)
            {
                UseShellExecute = true,
            });
            diagnosticsFolderStatus = "Opened route diagnostics folder.";
        }
        catch (Exception ex)
        {
            diagnosticsFolderStatus = $"Unable to open route diagnostics folder. {ex.Message}";
            log.Error(ex, "[MarketMafioso] Unable to open route diagnostics folder.");
        }
    }

    private MarketAcquisitionRouteLinePurchaseTotals ResolveActiveRouteLinePurchaseTotals(MarketAcquisitionWorldItemSubtask? activeSubtask)
    {
        if (activeSubtask == null)
            return new MarketAcquisitionRouteLinePurchaseTotals(activeWorldPurchasedQuantity, activeWorldSpentGil);

        var completedTotals = marketAcquisitionRouteRunner.GetLinePurchaseTotals(activeSubtask.LineId);
        return new MarketAcquisitionRouteLinePurchaseTotals(
            checked(completedTotals.PurchasedQuantity + activeLinePurchasedQuantity),
            checked(completedTotals.SpentGil + activeLineSpentGil));
    }

    private Vector4 GetDiagnosticsFolderStatusColor() =>
        diagnosticsFolderStatus.StartsWith("Unable", StringComparison.OrdinalIgnoreCase)
            ? ColError
            : ColMuted;

    private void DrawSettingsTab()
    {
        ImGui.Spacing();
        ImGui.TextColored(ColHeader, "Plugin Settings");
        ImGui.TextWrapped("Shared MarketMafioso client/server settings used by Inventory Reporter, Workshop Logistics, and receiver-backed features.");
        ImGui.Spacing();

        DrawServerSection();
        ImGui.Spacing();
        DrawInternalFeatureSettingsSection();
        if (IsMarketAcquisitionUnlocked())
        {
            ImGui.Spacing();
            DrawMarketAcquisitionSettingsSection();
        }
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
        ImGui.TextColored(ColHeader, "Server Connection");
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
            ? "Client API Key (required for this endpoint):"
            : "Client API Key (optional - sent as X-Api-Key header):");
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
            ImGui.TextColored(ColError, "This endpoint requires a client API key before plugin requests can be sent.");
        else if (endpoint.Kind == ReceiverEndpointKind.CustomRemote)
            ImGui.TextColored(ColMuted, "Custom remote endpoint. Client API key is required by default.");

        ImGui.Spacing();
        DrawDashboardOpenSection();
    }

    private void DrawMarketAcquisitionSettingsSection()
    {
        ImGui.TextColored(ColHeader, "Market Acquisition");
        ImGui.Separator();

        var enableOpportunistic = config.EnableOpportunisticWorldChecks;
        if (ImGui.Checkbox("Check every batch item on each visited world", ref enableOpportunistic))
        {
            config.EnableOpportunisticWorldChecks = enableOpportunistic;
            config.Save();
        }

        ImGui.TextColored(
            ColMuted,
            "Default on. While already on a world, MarketMafioso checks other unfinished items from the same claimed batch.");

        ImGui.Spacing();
        var recentWorldTtlHours = config.MarketAcquisitionRecentWorldTtlHours;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt("All-world recent check TTL (hours)", ref recentWorldTtlHours))
        {
            config.MarketAcquisitionRecentWorldTtlHours = Math.Clamp(recentWorldTtlHours, 1, 168);
            config.Save();
        }

        var ignoreRecentVisits = config.MarketAcquisitionIgnoreRecentWorldVisitsForSweep;
        if (ImGui.Checkbox("Full all-world resweep", ref ignoreRecentVisits))
        {
            config.MarketAcquisitionIgnoreRecentWorldVisitsForSweep = ignoreRecentVisits;
            config.Save();
        }

        ImGui.TextColored(
            ColMuted,
            "Default TTL is 18h. Full resweep ignores recent checked worlds while preparing all-world routes.");
    }

    private void DrawInternalFeatureSettingsSection()
    {
        ImGui.TextColored(ColHeader, "Internal Features");
        ImGui.Separator();

        DrawCraftQuoteSettingsSection();
        ImGui.Spacing();

        if (IsMarketAcquisitionUnlocked())
        {
            var unlockedAt = config.MarketAcquisitionUnlockedAtUtc == null
                ? "enabled"
                : $"enabled {config.MarketAcquisitionUnlockedAtUtc.Value:yyyy-MM-dd HH:mm:ss} UTC";
            ImGui.TextColored(ColSuccess, $"Market Acquisition {unlockedAt}.");
            ImGui.SameLine();
            if (ImGui.Button("Lock Market Acquisition"))
            {
                marketAcquisitionRouteRunner.Stop();
                AcquisitionDiagnostics.IsOpen = false;
                AutomationDiagnostics.IsOpen = false;
                MarketAcquisitionUnlock.Lock(config);
                config.Save();
                marketAcquisitionUnlockKeyBuffer = string.Empty;
                marketAcquisitionUnlockStatus = "Private module locked.";
            }

            ImGui.TextColored(ColMuted, "Locking hides the UI only. Existing local request state and server data are left untouched.");
            if (ImGui.Button("Automation Diagnostics"))
                AutomationDiagnostics.IsOpen = true;
            return;
        }

        AutomationDiagnostics.IsOpen = false;

        ImGui.TextColored(ColMuted, "Private/internal modules are hidden by default.");
        ImGui.Text("Unlock key:");
        var keyWidth = ImGui.GetContentRegionAvail().X - 82;
        ImGui.SetNextItemWidth(Math.Max(120f, keyWidth));
        var flags = showMarketAcquisitionUnlockKey ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password;
        ImGui.InputText("##marketAcquisitionUnlockKey", ref marketAcquisitionUnlockKeyBuffer, 256, flags);
        ImGui.SameLine();
        if (ImGui.Button(showMarketAcquisitionUnlockKey ? "Hide##marketAcquisitionUnlock" : "Show##marketAcquisitionUnlock", new Vector2(72, 0)))
            showMarketAcquisitionUnlockKey = !showMarketAcquisitionUnlockKey;

        if (ImGuiUi.Button("Unlock private module", !string.IsNullOrWhiteSpace(marketAcquisitionUnlockKeyBuffer)))
        {
            if (MarketAcquisitionUnlock.TryUnlock(config, marketAcquisitionUnlockKeyBuffer))
            {
                config.Save();
                marketAcquisitionUnlockKeyBuffer = string.Empty;
                marketAcquisitionUnlockStatus = "Private module unlocked.";
            }
            else
            {
                marketAcquisitionUnlockStatus = "Unlock key was not accepted.";
            }
        }

        ImGui.TextColored(
            marketAcquisitionUnlockStatus.Contains("not accepted", StringComparison.OrdinalIgnoreCase) ? ColError : ColMuted,
            marketAcquisitionUnlockStatus);
    }

    private void DrawCraftQuoteSettingsSection()
    {
        ImGui.TextColored(ColHeader, "Craft Quote Evidence");

        var enableWorkshopHostQuotes = config.EnableWorkshopHostCraftQuotes;
        if (ImGui.Checkbox("Enable Workshop Host craft quotes", ref enableWorkshopHostQuotes))
        {
            config.EnableWorkshopHostCraftQuotes = enableWorkshopHostQuotes;
            config.Save();
        }

        ImGui.TextColored(
            ColMuted,
            "Uses the configured Workshop Host service for advisory craft-cost evidence when the host advertises craft.appraise.");

        var enableManualFallback = config.EnableCraftArchitectManualFallback;
        if (ImGui.Checkbox("Enable manual craft-cost fallback", ref enableManualFallback))
        {
            config.EnableCraftArchitectManualFallback = enableManualFallback;
            config.Save();
        }

        ImGui.TextColored(
            ColMuted,
            "Default off. Workshop Host should be the normal quote path; manual craft cost entry is only for local troubleshooting.");
    }

    private CraftAppraisalRequestBuilderController CreateAcquisitionRequestBuilderCraftAppraisalController()
    {
        var capabilitiesClient = new WorkshopHostCapabilitiesClient(craftQuoteHttpClient);
        CraftAppraisalRequestBuilderController? controller = null;
        controller = new CraftAppraisalRequestBuilderController(
            new LastGoodCraftQuoteProvider(new CompositeCraftQuoteProvider([
                new WorkshopHostCraftQuoteProvider(
                    craftQuoteHttpClient,
                    () => config.EnableWorkshopHostCraftQuotes,
                    () => controller?.State.WorkshopHostAvailable == true,
                    () => config.ServerUrl,
                    () => config.ApiKey),
                new CraftArchitectFileQuoteProvider(() => config.CraftArchitectQuoteFilePath),
            ])),
            cancellationToken => capabilitiesClient.SupportsCraftAppraiseV1Async(
                config.ServerUrl,
                config.ApiKey,
                cancellationToken),
            Path.Combine(
                Plugin.PluginInterface.GetPluginConfigDirectory(),
                "craft-architect-quote-logs"));
        controller.State.WorkshopHostEnabled = config.EnableWorkshopHostCraftQuotes;
        return controller;
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

    private bool IsMarketAcquisitionUnlocked() => MarketAcquisitionUnlock.IsUnlocked(config);

    private IReadOnlyList<IAutomationDiagnosticProbe> CreateAutomationDiagnosticProbes()
    {
        return
        [
            new AutomationDiagnosticProbe("Retainer UI", RunRetainerUiDiagnosticProbe),
            new AutomationDiagnosticProbe("Market Board UI", RunMarketBoardUiDiagnosticProbe),
            new AutomationDiagnosticProbe("External Helpers", RunExternalHelperDiagnosticProbe),
        ];
    }

    private AutomationDiagnosticProbeResult RunRetainerUiDiagnosticProbe()
    {
        var state = new RetainerUiStateReader(Plugin.GameGui).DescribeRetainerUiState(
            [
                RetainerInventoryAddonNames.RetainerList,
                RetainerInventoryAddonNames.SelectString,
                RetainerInventoryAddonNames.InventoryLarge,
                RetainerInventoryAddonNames.InventorySmall,
                RetainerInventoryAddonNames.InputNumeric,
            ]);

        return new AutomationDiagnosticProbeResult(
            "Retainer UI",
            IsSuccess: true,
            state,
            new Dictionary<string, string?>
            {
                ["retainerList"] = DescribeAddon(RetainerInventoryAddonNames.RetainerList),
                ["selectString"] = DescribeAddon(RetainerInventoryAddonNames.SelectString),
                ["inventoryLarge"] = DescribeAddon(RetainerInventoryAddonNames.InventoryLarge),
                ["inventorySmall"] = DescribeAddon(RetainerInventoryAddonNames.InventorySmall),
                ["inputNumeric"] = DescribeAddon(RetainerInventoryAddonNames.InputNumeric),
            });
    }

    private AutomationDiagnosticProbeResult RunMarketBoardUiDiagnosticProbe()
    {
        var details = new Dictionary<string, string?>
        {
            ["itemSearch"] = DescribeAddon("ItemSearch"),
            ["itemSearchResult"] = DescribeAddon("ItemSearchResult"),
            ["itemDetail"] = DescribeAddon("ItemDetail"),
        };
        var isAnyMarketBoardAddonVisible = details.Values.Any(value => value?.Contains("visible", StringComparison.OrdinalIgnoreCase) == true);

        return new AutomationDiagnosticProbeResult(
            "Market Board UI",
            isAnyMarketBoardAddonVisible,
            isAnyMarketBoardAddonVisible
                ? "At least one tracked market-board addon is visible."
                : "No tracked market-board addon is visible.",
            details);
    }

    private AutomationDiagnosticProbeResult RunExternalHelperDiagnosticProbe()
    {
        var autoRetainerAvailable = autoRetainerRefresh.IsLoaded;
        var viwiAvailable = viwiWorkshoppaIpc.IsAvailable;
        return new AutomationDiagnosticProbeResult(
            "External Helpers",
            autoRetainerAvailable || viwiAvailable,
            "External helper availability probe completed.",
            new Dictionary<string, string?>
            {
                ["autoRetainer"] = autoRetainerAvailable ? "loaded" : "not loaded",
                ["viwiWorkshoppa"] = viwiAvailable ? "loaded" : "not loaded",
            });
    }

    private static unsafe string DescribeAddon(string addonName)
    {
        var addon = Plugin.GameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
        if (addon == null)
            return "not present";

        return $"{(addon->IsReady ? "ready" : "not ready")}, {(addon->IsVisible ? "visible" : "hidden")}";
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
                try
                {
                    retainerCacheStore?.Save(config.RetainerCache);
                }
                catch (Exception ex)
                {
                    log.Error(ex, "[MarketMafioso] Error saving cleared retainer inventory cache");
                }
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

    public void Dispose()
    {
        acquisitionRequestCancellation?.Dispose();
        marketBoardAutomationController.Dispose();
        marketAcquisitionRouteRunner.Dispose();
        acquisitionHttpClient.Dispose();
        craftQuoteHttpClient.Dispose();
    }
}
