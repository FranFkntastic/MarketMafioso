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
using MarketMafioso.Automation.Retainers;
using MarketMafioso.Automation.Travel;
using MarketMafioso.CraftArchitectCompanion;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.RetainerRestock;
using MarketMafioso.Windows.Main;
using MarketMafioso.Windows.MarketAcquisitionPanels;
using MarketMafioso.Windows.MarketAcquisitionRequestBuilder;
using MarketMafioso.Windows.RetainerRestock;
using MarketMafioso.Windows.WorkshopLogistics;
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
    private readonly WorkshopAssemblyRunner workshopAssemblyRunner;
    private readonly RetainerCacheFileStore? retainerCacheStore;
    private readonly IPlayerState playerState;
    private readonly IPluginLog log;
    private readonly HttpClient acquisitionHttpClient = new();
    private readonly HttpClient craftQuoteHttpClient = new();
    private readonly MarketAcquisitionRequestClient acquisitionClient;
    private readonly UniversalisMarketAcquisitionPlanSource acquisitionPlanSource;
    private readonly MarketAcquisitionWorldVisitCatalog marketAcquisitionWorldVisitCatalog;
    private readonly MarketAcquisitionPlanPreparationService marketAcquisitionPlanPreparationService;
    private readonly MarketBoardApproachService marketBoardApproachService;
    private readonly MarketAcquisitionRouteEngine routeEngine;
    private readonly string marketAcquisitionRouteDiagnosticsDirectory;
    private readonly OverviewTabPanel overviewTab;
    private readonly InventoryReporterTabPanel inventoryReporterTab;
    private readonly StatusTabPanel statusTab;
    private readonly SettingsTabPanel settingsTab;
    private readonly MarketAcquisitionPlanPanel marketAcquisitionPlanPanel = new();
    private readonly MarketAcquisitionRequestPickupPanel marketAcquisitionRequestPickupPanel;
    private readonly MarketAcquisitionAcceptedRequestPanel marketAcquisitionAcceptedRequestPanel;
    private readonly MarketAcquisitionDiagnosticsPanel marketAcquisitionDiagnosticsPanel;
    private readonly MarketAcquisitionGuidedRoutePanel marketAcquisitionGuidedRoutePanel;
    private readonly MarketAcquisitionRequestBuilderPanel acquisitionRequestBuilder;
    private readonly RetainerRestockTabPanel restockTab;
    private readonly WorkshopPrepQueuePanel workshopPrepQueue;
    private readonly WorkshopMaterialPanel workshopMaterials;
    private readonly WorkshopAssemblyPanel workshopAssembly;

    private readonly WorkshopProjectSelectionState workshopProjectSelection = new();
    private IReadOnlyList<MarketAcquisitionRequestView> pendingAcquisitionRequests = [];
    private MarketAcquisitionClaimView? claimedAcquisitionRequest;
    private string? claimedAcceptIdempotencyKey;
    private string? claimedRejectIdempotencyKey;
    private MarketAcquisitionPlan? acquisitionPlan;
    private string? currentAcquisitionPlanHash;
    private int marketInputCaptureIndex;
    private bool acquisitionRequestBusy = false;
    private string acquisitionStatus = "No dashboard request has been fetched this session.";
    private CancellationTokenSource? acquisitionRequestCancellation;
    private string workshopStatus = "Workshop prep queue is idle.";

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
            new DalamudMarketAcquisitionMarketBoardIo(
                marketBoardApproachService,
                marketBoardItemSearchDriver,
                marketBoardListingReader,
                marketBoardInputCaptureReader),
            new DalamudMarketAcquisitionPurchaseIo(marketBoardPurchaseExecutor, marketBoardPurchaseAdapter),
            new MarketAcquisitionRouteRequestReporter(config, acquisitionClient),
            new MarketAcquisitionWorldVisitEvidenceRecorder(config, marketAcquisitionWorldVisitCatalog),
            new MarketAcquisitionClaimLifecycleController(
                config,
                () => claimedAcquisitionRequest,
                value => claimedAcquisitionRequest = value,
                () => claimedAcceptIdempotencyKey,
                () => claimedRejectIdempotencyKey,
                () =>
                {
                    claimedAcceptIdempotencyKey = null;
                    claimedRejectIdempotencyKey = null;
                },
                value => acquisitionStatus = value,
                () => marketAcquisitionRouteRunner.StatusMessage,
                config.Save),
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
        statusTab = new StatusTabPanel(config, reporter, retainerCacheStore, log);
        marketAcquisitionRequestPickupPanel = new MarketAcquisitionRequestPickupPanel(
            () => _ = FetchDashboardRequestsAsync(),
            requestId => _ = ClaimAcquisitionRequestAsync(requestId));
        marketAcquisitionAcceptedRequestPanel = new MarketAcquisitionAcceptedRequestPanel(
            () => _ = AcceptClaimedAcquisitionRequestAsync(),
            () => _ = RejectClaimedAcquisitionRequestAsync(),
            ForgetLocalAcquisitionRequest,
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
            OnAcquisitionRequestBuilderDocumentAdopted);
        AcquisitionDiagnostics = new MarketAcquisitionDiagnosticsWindow(
            routeEngine.CreateSnapshot,
            () => acquisitionPlan,
            CanProbeLiveMarketBoard,
            () => _ = ProbeLiveMarketBoardAsync(),
            CaptureMarketBoardInputState,
            FinalizeMarketBoardInputCaptureLog,
            () => acquisitionRequestBuilderCraftAppraisal.State.CreateDiagnosticsSnapshot());
        AutomationDiagnostics = new AutomationDiagnosticsWindow(
            new AutomationDiagnosticProbeFactory(autoRetainerRefresh, viwiWorkshoppaIpc).Create(),
            IsMarketAcquisitionUnlocked);
        marketAcquisitionDiagnosticsPanel = new MarketAcquisitionDiagnosticsPanel(
            routeEngine.CreateSnapshot,
            marketAcquisitionRouteDiagnosticsDirectory,
            log,
            () => AcquisitionDiagnostics.IsOpen = true,
            () => AutomationDiagnostics.IsOpen = true,
            CaptureMarketBoardInputState,
            FinalizeMarketBoardInputCaptureLog);
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
            () => _ = routeEngine.Stop(),
            () => AcquisitionDiagnostics.IsOpen = false,
            () => AutomationDiagnostics.IsOpen = false,
            () => AutomationDiagnostics.IsOpen = true);

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

        routeEngine.MonitorMarketBoardPurchase();
        routeEngine.TickRoute(acquisitionRequestBusy);
    }

    public override void Draw()
    {
        DrawHeader();
        ImGui.Spacing();

        if (ImGui.BeginTabBar("##MarketMafiosoTabs"))
        {
            if (ImGui.BeginTabItem("Overview"))
            {
                overviewTab.Draw();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Inventory Reporter"))
            {
                inventoryReporterTab.Draw();
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
                marketAcquisitionDiagnosticsPanel.Draw();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Settings"))
            {
                settingsTab.Draw();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Status"))
            {
                statusTab.Draw();
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
            acquisitionRequestBusy,
            IsMarketAcquisitionRouteActive(),
            claimedAcquisitionRequest,
            acquisitionPlan,
            currentAcquisitionPlanHash));
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
            claimedAcquisitionRequest,
            pendingAcquisitionRequests,
            acquisitionRequestBusy,
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
            claimedAcquisitionRequest,
            acquisitionRequestBusy,
            claimedAcquisitionRequest is not null &&
            !acquisitionRequestBusy &&
            MarketAcquisitionPlanPreparationService.CanPrepareForStatus(claimedAcquisitionRequest.Status));
    }

    private void DrawMarketAcquisitionPlan()
    {
        marketAcquisitionPlanPanel.Draw(acquisitionPlan, IsAcquisitionPlanStale());
    }

    private bool CanProbeLiveMarketBoard()
    {
        return !acquisitionRequestBusy &&
               !routeEngine.IsRouteActive &&
               acquisitionPlan is { Status: "Ready" } &&
               !IsAcquisitionPlanStale() &&
               acquisitionPlan.WorldBatches.Count > 0;
    }

    private Task ProbeLiveMarketBoardAsync()
    {
        return RunAcquisitionRequestAsync(_ =>
        {
            var plan = acquisitionPlan ??
                       throw new InvalidOperationException("Prepare a live candidate plan before probing live market board listings.");
            var claimed = claimedAcquisitionRequest ??
                          throw new InvalidOperationException("No dashboard request is accepted.");
            routeEngine.ProbePreparedPlan(plan, claimed);
            acquisitionStatus = routeEngine.CreateSnapshot().VisibleAcquisitionStatus;
            return Task.CompletedTask;
        });
    }

    private bool IsAcquisitionPlanStale() =>
        acquisitionPlan is not null &&
        !string.IsNullOrWhiteSpace(currentAcquisitionPlanHash) &&
        !string.Equals(currentAcquisitionPlanHash, acquisitionRequestBuilder.CurrentIntentHash, StringComparison.Ordinal);

    private void DrawMarketBoardProbeStatus(MarketAcquisitionRouteEngineSnapshot snapshot)
    {
        DrawLiveCandidatePlanResult(snapshot);

        if (ImGuiUi.Button("Open Diagnostics", true))
            AcquisitionDiagnostics.IsOpen = true;
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
            ResetGuidedRoute("No route has started.");
            acquisitionStatus = result.StatusMessage;
        }).ConfigureAwait(false);
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

    private async Task<MarketAcquisitionClaimView> EnsureAcquisitionClaimReadyForPlanningAsync(
        MarketAcquisitionClaimView claimed,
        CancellationToken token)
    {
        if (!MarketAcquisitionPlanPreparationService.IsFailedStatus(claimed.Status))
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

    private void DrawMarketAcquisitionGuidedRoute()
    {
        marketAcquisitionGuidedRoutePanel.Draw(acquisitionPlan, IsAcquisitionPlanStale());
    }

    private Task StartGuidedRouteAsync(bool forceDiagnostics)
    {
        return RunAcquisitionRequestAsync(async token =>
        {
            var plan = acquisitionPlan ??
                       throw new InvalidOperationException("Prepare a plan before starting a guided route.");
            var claimed = claimedAcquisitionRequest ??
                          throw new InvalidOperationException("No dashboard request is accepted.");
            await EnsureRouteReportableClaimAsync(claimed, token).ConfigureAwait(false);
            var enableDiagnostics = MarketAcquisitionRouteDiagnosticsPolicy.ShouldCreatePackage(
                config.CreateMarketAcquisitionRouteDiagnosticPackages,
                forceDiagnostics);
            routeEngine.Start(
                plan,
                claimed,
                enableDiagnostics,
                config.EnableOpportunisticWorldChecks);
            routeEngine.ReportRouteProgress();
        });
    }

    private Task PauseGuidedRouteAsync()
    {
        marketBoardApproachService.StopNavigation();
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
        marketBoardApproachService.StopNavigation();
        routeEngine.Stop();
        routeEngine.ReportRouteProgress();
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
            routeEngine.Restart(plan);
            routeEngine.ReportRouteProgress();
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
            var result = routeEngine.ReprepareAndRestart(plan, DateTimeOffset.UtcNow);
            var snapshot = routeEngine.CreateSnapshot();
            if (snapshot.ActivePlan != null)
                acquisitionPlan = snapshot.ActivePlan;
            acquisitionStatus = result.Message;
            routeEngine.ReportRouteProgress();
        });
    }

    private void CaptureMarketBoardInputState()
    {
        try
        {
            var label = $"input-capture-{++marketInputCaptureIndex}";
            var result = routeEngine.CaptureInputState(label);
            acquisitionStatus = result.Success
                ? $"{result.Message} {routeEngine.CreateSnapshot().LastDiagnosticFilePath}"
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
            var result = routeEngine.FinalizeInputCaptureLog();
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
        routeEngine.Reset(status);
    }

    private async Task EnsureRouteReportableClaimAsync(MarketAcquisitionClaimView claimed, CancellationToken token)
    {
        claimed = await EnsureAcquisitionClaimReadyForPlanningAsync(claimed, token).ConfigureAwait(false);
        if (!MarketAcquisitionRouteProgressReporter.CanReportForRequestStatus(claimed.Status))
            throw new InvalidOperationException($"Request status {claimed.Status} cannot start a route. Fetch or accept a dashboard request first.");
    }

    private bool IsMarketAcquisitionRouteActive() =>
        routeEngine.IsRouteActive;

    private bool IsExpectedCharacterScopeGap() =>
        claimedAcquisitionRequest != null &&
        routeEngine.CreateSnapshot().ActiveStop?.Status == "TravelCommandSent";

    private string GetVisibleAcquisitionStatus()
    {
        if (acquisitionStatus.StartsWith("Route progress report failed", StringComparison.OrdinalIgnoreCase) &&
            IsMarketAcquisitionRouteActive())
            return routeEngine.CreateSnapshot().StatusMessage;

        return acquisitionStatus;
    }

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

    private static string FormatGil(uint gil) => $"{gil:N0} gil";

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
        acquisitionRequestCancellation?.Dispose();
        routeEngine.Dispose();
        acquisitionHttpClient.Dispose();
        craftQuoteHttpClient.Dispose();
    }
}
