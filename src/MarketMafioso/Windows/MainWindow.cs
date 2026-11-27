using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Threading.Tasks;
using MarketMafioso.Contracts.MarketIntelligence;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using MarketMafioso.AgentBridge;
using MarketMafioso.Automation.Travel;
using MarketMafioso.CraftArchitectCompanion;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.MarketAcquisition.MarketBoard;
using MarketMafioso.Quartermaster;
using MarketMafioso.Windows.Main;
using MarketMafioso.Windows.MarketAcquisitionPanels;
using MarketMafioso.Windows.MarketAcquisitionRequestBuilder;
using MarketMafioso.Windows.WorkshopLogistics;
using MarketMafioso.Windows.TradeQueue;
using MarketMafioso.MarketAcquisition.ExactAuthority;
using MarketMafioso.TradeQueue;
using MarketMafioso.WorkshopPrep;
using MarketMafioso.Diagnostics;
using MarketMafioso.MarketDiagnostics;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Automation.Retainers;
using Franthropy.Dalamud.Automation.MarketBoard;
using Franthropy.Dalamud.Automation.Vendors;
using Franthropy.Dalamud.Automation.Vendors.Coordination;
using Franthropy.Dalamud.Travel;
using Franthropy.Dalamud.UI.Windows;
using MarketMafioso.Automation.Runtime;
using MarketMafioso.Automation.MarketBoard;
using MarketMafiosoCaptureRegion = MarketMafioso.AgentBridge.AgentBridgeCaptureRegion;

namespace MarketMafioso.Windows;

public partial class MainWindow : Window, IDisposable
{
    private readonly Configuration config;
    private readonly HttpReporter reporter;
    private readonly InventoryScanner scanner;
    private readonly FranthropyRetainerReportSource retainerReports;
    private readonly QuartermasterIpcClient quartermaster;
    private readonly WorkshopProjectCatalog workshopCatalog;
    private readonly VIWIWorkshoppaIpc viwiWorkshoppaIpc;
    private readonly WorkshopAssemblyRunner workshopAssemblyRunner;
    private readonly IPlayerState playerState;
    private readonly IPluginLog log;
    private readonly MarketBoardAcquisitionController marketBoardAcquisition;
    private readonly RemoteSummoningBellProbe remoteSummoningBellProbe;

    public MarketListingOverlayWindow MarketListingOverlay { get; }
    private readonly IDataManager dataManager;
    private readonly MarketAcquisitionServices acquisitionServices;
    private readonly MarketIntelligencePassiveReporter marketIntelligencePassiveReporter;
    private readonly HttpClient craftQuoteHttpClient = new();
    private readonly MarketAcquisitionRequestWorkspace acquisitionWorkspace;
    private readonly MarketBoardApproachService marketBoardApproachService;
    private readonly MarketAcquisitionRouteEngine routeEngine;
    private readonly ConfigurationExactAcquisitionRouteExecutionStateStore exactAcquisitionRouteStateStore;
    private readonly string marketAcquisitionRouteDiagnosticsDirectory;
    private readonly StatusTabPanel statusTab;
    private readonly SettingsTabPanel settingsTab;
    private readonly MarketAcquisitionPlanPanel marketAcquisitionPlanPanel;
    private readonly MarketAcquisitionRequestPickupPanel marketAcquisitionRequestPickupPanel;
    private readonly MarketAcquisitionDiagnosticsPanel marketAcquisitionDiagnosticsPanel;
    private readonly UiStateCaptureService uiStateCapture;
    private readonly MarketAcquisitionGuidedRoutePanel marketAcquisitionGuidedRoutePanel;
    private readonly MarketAcquisitionRequestBuilderPanel acquisitionRequestBuilder;
    private readonly DepositionPanel acquisitionDeposition;
    private readonly Action openDepositionTab;
    // Set by the Acquisition tab's Placement button; selects the Deposition tab on that same frame.
    private bool depositionTabRequested;
    private readonly WorkshopPrepQueuePanel workshopPrepQueue;
    private readonly WorkshopMaterialPanel workshopMaterials;
    private readonly WorkshopAssemblyPanel workshopAssembly;

    private readonly WorkshopVendorRestockRunner workshopVendorRestockRunner;
    private readonly TradeQueuePanel tradeQueuePanel;
    private readonly TradeQueueRunner tradeQueueRunner;
    private readonly ITradeQueueIo tradeQueueIo;
    public AgentBridgeUiReviewRegistry AgentReviewRegistry { get; } = new();

    private readonly WorkshopProjectSelectionState workshopProjectSelection = new();
    private int marketInputCaptureIndex;
    private string workshopStatus = "Workshop prep queue is idle.";
    private string? agentRequestedTab;
    private string? agentRequestedWorkspaceView;
    private DateTimeOffset agentSelectionHoldUntilUtc;
    private bool clearAgentReviewWindowOverride;
    private int captureCollapseRestoreFramesRemaining;
    private Vector2? capturePresentationPreviousSize;
    private Vector2? capturePresentationRestoreSize;
    private WindowPlacementObservation? lastWindowPlacement;

    public MarketMafiosoCaptureRegion? AgentCaptureRegion { get; private set; }
    public AgentBridgeUiCaptureTransactionManager AgentCaptureTransactions { get; }

    private const string ProductSummary = "Workshop logistics, direct trade handoff, and self-hosted inventory history.";
    private const string WorkshopLogisticsModuleSummary = "Workshop Logistics tracks company workshop jobs, material shortages, Quartermaster requests, handoff, and assembly.";

    internal static readonly Vector4 ColHeader = MarketMafiosoUiTheme.Header;
    internal static readonly Vector4 ColSuccess = MarketMafiosoUiTheme.Success;
    internal static readonly Vector4 ColError = MarketMafiosoUiTheme.Error;
    internal static readonly Vector4 ColWarning = MarketMafiosoUiTheme.Warning;
    internal static readonly Vector4 ColMuted = MarketMafiosoUiTheme.Muted;

    internal MainWindow(
        Configuration config,
        HttpReporter reporter,
        InventoryScanner scanner,
        FranthropyRetainerReportSource retainerReports,
        QuartermasterIpcClient quartermaster,
        WorkshopProjectCatalog workshopCatalog,
        VIWIWorkshoppaIpc viwiWorkshoppaIpc,
        WorkshopAssemblyRunner workshopAssemblyRunner,
        WorkshopMaterialManifestExportService workshopMaterialManifestExport,
        IWorkshopMaterialCraftRecipeResolver workshopCraftRecipeResolver,
        TradeQueueRunner tradeQueueRunner,
        WorkshopHandoverTracker handoverTracker,
        ITradeQueueIo tradeQueueIo,
        IDataManager dataManager,
        IPlayerState playerState,
        MarketAcquisitionServices acquisitionServices,
        IRetainerListingRefreshControls retainerListingRefreshControls,
        IPluginLog log,
        WorkshopRestockService restock,
        RemoteSummoningBellProbe remoteSummoningBellProbe)
        : base("MarketMafioso##MarketMafiosoMainWindow",
               ImGuiWindowFlags.None)
    {
        AllowClickthrough = false;
        this.config = config;
        marketAcquisitionPlanPanel = new MarketAcquisitionPlanPanel(config);
        this.reporter = reporter;
        this.scanner = scanner;
        this.retainerReports = retainerReports;
        this.quartermaster = quartermaster;
        this.workshopCatalog = workshopCatalog;
        this.viwiWorkshoppaIpc = viwiWorkshoppaIpc;
        this.workshopAssemblyRunner = workshopAssemblyRunner;
        this.tradeQueueRunner = tradeQueueRunner;
        this.tradeQueueIo = tradeQueueIo;
        this.playerState = playerState;
        this.log = log;
        AgentCaptureTransactions = new AgentBridgeUiCaptureTransactionManager(
            () => IsOpen,
            value => IsOpen = value,
            () => Collapsed == true,
            value =>
            {
                captureCollapseRestoreFramesRemaining = 0;
                Collapsed = value;
                CollapsedCondition = ImGuiCond.Always;
            },
            RestoreCaptureCollapseState,
            beginPresentation: () =>
            {
                capturePresentationPreviousSize = AgentCaptureRegion?.WindowSize;
            },
            restorePresentation: () =>
            {
                capturePresentationRestoreSize = capturePresentationPreviousSize;
                capturePresentationPreviousSize = null;
            });
        this.acquisitionServices = acquisitionServices;
        marketIntelligencePassiveReporter = acquisitionServices.IntelligenceReporter;
        acquisitionWorkspace = acquisitionServices.Workspace;
        marketBoardAcquisition = acquisitionServices.Controller;
        exactAcquisitionRouteStateStore = acquisitionServices.ExactStateStore;
        routeEngine = acquisitionServices.RouteEngine;
        marketBoardApproachService = acquisitionServices.ApproachService;
        marketAcquisitionRouteDiagnosticsDirectory = acquisitionServices.DiagnosticsDirectory;
        this.remoteSummoningBellProbe = remoteSummoningBellProbe;
        MarketListingOverlay = new MarketListingOverlayWindow(marketBoardAcquisition, AgentReviewRegistry);

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(980, 560),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        if (WorkshopHostApiKeyRouting.NormalizeConfiguredKeys(config))
            config.Save();

        this.dataManager = dataManager;
        uiStateCapture = new UiStateCaptureService(
            Plugin.AddonLifecycle,
            Plugin.Framework,
            Plugin.Condition,
            Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "ui-state-captures"));
        statusTab = new StatusTabPanel(config, reporter);
        marketAcquisitionRequestPickupPanel = new MarketAcquisitionRequestPickupPanel(
            () => _ = FetchDashboardRequestsAsync(),
            requestId =>
            {
                QueueAgentTabSelection("Market Acquisition", "Acquisition");
                _ = ClaimAcquisitionRequestAsync(requestId);
            },
            AddAcquisitionLinesToWorkbench,
            ReturnAcquisitionLinesToInbox,
            () => QueueAgentTabSelection("Market Acquisition", "Acquisition"),
            AgentReviewRegistry);
        workshopVendorRestockRunner = restock.Runner;
        var vendorPlanner = new WorkshopVendorProcurementPlanner(
            DalamudGilVendorCatalogBuilder.Build(dataManager),
            restock.Access.Assess,
            itemId => workshopCraftRecipeResolver.TryResolveCraftRecipe(itemId, out _));
        tradeQueuePanel = new TradeQueuePanel(
            config,
            tradeQueueRunner,
            tradeQueueIo,
            AgentReviewRegistry,
            acquisitionServices.GetCurrentWorkshopAssembler);
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
            () => projectBrowser!.OpenAndFocus(),
            () => frozenQueueBrowser!.OpenAndFocus(),
            () => tradeQueuePanel.HasItems,
            tradeQueuePanel.ReplaceWithWorkshopMaterials,
            log);
        workshopMaterials = new WorkshopMaterialPanel(
            config,
            quartermaster,
            vendorPlanner,
            workshopVendorRestockRunner,
            GetWorkshopAvailability,
            acquisitionServices.GetCurrentQuartermasterOwnerScope,
            CanStageWorkshopMarketAcquisition,
            StageWorkshopMarketAcquisition,
            AgentReviewRegistry);
        workshopAssembly = new WorkshopAssemblyPanel(
            workshopAssemblyRunner,
            () => workshopStatus,
            status => workshopStatus = status,
            StartWorkshopAssembly,
            InspectWorkshopCraftingLog,
            () => config.WorkshopAssemblyOrder,
            order =>
            {
                config.WorkshopAssemblyOrder = order;
                config.Save();
            },
            AgentReviewRegistry);
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
                workshopPrepQueue.DeleteFrozenQueues,
                workshopPrepQueue.RestoreFrozenQueues,
                workshopPrepQueue.SaveCurrentQueueAsNew,
                workshopPrepQueue.ImportSavedJob,
                acquisitionServices.GetCurrentWorkshopAssembler,
                () => Plugin.Instance.WorkshopHandoffService,
                () => tradeQueuePanel.HasItems,
                handoverTracker,
                ObserveTradeableBags,
                tradeQueuePanel.TryHandOver,
                () => tradeQueuePanel.BoundShareId,
                ShowTradeQueue,
                GetWorkshopAvailability,
                new WorkshopShareRestockActions(
                    workshopVendorRestockRunner,
                    vendorPlanner,
                    acquisitionServices.GetCurrentQuartermasterOwnerScope,
                    scanner.CountWorkshopUsableInventory,
                    GetWorkshopAvailability,
                    CanStageWorkshopMarketAcquisition,
                    StageWorkshopMarketAcquisition,
                    message => log.Warning(message)),
                message => log.Information(message)),
            AgentReviewRegistry);
        frozenQueueBrowser = FrozenQueueBrowser;
        var acquisitionRequestBuilderCraftAppraisal = CreateAcquisitionRequestBuilderCraftAppraisalController();
        acquisitionRequestBuilder = new MarketAcquisitionRequestBuilderPanel(
            config,
            dataManager,
            acquisitionRequestBuilderCraftAppraisal,
            SyncAcquisitionRequestBuilderAsync,
            RefreshAcquisitionRequestBuilderRemoteAsync,
            acquisitionWorkspace.OnDocumentAdopted,
            AgentReviewRegistry,
            acquisitionServices.WorkbenchStock)
        {
            DrawRouteScopeExtras = DrawCrossRegionWorkbenchSection,
        };
        acquisitionDeposition = new DepositionPanel(config, dataManager, acquisitionRequestBuilder, acquisitionServices.Placement, AgentReviewRegistry);
        openDepositionTab = () =>
        {
            acquisitionDeposition.ShowPlan();
            depositionTabRequested = true;
        };
        acquisitionCompositionCatalog = new MarketAcquisitionWorkbenchCompositionCatalog(
            new ConfigurationMarketAcquisitionWorkbenchCompositionStore(config, config.Save));
        var acquisitionWorkbenchCompositions = new MarketAcquisitionWorkbenchCompositionPanel(
            acquisitionCompositionCatalog,
            (composition, characterName, world) =>
                _ = ReplaceWorkbenchFromCompositionAsync(composition, characterName, world),
            composition => acquisitionRequestBuilder.MergeComposition(composition),
            AgentReviewRegistry);
        AcquisitionCompositionWindow = new MarketAcquisitionWorkbenchCompositionWindow(
            acquisitionWorkbenchCompositions,
            CreateMarketAcquisitionCompositionContext);
        acquisitionServices.ConnectCurrentDocument(() => acquisitionRequestBuilder.CurrentDocument);
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
            new AutomationDiagnosticProbeFactory(quartermaster, viwiWorkshoppaIpc).Create(),
            () => true);
        marketAcquisitionDiagnosticsPanel = new MarketAcquisitionDiagnosticsPanel(
            routeEngine.CreateSnapshot,
            marketAcquisitionRouteDiagnosticsDirectory,
            log,
            AcquisitionDiagnostics.Draw,
            AutomationDiagnostics.Draw,
            () => { },
            acquisitionServices.IsMarketAcquisitionUnlocked,
            uiStateCapture,
            AgentReviewRegistry,
            () => acquisitionRequestBuilderCraftAppraisal.State.CreateDiagnosticsSnapshot(),
            () => config.EnableMarketAcquisitionDryRunTools,
            CanStartPreparedRouteDryRun,
            () => _ = StartPreparedRouteDryRunAsync(),
            () => routeEngine.ArmedExactAcquisitionDryRunScenario,
            scenario => routeEngine.ArmExactAcquisitionDryRunScenario(scenario)
#if DEBUG
            , CanSeedExactAcquisitionDryRunSunkState,
            SeedExactAcquisitionDryRunSunkState
#endif
            );
        marketAcquisitionGuidedRoutePanel = new MarketAcquisitionGuidedRoutePanel(
            routeEngine.CreateSnapshot,
            () => _ = StartGuidedRouteAsync(),
            CreateEvidenceRefreshConfirmation,
            CanProbeLiveMarketBoard,
            () => _ = ProbeLiveMarketBoardAsync(),
            () => _ = PauseGuidedRouteAsync(),
            () => _ = ResumeGuidedRouteAsync(),
            () => _ = RecoverGuidedRouteAsync(),
            () => _ = StopGuidedRouteAsync(),
            () => _ = RestartGuidedRouteAsync(),
            () => _ = ReprepareGuidedRouteAsync(),
            purchaseOccurred => routeEngine.ReconcileTerminalPurchaseEvidence(
                purchaseOccurred,
                purchaseOccurred
                    ? "The user confirmed that the submitted purchase completed."
                    : "The user confirmed that the submitted purchase did not complete."),
            () => routeEngine.RequestExactAcquisitionRecovery(acquisitionRequestBuilder.CurrentDocument),
            marketAcquisitionDiagnosticsPanel.DrawPostRunDiagnosticSummary,
            marketAcquisitionDiagnosticsPanel.DrawLatestWorldCompletionSummary,
            DrawMarketBoardProbeStatus,
            marketAcquisitionDiagnosticsPanel.OpenDiagnosticsFolder,
            AgentReviewRegistry,
            () => _ = routeEngine.ForgetShardLedger(),
            () => _ = routeEngine.DiscardManualRecoveryBlock(),
            () => _ = routeEngine.ForgetTravelLease());
        settingsTab = new SettingsTabPanel(
            config,
            reporter,
            log,
            () => _ = routeEngine.Stop(),
            retainerListingRefreshControls,
            Plugin.Instance.AcquisitionHostCheck,
            Plugin.Instance.RestartTimer,
            AgentReviewRegistry,
            marketIntelligencePassiveReporter.CreateStatus);

        acquisitionWorkspace.RestoreClaimIntoBuilder();
        acquisitionWorkspace.RestoreFinalizedDryRunPlan(
            acquisitionRequestBuilder.CurrentDocument,
            exactAcquisitionRouteStateStore);
    }

    public WorkshopProjectBrowserWindow ProjectBrowser { get; }
    public WorkshopFrozenQueueBrowserWindow FrozenQueueBrowser { get; }
    public MarketAcquisitionWorkbenchCompositionWindow AcquisitionCompositionWindow { get; }

    private readonly MarketAcquisitionWorkbenchCompositionCatalog acquisitionCompositionCatalog;
    public MarketAcquisitionDiagnosticsWindow AcquisitionDiagnostics { get; }
    public AutomationDiagnosticsWindow AutomationDiagnostics { get; }

    public event Action<MarketAcquisitionRouteEngineSnapshot> AcquisitionFeedbackTransition
    {
        add => routeEngine.FeedbackTransition += value;
        remove => routeEngine.FeedbackTransition -= value;
    }

    public AgentBridgeTruth CreateAgentBridgeTruth()
    {
        var snapshot = routeEngine.CreateSnapshot();
        var activeOperation = snapshot.ActiveOperation;
        var activeStop = snapshot.ActiveStop;
        var persistedExactAcquisition = exactAcquisitionRouteStateStore.Restore();
        var remoteBellProbe = remoteSummoningBellProbe.GetView();
        var normalBellCapture = remoteSummoningBellProbe.GetNormalCaptureView();
        var positionFrameOneShot = remoteSummoningBellProbe.GetPositionFrameOneShotView();
        var yieldBellProbe = remoteSummoningBellProbe.GetYieldProbeView();
        var warmBellProbe = remoteSummoningBellProbe.GetWarmSessionProbeView();
        var workshopReview = workshopMaterials.BuildReview();
        var workshopRun = workshopVendorRestockRunner.ActiveRun;
        var listingView = marketBoardAcquisition.GetView();
        var nativeListingPresentation = marketBoardAcquisition.GetNativePresentationState();
        var tradeExecution = tradeQueueRunner.Snapshot;
        var liveTradeOffer = tradeQueueIo.ObserveOffer();
        var tradeCapability = tradeQueueRunner.InspectExecutionCapability();
        var tradeInventory = tradeQueueIo.ObserveTradeableInventory();
        var tradeValidation = tradeInventory.IsAuthoritative
            ? TradeQueuePlanner.Validate(config.TradeQueueItems, tradeInventory.Stacks)
            : new TradeQueueValidationResult(
                false,
                TradeQueueValidationCode.InsufficientInventory,
                "Current tradeable inventory evidence is not ready.");
        var selectedTradePartner = tradeQueueIo.TryGetSelectedPartner(out var selectedPartner)
            ? CreateTradePartnerTruth(selectedPartner)
            : null;
        var availableTradePartners = tradeQueueIo.GetAvailablePartners()
            .Select(CreateTradePartnerTruth)
            .ToArray();
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
            MainWindowCollapseOverrideActive = CollapsedCondition != ImGuiCond.None,
            MainWindowPinned = IsPinned,
            AcquisitionDiagnosticsOpen = AcquisitionDiagnostics.IsOpen,
            WorkspaceStatus = acquisitionWorkspace.Status,
            WorkspaceBusy = acquisitionWorkspace.IsBusy,
            ClaimedRequestId = acquisitionWorkspace.ClaimedRequest?.Id,
            PreparedPlanStatus = acquisitionWorkspace.PreparedPlan?.Status,
            MarketIntelligenceReporting = marketIntelligencePassiveReporter.CreateStatus(),
            CraftAppraisal = acquisitionRequestBuilder.CreateAgentBridgeCraftAppraisalTruth(),
            WorkshopRestock = new AgentBridgeWorkshopRestockTruth
            {
                QueueSignature = workshopReview.QueueSignature,
                AutomaticallyBuyVendorMaterials = config.AutomaticallyBuyWorkshopVendorMaterials,
                MaterialCount = workshopReview.Materials.Count,
                ShortageLineCount = workshopReview.Materials.Count(line => line.Availability.Shortage > 0),
                OrdinaryGilCatalogLineCount = workshopReview.Materials.Count(line => line.Candidates.Count > 0),
                AccessibleVendorLineCount = workshopReview.Materials.Count(line => line.CanBuyAutomatically),
                SelectedVendorLineCount = workshopReview.Materials.Count(line => line.Selected && line.ApprovedVendorQuantity > 0),
                RetainerUnits = workshopReview.RetainerUnits,
                VendorUnits = workshopReview.VendorUnits,
                MaximumGil = workshopReview.MaximumGil,
                StopCount = workshopReview.Stops.Count,
                VendorLines = workshopReview.Materials
                    .Where(line => line.SelectedCandidate is not null)
                    .Select(line => new AgentBridgeWorkshopVendorLineTruth
                    {
                        ItemId = line.Availability.ItemId,
                        ItemName = line.Availability.ItemName,
                        VendorNeed = line.VendorNeed,
                        Selected = line.Selected,
                        ApprovedQuantity = line.ApprovedVendorQuantity,
                        UnitPriceGil = line.SelectedCandidate!.Offer.UnitPriceGil,
                        NpcId = line.SelectedCandidate.Offer.NpcId,
                        NpcName = line.SelectedCandidate.Offer.NpcName,
                        AccessState = line.SelectedCandidate.Access.State.ToString(),
                    })
                    .ToArray(),
                ActivePhase = workshopRun?.Phase.ToString(),
                ActiveMessage = workshopRun is null
                    ? null
                    : WorkshopVendorRestockPresentation.Describe(workshopRun, workshopReview),
                VerifiedReceiptCount = workshopRun?.Receipts.Count ?? 0,
                VerifiedQuantity = workshopRun?.Receipts.Sum(receipt => receipt.Quantity) ?? 0,
                VerifiedGil = workshopRun?.Receipts.Aggregate(
                    0UL,
                    (sum, receipt) => checked(sum + receipt.SpentGil)) ?? 0,
                ArmedItemId = workshopRun?.ArmedPurchase?.ItemId,
            },
            WorkshopAssembly = new AgentBridgeWorkshopAssemblyTruth
            {
                State = workshopAssemblyRunner.Progress.State.ToString(),
                Message = workshopAssemblyRunner.Progress.Message,
                HasActiveRun = workshopAssemblyRunner.HasActiveRun,
                IsRunning = workshopAssemblyRunner.IsRunning,
                IsPaused = workshopAssemblyRunner.IsPaused,
                ActiveProjectName = workshopAssemblyRunner.Progress.ActiveProjectName,
                ActiveWorkshopItemId = workshopAssemblyRunner.Progress.ActiveWorkshopItemId,
                ActiveMaterialItemId = workshopAssemblyRunner.Progress.ActiveMaterialItemId,
                CompletedProjects = workshopAssemblyRunner.Progress.CompletedProjects,
                TotalProjects = workshopAssemblyRunner.Progress.TotalProjects,
                UpdatedAt = workshopAssemblyRunner.Progress.UpdatedAt,
                DiagnosticFilePath = workshopAssemblyRunner.LastDiagnosticFilePath,
            },
            TradeQueue = new AgentBridgeTradeQueueTruth
            {
                ExecutionCapabilityAvailable = tradeCapability.Available,
                ExecutionCapabilityMessage = tradeCapability.Message,
                State = tradeExecution.State.ToString(),
                Message = tradeQueueRunner.HoldReason ?? tradeExecution.Message,
                RunId = tradeExecution.RunId,
                IsActive = tradeExecution.IsActive,
                CanResume = tradeQueueRunner.HasResumeCheckpoint,
                PartnerName = tradeExecution.PartnerName,
                BatchNumber = tradeExecution.BatchNumber,
                CompletedBatchCount = tradeExecution.CompletedBatchCount,
                InitialUnitCount = tradeExecution.InitialUnitCount,
                CompletedUnitCount = tradeExecution.CompletedUnitCount,
                RemainingLineCount = tradeExecution.RemainingItemCount,
                RemainingUnitCount = tradeExecution.RemainingUnitCount,
                QueueValid = tradeValidation.Success,
                QueueValidationMessage = tradeValidation.Message,
                ActionDelayMilliseconds = config.TradeQueueTiming.ActionDelayMilliseconds,
                TradeRetryMilliseconds = config.TradeQueueTiming.TradeRetryMilliseconds,
                AutoAcceptIncomingTrades = config.AutoAcceptIncomingTrades,
                IsTradeOpen = tradeQueueIo.IsTradeOpen,
                OfferedSlotCount = liveTradeOffer?.Items.Count ?? 0,
                LiveOffer = liveTradeOffer,
                ReadyDiagnostic = tradeQueueIo.IsTradeOpen ? tradeQueueIo.ReadyDiagnostic : null,
                CanReceiverReady = !tradeExecution.IsActive && tradeQueueIo.IsTradeOpen && tradeQueueIo.CanClickReady,
                CanReceiverConfirm = !tradeExecution.IsActive && tradeQueueIo.IsTradeOpen && tradeQueueIo.CanConfirmTrade,
                CanReceiverCancel = !tradeExecution.IsActive && tradeQueueIo.IsTradeOpen && tradeQueueIo.CanCancelTrade,
                SelectedPartner = selectedTradePartner,
                AvailablePartners = availableTradePartners,
                Queue = config.TradeQueueItems
                    .Where(item => item.Quantity > 0)
                    .Select(item => new AgentBridgeTradeQueueLineTruth
                    {
                        ItemId = item.ItemId,
                        ItemName = item.ItemName,
                        Quantity = item.Quantity,
                    })
                    .ToArray(),
            },
            RemoteMarket = new AgentBridgeRemoteMarketTruth
            {
                Available = marketBoardAcquisition.IsAvailable,
                ResultVisible = nativeListingPresentation.MatchesSnapshot,
                NativeResultAddonVisible = nativeListingPresentation.ResultAddonVisible,
                NativeAgentActive = nativeListingPresentation.AgentActive,
                NativeListingCount = nativeListingPresentation.ListingCount,
                NativeRequestId = nativeListingPresentation.RequestId,
                ViewRevision = listingView.Revision,
                ListingCount = listingView.Listings.Count,
                ItemId = listingView.Listings.FirstOrDefault()?.ItemId,
                HighQuality = listingView.Listings.FirstOrDefault()?.IsHighQuality,
                CheapestUnitPrice = listingView.Listings.Count == 0
                    ? null
                    : listingView.Listings.Min(listing => listing.UnitPrice),
                CurrentGil = listingView.GilOnHand,
                MarketContextSource = listingView.MarketContext?.Source,
                MarketContextSummary = listingView.MarketContextSummary,
            },
            MarketActors = acquisitionServices.GetMarketActorCapabilityTruth(),
            ControlledMarketActorListing = acquisitionServices.GetControlledListingTruth(),
            ControlledMarketActorBrowse = acquisitionServices.GetControlledBrowseTruth(),
            RemoteBellProbe = new AgentBridgeRemoteBellProbeTruth
            {
                Active = remoteBellProbe.Active,
                CanSubmit = remoteBellProbe.CanSubmit,
                State = remoteBellProbe.State,
                Message = remoteBellProbe.Message,
                Readiness = remoteBellProbe.Readiness,
                BellGameObjectId = remoteBellProbe.BellGameObjectId,
                Distance = remoteBellProbe.Distance,
                OrdinaryInteractionDistance = remoteBellProbe.OrdinaryInteractionDistance,
                LastEvidencePath = remoteBellProbe.LastEvidencePath,
                NormalCaptureActive = normalBellCapture.Active,
                NormalCaptureCanArm = normalBellCapture.CanArm,
                NormalCaptureState = normalBellCapture.State,
                NormalCaptureMessage = normalBellCapture.Message,
                NormalCaptureReadiness = normalBellCapture.Readiness,
                NormalCaptureLastEvidencePath = normalBellCapture.LastEvidencePath,
                PositionFrameOneShotPrepared = positionFrameOneShot.Prepared,
                PositionFrameOneShotCanPrepare = positionFrameOneShot.CanPrepare,
                PositionFrameOneShotCanFire = positionFrameOneShot.CanFire,
                PositionFrameOneShotState = positionFrameOneShot.State,
                PositionFrameOneShotMessage = positionFrameOneShot.Message,
                PositionFrameOneShotReadiness = positionFrameOneShot.Readiness,
                PositionFrameOneShotExpiresAtUtc = positionFrameOneShot.ExpiresAtUtc,
                YieldProbeActive = yieldBellProbe.Active,
                YieldProbeCanArmControl = yieldBellProbe.CanArmControl,
                YieldProbeCanReplaySessionFree = yieldBellProbe.CanReplaySessionFree,
                YieldProbeMode = yieldBellProbe.Mode,
                YieldProbeState = yieldBellProbe.State,
                YieldProbeMessage = yieldBellProbe.Message,
                YieldProbeReadiness = yieldBellProbe.Readiness,
                YieldProbeRetainerId = yieldBellProbe.RetainerId,
                YieldProbeOpcode = yieldBellProbe.Opcode,
                YieldProbeLastEvidencePath = yieldBellProbe.LastEvidencePath,
                WarmSessionActive = warmBellProbe.Active,
                WarmSessionCanArm = warmBellProbe.CanArm,
                WarmSessionCanReplayHeld = warmBellProbe.CanReplayHeldSession,
                WarmSessionMode = warmBellProbe.Mode,
                WarmSessionState = warmBellProbe.State,
                WarmSessionMessage = warmBellProbe.Message,
                WarmSessionReadiness = warmBellProbe.Readiness,
                WarmSessionHoldSeconds = warmBellProbe.HoldSeconds,
                WarmSessionDistanceMoved = warmBellProbe.DistanceMoved,
                WarmSessionRetainerId = warmBellProbe.RetainerId,
                WarmSessionOpcode = warmBellProbe.Opcode,
                WarmSessionLastEvidencePath = warmBellProbe.LastEvidencePath,
            },
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
                CompletionKind = snapshot.CompletionOutcome?.Kind,
                TargetRequestedQuantity = snapshot.CompletionOutcome?.TargetRequestedQuantity,
                TargetPurchasedQuantity = snapshot.CompletionOutcome?.TargetPurchasedQuantity,
                TargetRemainingQuantity = snapshot.CompletionOutcome?.TargetRemainingQuantity,
                ExecutionMode = snapshot.ExecutionMode.ToString(),
                ArmedExactAcquisitionDryRunScenario = routeEngine.ArmedExactAcquisitionDryRunScenario.ToString(),
                ExactAcquisitionDryRunFaultEligible = routeEngine.IsExactAcquisitionDryRunFaultEligible,
                ExactAcquisitionDryRunFaultInjected = routeEngine.WasExactAcquisitionDryRunFaultInjected,
                ExactAcquisitionPhase = snapshot.ExactAcquisitionExecution?.Phase.ToString(),
                ExactAcquisitionMessage = snapshot.ExactAcquisitionExecution?.Message,
                PersistedExactAcquisitionSunkReceiptCount = persistedExactAcquisition?.SunkPurchases?.Count ?? 0,
                PersistedExactAcquisitionSunkQuantity = persistedExactAcquisition?.Lines.Aggregate(
                    0ul,
                    (sum, line) => checked(sum + line.PurchasedQuantity)) ?? 0,
                PersistedExactAcquisitionSunkGil = persistedExactAcquisition?.TotalSpentGil ?? 0,
                ActiveExactAcquisitionRemainingQuantity = snapshot.ExactAcquisitionExecution?.Lines.Aggregate(
                    0ul,
                    (sum, line) => checked(sum + line.RequiredQuantity - line.PurchasedQuantity)) ?? 0,
                ActiveExactAcquisitionRemainingGil = AgentBridgeRouteTruthProjection.ResolveActiveExactAcquisitionRemainingGil(snapshot),
                ReportBacklogEntryCount = snapshot.ReportBacklog.PendingEntryCount,
                ReportBacklogRequestCount = snapshot.ReportBacklog.PendingRequestCount,
                ReportBacklogInFlightRequestCount = snapshot.ReportBacklog.InFlightRequestCount,
                ReportBacklogOldestEnqueuedAtUtc = snapshot.ReportBacklog.OldestEnqueuedAtUtc,
                ReportBacklogNextRetryAtUtc = snapshot.ReportBacklog.NextRetryAtUtc,
                ReportBacklogLastFailureKind = snapshot.ReportBacklog.LastFailureKind,
                ReportBacklogLastFailureAtUtc = snapshot.ReportBacklog.LastFailureAtUtc,
                ReportQuarantinedEntryCount = snapshot.ReportBacklog.QuarantinedEntryCount,
                ReportLastQuarantineStatus = snapshot.ReportBacklog.LastQuarantineStatus,
                ReportLastQuarantineAtUtc = snapshot.ReportBacklog.LastQuarantineAtUtc,
            },
        };
    }

    /// <summary>
    /// Why market automation is doing work right now, or null when idle; a paused route is not running.
    /// </summary>
    public string? GetMarketAutomationBusyReason()
    {
        if (acquisitionWorkspace.IsBusy || routeEngine.IsRouteRunning)
            return "A market acquisition route is running.";
        if (routeEngine.IsPurchaseAutomationBusy)
            return "A market-board purchase is in progress.";
        if (marketBoardAcquisition.IsListingPurchaseActive)
            return "A market listing purchase is in progress.";
        if (routeEngine.IsShardCheckpointActive)
            return "A shard acquisition checkpoint is in progress.";
        return null;
    }

    public void OpenRetainerListingRefreshSettings()
    {
        settingsTab.ShowPage("retainer-listings.refresh");
        TrySelectAgentBridgeTab("Settings");
    }

    private static AgentBridgeTradePartnerTruth CreateTradePartnerTruth(TradeQueuePartner partner) =>
        new()
        {
            Name = partner.Name,
            HomeWorld = partner.HomeWorldName,
            GameObjectId = partner.GameObjectId.ToString("X"),
        };

    public override void PreDraw()
    {
        ReleaseRestoredCaptureCollapseOverride();

        var captureTarget = ActiveCapturePresentationTarget();
        if (captureTarget is null)
        {
            if (capturePresentationRestoreSize is { } restoreSize)
            {
                ImGui.SetNextWindowSize(restoreSize, ImGuiCond.Always);
                capturePresentationRestoreSize = null;
            }
            if (lastWindowPlacement is { IsDocked: false } placement &&
                WindowPlacementRecovery.TryRecoverTitleBar(
                    placement.Position,
                    placement.Size,
                    placement.WorkPosition,
                    placement.WorkSize,
                    ImGui.GetFrameHeight(),
                    out var recoveredPosition))
            {
                ImGui.SetNextWindowPos(recoveredPosition, ImGuiCond.Always);
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

    private void RestoreCaptureCollapseState(bool wasOpen, bool wasCollapsed)
    {
        if (!wasOpen)
        {
            captureCollapseRestoreFramesRemaining = 0;
            Collapsed = null;
            CollapsedCondition = ImGuiCond.None;
            return;
        }

        Collapsed = wasCollapsed;
        CollapsedCondition = ImGuiCond.Always;
        captureCollapseRestoreFramesRemaining = 2;
    }

    private void ReleaseRestoredCaptureCollapseOverride()
    {
        if (captureCollapseRestoreFramesRemaining <= 0)
            return;

        captureCollapseRestoreFramesRemaining--;
        if (captureCollapseRestoreFramesRemaining > 0)
            return;

        Collapsed = null;
        CollapsedCondition = ImGuiCond.None;
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

            if (!acquisitionServices.IsMarketAcquisitionUnlocked())
                AcquisitionCompositionWindow.IsOpen = false;

            var viewport = ImGui.GetWindowViewport();
            var windowPosition = ImGui.GetWindowPos();
            var windowSize = ImGui.GetWindowSize();
            lastWindowPlacement = new(
                windowPosition,
                windowSize,
                viewport.WorkPos,
                viewport.WorkSize,
                ImGui.IsWindowDocked());
            AcquisitionCompositionWindow.AnchorTo(
                windowPosition,
                windowSize,
                DalamudOwnedWindowSurface.Capture());
            if (windowSize.X > 0f && windowSize.Y > 0f && viewport.Size.X > 0f && viewport.Size.Y > 0f)
            {
                AgentCaptureRegion = new MarketMafiosoCaptureRegion(
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
                if (ImGui.BeginTabItem("Workshop Logistics", GetAgentTabFlags("Workshop Logistics")))
                {
                    DrawWorkshopPrepTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Trade Queue", GetAgentTabFlags("Trade Queue")))
                {
                    tradeQueuePanel.Draw();
                    ImGui.EndTabItem();
                }

                if (acquisitionServices.IsMarketAcquisitionUnlocked() && ImGui.BeginTabItem("Market Acquisition", GetAgentTabFlags("Market Acquisition")))
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

    private sealed record WindowPlacementObservation(
        Vector2 Position,
        Vector2 Size,
        Vector2 WorkPosition,
        Vector2 WorkSize,
        bool IsDocked);

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
        if (!TryNormalizeAgentBridgeTab(tabName, out var mainTab, out var workspaceView))
            return false;

        var allowed = mainTab switch
        {
            "Workshop Logistics" or "Trade Queue" or "Settings" or "Status" => true,
            "Diagnostics" => true,
            "Market Acquisition" => acquisitionServices.IsMarketAcquisitionUnlocked(),
            _ => false,
        };
        if (!allowed || !IsAllowedWorkspaceView(mainTab, workspaceView))
            return false;

        QueueAgentTabSelection(mainTab, workspaceView);
        AgentOpenForReview();
        return true;
    }

    public void StageExternalExactAcquisition(ExactAcquisitionWorkbenchTransfer transfer)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        if (!acquisitionServices.IsMarketAcquisitionUnlocked())
            throw new InvalidOperationException("Market Acquisition must be unlocked before an external plan can be staged.");

        acquisitionRequestBuilder.StageExactAcquisitionTransfer(transfer);
        QueueAgentTabSelection("Market Acquisition", "Acquisition");
        IsOpen = true;
    }

    /// <summary>
    /// Stores a market-run job sent over IPC, opens Saved Jobs on it and loads it when the active queue is
    /// free. The returned message is shown to the sender's user verbatim.
    /// </summary>
    public WorkshopQueueOperationResult ImportWorkshopSavedJob(string? text)
    {
        var result = workshopPrepQueue.ImportSavedJob(text, WorkshopSavedJobOrigin.MarketRun);
        return result.Success && result.QueueId is { } queueId
            ? FrozenQueueBrowser.PresentSentJob(queueId, result)
            : result;
    }

    private bool CanStageMarketAcquisition() =>
        acquisitionServices.IsMarketAcquisitionUnlocked() &&
        !acquisitionWorkspace.IsBusy &&
        !routeEngine.IsRouteActive &&
        !acquisitionRequestBuilder.IsSynchronizing &&
        !acquisitionRequestBuilder.HasExactAcquisitionAuthority;

    private bool CanStageWorkshopMarketAcquisition() => CanStageMarketAcquisition();

    public bool CanStageContextMenuItemToWorkbench() => CanStageMarketAcquisition();

    private string StageWorkshopMarketAcquisition(WorkshopMaterialProcurement line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (!CanStageWorkshopMarketAcquisition())
            return "Market Acquisition is unavailable while its buy list or route is busy.";
        if (line.VendorNeed <= 0)
            return $"{line.Availability.ItemName} has no uncovered quantity to acquire.";

        var staged = acquisitionRequestBuilder.StageLines(
            [WorkshopMaterialPanel.CreateMarketAcquisitionLine(line)],
            "Workshop Logistics");
        QueueAgentTabSelection("Market Acquisition", "Acquisition");
        IsOpen = true;
        return staged > 0
            ? $"Added {line.VendorNeed:N0} {line.Availability.ItemName} to Market Acquisition."
            : $"{line.Availability.ItemName} is already in Market Acquisition.";
    }

    public string StageContextMenuItemToWorkbench(uint itemId, string itemName)
    {
        if (itemId == 0 || string.IsNullOrWhiteSpace(itemName))
            return "The selected item could not be resolved.";
        if (!CanStageContextMenuItemToWorkbench())
            return "Market Acquisition is unavailable while its buy list or route is busy.";

        var staged = acquisitionRequestBuilder.StageLines(
            [
                new MarketAcquisitionRequestLineDocument
                {
                    ItemId = itemId,
                    ItemName = itemName.Trim(),
                    QuantityMode = "TargetQuantity",
                    TargetQuantity = 1,
                    HqPolicy = "Either",
                },
            ]);
        QueueAgentTabSelection("Market Acquisition", "Acquisition");
        IsOpen = true;
        return staged > 0
            ? $"Added 1 {itemName.Trim()} to the Market Acquisition buy list."
            : $"{itemName.Trim()} is already in the Market Acquisition buy list.";
    }

    internal static bool TryNormalizeAgentBridgeTab(string tabName, out string mainTab, out string? workspaceView)
    {
        mainTab = string.Empty;
        workspaceView = null;
        if (string.IsNullOrWhiteSpace(tabName))
            return false;

        var separatorIndex = tabName.IndexOf('/');
        mainTab = separatorIndex < 0 ? tabName : tabName[..separatorIndex];
        workspaceView = separatorIndex < 0 ? null : tabName[(separatorIndex + 1)..];
        if (string.Equals(mainTab, "Retainers", StringComparison.Ordinal) ||
            string.Equals(mainTab, "Restock", StringComparison.Ordinal) ||
            string.Equals(mainTab, "Plan", StringComparison.Ordinal))
            return false;

        return true;
    }

    /// <summary>Opens the main window on the Trade Queue tab.</summary>
    private void ShowTradeQueue()
    {
        IsOpen = true;
        QueueAgentTabSelection("Trade Queue");
    }

    /// <summary>Tradeable player inventory (what the Trade Queue can trade), units per item.</summary>
    private IReadOnlyDictionary<uint, int>? ObserveTradeableBags()
    {
        var observation = tradeQueueIo.ObserveTradeableInventory();
        if (!observation.IsAuthoritative)
            return null;
        return observation.Stacks
            .Where(stack => stack.ItemId != TradeQueuePlanner.GilItemId)
            .GroupBy(stack => stack.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(stack => stack.Quantity));
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
            ("Market Acquisition", "Acquisition" or "Deposition" or "Workbench" or "Inbox" or "Route" or "Compose" or "Working Set" or "Request" or "Plan") => true,
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
            acquisitionServices.IsMarketAcquisitionUnlocked()
                ? "Utilities: Workshop Logistics, Trade Queue, Market Acquisition. Inventory reporting lives under Settings."
                : "Utilities: Workshop Logistics and Trade Queue. Inventory reporting lives under Settings.");
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

        var assemblyActive = ImGui.BeginTabItem("Assembly", GetAgentWorkspaceTabFlags("Assembly"));
        if (assemblyActive)
        {
            workshopAssembly.Draw(config.WorkshopPrepQueue.Count > 0);
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
        if (!assemblyActive)
            ImGui.TextColored(WorkshopAssemblyPanel.GetWorkshopStatusColor(workshopStatus), workshopStatus);
    }

    private void SetSplitWorkshopViews(bool split)
    {
        if (config.SplitWorkshopQueueAndMaterials == split)
            return;
        config.SplitWorkshopQueueAndMaterials = split;
        config.Save();
    }

    private void DrawMarketAcquisitionTab()
    {
        ImGui.Spacing();
        ImGui.TextColored(ColHeader, "Market Acquisition");
        ImGui.Spacing();

        if (!ImGui.BeginTabBar("##marketAcquisitionWorkspace"))
            return;

        if (ImGui.BeginTabItem(
                BuildCountedWorkspaceTabLabel("Inbox", acquisitionWorkspace.PendingRequests.Count, "MarketAcquisitionInbox"),
                GetAgentWorkspaceTabFlags("Inbox")))
        {
            DrawMarketAcquisitionPickupSection();
            ImGui.EndTabItem();
        }

        // Placement opens the Deposition tab while Quartermaster offers placement editing, else its own editor.
        acquisitionRequestBuilder.OpenDeposition = acquisitionDeposition.IsAvailable ? openDepositionTab : null;
        if (ImGui.BeginTabItem(
                BuildCountedWorkspaceTabLabel("Acquisition", acquisitionRequestBuilder.LineCount, "MarketAcquisitionWorkbench"),
                GetAgentWorkspaceTabFlags("Acquisition", "Workbench", "Compose", "Working Set", "Request", "Plan")))
        {
            DrawMarketAcquisitionWorkbench();
            ImGui.EndTabItem();
        }

        if (acquisitionDeposition.IsAvailable)
        {
            var flags = GetAgentWorkspaceTabFlags("Deposition");
            if (depositionTabRequested)
                flags |= ImGuiTabItemFlags.SetSelected;
            depositionTabRequested = false;
            var open = ImGui.BeginTabItem("Deposition###MarketAcquisitionDeposition", flags);
            AgentReviewRegistry.RegisterLastItem(
                "acquisition.deposition.tab",
                "Show the Deposition tab",
                AgentBridgeUiControlKind.Button,
                enabled: true,
                selected: open,
                open ? "Shown" : "Hidden",
                () => depositionTabRequested = true);
            if (open)
            {
                acquisitionDeposition.Draw(CreateMarketAcquisitionRequestBuilderContext());
                ImGui.EndTabItem();
            }
        }
        else
        {
            depositionTabRequested = false;
        }

        if (ImGui.BeginTabItem("Route", GetAgentWorkspaceTabFlags("Route")))
        {
            if (acquisitionWorkspace.PreparedPlan is null)
                ImGui.TextColored(ColMuted, "Finalize the buy list before starting a route.");
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

    internal static string BuildCountedWorkspaceTabLabel(string label, int count, string stableId) =>
        $"{label} ({count:N0})###{stableId}";

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
            IsMarketAcquisitionRouteActive(),
            hasScope ? MarketAcquisitionWorkbenchCompositionInsights.RegionForWorld(world) : null,
            acquisitionWorkspace.PreparedPlan is { Status: "Ready" } && !acquisitionWorkspace.IsPreparedPlanStale());
    }

    private void DrawMarketAcquisitionWorkbenchToolbar(MarketAcquisitionRequestBuilderContext context)
    {
        const float actionWidth = 310f;
        var startX = ImGui.GetCursorPosX();
        ImGui.SetCursorPosX(startX + Math.Max(0, ImGui.GetContentRegionAvail().X - actionWidth));

        var canMutate = !context.IsBusy && !context.IsRouteActive && !acquisitionRequestBuilder.IsSynchronizing;
        if (ImGuiUi.Button("New blank", canMutate))
            _ = StartBlankWorkbenchAsync(context);
        AgentReviewRegistry.Register(
            "acquisition.workbench.new-blank",
            "Preserve the current draft and start a local blank Workbench",
            AgentBridgeUiControlKind.Button,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            canMutate,
            false,
            acquisitionWorkspace.ClaimedRequest?.Id,
            () => _ = StartBlankWorkbenchAsync(context));

        ImGui.SameLine();
        if (ImGui.Button($"Compositions ({AcquisitionCompositionWindow.Count:N0})"))
            AcquisitionCompositionWindow.ToggleOpen();
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
            AcquisitionCompositionWindow.ToggleOpen);

        ImGui.SameLine();
        if (ImGui.Button("Recovery"))
            ImGui.OpenPopup("AcquisitionWorkbenchRecovery");
        var recoveryMinimum = ImGui.GetItemRectMin();
        var recoveryMaximum = ImGui.GetItemRectMax();
        ImGui.SameLine();
        ImGui.TextColored(
            acquisitionRequestBuilder.IsHostedSyncEnabled ? ColHeader : ColMuted,
            acquisitionRequestBuilder.IsHostedSyncEnabled ? "Hosted sync on" : "Local-only");
        var canClearWorkbench = acquisitionRequestBuilder.LineCount > 0 ||
                                acquisitionRequestBuilder.HasExactAcquisitionAuthority;
        AgentReviewRegistry.Register(
            "acquisition.recovery.clear-workbench",
            "Preserve the current draft and start a local blank Market Acquisition Workbench",
            AgentBridgeUiControlKind.Button,
            recoveryMinimum,
            recoveryMaximum,
            canMutate && canClearWorkbench,
            false,
            acquisitionRequestBuilder.LineCount.ToString(),
            () => _ = StartBlankWorkbenchAsync(context));
        AgentReviewRegistry.Register(
            "acquisition.recovery.clear-active-work-order",
            "Detach local hosted state without changing the server copy",
            AgentBridgeUiControlKind.Button,
            recoveryMinimum,
            recoveryMaximum,
            canMutate && HasMarketAcquisitionHostedState(),
            false,
            acquisitionWorkspace.ClaimedRequest?.Id,
            () => DetachMarketAcquisitionHostedStateLocally());
        AgentReviewRegistry.Register(
            "acquisition.recovery.restore-previous-workbench",
            "Restore the previously preserved Market Acquisition Workbench",
            AgentBridgeUiControlKind.Button,
            recoveryMinimum,
            recoveryMaximum,
            canMutate && acquisitionRequestBuilder.HasPreviousWorkbench,
            false,
            acquisitionRequestBuilder.HasPreviousWorkbench.ToString(),
            () => acquisitionRequestBuilder.RestorePreviousWorkbench());

        if (!ImGui.BeginPopup("AcquisitionWorkbenchRecovery"))
            return;

        if (ImGuiUi.MenuItem("New blank buy list", canMutate && canClearWorkbench))
            _ = StartBlankWorkbenchAsync(context);
        if (ImGuiUi.MenuItem("Restore previous buy list", canMutate && acquisitionRequestBuilder.HasPreviousWorkbench))
            acquisitionRequestBuilder.RestorePreviousWorkbench();
        if (ImGuiUi.MenuItem(
                "Sync hosted copy",
                canMutate && CanRequestMarketAcquisitionHostedSync(context)))
        {
            RequestMarketAcquisitionHostedSync(context);
        }
        if (ImGuiUi.MenuItem(
                "Pause hosted sync",
                canMutate && acquisitionRequestBuilder.IsHostedSyncEnabled))
        {
            acquisitionRequestBuilder.PauseHostedSync();
        }
        if (ImGuiUi.MenuItem(
                "Shelf hosted copy remotely",
                canMutate && CanShelfMarketAcquisitionHostedState()))
        {
            _ = ShelfMarketAcquisitionHostedStateRemotely();
        }
        if (ImGuiUi.MenuItem(
                "Detach hosted copy locally",
                canMutate && HasMarketAcquisitionHostedState()))
        {
            DetachMarketAcquisitionHostedStateLocally();
        }
        if (ImGuiUi.MenuItem(
                "Return active work order to sender",
                canMutate && acquisitionWorkspace.ClaimedRequest is { Status: "Claimed" }))
        {
            _ = ReturnActiveWorkOrderAsync();
        }

        ImGui.EndPopup();
    }

    private async Task StartBlankWorkbenchAsync(MarketAcquisitionRequestBuilderContext context)
    {
        await acquisitionRequestBuilder.WaitForRefreshAsync().ConfigureAwait(false);
        if (!PrepareMarketAcquisitionLocalReplacement())
            return;

        acquisitionRequestBuilder.StartBlankWorkbench(context);
    }

    private bool CanRequestMarketAcquisitionHostedSync(MarketAcquisitionRequestBuilderContext context)
    {
        if (!context.HasCharacterScope ||
            context.IsBusy ||
            context.IsRouteActive ||
            acquisitionRequestBuilder.IsSynchronizing ||
            !acquisitionRequestBuilder.DraftValidation.IsValid ||
            string.IsNullOrWhiteSpace(config.ServerUrl) ||
            string.IsNullOrWhiteSpace(WorkshopHostApiKeyRouting.ResolveAcquisitionKey(config)))
        {
            return false;
        }

        var remoteRequestId = acquisitionRequestBuilder.CurrentDocument.RemoteRequestId;
        var claim = acquisitionWorkspace.ClaimedRequest;
        return string.IsNullOrWhiteSpace(remoteRequestId)
            ? claim is null
            : claim is not null && string.Equals(claim.Id, remoteRequestId, StringComparison.Ordinal);
    }

    private void RequestMarketAcquisitionHostedSync(MarketAcquisitionRequestBuilderContext context)
    {
        if (!CanRequestMarketAcquisitionHostedSync(context))
        {
            acquisitionRequestBuilder.SetStatus(
                "Hosted sync needs a matching local claim. Detach the local hosted state before publishing a new browser copy.");
            return;
        }

        acquisitionRequestBuilder.RequestHostedSync();
    }

    private bool HasMarketAcquisitionHostedState() =>
        acquisitionRequestBuilder.IsHostedSyncEnabled ||
        acquisitionRequestBuilder.HasHostedAssociation ||
        acquisitionWorkspace.ClaimedRequest is not null ||
        acquisitionRequestBuilder.SyncStatus.Equals("SyncFailed", StringComparison.OrdinalIgnoreCase) ||
        acquisitionRequestBuilder.SyncStatus.Equals("RemoteChanged", StringComparison.OrdinalIgnoreCase);

    private bool CanShelfMarketAcquisitionHostedState() =>
        acquisitionRequestBuilder.HasHostedAssociation ||
        acquisitionWorkspace.ClaimedRequest is not null;

    private bool PrepareMarketAcquisitionLocalReplacement()
    {
        if (IsMarketAcquisitionRouteActive() || acquisitionWorkspace.IsBusy || acquisitionRequestBuilder.IsSynchronizing)
        {
            acquisitionRequestBuilder.SetStatus(
                "Stop the active route and wait for the current buy-list operation before replacing the local plan.");
            return false;
        }

        if (HasMarketAcquisitionHostedState() || acquisitionWorkspace.PreparedPlan is not null)
            acquisitionWorkspace.DetachLocalHostedState();
        return true;
    }

    private bool DetachMarketAcquisitionHostedStateLocally()
    {
        if (!PrepareMarketAcquisitionLocalReplacement())
            return false;

        acquisitionRequestBuilder.DetachHostedAssociation();
        return true;
    }

    private async Task ShelfMarketAcquisitionHostedStateRemotely()
    {
        await acquisitionRequestBuilder.WaitForRefreshAsync().ConfigureAwait(false);
        var current = acquisitionRequestBuilder.CurrentDocument;
        if (!await acquisitionWorkspace.ShelfActiveWorkOrderAsync(
                current.RemoteRequestId,
                current.RemoteRevision).ConfigureAwait(false))
        {
            acquisitionRequestBuilder.SetStatus(
                "Remote shelving failed; the local buy list and hosted association were retained.");
            return;
        }

        acquisitionRequestBuilder.MarkHostedCopyShelved();
    }

    private async Task ReplaceWorkbenchFromCompositionAsync(
        MarketAcquisitionWorkbenchComposition composition,
        string characterName,
        string world)
    {
        await acquisitionRequestBuilder.WaitForRefreshAsync().ConfigureAwait(false);
        if (!PrepareMarketAcquisitionLocalReplacement())
            return;

        acquisitionRequestBuilder.LoadComposition(composition, characterName, world);
        acquisitionCompositionCatalog.LinkSource(
            composition.Id,
            acquisitionRequestBuilder.CurrentDocument.LocalRequestId,
            loadedFromComposition: true);
    }

    private void DrawMarketAcquisitionFinalizationBar(MarketAcquisitionRequestBuilderContext context)
    {
        ImGui.Separator();
        var validation = acquisitionRequestBuilder.DraftValidation;
        var exactAcquisitionValidation = acquisitionRequestBuilder.ExactAcquisitionFinalizationValidation;
        var presentation = MarketAcquisitionWorkbenchFinalizationPresenter.Build(new(
            acquisitionRequestBuilder.LineCount,
            validation.IsValid && exactAcquisitionValidation.IsValid,
            validation.Errors.FirstOrDefault() ?? exactAcquisitionValidation.Error,
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
            acquisitionRequestBuilder.TargetQuantityTotal,
            acquisitionRequestBuilder.CurrentDocument.Lines.Count(line => line.QuantityMode == "TargetQuantity")));

        ImGui.BeginGroup();
        ImGui.TextColored(presentation.CanFinalize ? ColSuccess : ColMuted, presentation.Title);
        if (acquisitionRequestBuilder.StowToRetainersCount is var stowCount and > 0)
        {
            ImGui.SameLine(0, 0);
            ImGui.TextColored(ColMuted, $" · {stowCount:N0} stow to retainers");
        }
        ImGui.TextColored(ColMuted, presentation.Detail);
        ImGui.EndGroup();

        const float buttonWidth = 148f;
        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - buttonWidth));
        if (ImGuiUi.PrimaryButton("Finalize Plan", presentation.CanFinalize))
            _ = OpenAcquisitionFinalizationAsync();
        AgentReviewRegistry.Register(
            "acquisition.finalize",
            "Finalize the Market Acquisition Workbench for execution",
            AgentBridgeUiControlKind.Button,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            presentation.CanFinalize,
            false,
            acquisitionWorkspace.ClaimedRequest?.Id,
            () => _ = OpenAcquisitionFinalizationAsync());
        DrawAcquisitionFinalizationModal();
    }

    private async Task FinalizeMarketAcquisitionPlanAsync()
    {
        var reviewed = acquisitionFinalizationPlan;
        await acquisitionRequestBuilder.WaitForRefreshAsync().ConfigureAwait(false);
        if (reviewed is null || acquisitionFinalizationHash != ReviewedIntentHash(acquisitionRequestBuilder.CurrentDocument))
        {
            await OpenAcquisitionFinalizationAsync().ConfigureAwait(false);
            return;
        }
        if (!acquisitionRequestBuilder.FinalizeExactAcquisitionAuthority())
            return;
        var claimed = acquisitionWorkspace.ClaimedRequest;
        if (claimed is { Status: "Claimed" })
        {
            await acquisitionWorkspace.AcceptAsync().ConfigureAwait(false);
            claimed = acquisitionWorkspace.ClaimedRequest;
        }

        if (claimed is not null &&
            !MarketAcquisitionPlanPreparationService.CanPrepareForStatus(claimed.Status))
        {
            return;
        }

        if (acquisitionFinalizationHash != ReviewedIntentHash(acquisitionRequestBuilder.CurrentDocument))
        {
            await OpenAcquisitionFinalizationAsync().ConfigureAwait(false);
            return;
        }
        await acquisitionWorkspace.PreparePlanAsync(
            ResolvePlanningWorld(acquisitionServices.GetCurrentWorldName()), acquisitionServices.GetMarketAcquisitionRecentWorldTtl(),
            config.MarketAcquisitionIgnoreRecentWorldVisitsForSweep,
            acquisitionRequestBuilder.CurrentDocument, reviewed).ConfigureAwait(false);
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
            GetAcquisitionStatusColor(visibleStatus),
            routeEngine.StuckReportRequestId,
            DropStuckAcquisitionReport));
    }

    private void DropStuckAcquisitionReport(string requestId)
    {
        if (routeEngine.DropStuckReport(requestId))
        {
            log.Warning($"[MarketMafioso] User dropped the stuck report at the head of acquisition request {requestId}'s outbox.");
            acquisitionWorkspace.SetStatus("Dropped the stuck report; the order's other reports will keep sending.");
        }
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
            var request = acquisitionWorkspace.ResolveExecutionRequest(acquisitionRequestBuilder.CurrentDocument);
            routeEngine.ProbePreparedPlan(plan, request);
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
        var currentWorld = playerState.CurrentWorld.IsValid ? acquisitionServices.GetCurrentWorldName() : string.Empty;
        return acquisitionWorkspace.PreparePlanAsync(
            ResolvePlanningWorld(currentWorld),
            acquisitionServices.GetMarketAcquisitionRecentWorldTtl(),
            config.MarketAcquisitionIgnoreRecentWorldVisitsForSweep,
            acquisitionRequestBuilder.CurrentDocument);
    }

    private void DrawMarketAcquisitionGuidedRoute()
    {
        marketAcquisitionGuidedRoutePanel.Draw(
            acquisitionWorkspace.PreparedPlan,
            acquisitionWorkspace.IsPreparedPlanStale(),
            CanStartEvidenceRefresh(),
            ShowCrossRegion ? DrawCrossRegionPipeline : CrossRegion.Forwards.Count > 0 ? DrawCrossRegionForwards : null,
            suppressIdleRouteSummary: ShowCrossRegion &&
                (!CrossRegion.HasSession || CrossRegion.Phase is not (CrossRegionAcquisitionPhase.PreparingShopRoute or
                    CrossRegionAcquisitionPhase.Shopping or CrossRegionAcquisitionPhase.Paused)));
    }

    private bool CanStartEvidenceRefresh()
    {
        if (acquisitionWorkspace.IsBusy || routeEngine.IsRouteActive)
            return false;

        try
        {
            _ = acquisitionWorkspace.ResolveExecutionRequest(acquisitionRequestBuilder.CurrentDocument);
            return acquisitionWorkspace.PreparedPlan is { WorldBatches.Count: > 0 } &&
                   !acquisitionWorkspace.IsPreparedPlanStale();
        }
        catch
        {
            return false;
        }
    }

    private Task StartGuidedRouteAsync()
    {
        return acquisitionWorkspace.RunWithExecutionRequestAsync(acquisitionRequestBuilder.CurrentDocument, (request, _) =>
        {
            var plan = acquisitionWorkspace.RequirePreparedPlan("Prepare a plan before starting a guided route.");
            var diagnosticsLevel = MarketAcquisitionRouteDiagnosticsPolicy.Resolve(
                config.MarketAcquisitionRouteDiagnostics);
            var result = routeEngine.Start(
                plan,
                request,
                diagnosticsLevel != MarketAcquisitionRouteDiagnosticsLevel.Off,
                config.EnableOpportunisticWorldChecks,
                acquisitionRequestBuilder.CurrentDocument.ExactAcquisitionAuthority?.FinalizedContract,
                acquisitionRequestBuilder.CurrentDocument,
                diagnosticsLevel: diagnosticsLevel);
            if (result.TargetsAlreadyMet)
            {
                acquisitionWorkspace.ReplacePreparedPlan(plan with { Status = "TargetsAlreadyMet", WorldBatches = [], PlannedQuantity = 0, PlannedGil = 0 });
                acquisitionWorkspace.SetStatus(result.Message);
            }
            routeEngine.ReportRouteProgress();
            return Task.CompletedTask;
        });
    }

    private Action? CreateEvidenceRefreshConfirmation(MarketAcquisitionPlan? expectedPlan)
    {
        if (!CanStartEvidenceRefresh() || !ReferenceEquals(expectedPlan, acquisitionWorkspace.PreparedPlan))
            return null;

        var preparedPlan = acquisitionWorkspace.PreparedPlan!;
        var document = acquisitionRequestBuilder.CurrentDocument;
        var intentHash = MarketAcquisitionRequestDocumentHasher.ComputeIntentHash(document);
        var request = acquisitionWorkspace.ResolveExecutionRequest(document);
        return () => _ = StartEvidenceRefreshAsync(preparedPlan, intentHash, request.Id);
    }

    private Task StartEvidenceRefreshAsync(MarketAcquisitionPlan confirmedPlan, string confirmedIntentHash, string confirmedRequestId)
    {
        if (!CanStartEvidenceRefresh())
        {
            acquisitionWorkspace.SetStatus("Evidence-only scan was not started: the route is no longer available. No purchases were attempted.");
            return Task.CompletedTask;
        }

        return acquisitionWorkspace.RunWithExecutionRequestAsync(acquisitionRequestBuilder.CurrentDocument, (request, _) =>
        {
            // Recheck after any hosted-claim recovery as well as at the confirmation click.
            if (routeEngine.IsRouteActive || acquisitionWorkspace.IsPreparedPlanStale() ||
                !ReferenceEquals(confirmedPlan, acquisitionWorkspace.PreparedPlan) ||
                confirmedIntentHash != MarketAcquisitionRequestDocumentHasher.ComputeIntentHash(acquisitionRequestBuilder.CurrentDocument) ||
                confirmedRequestId != request.Id)
            {
                acquisitionWorkspace.SetStatus("Evidence-only scan was not started: the plan or request changed after confirmation opened. Review the current worlds and confirm again.");
                return Task.CompletedTask;
            }

            var preparedPlan = acquisitionWorkspace.RequirePreparedPlan(
                "Prepare a plan before refreshing its market evidence.");
            var plan = MarketAcquisitionEvidenceRefreshPlanBuilder.Build(
                request,
                preparedPlan.WorldBatches.Select(batch => batch.WorldName).ToArray(),
                DateTimeOffset.UtcNow);
            var diagnosticsLevel = MarketAcquisitionRouteDiagnosticsPolicy.Resolve(
                config.MarketAcquisitionRouteDiagnostics);
            routeEngine.StartEvidenceRefresh(
                plan,
                request,
                diagnosticsLevel != MarketAcquisitionRouteDiagnosticsLevel.Off,
                diagnosticsLevel);
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

    private Task RecoverGuidedRouteAsync()
    {
        return acquisitionWorkspace.RunWithExecutionRequestAsync(acquisitionRequestBuilder.CurrentDocument, (request, _) =>
        {
            routeEngine.Recover(request);
            routeEngine.ReportRouteProgress();
            return Task.CompletedTask;
        });
    }

    private Task StopGuidedRouteAsync()
    {
        routeEngine.Stop();
        routeEngine.ReportRouteProgress();
        return Task.CompletedTask;
    }

    private Task RestartGuidedRouteAsync()
    {
        return acquisitionWorkspace.RunWithExecutionRequestAsync(acquisitionRequestBuilder.CurrentDocument, (request, _) =>
        {
            var plan = acquisitionWorkspace.RequirePreparedPlan("Prepare a plan before restarting a guided route.");
            routeEngine.Restart(plan, request);
            routeEngine.ReportRouteProgress();
            return Task.CompletedTask;
        });
    }

    private Task ReprepareGuidedRouteAsync()
    {
        return acquisitionWorkspace.RunWithExecutionRequestAsync(acquisitionRequestBuilder.CurrentDocument, (request, _) =>
        {
            var plan = acquisitionWorkspace.RequirePreparedPlan("Prepare a plan before re-preparing a guided route.");
            var result = routeEngine.ReprepareAndRestart(plan, DateTimeOffset.UtcNow, request);
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
        return acquisitionWorkspace.RunWithExecutionRequestAsync(acquisitionRequestBuilder.CurrentDocument, (request, _) =>
        {
            if (!config.EnableMarketAcquisitionDryRunTools)
                throw new InvalidOperationException("Enable Market Acquisition dry-run tools in Advanced / Testing first.");
            var plan = acquisitionWorkspace.RequirePreparedPlan("Prepare a plan before starting a dry run.");
            var result = routeEngine.Start(
                plan,
                request,
                enableDiagnostics: true,
                config.EnableOpportunisticWorldChecks,
                acquisitionRequestBuilder.CurrentDocument.ExactAcquisitionAuthority?.FinalizedContract,
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
        acquisitionWorkspace.PreparedPlan?.Status == "Ready" &&
        !acquisitionWorkspace.IsPreparedPlanStale();

#if DEBUG
    private bool CanSeedExactAcquisitionDryRunSunkState()
    {
        if (!config.EnableMarketAcquisitionDryRunTools || acquisitionWorkspace.IsBusy || routeEngine.IsRouteActive ||
            acquisitionWorkspace.PreparedPlan is not { Status: "Ready" } plan ||
            acquisitionWorkspace.IsPreparedPlanStale() ||
            acquisitionRequestBuilder.CurrentDocument.ExactAcquisitionAuthority?.FinalizedContract is not { Transfer.DryRunOnly: true } contract)
            return false;
        try
        {
            var request = acquisitionWorkspace.ResolveExecutionRequest(acquisitionRequestBuilder.CurrentDocument);
            _ = ExactAcquisitionDryRunSunkStateSeeder.CreateSemanticSeed(
                contract,
                acquisitionRequestBuilder.CurrentDocument,
                request,
                plan);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string SeedExactAcquisitionDryRunSunkState()
    {
        if (!CanSeedExactAcquisitionDryRunSunkState())
            return "DEBUG sunk-state seed is unavailable for the current finalized dry-run route.";
        var plan = acquisitionWorkspace.PreparedPlan!;
        var document = acquisitionRequestBuilder.CurrentDocument;
        var request = acquisitionWorkspace.ResolveExecutionRequest(document);
        var contract = document.ExactAcquisitionAuthority!.FinalizedContract!;
        var seed = ExactAcquisitionDryRunSunkStateSeeder.CreateSemanticSeed(contract, document, request, plan);
        var result = ExactAcquisitionDryRunSunkStateSeeder.Seed(
            exactAcquisitionRouteStateStore,
            contract,
            document,
            request,
            plan,
            seed);
        acquisitionWorkspace.SetStatus(result.Message);
        return result.Message;
    }
#endif

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
        routeEngine.IsRouteActive || CrossRegion.IsActive;

    private bool IsExpectedCharacterScopeGap() =>
        routeEngine.IsRouteActive &&
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

    private IReadOnlyList<WorkshopMaterialAvailability> GetWorkshopAvailability() =>
        GetWorkshopAvailability(config.WorkshopPrepQueue);

    private IReadOnlyList<WorkshopMaterialAvailability> GetWorkshopAvailability(IReadOnlyList<WorkshopPrepQueueItem> items) =>
        items.Count == 0 ? [] : GetWorkshopAvailability(workshopCatalog.BuildRequirements(items), null);

    /// <summary>
    /// Availability for arbitrary requirements against workshop-usable bags (or <paramref name="playerInventory"/>
    /// when the caller already counted them), plus owner-scoped retainer reports and the Quartermaster snapshot.
    /// </summary>
    private IReadOnlyList<WorkshopMaterialAvailability> GetWorkshopAvailability(
        IReadOnlyList<WorkshopMaterialRequirement> requirements,
        IReadOnlyDictionary<uint, int>? playerInventory)
    {
        if (requirements.Count == 0)
            return [];

        playerInventory ??= scanner.CountWorkshopUsableInventory();
        var owner = acquisitionServices.GetCurrentQuartermasterOwnerScope();
        var retainers = new List<MarketMafioso.Contracts.Inventory.RetainerReport>();
        var hasDirectRetainerEvidence = false;
        if (owner.LocalContentId is > 0 && owner.HomeWorldId is > 0)
        {
            hasDirectRetainerEvidence = retainerReports.TryGetReports(
                new Franthropy.Observations.V1.ObservationOwner(owner.LocalContentId.Value, owner.HomeWorldId.Value),
                owner.CharacterName,
                owner.HomeWorldName,
                includeOwnerFields: false,
                config.IncludeItemNames,
                scanner.ResolveItemMetadata,
                out retainers);
        }
        Quartermaster.QuartermasterSnapshot? quartermasterSnapshot = null;
        quartermaster.TryGetSnapshot(out quartermasterSnapshot, out _);
        return WorkshopMaterialAvailabilityService.BuildAvailabilityWithFallback(
            requirements,
            playerInventory,
            hasDirectRetainerEvidence,
            retainers,
            quartermasterSnapshot,
            owner);
    }

    private void StartWorkshopAssembly(bool enableDiagnostics)
    {
        try
        {
            var preflight = WorkshopAssemblyPreflightService.Check(
                config.WorkshopPrepQueue,
                workshopCatalog.GetProjects(),
                scanner.CountWorkshopUsableInventory());
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

    private void InspectWorkshopCraftingLog()
    {
        try
        {
            IReadOnlyList<WorkshopAssemblyQueueEntry> context = [];
            try
            {
                context = WorkshopAssemblyPlanBuilder.Build(config.WorkshopPrepQueue, workshopCatalog.GetProjects()).Entries;
            }
            catch (InvalidOperationException)
            {
                // An empty or stale queue only loses the draft context in the report.
            }

            workshopStatus = workshopAssemblyRunner.StartCraftingLogInspection(context).Message;
        }
        catch (Exception ex)
        {
            workshopStatus = $"Unable to inspect the workshop crafting log. {ex.Message}";
            log.Warning(ex, "[MarketMafioso] Workshop crafting log inspection failed to start.");
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

    public void Dispose()
    {
        AgentCaptureTransactions.CancelActive();

        uiStateCapture.Dispose();
        craftQuoteHttpClient.Dispose();
        FrozenQueueBrowser.Dispose();
    }
}
