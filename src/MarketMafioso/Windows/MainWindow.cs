using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using MarketMafioso.AgentBridge;
using MarketMafioso.Automation.Retainers;
using MarketMafioso.Automation.Travel;
using MarketMafioso.CraftArchitectCompanion;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.RetainerRestock;
using MarketMafioso.Windows.Main;
using MarketMafioso.Windows.MarketAcquisitionPanels;
using MarketMafioso.Windows.MarketAcquisitionRequestBuilder;
using MarketMafioso.Windows.RetainerRestock;
using MarketMafioso.Windows.Squire;
using MarketMafioso.Windows.WorkshopLogistics;
using MarketMafioso.Squire.Observation;
using MarketMafioso.WorkshopPrep;
using MarketMafioso.Diagnostics;
using Franthropy.Dalamud.AgentBridge;

namespace MarketMafioso.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Configuration config;
    private readonly HttpReporter reporter;
    private readonly InventoryScanner scanner;
    private readonly AutoRetainerRefreshService autoRetainerRefresh;
    private readonly WorkshopProjectCatalog workshopCatalog;
    private readonly VIWIWorkshoppaIpc viwiWorkshoppaIpc;
    private readonly WorkshopAssemblyRunner workshopAssemblyRunner;
    private readonly RetainerCacheFileStore? retainerCacheStore;
    private readonly IPlayerState playerState;
    private readonly IPluginLog log;
    private readonly HttpClient acquisitionHttpClient = new();
    private readonly HttpClient craftQuoteHttpClient = new();
    private readonly MarketAcquisitionRequestWorkspace acquisitionWorkspace;
    private readonly MarketBoardApproachService marketBoardApproachService;
    private readonly MarketAcquisitionRouteEngine routeEngine;
    private readonly string marketAcquisitionRouteDiagnosticsDirectory;
    private readonly OverviewTabPanel overviewTab;
    private readonly InventoryReporterTabPanel inventoryReporterTab;
    private readonly SquireTabPanel squireTab;
    private readonly StatusTabPanel statusTab;
    private readonly SettingsTabPanel settingsTab;
    private readonly MarketAcquisitionPlanPanel marketAcquisitionPlanPanel = new();
    private readonly MarketAcquisitionRequestPickupPanel marketAcquisitionRequestPickupPanel;
    private readonly MarketAcquisitionAcceptedRequestPanel marketAcquisitionAcceptedRequestPanel;
    private readonly MarketAcquisitionDiagnosticsPanel marketAcquisitionDiagnosticsPanel;
    private readonly UiStateCaptureService uiStateCapture;
    private readonly MarketAcquisitionGuidedRoutePanel marketAcquisitionGuidedRoutePanel;
    private readonly MarketAcquisitionRequestBuilderPanel acquisitionRequestBuilder;
    private readonly RetainerRestockTabPanel restockTab;
    private readonly WorkshopPrepQueuePanel workshopPrepQueue;
    private readonly WorkshopMaterialPanel workshopMaterials;
    private readonly WorkshopAssemblyPanel workshopAssembly;
    public AgentBridgeUiReviewRegistry AgentReviewRegistry { get; } = new();

    private readonly WorkshopProjectSelectionState workshopProjectSelection = new();
    private int marketInputCaptureIndex;
    private string workshopStatus = "Workshop prep queue is idle.";
    private string? agentRequestedTab;
    private bool clearAgentReviewWindowOverride;
    private bool agentReviewWasPinned;
    private bool capturePresentationWasPinned;

    public AgentBridgeCaptureRegion? AgentCaptureRegion { get; private set; }
    public AgentBridgeUiCaptureTransactionManager AgentCaptureTransactions { get; }

    private const string ProductSummary = "Workshop logistics and self-hosted inventory history.";
    private const string WorkshopLogisticsModuleSummary = "Workshop Logistics tracks company workshop jobs, materials, retainer restock, handoff, and assembly.";
    private const string MarketAcquisitionModuleSummary = "Build, sync, and monitor acquisition requests from one persistent board.";

    internal static readonly Vector4 ColHeader = MarketMafiosoUiTheme.Header;
    internal static readonly Vector4 ColSuccess = MarketMafiosoUiTheme.Success;
    internal static readonly Vector4 ColError = MarketMafiosoUiTheme.Error;
    internal static readonly Vector4 ColMuted = MarketMafiosoUiTheme.Muted;

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
        this.workshopAssemblyRunner = workshopAssemblyRunner;
        this.retainerCacheStore = retainerCacheStore;
        this.playerState = playerState;
        this.log = log;
        AgentCaptureTransactions = new AgentBridgeUiCaptureTransactionManager(
            () => IsOpen,
            value => IsOpen = value,
            () => Collapsed == true,
            value =>
            {
                Collapsed = value;
                CollapsedCondition = ImGuiCond.Always;
            },
            beginPresentation: () =>
            {
                capturePresentationWasPinned = IsPinned;
                IsPinned = true;
            },
            restorePresentation: () => IsPinned = capturePresentationWasPinned);
        var acquisitionClient = new MarketAcquisitionRequestClient(acquisitionHttpClient);
        var acquisitionPlanSource = new UniversalisMarketAcquisitionPlanSource(acquisitionHttpClient);
        var marketAcquisitionWorldVisitCatalog = new MarketAcquisitionWorldVisitCatalog(config);
        var marketAcquisitionPlanPreparationService = new MarketAcquisitionPlanPreparationService(
            acquisitionPlanSource,
            marketAcquisitionWorldVisitCatalog,
            (ex, worldName, itemId) => log.Warning(
                ex,
                "[MarketMafioso] Unable to refresh Universalis evidence for {World} item {ItemId}.",
                worldName,
                itemId));
        acquisitionWorkspace = new MarketAcquisitionRequestWorkspace(
            config,
            acquisitionClient,
            marketAcquisitionPlanPreparationService,
            config.Save,
            ex => log.Warning(ex, "[MarketMafioso] Market acquisition request action failed."));
        var universalisFreshnessVerifier = new UniversalisMarketFreshnessVerifier(acquisitionHttpClient);
        var marketBoardListingReader = new MarketBoardListingReader(Plugin.GameGui);
        var marketBoardItemSearchDriver = new MarketBoardItemSearchDriver(Plugin.GameGui);
        var marketBoardInputCaptureReader = new MarketBoardInputCaptureReader(Plugin.GameGui);
        var marketBoardPurchaseAdapter = new DalamudMarketBoardPurchaseAdapter(Plugin.GameGui, log);
        var marketBoardPurchaseExecutor = new MarketBoardPurchaseExecutor(marketBoardPurchaseAdapter);
        this.marketBoardApproachService = marketBoardApproachService;
        this.marketAcquisitionRouteDiagnosticsDirectory = marketAcquisitionRouteDiagnosticsDirectory;
        var marketAcquisitionRouteRunner = new MarketAcquisitionRouteRunner(
            marketAcquisitionRouteDiagnosticsDirectory,
            universalisFreshnessVerifier.VerifyAsync);
        routeEngine = new MarketAcquisitionRouteEngine(
            marketAcquisitionRouteRunner,
            new DalamudMarketAcquisitionRouteContext(playerState),
            new DalamudMarketAcquisitionRouteUiAutomation(),
            new UnsupportedMarketAcquisitionRouteTravelCleanup(),
            new DalamudMarketAcquisitionMarketBoardIo(
                marketBoardApproachService,
                marketBoardItemSearchDriver,
                marketBoardListingReader,
                marketBoardInputCaptureReader),
            new DalamudMarketAcquisitionPurchaseIo(marketBoardPurchaseExecutor, marketBoardPurchaseAdapter),
            new MarketAcquisitionRouteRequestReporter(config, acquisitionClient),
            new MarketAcquisitionWorldVisitEvidenceRecorder(config, marketAcquisitionWorldVisitCatalog),
            acquisitionWorkspace.CreateClaimLifecycleController(
                () => marketAcquisitionRouteRunner.StatusMessage),
            new DalamudMarketAcquisitionRouteCallbackDispatcher(),
            new SystemMarketAcquisitionRouteClock());

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

        overviewTab = new OverviewTabPanel(IsMarketAcquisitionUnlocked);
        inventoryReporterTab = new InventoryReporterTabPanel(
            config,
            reporter,
            autoRetainerRefresh,
            Plugin.Instance.RestartTimer,
            config.Save);
        var squireSnapshotSource = new DalamudCharacterEquipmentSnapshotSource(playerState, dataManager, log);
        var squireCapabilities = new DalamudSquireDispositionCapabilitySource();
        squireTab = new SquireTabPanel(
            config,
            squireSnapshotSource,
            new DalamudSquireActionGameAdapter(squireSnapshotSource, playerState, Plugin.Condition, Plugin.GameGui, Plugin.Framework, squireCapabilities),
            squireCapabilities,
            AgentReviewRegistry,
            Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "squire-logs"));
        statusTab = new StatusTabPanel(config, reporter, retainerCacheStore, log);
        marketAcquisitionRequestPickupPanel = new MarketAcquisitionRequestPickupPanel(
            () => _ = FetchDashboardRequestsAsync(),
            requestId => _ = ClaimAcquisitionRequestAsync(requestId));
        marketAcquisitionAcceptedRequestPanel = new MarketAcquisitionAcceptedRequestPanel(
            () => _ = acquisitionWorkspace.AcceptAsync(),
            () => _ = acquisitionWorkspace.RejectAsync(),
            acquisitionWorkspace.ForgetLocalClaim,
            () => _ = PrepareMarketAcquisitionPlanAsync());
        restockTab = new RetainerRestockTabPanel(
            config,
            scanner,
            autoRetainerRefresh,
            workshopRetainerRestock,
            GetCurrentRetainerOwnerScope);
        WorkshopProjectBrowserWindow? projectBrowser = null;
        WorkshopFrozenQueueBrowserWindow? frozenQueueBrowser = null;
        workshopPrepQueue = new WorkshopPrepQueuePanel(
            config,
            workshopCatalog,
            viwiWorkshoppaIpc,
            workshopAssemblyRunner,
            workshopProjectSelection,
            workshopMaterialManifestExport,
            GetWorkshopAvailability,
            status => workshopStatus = status,
            () => projectBrowser!.IsOpen = true,
            () => frozenQueueBrowser!.IsOpen = true,
            log);
        workshopMaterials = new WorkshopMaterialPanel(autoRetainerRefresh, workshopRetainerRestock, GetWorkshopAvailability);
        workshopAssembly = new WorkshopAssemblyPanel(
            workshopAssemblyRunner,
            workshopRetainerRestock,
            () => workshopStatus,
            status => workshopStatus = status,
            StartWorkshopAssembly);
        ProjectBrowser = new WorkshopProjectBrowserWindow(
            config,
            workshopCatalog,
            workshopProjectSelection,
            workshopPrepQueue.AddWorkshopProject);
        projectBrowser = ProjectBrowser;
        FrozenQueueBrowser = new WorkshopFrozenQueueBrowserWindow(
            config,
            workshopCatalog,
            new WorkshopFrozenQueueBrowserActions(
                () => workshopPrepQueue.CanEditQueue,
                workshopPrepQueue.LoadFrozenQueue,
                workshopPrepQueue.OverwriteFrozenQueueWithCurrent,
                workshopPrepQueue.RenameFrozenQueue,
                workshopPrepQueue.DuplicateFrozenQueue,
                workshopPrepQueue.DeleteFrozenQueue,
                workshopPrepQueue.SaveCurrentQueueAsNew));
        frozenQueueBrowser = FrozenQueueBrowser;
        var acquisitionRequestBuilderCraftAppraisal = CreateAcquisitionRequestBuilderCraftAppraisalController();
        acquisitionRequestBuilder = new MarketAcquisitionRequestBuilderPanel(
            config,
            dataManager,
            acquisitionRequestBuilderCraftAppraisal,
            SyncAcquisitionRequestBuilderAsync,
            RefreshAcquisitionRequestBuilderRemoteAsync,
            acquisitionWorkspace.OnDocumentAdopted);
        acquisitionWorkspace.Connect(
            acquisitionRequestBuilder.AdoptRequest,
            acquisitionRequestBuilder.AdoptRestoredRequestIfSafe,
            () => acquisitionRequestBuilder.CurrentIntentHash,
            acquisitionRequestBuilder.MarkPlanPrepared,
            () => routeEngine.IsRouteActive,
            ResetGuidedRoute);
        AcquisitionDiagnostics = new MarketAcquisitionDiagnosticsWindow(
            routeEngine.CreateSnapshot,
            () => acquisitionWorkspace.PreparedPlan,
            CanProbeLiveMarketBoard,
            () => _ = ProbeLiveMarketBoardAsync(),
            () => acquisitionRequestBuilderCraftAppraisal.State.CreateDiagnosticsSnapshot());
        AutomationDiagnostics = new AutomationDiagnosticsWindow(
            new AutomationDiagnosticProbeFactory(autoRetainerRefresh, viwiWorkshoppaIpc).Create(),
            () => true);
        uiStateCapture = new UiStateCaptureService(
            Plugin.AddonLifecycle,
            Plugin.Framework,
            Plugin.Condition,
            Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "ui-state-captures"));
        marketAcquisitionDiagnosticsPanel = new MarketAcquisitionDiagnosticsPanel(
            routeEngine.CreateSnapshot,
            marketAcquisitionRouteDiagnosticsDirectory,
            log,
            AcquisitionDiagnostics.Draw,
            AutomationDiagnostics.Draw,
            uiStateCapture,
            AgentReviewRegistry);
        marketAcquisitionGuidedRoutePanel = new MarketAcquisitionGuidedRoutePanel(
            routeEngine.CreateSnapshot,
            forceDiagnostics => _ = StartGuidedRouteAsync(forceDiagnostics),
            () => _ = PauseGuidedRouteAsync(),
            () => _ = ResumeGuidedRouteAsync(),
            () => _ = StopGuidedRouteAsync(),
            () => _ = RestartGuidedRouteAsync(),
            () => _ = ReprepareGuidedRouteAsync(),
            marketAcquisitionDiagnosticsPanel.DrawPostRunDiagnosticSummary,
            marketAcquisitionDiagnosticsPanel.DrawLatestWorldCompletionSummary,
            DrawMarketBoardProbeStatus);
        settingsTab = new SettingsTabPanel(
            config,
            reporter,
            log,
            () => _ = routeEngine.Stop());

        acquisitionWorkspace.RestoreClaimIntoBuilder();
    }

    public WorkshopProjectBrowserWindow ProjectBrowser { get; }
    public WorkshopFrozenQueueBrowserWindow FrozenQueueBrowser { get; }
    public MarketAcquisitionDiagnosticsWindow AcquisitionDiagnostics { get; }
    public AutomationDiagnosticsWindow AutomationDiagnostics { get; }

    public AgentBridgeTruth CreateAgentBridgeTruth()
    {
        var snapshot = routeEngine.CreateSnapshot();
        var activeOperation = snapshot.ActiveOperation;
        var activeStop = snapshot.ActiveStop;
        return new AgentBridgeTruth
        {
            SchemaVersion = 1,
            PluginInstanceId = config.PluginInstanceId,
            ProcessId = Environment.ProcessId,
            PluginVersion = PluginBuildInfo.DisplayVersion,
            CharacterName = playerState.CharacterName ?? string.Empty,
            CurrentWorld = playerState.CurrentWorld.IsValid ? playerState.CurrentWorld.Value.Name.ToString() : string.Empty,
            HomeWorld = playerState.HomeWorld.IsValid ? playerState.HomeWorld.Value.Name.ToString() : string.Empty,
            MainWindowOpen = IsOpen,
            AcquisitionDiagnosticsOpen = AcquisitionDiagnostics.IsOpen,
            WorkspaceStatus = acquisitionWorkspace.Status,
            WorkspaceBusy = acquisitionWorkspace.IsBusy,
            ClaimedRequestId = acquisitionWorkspace.ClaimedRequest?.Id,
            PreparedPlanStatus = acquisitionWorkspace.PreparedPlan?.Status,
            Route = new AgentBridgeRouteTruth
            {
                State = snapshot.RouteState,
                StatusMessage = snapshot.StatusMessage,
                VisibleStatus = snapshot.VisibleAcquisitionStatus,
                IsActive = snapshot.IsRouteActive,
                IsRunning = snapshot.IsRunning,
                IsPaused = snapshot.IsPaused,
                ActiveWorld = activeStop?.WorldName,
                ActiveStopStatus = activeStop?.Status,
                ActiveOperationId = activeOperation?.OperationId,
                ActiveOperationKind = activeOperation?.Kind.ToString(),
                ActiveOperationPhase = activeOperation?.Phase.ToString(),
                ActiveOperationDisposition = activeOperation?.Disposition.ToString(),
                StopCount = snapshot.Stops.Count,
                CompletedOrProbedStopCount = snapshot.CompletedOrProbedStopCount,
            },
            Squire = squireTab.CreateAgentBridgeTruth(),
        };
    }

    public void OnFrameworkUpdate(IFramework _)
    {
        if (!IsMarketAcquisitionUnlocked())
            return;

        routeEngine.MonitorMarketBoardPurchase();
        routeEngine.TickRoute(acquisitionWorkspace.IsBusy);
    }

    public override void PreDraw()
    {
        if (!AgentCaptureTransactions.ShouldPresentInMainViewport("mmf.main-window"))
            return;
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowViewport(viewport.ID);
        ImGui.SetNextWindowPos(viewport.WorkPos + new Vector2(16, 16), ImGuiCond.Always);
        ImGui.SetNextWindowFocus();
    }

    public override void Draw()
    {
        AgentReviewRegistry.BeginFrame();
        AgentBridgeUiReviewFrame? reviewFrame = null;
        try
        {
            if (clearAgentReviewWindowOverride)
            {
                IsPinned = agentReviewWasPinned;
                Collapsed = null;
                CollapsedCondition = ImGuiCond.None;
                clearAgentReviewWindowOverride = false;
            }

        var viewport = ImGui.GetWindowViewport();
        var windowPosition = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        if (windowSize.X > 0f && windowSize.Y > 0f && viewport.Size.X > 0f && viewport.Size.Y > 0f)
        {
            AgentCaptureRegion = new AgentBridgeCaptureRegion(
                windowPosition,
                windowSize,
                viewport.Pos,
                viewport.Size,
                DateTimeOffset.UtcNow);
        }

        DrawHeader();
        ImGui.Spacing();

        if (ImGui.BeginTabBar("##MarketMafiosoTabs"))
        {
            if (ImGui.BeginTabItem("Overview", GetAgentTabFlags("Overview")))
            {
                overviewTab.Draw();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Inventory Reporter", GetAgentTabFlags("Inventory Reporter")))
            {
                inventoryReporterTab.Draw();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Squire", GetAgentTabFlags("Squire")))
            {
                squireTab.Draw();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Workshop Logistics", GetAgentTabFlags("Workshop Logistics")))
            {
                DrawWorkshopPrepTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Restock", GetAgentTabFlags("Restock")))
            {
                DrawRetainerRestockTab();
                ImGui.EndTabItem();
            }

            if (IsMarketAcquisitionUnlocked() && ImGui.BeginTabItem("Market Acquisition", GetAgentTabFlags("Market Acquisition")))
            {
                DrawMarketAcquisitionTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Diagnostics", GetAgentTabFlags("Diagnostics")))
            {
                marketAcquisitionDiagnosticsPanel.Draw();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Settings", GetAgentTabFlags("Settings")))
            {
                settingsTab.Draw();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Status", GetAgentTabFlags("Status")))
            {
                statusTab.Draw();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
            agentRequestedTab = null;
        }
        }
        finally
        {
            reviewFrame = AgentReviewRegistry.EndFrame();
            if (AgentCaptureRegion != null && reviewFrame != null && AgentCaptureTransactions.ShouldPresentInMainViewport("mmf.main-window"))
                AgentCaptureTransactions.MarkRendered("mmf.main-window", reviewFrame.FrameId);
        }
    }

    public bool TrySelectAgentBridgeTab(string tabName)
    {
        var allowed = tabName switch
        {
            "Overview" or "Inventory Reporter" or "Squire" or "Workshop Logistics" or "Restock" or "Settings" or "Status" => true,
            "Diagnostics" => true,
            "Market Acquisition" => IsMarketAcquisitionUnlocked(),
            _ => false,
        };
        if (!allowed)
            return false;

        if (string.Equals(tabName, "Squire", StringComparison.Ordinal))
            squireTab.RefreshForBridge();

        agentRequestedTab = tabName;
        AgentOpenForReview();
        return true;
    }

    public void AgentOpenForReview()
    {
        if (!clearAgentReviewWindowOverride)
            agentReviewWasPinned = IsPinned;

        IsOpen = true;
        IsPinned = true;
        Collapsed = false;
        CollapsedCondition = ImGuiCond.Always;
        clearAgentReviewWindowOverride = true;
    }

    public void AgentCaptureInputState()
    {
        if (!uiStateCapture.IsRecording)
            uiStateCapture.Start("agent-bridge-ui-transaction");
        else
            uiStateCapture.Mark("agent-bridge-capture-input-state");
    }

    public void AgentStopRoute()
    {
        if (!routeEngine.IsRouteActive)
            return;

        routeEngine.Stop();
        routeEngine.ReportRouteProgress();
    }

    private ImGuiTabItemFlags GetAgentTabFlags(string tabName) =>
        string.Equals(agentRequestedTab, tabName, StringComparison.Ordinal)
            ? ImGuiTabItemFlags.SetSelected
            : ImGuiTabItemFlags.None;

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

    private void DrawWorkshopPrepTab()
    {
        ImGui.Spacing();
        ImGui.TextColored(ColHeader, "Workshop Logistics");
        ImGui.TextWrapped(WorkshopLogisticsModuleSummary);
        ImGui.Spacing();

        var projects = workshopCatalog.GetProjects();

        workshopPrepQueue.Draw(projects);
        ImGui.Spacing();
        workshopMaterials.Draw();
        ImGui.Spacing();
        workshopAssembly.Draw(config.WorkshopPrepQueue.Count > 0);
        workshopPrepQueue.DrawConfirmations();
    }

    private void DrawRetainerRestockTab()
    {
        restockTab.Draw();
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
            acquisitionWorkspace.IsBusy,
            IsMarketAcquisitionRouteActive(),
            acquisitionWorkspace.ClaimedRequest,
            acquisitionWorkspace.PreparedPlan,
            acquisitionWorkspace.PreparedPlanHash));
    }

    private void DrawMarketAcquisitionExecutionPane()
    {
        ImGuiUi.SectionHeader("Accepted Request, Plan, and Route", ColHeader);
        DrawClaimedAcquisitionRequest();
        ImGui.Spacing();
        DrawMarketAcquisitionPlan();
        ImGui.Spacing();
        DrawMarketAcquisitionGuidedRoute();
    }

    private void DrawMarketAcquisitionPickupSection(bool compactWhenClaimed = false)
    {
        var hasScope = TryGetAcquisitionScope(out var characterName, out var world);
        var visibleStatus = GetVisibleAcquisitionStatus();
        marketAcquisitionRequestPickupPanel.Draw(new MarketAcquisitionRequestPickupContext(
            compactWhenClaimed,
            IsMarketAcquisitionRouteActive(),
            acquisitionWorkspace.ClaimedRequest,
            acquisitionWorkspace.PendingRequests,
            acquisitionWorkspace.IsBusy,
            !string.IsNullOrWhiteSpace(config.ApiKey),
            hasScope,
            characterName,
            world,
            IsExpectedCharacterScopeGap(),
            visibleStatus,
            GetAcquisitionStatusColor(visibleStatus)));
    }

    private void DrawClaimedAcquisitionRequest()
    {
        marketAcquisitionAcceptedRequestPanel.Draw(
            acquisitionWorkspace.ClaimedRequest,
            acquisitionWorkspace.IsBusy,
            acquisitionWorkspace.ClaimedRequest is not null &&
            !acquisitionWorkspace.IsBusy &&
            MarketAcquisitionPlanPreparationService.CanPrepareForStatus(acquisitionWorkspace.ClaimedRequest.Status));
    }

    private void DrawMarketAcquisitionPlan()
    {
        marketAcquisitionPlanPanel.Draw(acquisitionWorkspace.PreparedPlan, acquisitionWorkspace.IsPreparedPlanStale());
    }

    private bool CanProbeLiveMarketBoard()
    {
        return !acquisitionWorkspace.IsBusy &&
               !routeEngine.IsRouteActive &&
               acquisitionWorkspace.PreparedPlan is { Status: "Ready" } &&
               !acquisitionWorkspace.IsPreparedPlanStale() &&
               acquisitionWorkspace.PreparedPlan.WorldBatches.Count > 0;
    }

    private Task ProbeLiveMarketBoardAsync()
    {
        return acquisitionWorkspace.RunAsync(_ =>
        {
            var plan = acquisitionWorkspace.RequirePreparedPlan(
                "Prepare a live candidate plan before probing live market board listings.");
            var claimed = acquisitionWorkspace.RequireClaimedRequest("No dashboard request is accepted.");
            routeEngine.ProbePreparedPlan(plan, claimed);
            acquisitionWorkspace.SetStatus(routeEngine.CreateSnapshot().VisibleAcquisitionStatus);
            return Task.CompletedTask;
        });
    }

    private void DrawMarketBoardProbeStatus(MarketAcquisitionRouteEngineSnapshot snapshot)
    {
        DrawLiveCandidatePlanResult(snapshot);

        if (ImGuiUi.Button("Open Diagnostics", true))
            agentRequestedTab = "Diagnostics";
    }

    private void DrawLiveCandidatePlanResult(MarketAcquisitionRouteEngineSnapshot snapshot)
    {
        var candidatePlan = snapshot.LiveCandidatePlan;
        if (candidatePlan == null)
            return;

        ImGui.Spacing();
        ImGui.TextColored(
            candidatePlan.Status == "Ready" ? ColSuccess : ColHeader,
            $"Live candidates: {candidatePlan.Status}  -  Would buy {candidatePlan.WouldBuyQuantity:N0}/{candidatePlan.RequestedQuantity:N0}, spend {FormatGil(candidatePlan.WouldSpendGil)}");
        ImGui.TextWrapped(candidatePlan.Message);

        var summary = MarketAcquisitionLiveCandidatePresenter.BuildSummary(candidatePlan);
        ImGui.TextColored(ColMuted, $"{summary.WouldBuyRows:N0} buy row(s), {summary.SkippedRows:N0} skipped row(s).");

        var activeStop = snapshot.ActiveStop;
        var purchasingWorld = activeStop is { Status: "Purchasing" };

        var firstCandidate = MarketBoardPurchasePlanner.SelectFirstCandidate(candidatePlan);
        if (firstCandidate != null && purchasingWorld)
        {
            ImGui.TextColored(
                ColMuted,
                $"Next safe listing: {firstCandidate.Quantity:N0} @ {FormatGil(firstCandidate.UnitPrice)} ({FormatGil(firstCandidate.TotalGil)})");
        }

        if (purchasingWorld)
        {
            ImGui.TextColored(ColHeader, $"World batch running: purchased {snapshot.ActiveWorldPurchasedQuantity:N0}, spent {FormatGil(snapshot.ActiveWorldSpentGil)}.");
        }

        var purchaseSession = snapshot.PurchaseSession;
        var purchaseResult = snapshot.LastPurchaseResult;
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

    private Task FetchDashboardRequestsAsync()
    {
        TryGetAcquisitionScope(out var characterName, out var world);
        return acquisitionWorkspace.FetchPendingAsync(characterName, world);
    }

    private Task<MarketAcquisitionRequestBuilderSyncOutcome> SyncAcquisitionRequestBuilderAsync(
        MarketAcquisitionRequestDocument document)
    {
        TryGetAcquisitionScope(out var characterName, out var world);
        return acquisitionWorkspace.SyncAsync(document, characterName, world);
    }

    private Task<MarketAcquisitionRequestBuilderRefreshOutcome> RefreshAcquisitionRequestBuilderRemoteAsync(
        MarketAcquisitionRequestDocument document) =>
        acquisitionWorkspace.RefreshRemoteAsync(document);

    private Task ClaimAcquisitionRequestAsync(string requestId)
    {
        TryGetAcquisitionScope(out var characterName, out var world);
        return acquisitionWorkspace.ClaimAsync(requestId, characterName, world);
    }

    private Task PrepareMarketAcquisitionPlanAsync()
    {
        var currentWorld = playerState.CurrentWorld.IsValid ? GetCurrentWorldName() : string.Empty;
        return acquisitionWorkspace.PreparePlanAsync(
            currentWorld,
            GetMarketAcquisitionRecentWorldTtl(),
            config.MarketAcquisitionIgnoreRecentWorldVisitsForSweep);
    }

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

    private void DrawMarketAcquisitionGuidedRoute()
    {
        marketAcquisitionGuidedRoutePanel.Draw(
            acquisitionWorkspace.PreparedPlan,
            acquisitionWorkspace.IsPreparedPlanStale());
    }

    private Task StartGuidedRouteAsync(bool forceDiagnostics)
    {
        return acquisitionWorkspace.RunWithReportableClaimAsync((claimed, _) =>
        {
            var plan = acquisitionWorkspace.RequirePreparedPlan("Prepare a plan before starting a guided route.");
            var enableDiagnostics = MarketAcquisitionRouteDiagnosticsPolicy.ShouldCreatePackage(
                config.CreateMarketAcquisitionRouteDiagnosticPackages,
                forceDiagnostics);
            routeEngine.Start(
                plan,
                claimed,
                enableDiagnostics,
                config.EnableOpportunisticWorldChecks);
            routeEngine.ReportRouteProgress();
            return Task.CompletedTask;
        });
    }

    private Task PauseGuidedRouteAsync()
    {
        routeEngine.Pause();
        routeEngine.ReportRouteProgress();
        return Task.CompletedTask;
    }

    private Task ResumeGuidedRouteAsync()
    {
        routeEngine.Resume();
        routeEngine.ReportRouteProgress();
        return Task.CompletedTask;
    }

    private Task StopGuidedRouteAsync()
    {
        routeEngine.Stop();
        routeEngine.ReportRouteProgress();
        return Task.CompletedTask;
    }

    private Task RestartGuidedRouteAsync()
    {
        return acquisitionWorkspace.RunWithReportableClaimAsync((claimed, _) =>
        {
            var plan = acquisitionWorkspace.RequirePreparedPlan("Prepare a plan before restarting a guided route.");
            routeEngine.Restart(plan, claimed);
            routeEngine.ReportRouteProgress();
            return Task.CompletedTask;
        });
    }

    private Task ReprepareGuidedRouteAsync()
    {
        return acquisitionWorkspace.RunWithReportableClaimAsync((claimed, _) =>
        {
            var plan = acquisitionWorkspace.RequirePreparedPlan("Prepare a plan before re-preparing a guided route.");
            var result = routeEngine.ReprepareAndRestart(plan, DateTimeOffset.UtcNow, claimed);
            var snapshot = routeEngine.CreateSnapshot();
            if (snapshot.ActivePlan != null)
                acquisitionWorkspace.ReplacePreparedPlan(snapshot.ActivePlan);
            acquisitionWorkspace.SetStatus(result.Message);
            routeEngine.ReportRouteProgress();
            return Task.CompletedTask;
        });
    }

    private void CaptureMarketBoardInputState()
    {
        try
        {
            var label = $"input-capture-{++marketInputCaptureIndex}";
            var result = routeEngine.CaptureInputState(label);
            acquisitionWorkspace.SetStatus(result.Success
                ? $"{result.Message} {routeEngine.CreateSnapshot().LastDiagnosticFilePath}"
                : result.Message);
        }
        catch (Exception ex)
        {
            acquisitionWorkspace.SetStatus($"Unable to capture market board input state. {ex.Message}");
            log.Warning(ex, "[MarketMafioso] Unable to capture market board input state.");
        }
    }

    private void FinalizeMarketBoardInputCaptureLog()
    {
        try
        {
            var result = routeEngine.FinalizeInputCaptureLog();
            acquisitionWorkspace.SetStatus(result.Message);
        }
        catch (Exception ex)
        {
            acquisitionWorkspace.SetStatus($"Unable to finalize market board input capture log. {ex.Message}");
            log.Warning(ex, "[MarketMafioso] Unable to finalize market board input capture log.");
        }
    }

    private void ResetGuidedRoute(string status)
    {
        routeEngine.Reset(status);
    }

    private bool IsMarketAcquisitionRouteActive() =>
        routeEngine.IsRouteActive;

    private bool IsExpectedCharacterScopeGap() =>
        acquisitionWorkspace.ClaimedRequest != null &&
        routeEngine.CreateSnapshot().ActiveStop?.Status == "TravelCommandSent";

    private string GetVisibleAcquisitionStatus()
    {
        if (acquisitionWorkspace.Status.StartsWith("Route progress report failed", StringComparison.OrdinalIgnoreCase) &&
            IsMarketAcquisitionRouteActive())
            return routeEngine.CreateSnapshot().StatusMessage;

        return acquisitionWorkspace.Status;
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

    private static string FormatGil(uint gil) => $"{gil:N0} gil";

    private Vector4 GetAcquisitionStatusColor(string? visibleStatus = null)
    {
        if (acquisitionWorkspace.IsBusy)
            return ColHeader;

        var status = visibleStatus ?? acquisitionWorkspace.Status;
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
            acquisitionWorkspace.SetStatus($"Unable to open dashboard: {ex.Message}");
            log.Warning(ex, "[MarketMafioso] Unable to open market acquisition dashboard.");
        }
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

    private bool IsMarketAcquisitionUnlocked() => MarketAcquisitionUnlock.IsUnlocked(config);

    public void Dispose()
    {
        AgentCaptureTransactions.CancelActive();
        squireTab.Dispose();
        uiStateCapture.Dispose();
        acquisitionWorkspace.Dispose();
        routeEngine.Dispose();
        acquisitionHttpClient.Dispose();
        craftQuoteHttpClient.Dispose();
    }
}
