using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.Game;
using MarketMafioso.AgentBridge;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Automation.MarketBoard;
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.Automation.Runtime;
using MarketMafioso.Automation.Travel;
using MarketMafioso.Contracts.MarketIntelligence;
using MarketMafioso.MarketAcquisition.ExactAuthority;
using MarketMafioso.MarketAcquisition.MarketBoard;
using MarketMafioso.MarketDiagnostics;
using MarketMafioso.ProfileLogin;
using MarketMafioso.Quartermaster;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.MarketAcquisition;

internal sealed class MarketAcquisitionServices : IDisposable
{
    private readonly Configuration config;
    internal TickTimings LastTickTimings { get; } = new(10);
    private readonly IPlayerState playerState;
    private readonly InventoryScanner scanner;
    private readonly IPluginLog log;
    private readonly HttpClient acquisitionHttpClient = new();
    private readonly DalamudMarketPurchasePacketObserver marketPurchasePacketObserver;

    private Task? exactAcquisitionRecoveryTask;
    private Task? exactAcquisitionAutoResumeTask;
    private DateTimeOffset nextExactAcquisitionAutoResumeAtUtc;
    private readonly Dictionary<ulong, string> marketActorResolvedNames = [];
    private int marketActorNameRequestedCount;
    private readonly Func<bool> otherAutomationActive;
    private Func<MarketAcquisitionRequestDocument> currentDocument =
        () => throw new InvalidOperationException("Acquisition request document provider is not connected.");

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
    internal MarketAcquisitionWorkbenchStockService WorkbenchStock { get; }
    internal MarketAcquisitionPlacementService Placement { get; }
    internal CrossRegionAcquisitionCoordinator CrossRegion { get; }
    private readonly MarketAcquisitionCrossRegionShoppingRoute crossRegionShopping;

    internal MarketAcquisitionServices(
        Configuration config,
        IDataManager dataManager,
        IPlayerState playerState,
        InventoryScanner scanner,
        QuartermasterIpcClient quartermaster,
        MarketBoardApproachService approachService,
        IMarketBoardBrowseRuntime marketBoardBrowseRuntime,
        string diagnosticsDirectory,
        Func<bool> otherAutomationActive,
        IPluginLog log,
        ICharacterLoginDriver? characterLoginDriver = null)
    {
        this.otherAutomationActive = otherAutomationActive;
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
        WorkbenchStock = new MarketAcquisitionWorkbenchStockService(
            quartermaster,
            new QuartermasterWorkbenchClient(new DalamudQuartermasterWorkbenchIpcAdapter(Plugin.PluginInterface)),
            InventoryObserver,
            GetCurrentQuartermasterOwnerScope,
            () => currentDocument(),
            message => log.Warning(message));
        Placement = new MarketAcquisitionPlacementService(
            quartermaster,
            new QuartermasterPlacementClient(new DalamudQuartermasterPlacementIpcAdapter(Plugin.PluginInterface)),
            GetCurrentQuartermasterOwnerScope,
            () => currentDocument(),
            itemId => dataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(itemId)?.CanBeHq ?? true,
            message => log.Warning(message));
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
        var crossRegionGame = new DalamudCrossRegionGameState(playerState, characterLoginDriver, () => otherAutomationActive() || RouteEngine.IsRouteActive);
        crossRegionShopping = new MarketAcquisitionCrossRegionShoppingRoute(
            RouteEngine,
            Workspace,
            InventoryObserver,
            routeUiAutomation,
            () => playerState.CurrentWorld.IsValid ? playerState.CurrentWorld.Value.Name.ToString() : null,
            GetMarketAcquisitionRecentWorldTtl,
            () => MarketAcquisitionRouteDiagnosticsPolicy.Resolve(config.MarketAcquisitionRouteDiagnostics),
            () => config.EnableOpportunisticWorldChecks,
            () => crossRegionGame.CarriedGil);
        CrossRegion = new CrossRegionAcquisitionCoordinator(
            quartermaster,
            crossRegionGame,
            crossRegionShopping,
            new FileCrossRegionAcquisitionStore(Plugin.PluginInterface.GetPluginConfigDirectory()),
            log: (message, exception) => log.Warning(exception, "[MarketMafioso] {Message}", message),
            custody: quartermaster,
            directDelivery: quartermaster);
    }

    internal void ConnectCurrentDocument(Func<MarketAcquisitionRequestDocument> provider) => currentDocument = provider;

    public void Tick()
    {
        LastTickTimings.Start();
        WorldVisitCatalog.Tick();
        LastTickTimings.Mark("worldVisitCatalog");

        if (!IsMarketAcquisitionUnlocked())
        {
            LastTickTimings.Mark("unlockCheck");
            return;
        }
        LastTickTimings.Mark("unlockCheck");

        WorkbenchStock.Tick();
        LastTickTimings.Mark("workbenchStock");
        Placement.Tick();
        LastTickTimings.Mark("placement");
        ControlledBrowseProbe.Tick();
        LastTickTimings.Mark("controlledBrowseProbe");

        _ = Workspace.RenewLeaseIfDueAsync();
        if (Workspace.ConsumeLeaseLossSignal())
        {
            RouteEngine.Stop();
            LastTickTimings.Mark("lease");
            return;
        }
        LastTickTimings.Mark("lease");

        RouteEngine.MonitorMarketBoardPurchase();
        LastTickTimings.Mark("MonitorMarketBoardPurchase");
        RouteEngine.TickRoute(Workspace.IsBusy);
        LastTickTimings.Mark("TickRoute");
        CrossRegion.Tick();
        LastTickTimings.Mark("crossRegion");
        MaybeAutoResumeExactAcquisitionRoute();
        LastTickTimings.Mark("autoResume");
        if (RouteEngine.NeedsExactAcquisitionRecovery && (exactAcquisitionRecoveryTask is null || exactAcquisitionRecoveryTask.IsCompleted))
            exactAcquisitionRecoveryTask = RecoverExactAcquisitionRouteAsync();
        LastTickTimings.Mark("recoveryKick");
    }

    private Task RecoverExactAcquisitionRouteAsync()
    {
        return Workspace.RunWithExecutionRequestAsync(currentDocument(), async (request, token) =>
        {
            if (RouteEngine.ConsumeNoViableExactAcquisitionDryRunScenario())
            {
                RouteEngine.PauseExactAcquisitionRecovery(
                    "Diagnostic no-viable recovery: no exact-quality row remains inside the confirmed caps. Retry or return to Advisor.");
                RouteEngine.ReportRouteProgress();
                return;
            }
            var remainingClaim = RouteEngine.CreateExactAcquisitionRecoveryClaim(request);
            var currentWorld = playerState.CurrentWorld.IsValid ? GetCurrentWorldName() : string.Empty;
            MarketAcquisitionPlanPreparationResult result;
            try
            {
                result = await Workspace.PrepareRecoveryPlanAsync(
                    remainingClaim,
                    currentWorld,
                    GetMarketAcquisitionRecentWorldTtl(),
                    token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                RouteEngine.PauseExactAcquisitionRecovery(
                    $"Recovery preparation failed safely: {exception.Message} Retry when market evidence is available or return to Advisor.");
                RouteEngine.ReportRouteProgress();
                return;
            }
            if (result.Plan.Status != "Ready" || result.Plan.WorldBatches.Count == 0)
            {
                RouteEngine.PauseExactAcquisitionRecovery(
                    "No viable exact-quality route remains inside the confirmed caps. Wait for listings or return to Advisor.");
                return;
            }

            var start = RouteEngine.StartExactAcquisitionRecovery(
                result.Plan,
                remainingClaim,
                currentDocument());
            if (start.Success)
                Workspace.ReplacePreparedPlan(result.Plan);
            else
                Workspace.SetStatus(start.Message);
            RouteEngine.ReportRouteProgress();
        });
    }

    private void MaybeAutoResumeExactAcquisitionRoute()
    {
        var routeActive = RouteEngine.IsRouteActive;
        var persisted = ExactStateStore.Restore();
        var contract = currentDocument().ExactAcquisitionAuthority?.FinalizedContract;
        if (ExactAcquisitionRouteRecoveryLifecycle.ClearOrphanedState(
                routeActive,
                persisted,
                contract,
                ExactStateStore))
            return;
        if (Workspace.IsBusy || DateTimeOffset.UtcNow < nextExactAcquisitionAutoResumeAtUtc ||
            exactAcquisitionAutoResumeTask is { IsCompleted: false })
            return;
        var live = RouteEngine.CreateSnapshot();
        if (live.ExactAcquisitionExecution?.Phase == ExactAcquisitionRouteAuthorityPhase.RecoveryNeeded &&
            !live.IsRunning && !live.IsPaused)
        {
            nextExactAcquisitionAutoResumeAtUtc = DateTimeOffset.UtcNow.AddSeconds(30);
            exactAcquisitionAutoResumeTask = RecoverExactAcquisitionRouteAsync();
            return;
        }
        if (routeActive)
            return;
        if (persisted is null || !ExactAcquisitionRouteRecoveryLifecycle.CanAutoResume(persisted))
            return;
        if (contract is null)
            return;
        if (contract.Transfer.DryRunOnly)
            return;
        nextExactAcquisitionAutoResumeAtUtc = DateTimeOffset.UtcNow.AddSeconds(30);
        exactAcquisitionAutoResumeTask = AutoResumeExactAcquisitionRouteAsync(persisted, contract);
    }

    private Task AutoResumeExactAcquisitionRouteAsync(
        ExactAcquisitionRouteExecutionState persisted,
        ExactAcquisitionExecutionContract contract)
    {
        return Workspace.RunWithExecutionRequestAsync(currentDocument(), async (request, token) =>
        {
            var remainingClaim = ExactAcquisitionRouteAuthoritySession.CreateRecoveryClaim(request, persisted);
            if (remainingClaim.Lines.Count == 0)
            {
                ExactStateStore.Save(persisted with
                {
                    Phase = ExactAcquisitionRouteAuthorityPhase.Complete,
                    Message = "Persisted external exact-acquisition purchases already satisfy the finalized solution.",
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                });
                return;
            }
            var currentWorld = playerState.CurrentWorld.IsValid ? GetCurrentWorldName() : string.Empty;
            MarketAcquisitionPlanPreparationResult result;
            try
            {
                result = await Workspace.PrepareRecoveryPlanAsync(
                    remainingClaim,
                    currentWorld,
                    GetMarketAcquisitionRecentWorldTtl(),
                    token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                ExactStateStore.Save(ExactAcquisitionRouteRecoveryLifecycle.PauseUnavailable(
                    persisted,
                    $"Recovery preparation failed safely: {exception.Message} Retry when market evidence is available or return to Advisor."));
                return;
            }
            if (result.Plan.Status != "Ready" || result.Plan.WorldBatches.Count == 0)
            {
                ExactStateStore.Save(ExactAcquisitionRouteRecoveryLifecycle.PauseUnavailable(
                    persisted,
                    "No viable exact-quality route remains inside the confirmed caps. Wait for listings, retry recovery, or return to Advisor."));
                return;
            }
            var start = RouteEngine.Start(
                result.Plan,
                remainingClaim,
                enableDiagnostics: false,
                includeOpportunisticChecks: false,
                contract,
                currentDocument());
            if (start.Success)
                Workspace.ReplacePreparedPlan(result.Plan);
            else
                Workspace.SetStatus(start.Message);
            RouteEngine.ReportRouteProgress();
        });
    }

    public string OpenMarketListings() => Controller.OpenMarketBoard();

    public string OpenMarketListing(uint itemId) => Controller.OpenMarketItem(itemId);

    public void AgentStopRoute()
    {
        RouteEngine.Stop();
        RouteEngine.ReportRouteProgress();
    }

    public unsafe AgentBridgeMarketActorCapabilityTruth ProbeMarketActorNames()
    {
        var read = ReadCurrentMarketActorBook();
        var cache = NameCache.Instance();
        if (cache != null)
        {
            foreach (var contentId in read.Listings
                         .SelectMany(listing => new[] { listing.ArtisanContentId, listing.SellerOwnerContentId })
                         .Where(contentId => contentId is > 0)
                         .Select(contentId => contentId!.Value)
                         .Distinct()
                         .Take(24))
            {
                marketActorNameRequestedCount++;
                var name = cache->GetNameByContentId(contentId).ToString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    marketActorResolvedNames[contentId] = name.Trim();
                    IntelligenceReporter.EnqueueActorName(
                        contentId,
                        name,
                        "GameNameCache.ControlledProbe",
                        DateTimeOffset.UtcNow);
                }
            }
        }
        return CreateMarketActorCapabilityTruth(read);
    }

    public AgentBridgeControlledMarketListingTruth BeginControlledMarketActorListing(string itemName) =>
        CreateControlledMarketActorListingTruth(ControlledListingProbe.BeginPost(itemName, IsControlledListingBlockedByAutomation()));

    public AgentBridgeControlledMarketListingTruth RemoveControlledMarketActorListing() =>
        CreateControlledMarketActorListingTruth(ControlledListingProbe.BeginRemoval(IsControlledListingBlockedByAutomation()));

    public AgentBridgeControlledMarketBrowseTruth BeginControlledMarketActorBrowse(string itemName) =>
        CreateControlledMarketActorBrowseTruth(ControlledBrowseProbe.Begin(
            itemName,
            playerState.CurrentWorld.IsValid ? playerState.CurrentWorld.Value.Name.ToString() : string.Empty,
            IsControlledListingBlockedByAutomation()));

    private bool IsControlledListingBlockedByAutomation() =>
        Workspace.IsBusy || RouteEngine.IsRouteActive || otherAutomationActive();

    private static AgentBridgeControlledMarketListingTruth CreateControlledMarketActorListingTruth(ControlledMarketActorListingProbeView source) => new()
    {
        State = source.State,
        Message = source.Message,
        Active = source.Active,
        ItemName = source.ItemName,
        ItemId = source.ItemId,
        RetainerName = source.RetainerName,
        Quantity = source.Quantity,
        IsHq = source.IsHq,
        UnitPrice = source.UnitPrice,
        ListingSlot = source.ListingSlot,
        UpdatedAtUtc = source.UpdatedAtUtc,
    };

    private static AgentBridgeControlledMarketBrowseTruth CreateControlledMarketActorBrowseTruth(ControlledMarketActorBrowseProbeView source) => new()
    {
        State = source.State,
        Message = source.Message,
        Active = source.Active,
        ItemName = source.ItemName,
        ItemId = source.ItemId,
        WorldName = source.WorldName,
        BrowseOperationId = source.BrowseOperationId,
        ListingCount = source.ListingCount,
        ArtisanObservedCount = source.ArtisanObservedCount,
        UpdatedAtUtc = source.UpdatedAtUtc,
    };

    private MarketBoardReadResult ReadCurrentMarketActorBook()
    {
        if (!playerState.CurrentWorld.IsValid)
            return new() { Status = "WorldUnavailable", Message = "Current world is unavailable.", ReadState = MarketBoardListingReadState.Unavailable };
        return ListingReader.ReadCurrentListings(playerState.CurrentWorld.Value.Name.ToString());
    }

    private AgentBridgeMarketActorCapabilityTruth CreateMarketActorCapabilityTruth(MarketBoardReadResult read)
    {
        var currentContentId = playerState.ContentId;
        var rows = read.Listings.Select(listing =>
        {
            marketActorResolvedNames.TryGetValue(listing.SellerOwnerContentId ?? 0, out var sellerName);
            marketActorResolvedNames.TryGetValue(listing.ArtisanContentId ?? 0, out var artisanName);
            return new AgentBridgeMarketActorListingTruth
            {
                ListingId = listing.ListingId,
                RetainerId = listing.RetainerId,
                RetainerName = string.IsNullOrWhiteSpace(listing.RetainerName) ? null : listing.RetainerName,
                SellerOwnerContentId = listing.SellerOwnerContentId,
                ArtisanContentId = listing.ArtisanContentId,
                SellerOwnerName = sellerName,
                ArtisanName = artisanName,
                SellerOwnerMatchesCurrentCharacter = currentContentId != 0 && listing.SellerOwnerContentId == currentContentId,
                ArtisanMatchesCurrentCharacter = currentContentId != 0 && listing.ArtisanContentId == currentContentId,
                IsSelfCraftedSale = listing.SellerOwnerContentId is > 0 && listing.SellerOwnerContentId == listing.ArtisanContentId,
                IsHq = listing.IsHq,
            };
        }).ToArray();
        return new()
        {
            ReadState = $"{read.Status}/{read.ReadState}",
            BrowseOperationId = string.IsNullOrWhiteSpace(read.BrowseOperationId) ? null : read.BrowseOperationId,
            ItemId = read.ItemId == 0 ? null : read.ItemId,
            WorldName = string.IsNullOrWhiteSpace(read.WorldName) ? null : read.WorldName,
            CurrentCharacterContentId = currentContentId,
            ListingCount = rows.Length,
            SellerOwnerObservedCount = rows.Count(row => row.SellerOwnerContentId is > 0),
            ArtisanObservedCount = rows.Count(row => row.ArtisanContentId is > 0),
            SelfCraftedCount = rows.Count(row => row.IsSelfCraftedSale),
            NameResolvedCount = rows
                .SelectMany(row => new[]
                {
                    (row.SellerOwnerContentId, row.SellerOwnerName),
                    (row.ArtisanContentId, row.ArtisanName),
                })
                .Where(actor => actor.Item1 is > 0 && !string.IsNullOrWhiteSpace(actor.Item2))
                .Select(actor => actor.Item1!.Value)
                .Distinct()
                .Count(),
            NameRequestedCount = marketActorNameRequestedCount,
            Listings = rows,
        };
    }

    internal TimeSpan GetMarketAcquisitionRecentWorldTtl()
    {
        var ttlHours = Math.Clamp(config.MarketAcquisitionRecentWorldTtlHours, 1, 168);
        if (ttlHours != config.MarketAcquisitionRecentWorldTtlHours)
        {
            config.MarketAcquisitionRecentWorldTtlHours = ttlHours;
            config.Save();
        }

        return TimeSpan.FromHours(ttlHours);
    }

    internal string GetCurrentWorldName()
    {
        if (!playerState.CurrentWorld.IsValid)
            throw new InvalidOperationException("Current world is unavailable.");

        return playerState.CurrentWorld.Value.Name.ToString();
    }

    internal bool IsMarketAcquisitionUnlocked() => MarketAcquisitionUnlock.IsUnlocked(config);

    internal AgentBridgeMarketActorCapabilityTruth GetMarketActorCapabilityTruth() =>
        CreateMarketActorCapabilityTruth(ReadCurrentMarketActorBook());

    internal AgentBridgeControlledMarketListingTruth GetControlledListingTruth() =>
        CreateControlledMarketActorListingTruth(ControlledListingProbe.Snapshot());

    internal AgentBridgeControlledMarketBrowseTruth GetControlledBrowseTruth() =>
        CreateControlledMarketActorBrowseTruth(ControlledBrowseProbe.Snapshot());

    internal WorkshopAssembler? GetCurrentWorkshopAssembler()
    {
        var owner = GetCurrentQuartermasterOwnerScope();
        return owner.IsAvailable &&
               !string.IsNullOrWhiteSpace(owner.CharacterName) &&
               !string.IsNullOrWhiteSpace(owner.HomeWorldName)
            ? new WorkshopAssembler(owner.CharacterName, owner.HomeWorldName)
            : null;
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
        WorkbenchStock.Dispose();
        Placement.Dispose();
        crossRegionShopping.Dispose();
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
