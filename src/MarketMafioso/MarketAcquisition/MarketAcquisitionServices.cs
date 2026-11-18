using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Automation.MarketBoard;
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.Automation.Runtime;
using MarketMafioso.Automation.Travel;
using MarketMafioso.Contracts.MarketIntelligence;
using MarketMafioso.MarketAcquisition.ExactAuthority;
using MarketMafioso.MarketAcquisition.MarketBoard;
using MarketMafioso.MarketDiagnostics;
using MarketMafioso.Quartermaster;

namespace MarketMafioso.MarketAcquisition;

internal sealed class MarketAcquisitionServices : IDisposable
{
    private readonly Configuration config;
    private readonly IPlayerState playerState;
    private readonly InventoryScanner scanner;
    private readonly IPluginLog log;
    private readonly HttpClient acquisitionHttpClient = new();
    private readonly DalamudMarketPurchasePacketObserver marketPurchasePacketObserver;

    internal MarketIntelligencePassiveReporter IntelligenceReporter { get; }
    internal MarketAcquisitionWorldVisitCatalog WorldVisitCatalog { get; }
    internal MarketAcquisitionRequestWorkspace Workspace { get; }
    internal MarketBoardListingReader ListingReader { get; }
    internal MarketBoardAcquisitionController Controller { get; }
    internal ControlledMarketActorListingProbe ControlledListingProbe { get; }
    internal ControlledMarketActorBrowseProbe ControlledBrowseProbe { get; }
    internal ConfigurationExactAcquisitionRouteExecutionStateStore ExactStateStore { get; }
    internal MarketAcquisitionRouteEngine RouteEngine { get; }
    internal MarketBoardApproachService ApproachService { get; }
    internal string DiagnosticsDirectory { get; }
    internal RetainerAwareMarketAcquisitionInventoryObserver InventoryObserver { get; }

    internal MarketAcquisitionServices(
        Configuration config,
        IDataManager dataManager,
        IPlayerState playerState,
        InventoryScanner scanner,
        QuartermasterIpcClient quartermaster,
        MarketBoardApproachService approachService,
        IMarketBoardBrowseRuntime marketBoardBrowseRuntime,
        string diagnosticsDirectory,
        IPluginLog log)
    {
        this.config = config;
        this.playerState = playerState;
        this.scanner = scanner;
        this.log = log;
        ApproachService = approachService;
        DiagnosticsDirectory = diagnosticsDirectory;
        var acquisitionClient = new MarketAcquisitionRequestClient(acquisitionHttpClient);
        IntelligenceReporter = new MarketIntelligencePassiveReporter(
            config,
            acquisitionHttpClient,
            Plugin.PluginInterface.GetPluginConfigDirectory(),
            ex => log.Warning(ex, "[MarketMafioso] Market intelligence reporting requires attention."),
            message => log.Information("[MarketMafioso] {Message}", message),
            (exception, message) => log.Warning(exception, "[MarketMafioso] {Message}", message));
        var acquisitionPlanSource = new UniversalisMarketAcquisitionPlanSource(acquisitionHttpClient);
        WorldVisitCatalog = new MarketAcquisitionWorldVisitCatalog(config);
        InventoryObserver = new RetainerAwareMarketAcquisitionInventoryObserver(
            new DalamudMarketAcquisitionInventoryObserver(dataManager), quartermaster, GetCurrentQuartermasterOwnerScope, scanner.ResolveItemName);
        var marketAcquisitionPlanPreparationService = new MarketAcquisitionPlanPreparationService(
            acquisitionPlanSource,
            WorldVisitCatalog,
            InventoryObserver);
        Workspace = new MarketAcquisitionRequestWorkspace(
            config,
            acquisitionClient,
            marketAcquisitionPlanPreparationService,
            config.Save,
            ex => log.Warning(ex, "[MarketMafioso] Market acquisition request action failed."));
        var universalisFreshnessVerifier = new UniversalisMarketFreshnessVerifier(acquisitionHttpClient);
        ListingReader = new MarketBoardListingReader(marketBoardBrowseRuntime);
        var marketBoardItemSearchDriver = new MarketBoardItemSearchDriver(
            Plugin.GameGui,
            marketBoardBrowseRuntime);
        var marketBoardInputCaptureReader = new MarketBoardInputCaptureReader(Plugin.GameGui);
        var marketBoardPurchaseAdapter = new DalamudMarketBoardPurchaseAdapter(Plugin.GameGui, log);
        var marketBoardPurchaseExecutor = new MarketBoardPurchaseExecutor(marketBoardPurchaseAdapter);
        marketPurchasePacketObserver = new(Plugin.GameInteropProvider, log);
        var marketPurchaseEvidence = new MarketPurchaseEvidenceCoordinator(
            new MarketPurchaseEvidenceFileStore(Path.Combine(
                Plugin.PluginInterface.GetPluginConfigDirectory(),
                "market-purchase-evidence.json")),
            marketPurchasePacketObserver.Queue,
            () => Plugin.Framework.IsInFrameworkUpdateThread);
        Controller = new MarketBoardAcquisitionController(
            config,
            Plugin.MarketBoard,
            Plugin.ClientState,
            Plugin.ObjectTable,
            Plugin.Framework,
            Plugin.GameGui,
            Plugin.Condition,
            Plugin.ChatGui,
            Plugin.NotificationManager,
            Plugin.GameInteropProvider,
            Plugin.AddonLifecycle,
            log,
            scanner.ResolveItemName,
            (itemId, itemName, intent, previousOperationId) =>
                marketBoardItemSearchDriver.Search(
                    itemId,
                    itemName,
                    MarketBoardBrowseOwner.MarketListingAcquisition,
                    intent,
                    previousOperationId),
            marketBoardBrowseRuntime,
            Plugin.PluginInterface,
            Plugin.PluginInterface.GetPluginConfigDirectory(),
            ReportPassiveMarketObservation);
        ControlledListingProbe = new ControlledMarketActorListingProbe(
            config,
            Plugin.DataManager,
            Plugin.Condition,
            Plugin.Framework,
            Plugin.GameGui,
            log,
            Plugin.ObjectTable,
            Plugin.TargetManager,
            Plugin.SigScanner,
            Plugin.PluginInterface,
            Plugin.GameInventory);
        ControlledBrowseProbe = new ControlledMarketActorBrowseProbe(
            config,
            Plugin.DataManager,
            Plugin.CommandManager.ProcessCommand,
            ApproachService.IsMarketBoardReady,
            (itemId, itemName) => Controller.SearchItem(
                itemId,
                itemName,
                MarketBoardItemSearchIntent.RequireFreshBrowse),
            ListingReader.ReadCurrentListings);
        var routeUiAutomation = new DalamudMarketAcquisitionRouteUiAutomation(
            Controller.TryCloseMarketBoardForTravel);
        var lifestream = new LifestreamIpc(Plugin.PluginInterface, log);
        var shardCheckpointRuntime = new DalamudShardAcquisitionCheckpointRuntime(
            playerState,
            scanner,
            routeUiAutomation,
            new Franthropy.Dalamud.Travel.DalamudLifestreamPropertyTravel(Plugin.PluginInterface),
            lifestream);
        var shardCheckpoints = new ShardAcquisitionCheckpointCoordinator(
            quartermaster,
            shardCheckpointRuntime,
            new ConfigurationShardAcquisitionCheckpointStateStore(config, config.Save));
        var marketAcquisitionRouteRunner = new MarketAcquisitionRouteRunner(
            DiagnosticsDirectory,
            universalisFreshnessVerifier.VerifyAsync,
            () => new MarketAcquisitionRouteDiagnosticRetentionPolicy
            {
                EnableColdArchive = config.ArchiveCompletedMarketAcquisitionRouteDiagnostics,
                HotRetentionDays = config.MarketAcquisitionRouteDiagnosticsHotDays,
                HotRetentionRunCount = config.MarketAcquisitionRouteDiagnosticsHotRuns,
            },
            (exception, message) => Plugin.Log.Error(exception, "[MarketMafioso] {Message}", message));
        ExactStateStore = new ConfigurationExactAcquisitionRouteExecutionStateStore(
            config, () => config.SaveRequired("ExactAcquisitionRouteAuthority"));
        RouteEngine = new MarketAcquisitionRouteEngine(
            marketAcquisitionRouteRunner,
            new DalamudMarketAcquisitionRouteContext(playerState, lifestream),
            routeUiAutomation,
            new UnsupportedMarketAcquisitionRouteTravelCleanup(),
            new DalamudMarketAcquisitionMarketBoardIo(
                ApproachService,
                marketBoardItemSearchDriver,
                ListingReader,
                marketBoardInputCaptureReader,
                marketBoardBrowseRuntime),
            new DalamudMarketAcquisitionPurchaseIo(
                marketBoardPurchaseExecutor,
                marketBoardPurchaseAdapter,
                marketPurchaseEvidence,
                playerState),
            new MarketAcquisitionRouteRequestReporter(config, acquisitionClient),
            new MarketAcquisitionWorldVisitEvidenceRecorder(WorldVisitCatalog),
            Workspace.CreateClaimLifecycleController(
                () => marketAcquisitionRouteRunner.StatusMessage),
            new DalamudMarketAcquisitionRouteCallbackDispatcher(),
            new SystemMarketAcquisitionRouteClock(),
            ExactStateStore,
            new FileMarketAcquisitionReportOutbox(Path.Combine(
                Plugin.PluginInterface.GetPluginConfigDirectory(),
                "market-acquisition-report-outbox.json"),
                message => log.Information("[MarketMafioso] {Message}", message),
                (exception, message) => log.Warning(exception, "[MarketMafioso] {Message}", message)),
            shardCheckpoints,
            new FileMarketAcquisitionReportOutbox(Path.Combine(
                Plugin.PluginInterface.GetPluginConfigDirectory(),
                "market-acquisition-report-dead-letter.json"),
                message => log.Information("[MarketMafioso] {Message}", message),
                (exception, message) => log.Warning(exception, "[MarketMafioso] {Message}", message),
                deadLetterOnly: true),
            config.PluginInstanceId,
            PluginBuildInfo.DisplayVersion,
            () => config.MarketAcquisitionExhaustiveResearchMode,
            IntelligenceReporter,
            InventoryObserver,
            new MarketAcquisitionAethernetDeparture(
                new VNavmeshIpc(new DalamudVNavmeshIpcAdapter(Plugin.PluginInterface, log)),
                lifestream,
                () => config.MarketAcquisitionAethernetDeparture));
    }

    internal QuartermasterOwnerScope GetCurrentQuartermasterOwnerScope() =>
        new(
            playerState.ContentId == 0 ? null : playerState.ContentId,
            playerState.HomeWorld.IsValid ? playerState.HomeWorld.Value.RowId : null,
            playerState.CharacterName,
            playerState.HomeWorld.IsValid ? playerState.HomeWorld.Value.Name.ToString() : null);

    private void ReportPassiveMarketObservation(MarketBoardListingRevision revision)
    {
        if (string.IsNullOrWhiteSpace(revision.VerifiedBrowseOperationId) ||
            !playerState.CurrentWorld.IsValid)
            return;

        try
        {
            var worldName = playerState.CurrentWorld.Value.Name.ToString();
            var nativeRead = ListingReader.ReadCurrentListings(worldName);
            var nativeByListingId = nativeRead.IsFresh &&
                                    string.Equals(nativeRead.BrowseOperationId, revision.VerifiedBrowseOperationId, StringComparison.Ordinal)
                ? nativeRead.Listings.ToDictionary(listing => listing.ListingId, StringComparer.Ordinal)
                : new Dictionary<string, MarketBoardLiveListing>(StringComparer.Ordinal);
            IntelligenceReporter.Enqueue(new MarketEvidenceUploadRequest
            {
                SchemaVersion = 2,
                IdempotencyKey = $"{config.PluginInstanceId}:passive:{revision.VerifiedBrowseOperationId}",
                OccurrenceId = revision.VerifiedBrowseOperationId,
                SourceKind = MarketEvidenceSources.PassiveMarketBoard,
                SourceVersion = "3",
                SourceInstanceId = config.PluginInstanceId,
                SourceBuild = typeof(MarketAcquisitionServices).Assembly.GetName().Version?.ToString(),
                CaptureMode = "PassiveBrowse",
                ItemId = revision.Source.ItemId,
                ItemName = scanner.ResolveItemName(revision.Source.ItemId),
                DataCenter = MarketAcquisitionWorldCatalog.ResolveDataCenter(worldName),
                WorldName = worldName,
                ObservedAtUtc = DateTimeOffset.UtcNow,
                Coverage = revision.Listings.Count == 0
                    ? MarketEvidenceCoverage.Empty
                    : revision.IsComplete ? MarketEvidenceCoverage.Complete : MarketEvidenceCoverage.Partial,
                ReportedListingCount = revision.Source.ListingCount,
                ListingCapacity = 100,
                IsTruncated = !revision.IsComplete,
                Listings = revision.Listings.Select(listing =>
                {
                    nativeByListingId.TryGetValue(listing.ListingId.ToString(), out var native);
                    return new MarketEvidenceUploadListing
                    {
                        ListingId = listing.ListingId.ToString(),
                        RetainerId = listing.RetainerId.ToString(),
                        RetainerName = listing.RetainerName,
                        RetainerNameSource = string.IsNullOrWhiteSpace(listing.RetainerName) ? null : "DalamudMarketBoardPacket",
                        SellerOwnerContentId = null,
                        ArtisanContentId = native?.ArtisanContentId,
                        Quantity = listing.Quantity,
                        UnitPrice = listing.UnitPrice,
                        IsHq = listing.IsHighQuality,
                    };
                }).ToArray(),
            });
        }
        catch (Exception exception)
        {
            log.Warning(exception, "[MarketMafioso] Passive market evidence could not be queued.");
        }
    }

    public void Dispose()
    {
        Workspace.Dispose();
        RouteEngine.Dispose();
        WorldVisitCatalog.Dispose();
        Controller.Dispose();
        IntelligenceReporter.Dispose();
        ControlledListingProbe.Dispose();
        marketPurchasePacketObserver.Dispose();
        acquisitionHttpClient.Dispose();
    }
}
