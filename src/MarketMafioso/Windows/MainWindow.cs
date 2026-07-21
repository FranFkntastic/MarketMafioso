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
using MarketMafioso.Squire;
using MarketMafioso.Windows.Main;
using MarketMafioso.Windows.MarketAcquisitionPanels;
using MarketMafioso.Windows.MarketAcquisitionRequestBuilder;
using MarketMafioso.Windows.RetainerRestock;
using MarketMafioso.Windows.Squire;
using MarketMafioso.Windows.WorkshopLogistics;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Squire.Outfitter.Acquisition;
using MarketMafioso.Squire.Outfitter.Utility;
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
    private readonly IDataManager dataManager;
    private readonly HttpClient acquisitionHttpClient = new();
    private readonly HttpClient craftQuoteHttpClient = new();
    private readonly MarketAcquisitionRequestWorkspace acquisitionWorkspace;
    private readonly MarketBoardApproachService marketBoardApproachService;
    private readonly MarketAcquisitionRouteEngine routeEngine;
    private readonly ConfigurationOutfitterRouteExecutionStateStore outfitterRouteStateStore;
    private readonly string marketAcquisitionRouteDiagnosticsDirectory;
    private readonly SquireTabPanel squireTab;
    private readonly DalamudCharacterEquipmentSnapshotSource squireSnapshotSource;
    private readonly LuminaRenderedEquipmentDefinitionLookup renderedDefinitionLookup;
    private readonly StatusTabPanel statusTab;
    private readonly SettingsTabPanel settingsTab;
    private readonly MarketAcquisitionPlanPanel marketAcquisitionPlanPanel = new();
    private readonly MarketAcquisitionRequestPickupPanel marketAcquisitionRequestPickupPanel;
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
    private Task? outfitterRecoveryTask;
    private Task? outfitterAutoResumeTask;
    private DateTimeOffset nextOutfitterAutoResumeAtUtc;
    private string workshopStatus = "Workshop prep queue is idle.";
    private string? agentRequestedTab;
    private string? agentRequestedWorkspaceView;
    private DateTimeOffset agentSelectionHoldUntilUtc;
    private bool clearAgentReviewWindowOverride;
    private Vector2? capturePresentationPreviousSize;
    private Vector2? capturePresentationRestoreSize;

    public AgentBridgeCaptureRegion? AgentCaptureRegion { get; private set; }
    public AgentBridgeUiCaptureTransactionManager AgentCaptureTransactions { get; }

    private const string ProductSummary = "Workshop logistics and self-hosted inventory history.";
    private const string WorkshopLogisticsModuleSummary = "Workshop Logistics tracks company workshop jobs, materials, retainer restock, handoff, and assembly.";

    internal static readonly Vector4 ColHeader = MarketMafiosoUiTheme.Header;
    internal static readonly Vector4 ColSuccess = MarketMafiosoUiTheme.Success;
    internal static readonly Vector4 ColError = MarketMafiosoUiTheme.Error;
    internal static readonly Vector4 ColWarning = MarketMafiosoUiTheme.Warning;
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
        IPluginLog log,
        IRenderedCharacterAdvisorProbe renderedCharacterAdvisorProbe)
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
                capturePresentationPreviousSize = AgentCaptureRegion?.WindowSize;
            },
            restorePresentation: () =>
            {
                capturePresentationRestoreSize = capturePresentationPreviousSize;
                capturePresentationPreviousSize = null;
            });
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
        outfitterRouteStateStore = new ConfigurationOutfitterRouteExecutionStateStore(config);
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
            new SystemMarketAcquisitionRouteClock(),
            new FileMarketAcquisitionReportOutbox(Path.Combine(
                Plugin.PluginInterface.GetPluginConfigDirectory(),
                "market-acquisition-report-outbox.json")),
            outfitterRouteStateStore);

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(980, 560),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        if (WorkshopHostApiKeyRouting.NormalizeConfiguredKeys(config))
            config.Save();

        squireSnapshotSource = new DalamudCharacterEquipmentSnapshotSource(playerState, dataManager, log);
        var playerAdvisorBaselineSource = new DalamudPlayerAdvisorBaselineSource(squireSnapshotSource, playerState, dataManager);
        this.dataManager = dataManager;
        renderedDefinitionLookup = new LuminaRenderedEquipmentDefinitionLookup(dataManager);
        var squireCapabilities = new DalamudSquireDispositionCapabilitySource();
        var squireRuleStore = new SquireCleanupRuleStore(config);
        var squireVnavmesh = new VNavmeshIpc(new DalamudVNavmeshIpcAdapter(Plugin.PluginInterface, log));
        var squireLifestream = new LifestreamIpc(Plugin.PluginInterface, log);
        uiStateCapture = new UiStateCaptureService(
            Plugin.AddonLifecycle,
            Plugin.Framework,
            Plugin.Condition,
            Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "ui-state-captures"));
        squireTab = new SquireTabPanel(
            config,
            squireSnapshotSource,
            new DalamudSquireActionGameAdapter(
                squireSnapshotSource,
                playerState,
                Plugin.Condition,
                Plugin.GameGui,
                Plugin.Framework,
                dataManager,
                squireCapabilities,
                Plugin.CommandManager,
                Plugin.ObjectTable,
                Plugin.TargetManager,
                Plugin.PluginInterface,
                Plugin.Log,
                () => squireRuleStore.CreatePolicy(playerState.IsLoaded && playerState.ContentId != 0 ? playerState.ContentId : null),
                () => SquireExecutionRecoveryPolicy.From(config.Squire),
                () =>
                {
                    if (routeEngine.IsRouteActive)
                        return "The purchase route currently owns MMF automation.";
                    if (acquisitionWorkspace.IsBusy)
                        return "The purchase workspace is busy.";
                    if (autoRetainerRefresh.IsRefreshing)
                        return "The AutoRetainer inventory refresh is running.";
                    if (autoRetainerRefresh.IsStartQueued)
                        return "The AutoRetainer inventory refresh is queued to start.";
                    if (workshopAssemblyRunner.HasActiveRun)
                        return "The Workshop Assembly runner currently owns MMF automation.";
                    if (squireVnavmesh.IsRunning)
                        return "vnavmesh is currently moving the character.";
                    if (squireLifestream.IsAvailable && squireLifestream.TryIsBusy(out var lifestreamBusy) && lifestreamBusy)
                        return "Lifestream is currently handling travel.";
                    return null;
                }),
            squireCapabilities,
            AgentReviewRegistry,
            Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "squire-logs"),
            uiStateCapture,
            Plugin.GameInventory,
            dataManager,
            acquisitionPlanSource,
            playerAdvisorBaselineSource);
        statusTab = new StatusTabPanel(config, reporter, retainerCacheStore, log);
        marketAcquisitionRequestPickupPanel = new MarketAcquisitionRequestPickupPanel(
            () => _ = FetchDashboardRequestsAsync(),
            requestId =>
            {
                QueueAgentTabSelection("Market Acquisition", "Workbench");
                _ = ClaimAcquisitionRequestAsync(requestId);
            },
            AddAcquisitionLinesToWorkbench,
            ReturnAcquisitionLinesToInbox,
            () => QueueAgentTabSelection("Market Acquisition", "Workbench"),
            AgentReviewRegistry);
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
            acquisitionWorkspace.OnDocumentAdopted,
            AgentReviewRegistry);
        var acquisitionWorkbenchCompositions = new MarketAcquisitionWorkbenchCompositionPanel(
            new MarketAcquisitionWorkbenchCompositionCatalog(
            new ConfigurationMarketAcquisitionWorkbenchCompositionStore(config, config.Save)),
            acquisitionRequestBuilder.LoadComposition,
            composition => acquisitionRequestBuilder.MergeComposition(composition),
            AgentReviewRegistry);
        AcquisitionCompositionWindow = new MarketAcquisitionWorkbenchCompositionWindow(
            acquisitionWorkbenchCompositions,
            CreateMarketAcquisitionCompositionContext);
        squireTab.ConnectMarketAcquisition(
            transfer =>
            {
                acquisitionRequestBuilder.StageOutfitterTransfer(transfer);
                QueueAgentTabSelection("Market Acquisition", "Workbench");
            });
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
        marketAcquisitionDiagnosticsPanel = new MarketAcquisitionDiagnosticsPanel(
            routeEngine.CreateSnapshot,
            marketAcquisitionRouteDiagnosticsDirectory,
            log,
            AcquisitionDiagnostics.Draw,
            AutomationDiagnostics.Draw,
            squireTab.DrawDiagnosticTools,
            IsMarketAcquisitionUnlocked,
            uiStateCapture,
            AgentReviewRegistry,
            () => acquisitionRequestBuilderCraftAppraisal.State.CreateDiagnosticsSnapshot(),
            () => config.EnableMarketAcquisitionDryRunTools,
            CanStartPreparedRouteDryRun,
            () => _ = StartPreparedRouteDryRunAsync(),
            () => routeEngine.ArmedOutfitterDryRunScenario,
            scenario => routeEngine.ArmOutfitterDryRunScenario(scenario)
#if DEBUG
            , CanSeedOutfitterDryRunSunkState,
            SeedOutfitterDryRunSunkState
#endif
            );
        marketAcquisitionGuidedRoutePanel = new MarketAcquisitionGuidedRoutePanel(
            routeEngine.CreateSnapshot,
            forceDiagnostics => _ = StartGuidedRouteAsync(forceDiagnostics),
            () => _ = StartEvidenceRefreshAsync(),
            CanProbeLiveMarketBoard,
            () => _ = ProbeLiveMarketBoardAsync(),
            () => _ = PauseGuidedRouteAsync(),
            () => _ = ResumeGuidedRouteAsync(),
            () => _ = StopGuidedRouteAsync(),
            () => _ = RestartGuidedRouteAsync(),
            () => _ = ReprepareGuidedRouteAsync(),
            () => routeEngine.RequestOutfitterRecovery(acquisitionRequestBuilder.CurrentDocument),
            ReturnToOutfitterAdvisor,
            marketAcquisitionDiagnosticsPanel.DrawPostRunDiagnosticSummary,
            marketAcquisitionDiagnosticsPanel.DrawLatestWorldCompletionSummary,
            DrawMarketBoardProbeStatus,
            AgentReviewRegistry);
        settingsTab = new SettingsTabPanel(
            config,
            reporter,
            autoRetainerRefresh,
            log,
            () => _ = routeEngine.Stop(),
            Plugin.Instance.RestartTimer,
            playerState,
            dataManager,
            () => squireTab.CurrentAnalysis,
            squireTab.RequestPolicyRefresh,
            AgentReviewRegistry);

        acquisitionWorkspace.RestoreClaimIntoBuilder();
        acquisitionWorkspace.RestoreFinalizedDryRunPlan(
            acquisitionRequestBuilder.CurrentDocument,
            outfitterRouteStateStore);
    }

    public WorkshopProjectBrowserWindow ProjectBrowser { get; }
    public WorkshopFrozenQueueBrowserWindow FrozenQueueBrowser { get; }
    public MarketAcquisitionWorkbenchCompositionWindow AcquisitionCompositionWindow { get; }
    public MarketAcquisitionDiagnosticsWindow AcquisitionDiagnostics { get; }
    public AutomationDiagnosticsWindow AutomationDiagnostics { get; }

    public AgentBridgeTruth CreateAgentBridgeTruth()
    {
        var snapshot = routeEngine.CreateSnapshot();
        var activeOperation = snapshot.ActiveOperation;
        var activeStop = snapshot.ActiveStop;
        var persistedOutfitter = outfitterRouteStateStore.Restore();
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
            MainWindowPinned = IsPinned,
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
                ExecutionMode = snapshot.ExecutionMode.ToString(),
                ArmedOutfitterDryRunScenario = routeEngine.ArmedOutfitterDryRunScenario.ToString(),
                OutfitterDryRunFaultEligible = routeEngine.IsOutfitterDryRunFaultEligible,
                OutfitterDryRunFaultInjected = routeEngine.WasOutfitterDryRunFaultInjected,
                OutfitterPhase = snapshot.OutfitterExecution?.Phase.ToString(),
                OutfitterMessage = snapshot.OutfitterExecution?.Message,
                PersistedOutfitterSunkReceiptCount = persistedOutfitter?.SunkPurchases?.Count ?? 0,
                PersistedOutfitterSunkQuantity = persistedOutfitter?.Lines.Aggregate(
                    0ul,
                    (sum, line) => checked(sum + line.PurchasedQuantity)) ?? 0,
                PersistedOutfitterSunkGil = persistedOutfitter?.TotalSpentGil ?? 0,
                ActiveOutfitterRemainingQuantity = snapshot.OutfitterExecution?.Lines.Aggregate(
                    0ul,
                    (sum, line) => checked(sum + line.RequiredQuantity - line.PurchasedQuantity)) ?? 0,
                ActiveOutfitterRemainingGil = AgentBridgeRouteTruthProjection.ResolveActiveOutfitterRemainingGil(snapshot),
            },
            Squire = squireTab.CreateAgentBridgeTruth(),
        };
    }

    public void OnFrameworkUpdate(IFramework _framework)
    {
        squireTab.OnFrameworkUpdate();

        if (!IsMarketAcquisitionUnlocked())
            return;

        _ = acquisitionWorkspace.RenewLeaseIfDueAsync();
        if (acquisitionWorkspace.ConsumeLeaseLossSignal())
        {
            routeEngine.Stop();
            return;
        }

        routeEngine.MonitorMarketBoardPurchase();
        routeEngine.TickRoute(acquisitionWorkspace.IsBusy);
        MaybeAutoResumeOutfitterRoute();
        if (routeEngine.NeedsOutfitterRecovery && (outfitterRecoveryTask is null || outfitterRecoveryTask.IsCompleted))
            outfitterRecoveryTask = RecoverOutfitterRouteAsync();
    }

    public override void PreDraw()
    {
        var captureTarget = ActiveCapturePresentationTarget();
        if (captureTarget is null)
        {
            if (capturePresentationRestoreSize is { } restoreSize)
            {
                ImGui.SetNextWindowSize(restoreSize, ImGuiCond.Always);
                capturePresentationRestoreSize = null;
            }
            return;
        }
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowViewport(viewport.ID);
        ImGui.SetNextWindowPos(viewport.WorkPos + new Vector2(16, 16), ImGuiCond.Always);
        ImGui.SetNextWindowSize(
            captureTarget == "mmf.main-window.compact"
                ? new Vector2(980, 560)
                : viewport.WorkSize * 0.5f,
            ImGuiCond.Always);
        ImGui.SetNextWindowFocus();
    }

    private string? ActiveCapturePresentationTarget()
    {
        if (AgentCaptureTransactions.ShouldPresentInMainViewport("mmf.main-window.compact"))
            return "mmf.main-window.compact";
        return AgentCaptureTransactions.ShouldPresentInMainViewport("mmf.main-window")
            ? "mmf.main-window"
            : null;
    }

    public override void Draw()
    {
        ClearAgentReviewWindowOverride();

            if (!IsMarketAcquisitionUnlocked())
                AcquisitionCompositionWindow.IsOpen = false;

            var viewport = ImGui.GetWindowViewport();
            var windowPosition = ImGui.GetWindowPos();
            var windowSize = ImGui.GetWindowSize();
            AcquisitionCompositionWindow.AnchorTo(windowPosition, windowSize);
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

                if (ImGui.BeginTabItem("Retainers", GetAgentTabFlags("Retainers")))
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
                if (agentSelectionHoldUntilUtc != default && DateTimeOffset.UtcNow >= agentSelectionHoldUntilUtc)
                {
                    agentRequestedTab = null;
                    agentRequestedWorkspaceView = null;
                    agentSelectionHoldUntilUtc = default;
                }
            }
    }

    public void BeginAgentReviewFrame() => AgentReviewRegistry.BeginFrame();

    public void EndAgentReviewFrame()
    {
        var reviewFrame = AgentReviewRegistry.EndFrame();
        var captureTarget = ActiveCapturePresentationTarget();
        if (AgentCaptureRegion != null && captureTarget is not null)
            AgentCaptureTransactions.MarkRendered(captureTarget, reviewFrame.FrameId);
    }

    public bool TrySelectAgentBridgeTab(string tabName)
    {
        var separatorIndex = tabName.IndexOf('/');
        var mainTab = separatorIndex < 0 ? tabName : tabName[..separatorIndex];
        var workspaceView = separatorIndex < 0 ? null : tabName[(separatorIndex + 1)..];
        if (string.Equals(mainTab, "Restock", StringComparison.Ordinal))
            mainTab = "Retainers";
        if (string.Equals(workspaceView, "Plan and run", StringComparison.Ordinal))
            workspaceView = "Withdrawal plan";
        var allowed = mainTab switch
        {
            "Squire" or "Workshop Logistics" or "Retainers" or "Settings" or "Status" => true,
            "Diagnostics" => true,
            "Market Acquisition" => IsMarketAcquisitionUnlocked(),
            _ => false,
        };
        if (!allowed || !IsAllowedWorkspaceView(mainTab, workspaceView))
            return false;

        if (string.Equals(mainTab, "Squire", StringComparison.Ordinal))
            squireTab.RefreshForBridge();

        QueueAgentTabSelection(mainTab, workspaceView);
        AgentOpenForReview();
        return true;
    }

    public bool TryOpenSyntheticAdvisorReview()
    {
#if DEBUG
        squireTab.OpenSyntheticAdvisorReview();
        QueueAgentTabSelection("Squire");
        AgentOpenForReview();
        return true;
#else
        return false;
#endif
    }

    private void QueueAgentTabSelection(string mainTab, string? workspaceView = null)
    {
        agentRequestedTab = mainTab;
        agentRequestedWorkspaceView = workspaceView;
        // ImGui commits SetSelected at the end of a tab bar. Hold the semantic request long enough for
        // both a parent and nested tab bar to render, independent of the client's current frame rate.
        agentSelectionHoldUntilUtc = DateTimeOffset.UtcNow.AddSeconds(2);
    }

    private static bool IsAllowedWorkspaceView(string mainTab, string? workspaceView) =>
        workspaceView is null || (mainTab, workspaceView) switch
        {
            ("Workshop Logistics", "Combined" or "Queue" or "Materials" or "Assembly") => true,
            ("Retainers", "Quick deposit" or "Browse stock" or "Withdrawal plan") => true,
            ("Market Acquisition", "Workbench" or "Inbox" or "Route" or "Compose" or "Working Set" or "Request" or "Plan") => true,
            _ => false,
        };

    public void AgentOpenForReview()
    {
        IsOpen = true;
        Collapsed = false;
        CollapsedCondition = ImGuiCond.Always;
        clearAgentReviewWindowOverride = true;
    }

    public void AgentCloseAfterReview()
    {
        AgentCaptureTransactions.CancelActive();
        ClearAgentReviewWindowOverride();
        IsOpen = false;
    }

    public override void OnClose()
    {
        AgentCaptureTransactions.CancelActive();
        ClearAgentReviewWindowOverride();
        IsOpen = false;
    }

    private void ClearAgentReviewWindowOverride()
    {
        if (!clearAgentReviewWindowOverride)
            return;

        Collapsed = null;
        CollapsedCondition = ImGuiCond.None;
        clearAgentReviewWindowOverride = false;
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
                ? "Utilities: Squire, Workshop Logistics, Retainers, Market Acquisition. Inventory reporting lives under Settings."
                : "Utilities: Squire, Workshop Logistics, Retainers. Inventory reporting lives under Settings.");
    }

    private void DrawWorkshopPrepTab()
    {
        UtilityWorkspaceUi.DrawModuleHeader("Workshop Logistics", WorkshopLogisticsModuleSummary);

        var projects = workshopCatalog.GetProjects();
        var availability = GetWorkshopAvailability();
        var shortageItems = availability.Count(item => item.Shortage > 0);
        var missingUnits = availability.Sum(item => item.Shortage);
        var progress = workshopAssemblyRunner.Progress;
        UtilityWorkspaceUi.DrawStatusStrip(
            "##workshopLogisticsStatus",
            [
                new("Active queue", $"{config.WorkshopPrepQueue.Count:N0} project(s); {config.WorkshopPrepQueue.Sum(item => item.Quantity):N0} build(s)", config.WorkshopPrepQueue.Count > 0 ? ColHeader : ColMuted),
                new(
                    "Materials",
                    availability.Count == 0 ? "No materials yet" : shortageItems == 0 ? "No shortages" : $"{shortageItems:N0} item(s); {missingUnits:N0} units missing",
                    availability.Count == 0 ? ColMuted : shortageItems == 0 ? ColSuccess : ColWarning),
                new("Assembly", workshopAssemblyRunner.HasActiveRun ? progress.Message : "Idle", workshopAssemblyRunner.HasActiveRun ? ColHeader : ColMuted),
            ]);
        ImGui.TextColored(GetWorkshopStatusColor(), workshopStatus);
        var splitWorkshopViews = config.SplitWorkshopQueueAndMaterials;
        if (ImGui.Checkbox("Split queue and materials into separate tabs", ref splitWorkshopViews))
            SetSplitWorkshopViews(splitWorkshopViews);
        AgentReviewRegistry.Register(
            "workshop-logistics.split-views",
            "Split queue and materials into separate tabs",
            AgentBridgeUiControlKind.Toggle,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            true,
            config.SplitWorkshopQueueAndMaterials,
            config.SplitWorkshopQueueAndMaterials ? "Split" : "Combined",
            () => SetSplitWorkshopViews(!config.SplitWorkshopQueueAndMaterials));
        ImGui.Spacing();

        if (!ImGui.BeginTabBar("##workshopLogisticsWorkspace"))
            return;

        var useSplitViews = agentRequestedWorkspaceView switch
        {
            "Combined" => false,
            "Queue" or "Materials" => true,
            _ => config.SplitWorkshopQueueAndMaterials,
        };

        if (!useSplitViews && ImGui.BeginTabItem("Queue + Materials", GetAgentWorkspaceTabFlags("Combined")))
        {
            workshopPrepQueue.Draw(projects);
            workshopPrepQueue.DrawConfirmations();
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            workshopMaterials.Draw(availability);
            ImGui.EndTabItem();
        }

        if (useSplitViews && ImGui.BeginTabItem($"Queue ({config.WorkshopPrepQueue.Count})", GetAgentWorkspaceTabFlags("Queue")))
        {
            workshopPrepQueue.Draw(projects);
            workshopPrepQueue.DrawConfirmations();
            ImGui.EndTabItem();
        }

        if (useSplitViews && ImGui.BeginTabItem($"Materials ({availability.Count})", GetAgentWorkspaceTabFlags("Materials")))
        {
            workshopMaterials.Draw(availability);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Assembly", GetAgentWorkspaceTabFlags("Assembly")))
        {
            workshopAssembly.Draw(config.WorkshopPrepQueue.Count > 0);
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void SetSplitWorkshopViews(bool split)
    {
        if (config.SplitWorkshopQueueAndMaterials == split)
            return;
        config.SplitWorkshopQueueAndMaterials = split;
        config.Save();
    }

    private void DrawRetainerRestockTab()
    {
        restockTab.Draw(agentRequestedWorkspaceView);
    }

    private void DrawMarketAcquisitionTab()
    {
        ImGui.Spacing();
        ImGui.TextColored(ColHeader, "Market Acquisition");
        ImGui.Spacing();

        if (!ImGui.BeginTabBar("##marketAcquisitionWorkspace"))
            return;

        if (ImGui.BeginTabItem($"Inbox ({acquisitionWorkspace.PendingRequests.Count})", GetAgentWorkspaceTabFlags("Inbox")))
        {
            DrawMarketAcquisitionPickupSection();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem($"Workbench ({acquisitionRequestBuilder.LineCount})", GetAgentWorkspaceTabFlags("Workbench", "Compose", "Working Set", "Request", "Plan")))
        {
            DrawMarketAcquisitionWorkbench();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Route", GetAgentWorkspaceTabFlags("Route")))
        {
            if (acquisitionWorkspace.PreparedPlan is null)
                ImGui.TextColored(ColMuted, "Finalize the Workbench before starting a route.");
            DrawMarketAcquisitionPlan();
            ImGui.Spacing();
            DrawMarketAcquisitionGuidedRoute();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private ImGuiTabItemFlags GetAgentWorkspaceTabFlags(string viewName, params string[] legacyViewNames) =>
        ShouldSelectAgentWorkspaceTab(agentRequestedWorkspaceView, viewName, legacyViewNames)
            ? ImGuiTabItemFlags.SetSelected
            : ImGuiTabItemFlags.None;

    internal static bool ShouldSelectAgentWorkspaceTab(string? requestedView, string viewName, params string[] legacyViewNames) =>
        string.Equals(requestedView, viewName, StringComparison.Ordinal) ||
        legacyViewNames.Any(legacyViewName => string.Equals(requestedView, legacyViewName, StringComparison.Ordinal));

    private void DrawMarketAcquisitionWorkbench()
    {
        var context = CreateMarketAcquisitionRequestBuilderContext();
        DrawMarketAcquisitionWorkbenchToolbar(context);
        acquisitionRequestBuilder.Draw(context, reservedFooterHeight: 54f);
        DrawMarketAcquisitionFinalizationBar(context);
    }

    private MarketAcquisitionRequestBuilderContext CreateMarketAcquisitionRequestBuilderContext()
    {
        var hasScope = TryGetAcquisitionScope(out var characterName, out var world);
        var foregroundBusy = acquisitionWorkspace.IsBusy && !acquisitionRequestBuilder.IsRefreshing;
        return new MarketAcquisitionRequestBuilderContext(
            characterName,
            world,
            hasScope,
            IsExpectedCharacterScopeGap(),
            foregroundBusy,
            IsMarketAcquisitionRouteActive(),
            acquisitionWorkspace.ClaimedRequest,
            acquisitionWorkspace.PreparedPlan,
            acquisitionWorkspace.PreparedPlanHash);
    }

    private MarketAcquisitionWorkbenchCompositionContext CreateMarketAcquisitionCompositionContext()
    {
        var hasScope = TryGetAcquisitionScope(out var characterName, out var world);
        var foregroundBusy = acquisitionWorkspace.IsBusy && !acquisitionRequestBuilder.IsRefreshing;
        return new MarketAcquisitionWorkbenchCompositionContext(
            acquisitionRequestBuilder.CurrentDocument,
            characterName,
            world,
            hasScope,
            foregroundBusy,
            IsMarketAcquisitionRouteActive());
    }

    private void DrawMarketAcquisitionWorkbenchToolbar(MarketAcquisitionRequestBuilderContext context)
    {
        const float actionWidth = 212f;
        var startX = ImGui.GetCursorPosX();
        ImGui.SetCursorPosX(startX + Math.Max(0, ImGui.GetContentRegionAvail().X - actionWidth));

        if (ImGui.Button($"Compositions ({AcquisitionCompositionWindow.Count:N0})"))
            AcquisitionCompositionWindow.IsOpen = !AcquisitionCompositionWindow.IsOpen;
        AgentReviewRegistry.Register(
            "acquisition.compositions.open",
            AcquisitionCompositionWindow.IsOpen
                ? "Close saved Market Acquisition compositions"
                : "Open saved Market Acquisition compositions",
            AgentBridgeUiControlKind.Button,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            true,
            AcquisitionCompositionWindow.IsOpen,
            AcquisitionCompositionWindow.Count.ToString(),
            () => AcquisitionCompositionWindow.IsOpen = !AcquisitionCompositionWindow.IsOpen);

        ImGui.SameLine();
        if (ImGui.Button("Recovery"))
            ImGui.OpenPopup("AcquisitionWorkbenchRecovery");
        var recoveryMinimum = ImGui.GetItemRectMin();
        var recoveryMaximum = ImGui.GetItemRectMax();
        var canMutate = !context.IsBusy && !context.IsRouteActive && !acquisitionRequestBuilder.IsSynchronizing;
        AgentReviewRegistry.Register(
            "acquisition.recovery.clear-workbench",
            "Clear the local Market Acquisition Workbench",
            AgentBridgeUiControlKind.Button,
            recoveryMinimum,
            recoveryMaximum,
            canMutate && acquisitionRequestBuilder.LineCount > 0,
            false,
            acquisitionRequestBuilder.LineCount.ToString(),
            () => acquisitionRequestBuilder.ClearWorkbench(context));
        AgentReviewRegistry.Register(
            "acquisition.recovery.clear-active-work-order",
            "Forget the stale local Market Acquisition work-order claim",
            AgentBridgeUiControlKind.Button,
            recoveryMinimum,
            recoveryMaximum,
            canMutate && acquisitionWorkspace.ClaimedRequest is not null,
            false,
            acquisitionWorkspace.ClaimedRequest?.Id,
            acquisitionWorkspace.ForgetLocalClaim);

        if (!ImGui.BeginPopup("AcquisitionWorkbenchRecovery"))
            return;

        if (ImGuiUi.MenuItem("Clear Workbench", canMutate && acquisitionRequestBuilder.LineCount > 0))
            acquisitionRequestBuilder.ClearWorkbench(context);
        if (ImGuiUi.MenuItem("Clear active work order", canMutate && acquisitionWorkspace.ClaimedRequest is not null))
            acquisitionWorkspace.ForgetLocalClaim();
        if (ImGuiUi.MenuItem(
                "Return active work order to sender",
                canMutate && acquisitionWorkspace.ClaimedRequest is { Status: "Claimed" }))
        {
            _ = ReturnActiveWorkOrderAsync();
        }

        ImGui.EndPopup();
    }

    private void DrawMarketAcquisitionFinalizationBar(MarketAcquisitionRequestBuilderContext context)
    {
        ImGui.Separator();
        var validation = acquisitionRequestBuilder.DraftValidation;
        var outfitterValidation = acquisitionRequestBuilder.OutfitterFinalizationValidation;
        var presentation = MarketAcquisitionWorkbenchFinalizationPresenter.Build(new(
            acquisitionRequestBuilder.LineCount,
            validation.IsValid && outfitterValidation.IsValid,
            validation.Errors.FirstOrDefault() ?? outfitterValidation.Error,
            context.HasCharacterScope,
            context.IsBusy,
            context.IsRouteActive,
            acquisitionRequestBuilder.IsSynchronizing,
            acquisitionRequestBuilder.SyncStatus,
            acquisitionRequestBuilder.VisibleStatus,
            acquisitionWorkspace.ClaimedRequest?.Status,
            acquisitionWorkspace.ClaimedRequest is not null,
            acquisitionWorkspace.PreparedPlan is not null,
            acquisitionWorkspace.IsPreparedPlanStale(),
            acquisitionWorkspace.Status,
            acquisitionRequestBuilder.TotalSpendCeiling,
            acquisitionRequestBuilder.TargetQuantityTotal));

        ImGui.BeginGroup();
        ImGui.TextColored(presentation.CanFinalize ? ColSuccess : ColMuted, presentation.Title);
        ImGui.TextColored(ColMuted, presentation.Detail);
        ImGui.EndGroup();

        const float buttonWidth = 148f;
        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - buttonWidth));
        if (ImGuiUi.PrimaryButton("Finalize Plan", presentation.CanFinalize))
            _ = FinalizeMarketAcquisitionPlanAsync();
        AgentReviewRegistry.Register(
            "acquisition.finalize",
            "Finalize the Market Acquisition Workbench for execution",
            AgentBridgeUiControlKind.Button,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            presentation.CanFinalize,
            false,
            acquisitionWorkspace.ClaimedRequest?.Id,
            () => _ = FinalizeMarketAcquisitionPlanAsync());
    }

    private async Task FinalizeMarketAcquisitionPlanAsync()
    {
        await acquisitionRequestBuilder.WaitForRefreshAsync().ConfigureAwait(false);
        if (!acquisitionRequestBuilder.FinalizeOutfitterAuthority())
            return;
        var claimed = acquisitionWorkspace.ClaimedRequest;
        if (claimed is { Status: "Claimed" })
        {
            await acquisitionWorkspace.AcceptAsync().ConfigureAwait(false);
            claimed = acquisitionWorkspace.ClaimedRequest;
        }

        if (claimed is null ||
            !MarketAcquisitionPlanPreparationService.CanPrepareForStatus(claimed.Status))
        {
            return;
        }

        await PrepareMarketAcquisitionPlanAsync().ConfigureAwait(false);
    }

    private async Task ReturnActiveWorkOrderAsync()
    {
        await acquisitionRequestBuilder.WaitForRefreshAsync().ConfigureAwait(false);
        await acquisitionWorkspace.RejectAsync().ConfigureAwait(false);
    }

    private void DrawMarketAcquisitionPickupSection()
    {
        var hasScope = TryGetAcquisitionScope(out var characterName, out var world);
        var visibleStatus = GetVisibleAcquisitionStatus();
        marketAcquisitionRequestPickupPanel.Draw(new MarketAcquisitionRequestPickupContext(
            false,
            IsMarketAcquisitionRouteActive(),
            acquisitionWorkspace.ClaimedRequest,
            acquisitionWorkspace.PendingRequests,
            acquisitionRequestBuilder.CurrentDocument.Lines.Select(line => line.ItemId).ToHashSet(),
            acquisitionWorkspace.IsBusy,
            !string.IsNullOrWhiteSpace(WorkshopHostApiKeyRouting.ResolveAcquisitionKey(config)),
            hasScope,
            characterName,
            world,
            IsExpectedCharacterScopeGap(),
            visibleStatus,
            GetAcquisitionStatusColor(visibleStatus)));
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
            QueueAgentTabSelection("Diagnostics");
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

    private void AddAcquisitionLinesToWorkbench(IReadOnlyList<MarketAcquisitionRequestLineDocument> lines) =>
        acquisitionRequestBuilder.StageLines(lines);

    private void ReturnAcquisitionLinesToInbox(IReadOnlyList<uint> itemIds) =>
        acquisitionRequestBuilder.ReturnLines(itemIds);

    private Task PrepareMarketAcquisitionPlanAsync()
    {
        var currentWorld = playerState.CurrentWorld.IsValid ? GetCurrentWorldName() : string.Empty;
        return acquisitionWorkspace.PreparePlanAsync(
            currentWorld,
            GetMarketAcquisitionRecentWorldTtl(),
            config.MarketAcquisitionIgnoreRecentWorldVisitsForSweep,
            acquisitionRequestBuilder.CurrentDocument);
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
            acquisitionWorkspace.IsPreparedPlanStale(),
            CanStartEvidenceRefresh());
    }

    private bool CanStartEvidenceRefresh()
    {
        var claim = acquisitionWorkspace.ClaimedRequest;
        if (claim == null || acquisitionWorkspace.IsBusy || routeEngine.IsRouteActive)
            return false;

        if (claim.WorldMode.Equals("Selected", StringComparison.OrdinalIgnoreCase))
            return true;

        return claim.WorldMode.Equals("CurrentWorldOnly", StringComparison.OrdinalIgnoreCase) &&
               playerState.CurrentWorld.IsValid;
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
                config.EnableOpportunisticWorldChecks,
                acquisitionRequestBuilder.CurrentDocument.OutfitterAuthority?.FinalizedContract,
                acquisitionRequestBuilder.CurrentDocument);
            routeEngine.ReportRouteProgress();
            return Task.CompletedTask;
        });
    }

    private Task StartEvidenceRefreshAsync()
    {
        return acquisitionWorkspace.RunWithReportableClaimAsync((claimed, _) =>
        {
            var currentWorld = playerState.CurrentWorld.IsValid ? GetCurrentWorldName() : string.Empty;
            var plan = MarketAcquisitionEvidenceRefreshPlanBuilder.Build(claimed, currentWorld, DateTimeOffset.UtcNow);
            var enableDiagnostics = config.CreateMarketAcquisitionRouteDiagnosticPackages;
            routeEngine.StartEvidenceRefresh(plan, claimed, enableDiagnostics);
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

    private Task StartPreparedRouteDryRunAsync()
    {
        return acquisitionWorkspace.RunAsync(_ =>
        {
            if (!config.EnableMarketAcquisitionDryRunTools)
                throw new InvalidOperationException("Enable Market Acquisition dry-run tools in Advanced / Testing first.");
            var claimed = acquisitionWorkspace.ClaimedRequest ??
                          throw new InvalidOperationException("Accept or restore a Workbench request before starting a dry run.");
            var plan = acquisitionWorkspace.RequirePreparedPlan("Prepare a plan before starting a dry run.");
            var result = routeEngine.Start(
                plan,
                claimed,
                enableDiagnostics: true,
                config.EnableOpportunisticWorldChecks,
                acquisitionRequestBuilder.CurrentDocument.OutfitterAuthority?.FinalizedContract,
                acquisitionRequestBuilder.CurrentDocument,
                MarketAcquisitionExecutionMode.DryRun);
            acquisitionWorkspace.SetStatus(result.Message);
            return Task.CompletedTask;
        });
    }

    private bool CanStartPreparedRouteDryRun() =>
        config.EnableMarketAcquisitionDryRunTools &&
        !acquisitionWorkspace.IsBusy &&
        !routeEngine.IsRouteActive &&
        acquisitionWorkspace.ClaimedRequest is not null &&
        acquisitionWorkspace.PreparedPlan?.Status == "Ready" &&
        !acquisitionWorkspace.IsPreparedPlanStale();

#if DEBUG
    private bool CanSeedOutfitterDryRunSunkState()
    {
        if (!config.EnableMarketAcquisitionDryRunTools || acquisitionWorkspace.IsBusy || routeEngine.IsRouteActive ||
            acquisitionWorkspace.ClaimedRequest is not { } claim || acquisitionWorkspace.PreparedPlan is not { Status: "Ready" } plan ||
            acquisitionWorkspace.IsPreparedPlanStale() ||
            acquisitionRequestBuilder.CurrentDocument.OutfitterAuthority?.FinalizedContract is not { Transfer.DryRunOnly: true } contract)
            return false;
        try
        {
            _ = OutfitterDryRunSunkStateSeeder.CreateSemanticSeed(
                contract,
                acquisitionRequestBuilder.CurrentDocument,
                claim,
                plan);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string SeedOutfitterDryRunSunkState()
    {
        if (!CanSeedOutfitterDryRunSunkState())
            return "DEBUG sunk-state seed is unavailable for the current finalized dry-run route.";
        var claim = acquisitionWorkspace.ClaimedRequest!;
        var plan = acquisitionWorkspace.PreparedPlan!;
        var document = acquisitionRequestBuilder.CurrentDocument;
        var contract = document.OutfitterAuthority!.FinalizedContract!;
        var seed = OutfitterDryRunSunkStateSeeder.CreateSemanticSeed(contract, document, claim, plan);
        var result = OutfitterDryRunSunkStateSeeder.Seed(
            outfitterRouteStateStore,
            contract,
            document,
            claim,
            plan,
            seed);
        acquisitionWorkspace.SetStatus(result.Message);
        return result.Message;
    }
#endif

    private Task RecoverOutfitterRouteAsync()
    {
        return acquisitionWorkspace.RunWithReportableClaimAsync(async (claimed, token) =>
        {
            if (routeEngine.ConsumeNoViableOutfitterDryRunScenario())
            {
                routeEngine.PauseOutfitterRecovery(
                    "Diagnostic no-viable recovery: no exact-quality row remains inside the confirmed caps. Retry or return to Advisor.");
                routeEngine.ReportRouteProgress();
                return;
            }
            var remainingClaim = routeEngine.CreateOutfitterRecoveryClaim(claimed);
            var currentWorld = playerState.CurrentWorld.IsValid ? GetCurrentWorldName() : string.Empty;
            MarketAcquisitionPlanPreparationResult result;
            try
            {
                result = await acquisitionWorkspace.PrepareRecoveryPlanAsync(
                    remainingClaim,
                    currentWorld,
                    GetMarketAcquisitionRecentWorldTtl(),
                    token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                routeEngine.PauseOutfitterRecovery(
                    $"Recovery preparation failed safely: {exception.Message} Retry when market evidence is available or return to Advisor.");
                routeEngine.ReportRouteProgress();
                return;
            }
            if (result.Plan.Status != "Ready" || result.Plan.WorldBatches.Count == 0)
            {
                routeEngine.PauseOutfitterRecovery(
                    "No viable exact-quality route remains inside the confirmed caps. Wait for listings or return to Advisor.");
                return;
            }

            var start = routeEngine.StartOutfitterRecovery(
                result.Plan,
                remainingClaim,
                acquisitionRequestBuilder.CurrentDocument);
            if (start.Success)
                acquisitionWorkspace.ReplacePreparedPlan(result.Plan);
            else
                acquisitionWorkspace.SetStatus(start.Message);
            routeEngine.ReportRouteProgress();
        });
    }

    public MinerBotanistAdvisorSessionState CreateAgentAdvisorState() => squireTab.AdvisorState;

    public AgentBridgeInventoryStructSnapshot CreateAgentInventoryStructSnapshot()
    {
        var snapshot = squireSnapshotSource.Capture();
        var items = snapshot.Instances
            .Select(instance => new AgentBridgeInventoryStructItem(
                instance.Fingerprint.Container,
                instance.Fingerprint.SlotIndex,
                instance.Fingerprint.ItemId,
                instance.Fingerprint.IsHighQuality,
                instance.Fingerprint.Quantity,
                instance.IsEquipped,
                instance.Fingerprint.MateriaIds.ToArray()))
            .ToArray();
        var diagnostics = snapshot.Diagnostics.Components
            .Select(value => $"{value.Component}:{value.Status}{(value.Message is null ? string.Empty : $" ({value.Message})")}")
            .ToArray();
        return new(
            snapshot.Identity.Scope?.Name ?? "Unknown",
            snapshot.Identity.Scope?.HomeWorldId ?? 0,
            snapshot.Identity.CapturedAt,
            items,
            diagnostics,
            "Bounded direct container evidence. Rendered differential tooling audits item and quality decoding after game or Dalamud changes.");
    }

    public uint? ResolveRenderedItemName(string name, RenderedItemDetailObservation observation)
    {
        var matches = renderedDefinitionLookup.FindByExactName(name);
        if (matches.Count == 1)
            return matches[0].ItemId;
        if (matches.Count == 0)
        {
            // Soul crystals and other non-equipment items have no equipment definition;
            // the differential still needs their raw sheet identity.
            var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            var raw = sheet?.Where(value => value.RowId > 0 && string.Equals(value.Name.ToString(), name, StringComparison.Ordinal)).ToArray() ?? [];
            return raw.Length == 1 ? raw[0].RowId : null;
        }
        var narrowed = matches
            .Where(value =>
                (observation.ItemLevel is not { } itemLevel || value.ItemLevel == itemLevel) &&
                (observation.EquipLevel is not { } equipLevel || value.EquipLevel == equipLevel))
            .ToArray();
        return narrowed.Length == 1 ? narrowed[0].ItemId : null;
    }

    public uint? ResolveRenderedBagItemId(string name, uint? structItemIdHint)
    {
        var items = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        if (items is null)
            return null;
        var matches = items
            .Where(value => value.RowId > 0 && string.Equals(value.Name.ToString(), name, StringComparison.Ordinal))
            .Select(value => value.RowId)
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => null,
            _ => structItemIdHint is { } hint && matches.Contains(hint) ? hint : null,
        };
    }

    public void InvalidateAdvisorForPlayerStateChange() => squireTab.InvalidateAdvisorForPlayerStateChange();

    private void ReturnToOutfitterAdvisor()
    {
        squireTab.OpenOutfitterAdvisor();
        QueueAgentTabSelection("Squire");
    }

    private void MaybeAutoResumeOutfitterRoute()
    {
        var routeActive = routeEngine.IsRouteActive;
        var persisted = outfitterRouteStateStore.Restore();
        var contract = acquisitionRequestBuilder.CurrentDocument.OutfitterAuthority?.FinalizedContract;
        if (OutfitterRouteRecoveryLifecycle.ClearOrphanedState(
                routeActive,
                persisted,
                contract,
                outfitterRouteStateStore))
            return;
        if (acquisitionWorkspace.IsBusy || DateTimeOffset.UtcNow < nextOutfitterAutoResumeAtUtc ||
            outfitterAutoResumeTask is { IsCompleted: false })
            return;
        var live = routeEngine.CreateSnapshot();
        if (live.OutfitterExecution?.Phase == OutfitterRouteAuthorityPhase.RecoveryNeeded &&
            !live.IsRunning && !live.IsPaused)
        {
            nextOutfitterAutoResumeAtUtc = DateTimeOffset.UtcNow.AddSeconds(30);
            outfitterAutoResumeTask = RecoverOutfitterRouteAsync();
            return;
        }
        if (routeActive)
            return;
        if (persisted is null || !OutfitterRouteRecoveryLifecycle.CanAutoResume(persisted))
            return;
        if (contract is null)
            return;
        if (contract.Transfer.DryRunOnly)
            return;
        if (acquisitionWorkspace.ClaimedRequest is null)
            return;
        nextOutfitterAutoResumeAtUtc = DateTimeOffset.UtcNow.AddSeconds(30);
        outfitterAutoResumeTask = AutoResumeOutfitterRouteAsync(persisted, contract);
    }

    private Task AutoResumeOutfitterRouteAsync(
        OutfitterRouteExecutionState persisted,
        OutfitterExecutionContract contract)
    {
        return acquisitionWorkspace.RunWithReportableClaimAsync(async (claimed, token) =>
        {
            var remainingClaim = OutfitterRouteAuthoritySession.CreateRecoveryClaim(claimed, persisted);
            if (remainingClaim.Lines.Count == 0)
            {
                outfitterRouteStateStore.Save(persisted with
                {
                    Phase = OutfitterRouteAuthorityPhase.Complete,
                    Message = "Persisted Squire purchases already satisfy the finalized solution.",
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                });
                return;
            }
            var currentWorld = playerState.CurrentWorld.IsValid ? GetCurrentWorldName() : string.Empty;
            MarketAcquisitionPlanPreparationResult result;
            try
            {
                result = await acquisitionWorkspace.PrepareRecoveryPlanAsync(
                    remainingClaim,
                    currentWorld,
                    GetMarketAcquisitionRecentWorldTtl(),
                    token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                outfitterRouteStateStore.Save(OutfitterRouteRecoveryLifecycle.PauseUnavailable(
                    persisted,
                    $"Recovery preparation failed safely: {exception.Message} Retry when market evidence is available or return to Advisor."));
                return;
            }
            if (result.Plan.Status != "Ready" || result.Plan.WorldBatches.Count == 0)
            {
                outfitterRouteStateStore.Save(OutfitterRouteRecoveryLifecycle.PauseUnavailable(
                    persisted,
                    "No viable exact-quality route remains inside the confirmed caps. Wait for listings, retry recovery, or return to Advisor."));
                return;
            }
            var start = routeEngine.Start(
                result.Plan,
                remainingClaim,
                enableDiagnostics: false,
                includeOpportunisticChecks: false,
                contract,
                acquisitionRequestBuilder.CurrentDocument);
            if (start.Success)
                acquisitionWorkspace.ReplacePreparedPlan(result.Plan);
            else
                acquisitionWorkspace.SetStatus(start.Message);
            routeEngine.ReportRouteProgress();
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
