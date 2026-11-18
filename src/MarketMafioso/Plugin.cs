using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Franthropy.Dalamud.Automation.Retainers;
using Franthropy.Dalamud.Observations;
using Franthropy.Dalamud.Travel;
using Franthropy.Observations.V1;
using Dalamud.Interface.Windowing;
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.Automation.Runtime;
using MarketMafioso.Automation.Travel;
using MarketMafioso.AgentBridge;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.MarketDiagnostics;
using MarketMafioso.Quartermaster;
using MarketMafioso.ProfileLogin;
using MarketMafioso.WorkshopPrep;
using MarketMafioso.SquireIntegration;
using MarketMafioso.TradeQueue;
using MarketMafioso.Windows;

namespace MarketMafioso;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IAgentLifecycle AgentLifecycle { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ITextureReadbackProvider TextureReadbackProvider { get; private set; } = null!;
    [PluginService] internal static IGameInventory GameInventory { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] internal static IMarketBoard MarketBoard { get; private set; } = null!;
    [PluginService] internal static INotificationManager NotificationManager { get; private set; } = null!;
    [PluginService] internal static IDtrBar DtrBar { get; private set; } = null!;
    [PluginService] internal static IAetheryteList AetheryteList { get; private set; } = null!;
    [PluginService] internal static IContextMenu ContextMenu { get; private set; } = null!;

    internal static Plugin Instance { get; private set; } = null!;

    private const string CmdMain = "/mmf";
    private const string AcquisitionStep = "acquisition";
    private readonly TickTimings frameTimings = new(13);
    private long nextSlowFrameLog;
    private int suppressedSlowFrames;

    public Configuration Configuration { get; init; }

    private readonly InventoryScanner scanner;
    private readonly FranthropyRetainerReportSource retainerReports;
    private readonly HttpReporter reporter;
    private readonly InventoryDeliveryCoalescer inventoryDelivery;
    private readonly RetainerSaleChatObserver retainerSaleChatObserver;
    private readonly RetainerHistoryObserver retainerHistoryObserver;
    private readonly DalamudMarketBoardBrowseObserver marketBoardBrowseObserver;
    private readonly RetainerListingRefreshCoordinator retainerListingRefresh;
    private readonly MarketLockoutAttention marketLockoutAttention;
    private readonly RetainerListingRefreshReadinessGate retainerListingRefreshReadiness = new();
    private readonly FranthropyRetainerListingRefreshSource sharedObservationListings;
    private readonly DalamudSharedObservationClient sharedObservationClient;
    private readonly DalamudSharedObservationHost? sharedObservationHost;
    private readonly RemoteMarketAccessProbe remoteMarketAccessProbe;
    private readonly RemoteMarketProbeWindow remoteMarketProbeWindow;
    private readonly QuartermasterIpcClient quartermaster;
    private readonly StandaloneSquireIpcClient standaloneSquire;
    private readonly ExactAcquisitionIpcProvider exactAcquisitionIpc;
    private readonly WorkshopSavedJobIpcProvider workshopSavedJobIpc;

    /// <summary>Workshop Host delivery for split saved-job shares.</summary>
    public IWorkshopHandoffService WorkshopHandoffService { get; }
    internal MarketAcquisitionHostCheck AcquisitionHostCheck { get; }
    private readonly MarketAcquisitionItemContextMenu marketAcquisitionItemContextMenu;
    private readonly WorkshopProjectCatalog workshopCatalog;
    private readonly VIWIWorkshoppaIpc viwiWorkshoppaIpc;
    private readonly WorkshopAssemblyRunner workshopAssemblyRunner;
    private readonly WorkshopMaterialManifestExportService workshopMaterialManifestExport;
    private readonly ExternalAutomationCoordinator tradeAutomationCoordinator;
    private readonly DalamudTradeQueueIo tradeQueueIo;
    private readonly TradeAutoAcceptController tradeAutoAcceptController;
    private readonly TradeQueueRunner tradeQueueRunner;
    private readonly WindowSystem windowSystem = new("MarketMafioso");
    private readonly MainWindow mainWindow;
    private readonly MarketAcquisitionServices acquisitionServices;
    private readonly WorkshopHandoverTracker handoverTracker;
    private readonly RemoteSummoningBellProbe remoteSummoningBellProbe;
    private readonly WorkshopRestockService workshopRestock;
    private readonly AgentBridgeProofStore agentBridgeProofStore;
    private readonly AgentBridgeProofWindow agentBridgeProofWindow;
    private readonly AgentBridgeHost agentBridge;
    private readonly AgentBridgeViewportCaptureService agentBridgeViewportCapture;
    private readonly ProfileLoginCoordinator profileLoginCoordinator;
    private readonly ProfileDataCenterCoordinator profileDataCenterCoordinator;

    private CancellationTokenSource? timerCancellation;

    public Plugin()
    {
        Instance = this;
        ECommonsMain.ReducedLogging = true;
        ECommonsMain.Init(PluginInterface, this);

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.LoadRuntimeState(PluginInterface.GetPluginConfigDirectory());
        AcquisitionHostCheck = new(Configuration, Framework, Log);
        WorkshopHandoffService = new HostWorkshopHandoffService(Configuration, PlayerState, ClientState, Framework, Log);
        profileDataCenterCoordinator = new ProfileDataCenterCoordinator(
            PluginInterface.GetPluginConfigDirectory(), new DalamudDataCenterConnection(DataManager, ClientState, ObjectTable, GameGui));
        var characterLoginDriver = new DalamudCharacterLoginDriver(new DalamudLifestreamLogin(PluginInterface),
            () => DalamudCharacterLoginDriver.IsLifestreamLoaded(PluginInterface));
        profileLoginCoordinator = new ProfileLoginCoordinator(
            PluginInterface.GetPluginConfigDirectory(),
            characterLoginDriver,
            ObserveLoggedInCharacter,
            (message, exception) =>
            {
                if (exception is null) Log.Warning("[MarketMafioso] {Message}", message);
                else Log.Error(exception, "[MarketMafioso] {Message}", message);
            });
        Legacy.LegacyRetainerMigrationSource.Preserve(
            Configuration,
            Path.Combine(PluginInterface.GetPluginConfigDirectory(), "retainer-cache.json"));
        scanner = new InventoryScanner(DataManager, Log);
        quartermaster = new QuartermasterIpcClient(new DalamudQuartermasterIpcAdapter(PluginInterface));
        sharedObservationClient = new DalamudSharedObservationClient(new DalamudSharedObservationClientOptions
        {
            PluginConfigDirectory = PluginInterface.GetPluginConfigDirectory(),
            CurrentOwner = () => PlayerState.ContentId == 0 || !PlayerState.HomeWorld.IsValid
                ? null
                : new ObservationOwner(PlayerState.ContentId, PlayerState.HomeWorld.Value.RowId),
            Diagnostic = (message, exception) =>
            {
                if (exception is null)
                    Log.Warning("[MarketMafioso] {Message}", message);
                else
                    Log.Error(exception, "[MarketMafioso] {Message}", message);
            },
        });
        retainerReports = new FranthropyRetainerReportSource(sharedObservationClient);
        standaloneSquire = new StandaloneSquireIpcClient(new DalamudStandaloneSquireIpcAdapter(PluginInterface));
        reporter = new HttpReporter(
            Configuration,
            PlayerState,
            Log,
            ChatGui,
            scanner,
            retainerReports,
            quartermaster);
        inventoryDelivery = new InventoryDeliveryCoalescer(
            TimeSpan.FromMilliseconds(500),
            token => Framework.RunOnTick(
                () => reporter.SendDeltaReportAsync(quiet: true),
                cancellationToken: token),
            (message, exception) =>
            {
                if (exception is null)
                    Log.Information("[MarketMafioso] {Message}", message);
                else
                    Log.Warning(exception, "[MarketMafioso] {Message}", message);
            });
        retainerSaleChatObserver = new RetainerSaleChatObserver(
            Configuration,
            PlayerState,
            DataManager,
            ChatGui,
            Log,
            Path.Combine(PluginInterface.GetPluginConfigDirectory(), "market-sale-outbox.json"));
        retainerHistoryObserver = new RetainerHistoryObserver(
            Configuration,
            AddonLifecycle,
            GameGui,
            PlayerState,
            DataManager,
            Log,
            retainerSaleChatObserver.EnqueueExternal);
        marketBoardBrowseObserver = new DalamudMarketBoardBrowseObserver(
            GameInteropProvider,
            Framework,
            GameGui,
            ClientState,
            Configuration.MarketBoardSessionCircuitBreaker,
            Configuration.Save,
            () => PlayerState.CurrentWorld.IsValid ? PlayerState.CurrentWorld.Value.Name.ToString() : null,
            Log);
        sharedObservationListings = new FranthropyRetainerListingRefreshSource(
            sharedObservationClient,
            PlayerState);
        retainerListingRefresh = new RetainerListingRefreshCoordinator(
            Configuration,
            sharedObservationListings,
            marketBoardBrowseObserver,
            Configuration.Save,
            message => ChatGui.PrintError($"[MMF] {message}"),
            message => Log.Information("[MarketMafioso] {Message}", message));
        sharedObservationListings.Changed += retainerListingRefresh.NotifyListingCaptureChanged;
        marketLockoutAttention = new MarketLockoutAttention(
            DtrBar,
            NotificationManager,
            marketBoardBrowseObserver,
            retainerListingRefresh,
            characterLoginDriver,
            SampleRelogGameState,
            ResolveItemName,
            () => mainWindow?.OpenRetainerListingRefreshSettings(),
            message => Log.Information("[MarketMafioso] {Message}", message));
        workshopCatalog = new WorkshopProjectCatalog(DataManager, Log);
        remoteMarketAccessProbe = new RemoteMarketAccessProbe(
            Configuration,
            MarketBoard,
            ClientState,
            ObjectTable,
            Framework,
            AddonLifecycle,
            GameGui,
            ChatGui,
            Log,
            marketBoardBrowseObserver,
            PluginInterface.GetPluginConfigDirectory());
        remoteMarketProbeWindow = new RemoteMarketProbeWindow(remoteMarketAccessProbe);
        viwiWorkshoppaIpc = new VIWIWorkshoppaIpc(new DalamudVIWIWorkshoppaIpcAdapter(PluginInterface, Log));
        workshopAssemblyRunner = new WorkshopAssemblyRunner(
            Framework,
            Log,
            new WorkshopAssemblyUiAutomation(
                GameGui,
                Log,
                ObjectTable,
                TargetManager,
                Condition,
                SigScanner,
                AgentLifecycle,
                new ExternalAutomationCoordinator(
                    new DalamudPluginDataStore(PluginInterface),
                    Log,
                    new DalamudPandoraFeatureControl(PluginInterface),
                    new DalamudTextAdvanceExternalControl(PluginInterface)),
                new WorkshopNativeFaultMonitor(AgentLifecycle, Log, ResolveDalamudLogPath()),
                GameInteropProvider, DataManager),
            Path.Combine(PluginInterface.GetPluginConfigDirectory(), "workshop-assembly-logs"),
            entry =>
            {
                var result = WorkshopQueueService.DecrementActiveQueue(Configuration, entry.WorkshopItemId);
                if (!result.Success)
                    Log.Warning("[MarketMafioso] {Message}", result.Message);

                Configuration.Save();
            },
            getAssemblyOrder: () => Configuration.WorkshopAssemblyOrder);
        var workshopCraftRecipeResolver = new LuminaWorkshopMaterialCraftRecipeResolver(DataManager);
        workshopMaterialManifestExport = new WorkshopMaterialManifestExportService(
            workshopCraftRecipeResolver);
        tradeAutomationCoordinator = new ExternalAutomationCoordinator(
            new DalamudPluginDataStore(PluginInterface),
            Log);
        tradeQueueIo = new DalamudTradeQueueIo(
            GameGui,
            TargetManager,
            ObjectTable,
            Condition,
            SigScanner,
            DataManager,
            Log);
        tradeAutoAcceptController = new TradeAutoAcceptController(
            tradeQueueIo,
            Configuration.TradeQueueTiming,
            Log);
        tradeQueueRunner = new TradeQueueRunner(
            Configuration.TradeQueueItems,
            Configuration.TradeQueueTiming,
            () => Configuration.SaveRequired("TradeQueueRunner.Checkpoint"),
            tradeQueueIo,
            new Franthropy.Dalamud.Automation.Inventory.DalamudItemQualityLoweringAutomation(
                GameGui,
                DalamudTradeQueueIo.SupportedInventories),
            tradeAutomationCoordinator,
            Log,
            Configuration.TradeQueuePolicy,
            Configuration.TradeQueueRecovery ??= new());
        workshopRestock = new WorkshopRestockService(Configuration, quartermaster, workshopCatalog, DataManager, PlayerState, Log);
        remoteSummoningBellProbe = new RemoteSummoningBellProbe(
            Configuration,
            ClientState,
            ObjectTable,
            TargetManager,
            DataManager,
            GameInteropProvider,
            SigScanner,
            Framework,
            GameGui,
            Condition,
            KeyState,
            ChatGui,
            Log,
            PluginInterface,
            PluginInterface.GetPluginConfigDirectory());
        acquisitionServices = new MarketAcquisitionServices(
            Configuration,
            DataManager,
            PlayerState,
            scanner,
            quartermaster,
            new MarketBoardApproachService(
                GameGui,
                ObjectTable,
                TargetManager,
                new VNavmeshIpc(new DalamudVNavmeshIpcAdapter(PluginInterface, Log)),
                Log),
            marketBoardBrowseObserver,
            Path.Combine(PluginInterface.GetPluginConfigDirectory(), "market-acquisition-route-logs"),
            () => tradeQueueRunner.Snapshot.IsActive || workshopAssemblyRunner.IsRunning,
            Log);
        handoverTracker = new WorkshopHandoverTracker(
            Configuration,
            () => WorkshopHandoffService,
            share => workshopCatalog.BuildRequirements(share.Items).Select(material => material.ItemId).ToHashSet(),
            acquisitionServices.GetCurrentWorkshopAssembler,
            () => tradeQueueRunner.IsActive,
            Log);
        tradeQueueRunner.RecipientGuard = handoverTracker.CheckRecipient;
        tradeQueueRunner.BatchStarting += handoverTracker.OnBatchStarting;
        tradeQueueRunner.BatchLocking += handoverTracker.OnBatchLocking;
        tradeQueueRunner.BatchAbandoned += handoverTracker.OnBatchAbandoned;
        tradeQueueRunner.BatchVerified += handoverTracker.OnBatchVerified;
        mainWindow = new MainWindow(
            Configuration,
            reporter,
            scanner,
            retainerReports,
            quartermaster,
            standaloneSquire,
            workshopCatalog,
            viwiWorkshoppaIpc,
            workshopAssemblyRunner,
            workshopMaterialManifestExport,
            workshopCraftRecipeResolver,
            tradeQueueRunner,
            handoverTracker,
            tradeQueueIo,
            DataManager,
            PlayerState,
            acquisitionServices,
            marketLockoutAttention,
            Log,
            workshopRestock,
            remoteSummoningBellProbe);
        exactAcquisitionIpc = new ExactAcquisitionIpcProvider(PluginInterface, mainWindow.StageExternalExactAcquisition);
        workshopSavedJobIpc = new WorkshopSavedJobIpcProvider(PluginInterface, Framework, mainWindow.ImportWorkshopSavedJob);
        marketAcquisitionItemContextMenu = new MarketAcquisitionItemContextMenu(
            ContextMenu,
            GameGui,
            DataManager,
            Framework,
            ChatGui,
            () => MarketAcquisitionUnlock.IsUnlocked(Configuration),
            mainWindow.CanStageContextMenuItemToWorkbench,
            mainWindow.StageContextMenuItemToWorkbench);

        mainWindow.AcquisitionFeedbackTransition += OnAcquisitionFeedbackTransition;
        agentBridgeProofStore = new AgentBridgeProofStore();
        agentBridgeProofWindow = new AgentBridgeProofWindow(agentBridgeProofStore);
        agentBridgeViewportCapture = new AgentBridgeViewportCaptureService(
            PluginInterface.GetPluginConfigDirectory(),
            Configuration.PluginInstanceId,
            () => mainWindow.AgentCaptureRegion,
            action => Framework.RunOnTick(action),
            TextureProvider,
            TextureReadbackProvider);
        agentBridge = new AgentBridgeHost(
            Configuration,
            PluginInterface.GetPluginConfigDirectory(),
            PluginInterface.AssemblyLocation.FullName,
            action => Framework.RunOnTick(action),
            new MarketMafiosoBridgeProvider(
                mainWindow.CreateAgentBridgeTruth,
                mainWindow.AgentOpenForReview,
                mainWindow.AgentCloseAfterReview,
                () => mainWindow.TrySelectAgentBridgeTab("Diagnostics"),
                agentBridgeProofWindow.OpenAndFocus,
                mainWindow.TrySelectAgentBridgeTab,
                mainWindow.AgentCaptureInputState,
                acquisitionServices.AgentStopRoute,
                acquisitionServices.ProbeMarketActorNames,
                acquisitionServices.BeginControlledMarketActorListing,
                acquisitionServices.RemoveControlledMarketActorListing,
                acquisitionServices.BeginControlledMarketActorBrowse,
                () => MarketAcquisitionUnlock.IsUnlocked(Configuration),
                mainWindow.AgentReviewRegistry),
            agentBridgeProofStore,
            agentBridgeViewportCapture.CaptureAsync,
            () => Configuration.EnableAgentBridgeScreenshots,
            mainWindow.AgentCaptureTransactions.Begin,
            mainWindow.AgentCaptureTransactions.Complete,
            mainWindow.AgentCaptureTransactions.Cancel,
            PluginInterface,
            CommandManager,
            Framework);

        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(mainWindow.ProjectBrowser);
        windowSystem.AddWindow(mainWindow.FrozenQueueBrowser);
        windowSystem.AddWindow(mainWindow.AcquisitionCompositionWindow);
        windowSystem.AddWindow(agentBridgeProofWindow);
        windowSystem.AddWindow(remoteMarketProbeWindow);
        windowSystem.AddWindow(mainWindow.MarketListingOverlay);

        CommandManager.AddHandler(CmdMain, new CommandInfo(OnCommand)
        {
            HelpMessage =
                "Open the MarketMafioso toolbox window. " +
                "Use \"/mmf send\" to send an inventory report immediately, or " +
                "\"/mmf capture-bell\" to arm the passive normal-bell flight recorder. " +
                "Use \"/mmf capture-bell-lifecycle\" for the complete open/select/return/close trace. " +
                "Yield tests: \"/mmf probe-bell-yield-control\" then \"/mmf probe-bell-yield-direct\". " +
                "Native signature tests: \"/mmf probe-bell-native-call\" or \"/mmf probe-bell-native-select\". " +
                "Retainer RPC test: \"/mmf probe-retainer-rpc-control\" then \"/mmf probe-retainer-rpc-bind-test\". " +
                "Warm retention: \"/mmf probe-bell-warm\", \"/mmf probe-bell-warm-delay <seconds>\", " +
                "\"/mmf probe-bell-warm-move <yalms>\", \"/mmf probe-bell-warm-unlock-move <yalms>\", " +
                "or \"/mmf probe-bell-warm-manual\". " +
                "Scene-2 oddballs: \"/mmf probe-bell-scene2-ui\" and \"/mmf probe-bell-scene2-move <yalms>\". " +
                "Use \"/mmf probe-bell-warm-ui\" only for the old manual select/Quit bootstrap.",
        });

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += OpenConfigUi;
        Framework.Update += OnFrameworkUpdate;
        quartermaster.Changed += OnQuartermasterChanged;
        sharedObservationClient.RetainersChanged += OnSharedRetainersChanged;
        GameInventory.InventoryChanged += OnInventoryChanged;

        StartTimer();
        sharedObservationClient.Start();
        AcquisitionHostCheck.Start();
        Log.Information("[MarketMafioso] Plugin loaded. Use /mmf to open settings.");

        try
        {
            sharedObservationHost = new DalamudSharedObservationHost(new DalamudSharedObservationHostOptions
            {
                PluginConfigDirectory = PluginInterface.GetPluginConfigDirectory(),
                PluginName = "MarketMafioso",
                PluginInstanceId = Guid.NewGuid().ToString("N"),
                GameBuild = Franthropy.Dalamud.Diagnostics.GameClientVersion.ReadCurrentGameVersion(),
                GameInventory = GameInventory,
                PlayerState = PlayerState,
                AddonLifecycle = AddonLifecycle,
                Framework = Framework,
                InteropProvider = GameInteropProvider,
                SigScanner = SigScanner,
                PluginLog = Log,
                Diagnostic = (message, exception) =>
                {
                    if (exception is null)
                        Log.Warning("[MarketMafioso] {Message}", message);
                    else
                        Log.Error(exception, "[MarketMafioso] {Message}", message);
                },
            });
            sharedObservationHost.Start();
        }
        catch (Exception exception)
        {
            sharedObservationHost?.Dispose();
            sharedObservationHost = null;
            Log.Error(exception, "[MarketMafioso] Shared observation hosting is unavailable.");
        }
    }

    private void OnCommand(string command, string args)
    {
        var commandParts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var commandName = commandParts.Length == 0
            ? string.Empty
            : commandParts[0].ToLowerInvariant();
        var commandArgument = commandParts.Length < 2
            ? string.Empty
            : commandParts[1].Trim();
        switch (commandName)
        {
            case "send":
                Framework.RunOnTick(() => _ = reporter.SendReportAsync());
                break;

            case "market":
#if DEBUG
                if (!string.IsNullOrWhiteSpace(commandArgument))
                {
                    var itemName = commandArgument.Trim().Trim('"');
                    var matches = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>()
                        .Where(item => string.Equals(item.Name.ToString(), itemName, StringComparison.OrdinalIgnoreCase))
                        .Take(2)
                        .ToArray();
                    if (matches.Length != 1)
                    {
                        ChatGui.PrintError(matches.Length == 0
                            ? $"[MMF] No exact market item named \"{itemName}\" was found."
                            : $"[MMF] More than one market item is named \"{itemName}\".");
                        break;
                    }

                    ChatGui.Print($"[MMF] Market listings: {acquisitionServices.OpenMarketListing(matches[0].RowId)}");
                    break;
                }
#endif
                ChatGui.Print($"[MMF] Market listings: {acquisitionServices.OpenMarketListings()}");
                break;

            case "probe-market":
#if DEBUG
            {
                var probeMessage = remoteMarketAccessProbe.BeginProbe();
                remoteMarketProbeWindow.IsOpen = true;
                ChatGui.Print($"[MMF] {probeMessage}");
                break;
            }
#else
                ChatGui.Print("[MMF] Remote market probe is only available in debug builds.");
                break;
#endif

            case "probe-bell":
#if DEBUG
            {
                var probeMessage = remoteSummoningBellProbe.BeginProbe();
                ChatGui.Print($"[MMF] Remote bell probe: {probeMessage}");
                break;
            }
#else
                ChatGui.Print("[MMF] Remote bell probe is only available in debug builds.");
                break;
#endif

            case "capture-bell":
#if DEBUG
                ChatGui.Print($"[MMF] Normal bell capture: {remoteSummoningBellProbe.BeginNormalCapture()}");
                break;
#else
                ChatGui.Print("[MMF] Normal bell capture is only available in debug builds.");
                break;
#endif

            case "capture-bell-lifecycle":
#if DEBUG
                ChatGui.Print($"[MMF] Bell lifecycle capture: {remoteSummoningBellProbe.BeginNormalLifecycleCapture()}");
                break;
#else
                ChatGui.Print("[MMF] Bell lifecycle capture is only available in debug builds.");
                break;
#endif

            case "capture-bell-status":
#if DEBUG
                ChatGui.Print($"[MMF] Normal bell capture: {remoteSummoningBellProbe.GetNormalCaptureStatus()}");
                break;
#else
                ChatGui.Print("[MMF] Normal bell capture is only available in debug builds.");
                break;
#endif

            case "capture-bell-cancel":
#if DEBUG
                ChatGui.Print($"[MMF] Normal bell capture: {remoteSummoningBellProbe.CancelNormalCapture()}");
                break;
#else
                ChatGui.Print("[MMF] Normal bell capture is only available in debug builds.");
                break;
#endif

            case "probe-bell-yield-control":
#if DEBUG
                ChatGui.Print($"[MMF] YieldEventScene2 control: {remoteSummoningBellProbe.BeginYieldControl()}");
                break;
#else
                ChatGui.Print("[MMF] YieldEventScene2 probes are only available in debug builds.");
                break;
#endif

            case "probe-bell-yield-direct":
#if DEBUG
                ChatGui.Print($"[MMF] YieldEventScene2 direct probe: {remoteSummoningBellProbe.BeginYieldSessionFreeReplay()}");
                break;
#else
                ChatGui.Print("[MMF] YieldEventScene2 probes are only available in debug builds.");
                break;
#endif

            case "probe-bell-yield-status":
#if DEBUG
                ChatGui.Print($"[MMF] YieldEventScene2 probe: {remoteSummoningBellProbe.GetYieldProbeStatus()}");
                break;
#else
                ChatGui.Print("[MMF] YieldEventScene2 probes are only available in debug builds.");
                break;
#endif

            case "probe-bell-native-call":
#if DEBUG
                ChatGui.Print($"[MMF] Native CallRetainer probe: {remoteSummoningBellProbe.BeginNativeRetainerVerb(NativeRetainerVerb.CallRetainer)}");
                break;
#else
                ChatGui.Print("[MMF] Native retainer-verb probes are only available in debug builds.");
                break;
#endif

            case "probe-bell-native-select":
#if DEBUG
                ChatGui.Print($"[MMF] Native SelectRetainer probe: {remoteSummoningBellProbe.BeginNativeRetainerVerb(NativeRetainerVerb.SelectRetainer)}");
                break;
#else
                ChatGui.Print("[MMF] Native retainer-verb probes are only available in debug builds.");
                break;
#endif

            case "probe-bell-yield-cancel":
#if DEBUG
                ChatGui.Print($"[MMF] YieldEventScene2 probe: {remoteSummoningBellProbe.CancelYieldProbe()}");
                break;
#else
                ChatGui.Print("[MMF] YieldEventScene2 probes are only available in debug builds.");
                break;
#endif

            case "probe-retainer-rpc-control":
#if DEBUG
                ChatGui.Print($"[MMF] Retainer RPC control: {remoteSummoningBellProbe.BeginRetainerRpcControl()}");
                break;
#else
                ChatGui.Print("[MMF] Retainer RPC probes are only available in debug builds.");
                break;
#endif

            case "probe-retainer-rpc-bind-test":
#if DEBUG
                ChatGui.Print($"[MMF] Retainer RPC bind test: {remoteSummoningBellProbe.BeginRetainerRpcBindTest()}");
                break;
#else
                ChatGui.Print("[MMF] Retainer RPC probes are only available in debug builds.");
                break;
#endif

            case "probe-retainer-rpc-status":
#if DEBUG
                ChatGui.Print($"[MMF] Retainer RPC probe: {remoteSummoningBellProbe.GetRetainerRpcProbeStatus()}");
                break;
#else
                ChatGui.Print("[MMF] Retainer RPC probes are only available in debug builds.");
                break;
#endif

            case "probe-retainer-rpc-cancel":
#if DEBUG
                ChatGui.Print($"[MMF] Retainer RPC probe: {remoteSummoningBellProbe.CancelRetainerRpcProbe()}");
                break;
#else
                ChatGui.Print("[MMF] Retainer RPC probes are only available in debug builds.");
                break;
#endif

            case "probe-bell-warm":
#if DEBUG
                ChatGui.Print($"[MMF] Warm-session retention: {remoteSummoningBellProbe.BeginWarmSessionRetentionProbe()}");
                break;
#else
                ChatGui.Print("[MMF] Warm-session retention is only available in debug builds.");
                break;
#endif

            case "probe-bell-warm-delay":
#if DEBUG
                if (!int.TryParse(commandArgument, out var delaySeconds))
                {
                    ChatGui.PrintError("[MMF] Usage: /mmf probe-bell-warm-delay <seconds>, from 1 through 300.");
                    break;
                }
                ChatGui.Print(
                    $"[MMF] Warm-session retention: " +
                    remoteSummoningBellProbe.BeginDelayedWarmSessionRetentionProbe(TimeSpan.FromSeconds(delaySeconds)));
                break;
#else
                ChatGui.Print("[MMF] Warm-session retention is only available in debug builds.");
                break;
#endif

            case "probe-bell-warm-move":
#if DEBUG
                if (!int.TryParse(commandArgument, out var movementYalms))
                {
                    ChatGui.PrintError("[MMF] Usage: /mmf probe-bell-warm-move <yalms>, from 1 through 100.");
                    break;
                }
                ChatGui.Print(
                    $"[MMF] Warm-session retention: " +
                    remoteSummoningBellProbe.BeginDistanceWarmSessionRetentionProbe(movementYalms));
                break;
#else
                ChatGui.Print("[MMF] Warm-session retention is only available in debug builds.");
                break;
#endif

            case "probe-bell-warm-unlock-move":
#if DEBUG
                if (!int.TryParse(commandArgument, out var unlockedMovementYalms))
                {
                    ChatGui.PrintError("[MMF] Usage: /mmf probe-bell-warm-unlock-move <yalms>, from 1 through 100.");
                    break;
                }
                ChatGui.Print(
                    $"[MMF] Warm-session retention: " +
                    remoteSummoningBellProbe.BeginLocallyUnlockedDistanceWarmSessionRetentionProbe(unlockedMovementYalms));
                break;
#else
                ChatGui.Print("[MMF] Warm-session retention is only available in debug builds.");
                break;
#endif

            case "probe-bell-warm-manual":
#if DEBUG
                ChatGui.Print($"[MMF] Warm-session retention: {remoteSummoningBellProbe.BeginManualWarmSessionRetentionProbe()}");
                break;
#else
                ChatGui.Print("[MMF] Warm-session retention is only available in debug builds.");
                break;
#endif

            case "probe-bell-scene2-ui":
#if DEBUG
                ChatGui.Print($"[MMF] Scene-2 UI resurrection: {remoteSummoningBellProbe.BeginScene2UiResurrectionProbe()}");
                break;
#else
                ChatGui.Print("[MMF] Scene-2 probes are only available in debug builds.");
                break;
#endif

            case "probe-bell-scene2-move":
#if DEBUG
                if (!int.TryParse(commandArgument, out var scene2MovementYalms))
                {
                    ChatGui.PrintError("[MMF] Usage: /mmf probe-bell-scene2-move <yalms>, from 1 through 100.");
                    break;
                }
                ChatGui.Print(
                    $"[MMF] Scene-2 distance continuation: " +
                    remoteSummoningBellProbe.BeginScene2DistanceContinuationProbe(scene2MovementYalms));
                break;
#else
                ChatGui.Print("[MMF] Scene-2 probes are only available in debug builds.");
                break;
#endif

            case "probe-bell-warm-ui":
#if DEBUG
                ChatGui.Print($"[MMF] Warm-session retention: {remoteSummoningBellProbe.BeginManualUiWarmSessionRetentionProbe()}");
                break;
#else
                ChatGui.Print("[MMF] Warm-session retention is only available in debug builds.");
                break;
#endif

            case "probe-bell-warm-replay":
#if DEBUG
                ChatGui.Print($"[MMF] Warm-session retention: {remoteSummoningBellProbe.ReplayHeldWarmSession()}");
                break;
#else
                ChatGui.Print("[MMF] Warm-session retention is only available in debug builds.");
                break;
#endif

            case "probe-bell-warm-status":
#if DEBUG
                ChatGui.Print($"[MMF] Warm-session retention: {remoteSummoningBellProbe.GetWarmSessionProbeStatus()}");
                break;
#else
                ChatGui.Print("[MMF] Warm-session retention is only available in debug builds.");
                break;
#endif

            case "probe-bell-warm-cancel":
#if DEBUG
                ChatGui.Print($"[MMF] Warm-session retention: {remoteSummoningBellProbe.CancelWarmSessionProbe()}");
                break;
#else
                ChatGui.Print("[MMF] Warm-session retention is only available in debug builds.");
                break;
#endif

            default:
                mainWindow.IsOpen = !mainWindow.IsOpen;
                break;
        }
    }

    private void OnAcquisitionFeedbackTransition(MarketAcquisitionRouteEngineSnapshot snapshot)
    {
        if (!Configuration.MarketAcquisitionRouteAlerts)
            return;
        var completed = snapshot.RouteState == "Completed" && snapshot.RecoveryBlockedReason == null &&
                        snapshot.ExactAcquisitionExecution?.Phase is not
                            (MarketAcquisition.ExactAuthority.ExactAcquisitionRouteAuthorityPhase.RecoveryNeeded or
                             MarketAcquisition.ExactAuthority.ExactAcquisitionRouteAuthorityPhase.Paused);
        var outcome = snapshot.CompletionOutcome;
        // Completion kinds are extensible (budget, inventory, etc.). Do not
        // whitelist kinds and accidentally suppress a newer terminal outcome.
        var detail = outcome?.GetType().GetProperty("Detail")?.GetValue(outcome)?.ToString();
        var reason = completed
            ? $"{outcome?.Kind ?? snapshot.RouteState}: {detail ?? snapshot.StatusMessage}"
            : snapshot.RecoveryBlockedReason ?? (snapshot.RouteState == "Failed" ? snapshot.StatusMessage :
                snapshot.ExactAcquisitionExecution?.Message ?? snapshot.VisibleAcquisitionStatus);
        var message = $"Acquisition {(completed ? "finished" : "needs attention")}: {snapshot.ConfirmedPurchaseQuantity:N0} items bought, {snapshot.ConfirmedSpendGil:N0} gil, {snapshot.ConfirmedPurchaseWorldCount:N0} worlds; {reason}";
        message = message.Replace('\r', ' ').Replace('\n', ' ');
        ChatGui.Print($"[MMF] {message}");
        NotificationManager.AddNotification(new Dalamud.Interface.ImGuiNotification.Notification
        {
            Title = "MMF Acquisition Route",
            Content = message,
            Type = completed && outcome?.TargetRemainingQuantity == 0
                ? Dalamud.Interface.ImGuiNotification.NotificationType.Success
                : Dalamud.Interface.ImGuiNotification.NotificationType.Warning,
            InitialDuration = TimeSpan.FromSeconds(12),
            Minimized = false,
        });
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var start = frameTimings.Start();
        Configuration.RetryPendingSave();
        frameTimings.Mark("retryPendingSave");
        profileDataCenterCoordinator.Tick();
        frameTimings.Mark("profileDataCenter");
        if (!profileDataCenterCoordinator.OwnsLobby) profileLoginCoordinator.Tick();
        frameTimings.Mark("profileLogin");
        retainerSaleChatObserver.Tick();
        frameTimings.Mark("retainerSaleChat");
        retainerHistoryObserver.Tick();
        frameTimings.Mark("retainerHistory");
        var nowUtc = DateTimeOffset.UtcNow;
        var retainerSessionActive = IsRetainerSessionActive();
        var (immediatelyReady, immediateDeferredReason) = GetImmediateRetainerListingRefreshReadiness(retainerSessionActive);
        var (refreshReady, refreshDeferredReason) = retainerListingRefreshReadiness.Observe(
            nowUtc,
            immediatelyReady,
            immediateDeferredReason);
        retainerListingRefresh.Tick(
            nowUtc,
            refreshReady,
            refreshDeferredReason);
        frameTimings.Mark("retainerListingRefresh");
        marketLockoutAttention.Tick(nowUtc);
        frameTimings.Mark("marketLockoutAttention");
        mainWindow.MarketListingOverlay.SynchronizePresentationLifetime();
        frameTimings.Mark("listingOverlay");
        if (Configuration.AutoAcceptIncomingTrades || tradeQueueRunner.IsActive)
            tradeAutomationCoordinator.SuppressDropboxAutoAccept();
        else
            tradeAutomationCoordinator.RestoreDropboxAutoAccept();
        tradeAutoAcceptController.Tick(
            Configuration.AutoAcceptIncomingTrades && !tradeQueueRunner.IsActive);
        frameTimings.Mark("tradeAutoAccept");
        tradeQueueRunner.Tick();
        frameTimings.Mark("tradeQueue");
        workshopRestock.Tick();
        frameTimings.Mark("workshopRestock");
        acquisitionServices.Tick();
        frameTimings.Mark(AcquisitionStep);
        agentBridge.Tick();
        var end = frameTimings.Mark("agentBridge");
        if (end - start > Stopwatch.Frequency / 20)
            LogSlowFrame(start, end);
    }

    private void LogSlowFrame(long start, long end)
    {
        if (end < nextSlowFrameLog)
        {
            suppressedSlowFrames++;
            return;
        }

        var details = frameTimings.Format(AcquisitionStep, acquisitionServices.LastTickTimings);
        var suffix = suppressedSlowFrames == 0 ? string.Empty : $" (+{suppressedSlowFrames} suppressed)";
        Log.Warning("[MarketMafioso] Slow frame {TotalMs:F1} ms: {Steps:l}{Suppression:l}",
            (end - start) * 1000d / Stopwatch.Frequency, details, suffix);
        nextSlowFrameLog = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
        suppressedSlowFrames = 0;
    }

    private static (bool LoggedIn, string Name, string HomeWorld) ObserveLoggedInCharacter()
    {
        var player = ObjectTable.LocalPlayer;
        return (ClientState.IsLoggedIn && player is not null,
            player?.Name.TextValue ?? string.Empty,
            PlayerState.HomeWorld.IsValid ? PlayerState.HomeWorld.Value.Name.ToString() : string.Empty);
    }

    private static string? ResolveItemName(uint itemId)
    {
        try
        {
            var name = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>()?.GetRowOrDefault(itemId)?.Name.ToString();
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // A relog interrupts whatever MMF automation is driving the character, so it is refused while any runs.
    private string? GetRelogBlockingReason()
    {
        if (workshopAssemblyRunner.IsRunning)
            return "A workshop run is in progress.";
        if (workshopRestock.Runner.IsRunning)
            return "A workshop vendor restock is in progress.";
        if (tradeQueueRunner.IsActive)
            return "Trade Queue execution is in progress.";
        return mainWindow?.GetMarketAutomationBusyReason();
    }

    private MarketRelogGameState SampleRelogGameState()
    {
        var who = ObserveLoggedInCharacter();
        return new MarketRelogGameState
        {
            LoggedIn = who.LoggedIn,
            Name = who.Name,
            HomeWorld = who.HomeWorld,
            CurrentDataCenterId = PlayerState.CurrentWorld.IsValid ? PlayerState.CurrentWorld.Value.DataCenter.RowId : null,
            HomeDataCenterId = PlayerState.HomeWorld.IsValid ? PlayerState.HomeWorld.Value.DataCenter.RowId : null,
            IsConditionSet = flag => Condition[flag],
            RetainerSessionOpen = IsRetainerSessionActive(),
            AutomationBusyReason = GetRelogBlockingReason(),
        };
    }

    private static (bool Ready, string? Reason) GetImmediateRetainerListingRefreshReadiness(bool retainerSessionActive)
    {
        if (!ClientState.IsLoggedIn || PlayerState.ContentId == 0)
            return (false, "Waiting for a logged-in character before refreshing retainer listings.");
        if (retainerSessionActive)
            return (false, "Waiting for the retainer session to close completely.");
        if (Condition[ConditionFlag.InCombat] ||
            Condition[ConditionFlag.Crafting] ||
            Condition[ConditionFlag.Gathering] ||
            Condition[ConditionFlag.WatchingCutscene] ||
            Condition[ConditionFlag.WatchingCutscene78] ||
            Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
            Condition[ConditionFlag.OccupiedInEvent] ||
            Condition[ConditionFlag.OccupiedInQuestEvent])
        {
            return (false, "Waiting for ordinary character activity to become idle before refreshing retainer listings.");
        }

        return (true, null);
    }

    // pluginConfigs\MarketMafioso -> pluginConfigs -> launcher root, which holds dalamud.log
    // for this profile (multibox profiles each have their own root).
    private static string? ResolveDalamudLogPath()
    {
        try
        {
            var root = Directory.GetParent(PluginInterface.GetPluginConfigDirectory())?.Parent;
            return root == null ? null : Path.Combine(root.FullName, "dalamud.log");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static unsafe bool IsRetainerSessionActive() =>
        Condition[ConditionFlag.OccupiedSummoningBell] ||
        IsAddonVisible("RetainerList") ||
        IsAddonVisible("RetainerSellList") ||
        IsAddonVisible("RetainerSell");

    private static unsafe bool IsAddonVisible(string name)
    {
        var addon = GameGui.GetAddonByName<AtkUnitBase>(name, 1);
        return addon != null && addon->IsReady && addon->IsVisible;
    }

    private void DrawUI()
    {
        if (!mainWindow.IsOpen)
            mainWindow.AcquisitionCompositionWindow.IsOpen = false;

        mainWindow.FrozenQueueBrowser.PumpHandoffResults();
        mainWindow.BeginAgentReviewFrame();
        try
        {
            windowSystem.Draw();
        }
        finally
        {
            mainWindow.EndAgentReviewFrame();
        }
    }
    private void OpenConfigUi() => mainWindow.IsOpen = true;

    public void Dispose()
    {
        AcquisitionHostCheck.Dispose();
        StopTimer();
        marketAcquisitionItemContextMenu.Dispose();
        exactAcquisitionIpc.Dispose();
        workshopSavedJobIpc.Dispose();
        (WorkshopHandoffService as IDisposable)?.Dispose();
        agentBridge.Dispose();

        PluginInterface.UiBuilder.Draw -= DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenConfigUi;
        Framework.Update -= OnFrameworkUpdate;
        quartermaster.Changed -= OnQuartermasterChanged;
        sharedObservationClient.RetainersChanged -= OnSharedRetainersChanged;
        GameInventory.InventoryChanged -= OnInventoryChanged;
        inventoryDelivery.Dispose();

        CommandManager.RemoveHandler(CmdMain);

        workshopAssemblyRunner.Dispose();
        tradeQueueRunner.Dispose();
        tradeAutomationCoordinator.Dispose();

        windowSystem.RemoveAllWindows();
        mainWindow.ProjectBrowser.Dispose();
        mainWindow.AcquisitionCompositionWindow.Dispose();
        mainWindow.AcquisitionFeedbackTransition -= OnAcquisitionFeedbackTransition;
        mainWindow.Dispose();
        tradeQueueRunner.BatchVerified -= handoverTracker.OnBatchVerified;
        tradeQueueRunner.BatchStarting -= handoverTracker.OnBatchStarting;
        tradeQueueRunner.BatchLocking -= handoverTracker.OnBatchLocking;
        tradeQueueRunner.BatchAbandoned -= handoverTracker.OnBatchAbandoned;
        tradeQueueRunner.RecipientGuard = null;
        handoverTracker.Dispose();
        acquisitionServices.Dispose();
        remoteSummoningBellProbe.Dispose();
        workshopRestock.Dispose();
        reporter.Dispose();
        remoteMarketAccessProbe.Dispose();
        marketLockoutAttention.Dispose();
        marketBoardBrowseObserver.Dispose();
        retainerHistoryObserver.Dispose();
        retainerSaleChatObserver.Dispose();
        sharedObservationListings.Changed -= retainerListingRefresh.NotifyListingCaptureChanged;
        sharedObservationListings.Dispose();
        sharedObservationClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
        sharedObservationHost?.Dispose();
        quartermaster.Dispose();
        ECommonsMain.Dispose();
    }

    public void RestartTimer() => StartTimer();

    private void OnQuartermasterChanged(QuartermasterChanged changed)
    {
        if (!ShouldScheduleInventoryReport(changed.Kind))
            return;
        ScheduleInventoryReport($"Quartermaster revision {changed.Revision}");
    }

    internal static bool ShouldScheduleInventoryReport(string? kind) => kind switch
    {
        "periodic" or "opened" or QuartermasterIpcClient.OperationChangedKind or QuartermasterIpcClient.RetainerListingsChangedKind => false,
        _ => true,
    };

    private void OnSharedRetainersChanged(object? sender, SharedRetainerObservationSnapshot snapshot) =>
        ScheduleInventoryReport($"Franthropy retainer revision {snapshot.Revision}");

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        if (events.Count > 0)
            ScheduleInventoryReport($"{events.Count} game inventory event(s)");
    }

    private void ScheduleInventoryReport(string reason)
    {
        inventoryDelivery.Notify(reason);
    }

    private void StartTimer()
    {
        StopTimer();
        if (!Configuration.EnableAutoSendTimer || Configuration.AutoSendIntervalMinutes <= 0) return;

        timerCancellation = new CancellationTokenSource();
        var token = timerCancellation.Token;

        Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMinutes(Configuration.AutoSendIntervalMinutes), token).ConfigureAwait(false);
                    if (token.IsCancellationRequested) break;

                    _ = Framework.RunOnTick(async () => await reporter.SendReportAsync());
                }
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[MarketMafioso] Error in auto-send timer loop");
            }
        }, token);

        Log.Debug($"[MarketMafioso] Auto-send timer started (every {Configuration.AutoSendIntervalMinutes} minute(s))");
    }

    private void StopTimer()
    {
        if (timerCancellation != null)
        {
            timerCancellation.Cancel();
            timerCancellation.Dispose();
            timerCancellation = null;
            Log.Debug("[MarketMafioso] Auto-send timer stopped");
        }
    }
}
