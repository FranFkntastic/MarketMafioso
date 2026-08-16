using System;
using System.Collections.Generic;
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
using Franthropy.Dalamud.Observations;
using Franthropy.Dalamud.Runtime;
using Franthropy.Observations.V1;
using Dalamud.Interface.Windowing;
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.Automation.Runtime;
using MarketMafioso.Automation.Travel;
using MarketMafioso.AgentBridge;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.MarketDiagnostics;
using MarketMafioso.Quartermaster;
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
    [PluginService] internal static IAetheryteList AetheryteList { get; private set; } = null!;
    [PluginService] internal static IContextMenu ContextMenu { get; private set; } = null!;

    internal static Plugin Instance { get; private set; } = null!;

    private const string CmdMain = "/mmf";

    public Configuration Configuration { get; init; }

    private readonly InventoryScanner scanner;
    private readonly FranthropyRetainerReportSource retainerReports;
    private readonly HttpReporter reporter;
    private readonly InventoryDeliveryCoalescer inventoryDelivery;
    private readonly RetainerSaleChatObserver retainerSaleChatObserver;
    private readonly RetainerHistoryObserver retainerHistoryObserver;
    private readonly DalamudMarketBoardBrowseObserver marketBoardBrowseObserver;
    private readonly RetainerListingRefreshCoordinator retainerListingRefresh;
    private readonly RetainerListingRefreshReadinessGate retainerListingRefreshReadiness = new();
    private readonly FranthropyRetainerListingRefreshSource sharedObservationListings;
    private readonly DalamudSharedObservationClient sharedObservationClient;
    private readonly DalamudSharedObservationHost? sharedObservationHost;
    private readonly RemoteMarketAccessProbe remoteMarketAccessProbe;
    private readonly RemoteMarketProbeWindow remoteMarketProbeWindow;
    private readonly QuartermasterIpcClient quartermaster;
    private readonly StandaloneSquireIpcClient standaloneSquire;
    private readonly ExactAcquisitionIpcProvider exactAcquisitionIpc;
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
    private readonly FramePacingGovernor framePacingGovernor = new();
    private readonly AgentBridgeProofStore agentBridgeProofStore;
    private readonly AgentBridgeProofWindow agentBridgeProofWindow;
    private readonly AgentBridgeHost agentBridge;
    private readonly AgentBridgeViewportCaptureService agentBridgeViewportCapture;

    private CancellationTokenSource? timerCancellation;

    public Plugin()
    {
        Instance = this;
        ECommonsMain.ReducedLogging = true;
        ECommonsMain.Init(PluginInterface, this);

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
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
                new ExternalAutomationCoordinator(
                    new DalamudPluginDataStore(PluginInterface),
                    Log,
                    new DalamudPandoraFeatureControl(PluginInterface))),
            Path.Combine(PluginInterface.GetPluginConfigDirectory(), "workshop-assembly-logs"),
            entry =>
            {
                var result = WorkshopQueueService.DecrementActiveQueue(Configuration, entry.WorkshopItemId);
                if (!result.Success)
                    Log.Warning("[MarketMafioso] {Message}", result.Message);

                Configuration.Save();
            });
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
            Configuration.Save,
            tradeQueueIo,
            new Franthropy.Dalamud.Automation.Inventory.DalamudItemQualityLoweringAutomation(
                GameGui,
                DalamudTradeQueueIo.SupportedInventories),
            tradeAutomationCoordinator,
            Log,
            Configuration.TradeQueuePolicy);
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
            tradeQueueIo,
            DataManager,
            PlayerState,
            new MarketBoardApproachService(
                GameGui,
                ObjectTable,
                TargetManager,
                new VNavmeshIpc(new DalamudVNavmeshIpcAdapter(PluginInterface, Log)),
                Log),
            marketBoardBrowseObserver,
            itemId => retainerListingRefresh.ForceRetry(itemId),
            Path.Combine(PluginInterface.GetPluginConfigDirectory(), "market-acquisition-route-logs"),
            framePacingGovernor,
            Log);
        exactAcquisitionIpc = new ExactAcquisitionIpcProvider(PluginInterface, mainWindow.StageExternalExactAcquisition);
        marketAcquisitionItemContextMenu = new MarketAcquisitionItemContextMenu(
            ContextMenu,
            GameGui,
            DataManager,
            Framework,
            ChatGui,
            () => MarketAcquisitionUnlock.IsUnlocked(Configuration),
            mainWindow.CanStageContextMenuItemToWorkbench,
            mainWindow.StageContextMenuItemToWorkbench);

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
                mainWindow.AgentStopRoute,
                mainWindow.ProbeMarketActorNames,
                mainWindow.BeginControlledMarketActorListing,
                mainWindow.RemoveControlledMarketActorListing,
                mainWindow.BeginControlledMarketActorBrowse,
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
        Log.Information("[MarketMafioso] Plugin loaded. Use /mmf to open settings.");

        try
        {
            sharedObservationHost = new DalamudSharedObservationHost(new DalamudSharedObservationHostOptions
            {
                PluginConfigDirectory = PluginInterface.GetPluginConfigDirectory(),
                PluginName = "MarketMafioso",
                PluginInstanceId = Guid.NewGuid().ToString("N"),
                GameBuild = Franthropy.Dalamud.Diagnostics.GamePatchCompatibilityGate.ReadCurrentGameVersion(),
                GameInventory = GameInventory,
                PlayerState = PlayerState,
                AddonLifecycle = AddonLifecycle,
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

                    ChatGui.Print($"[MMF] Market listings: {mainWindow.OpenMarketListing(matches[0].RowId)}");
                    break;
                }
#endif
                ChatGui.Print($"[MMF] Market listings: {mainWindow.OpenMarketListings()}");
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
                var probeMessage = mainWindow.BeginRemoteSummoningBellProbe();
                ChatGui.Print($"[MMF] Remote bell probe: {probeMessage}");
                break;
            }
#else
                ChatGui.Print("[MMF] Remote bell probe is only available in debug builds.");
                break;
#endif

            case "capture-bell":
#if DEBUG
                ChatGui.Print($"[MMF] Normal bell capture: {mainWindow.BeginNormalSummoningBellCapture()}");
                break;
#else
                ChatGui.Print("[MMF] Normal bell capture is only available in debug builds.");
                break;
#endif

            case "capture-bell-lifecycle":
#if DEBUG
                ChatGui.Print($"[MMF] Bell lifecycle capture: {mainWindow.BeginNormalSummoningBellLifecycleCapture()}");
                break;
#else
                ChatGui.Print("[MMF] Bell lifecycle capture is only available in debug builds.");
                break;
#endif

            case "capture-bell-status":
#if DEBUG
                ChatGui.Print($"[MMF] Normal bell capture: {mainWindow.GetNormalSummoningBellCaptureStatus()}");
                break;
#else
                ChatGui.Print("[MMF] Normal bell capture is only available in debug builds.");
                break;
#endif

            case "capture-bell-cancel":
#if DEBUG
                ChatGui.Print($"[MMF] Normal bell capture: {mainWindow.CancelNormalSummoningBellCapture()}");
                break;
#else
                ChatGui.Print("[MMF] Normal bell capture is only available in debug builds.");
                break;
#endif

            case "probe-bell-yield-control":
#if DEBUG
                ChatGui.Print($"[MMF] YieldEventScene2 control: {mainWindow.BeginYieldEventSceneControl()}");
                break;
#else
                ChatGui.Print("[MMF] YieldEventScene2 probes are only available in debug builds.");
                break;
#endif

            case "probe-bell-yield-direct":
#if DEBUG
                ChatGui.Print($"[MMF] YieldEventScene2 direct probe: {mainWindow.BeginYieldEventSceneDirectProbe()}");
                break;
#else
                ChatGui.Print("[MMF] YieldEventScene2 probes are only available in debug builds.");
                break;
#endif

            case "probe-bell-yield-status":
#if DEBUG
                ChatGui.Print($"[MMF] YieldEventScene2 probe: {mainWindow.GetYieldEventSceneProbeStatus()}");
                break;
#else
                ChatGui.Print("[MMF] YieldEventScene2 probes are only available in debug builds.");
                break;
#endif

            case "probe-bell-native-call":
#if DEBUG
                ChatGui.Print($"[MMF] Native CallRetainer probe: {mainWindow.BeginNativeCallRetainerProbe()}");
                break;
#else
                ChatGui.Print("[MMF] Native retainer-verb probes are only available in debug builds.");
                break;
#endif

            case "probe-bell-native-select":
#if DEBUG
                ChatGui.Print($"[MMF] Native SelectRetainer probe: {mainWindow.BeginNativeSelectRetainerProbe()}");
                break;
#else
                ChatGui.Print("[MMF] Native retainer-verb probes are only available in debug builds.");
                break;
#endif

            case "probe-bell-yield-cancel":
#if DEBUG
                ChatGui.Print($"[MMF] YieldEventScene2 probe: {mainWindow.CancelYieldEventSceneProbe()}");
                break;
#else
                ChatGui.Print("[MMF] YieldEventScene2 probes are only available in debug builds.");
                break;
#endif

            case "probe-retainer-rpc-control":
#if DEBUG
                ChatGui.Print($"[MMF] Retainer RPC control: {mainWindow.BeginRetainerRpcControlProbe()}");
                break;
#else
                ChatGui.Print("[MMF] Retainer RPC probes are only available in debug builds.");
                break;
#endif

            case "probe-retainer-rpc-bind-test":
#if DEBUG
                ChatGui.Print($"[MMF] Retainer RPC bind test: {mainWindow.BeginRetainerRpcBindProbe()}");
                break;
#else
                ChatGui.Print("[MMF] Retainer RPC probes are only available in debug builds.");
                break;
#endif

            case "probe-retainer-rpc-status":
#if DEBUG
                ChatGui.Print($"[MMF] Retainer RPC probe: {mainWindow.GetRetainerRpcProbeStatus()}");
                break;
#else
                ChatGui.Print("[MMF] Retainer RPC probes are only available in debug builds.");
                break;
#endif

            case "probe-retainer-rpc-cancel":
#if DEBUG
                ChatGui.Print($"[MMF] Retainer RPC probe: {mainWindow.CancelRetainerRpcProbe()}");
                break;
#else
                ChatGui.Print("[MMF] Retainer RPC probes are only available in debug builds.");
                break;
#endif

            case "probe-bell-warm":
#if DEBUG
                ChatGui.Print($"[MMF] Warm-session retention: {mainWindow.BeginWarmSessionRetentionProbe()}");
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
                    mainWindow.BeginDelayedWarmSessionRetentionProbe(TimeSpan.FromSeconds(delaySeconds)));
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
                    mainWindow.BeginDistanceWarmSessionRetentionProbe(movementYalms));
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
                    mainWindow.BeginLocallyUnlockedDistanceWarmSessionRetentionProbe(unlockedMovementYalms));
                break;
#else
                ChatGui.Print("[MMF] Warm-session retention is only available in debug builds.");
                break;
#endif

            case "probe-bell-warm-manual":
#if DEBUG
                ChatGui.Print($"[MMF] Warm-session retention: {mainWindow.BeginManualWarmSessionRetentionProbe()}");
                break;
#else
                ChatGui.Print("[MMF] Warm-session retention is only available in debug builds.");
                break;
#endif

            case "probe-bell-scene2-ui":
#if DEBUG
                ChatGui.Print($"[MMF] Scene-2 UI resurrection: {mainWindow.BeginScene2UiResurrectionProbe()}");
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
                    mainWindow.BeginScene2DistanceContinuationProbe(scene2MovementYalms));
                break;
#else
                ChatGui.Print("[MMF] Scene-2 probes are only available in debug builds.");
                break;
#endif

            case "probe-bell-warm-ui":
#if DEBUG
                ChatGui.Print($"[MMF] Warm-session retention: {mainWindow.BeginManualUiWarmSessionRetentionProbe()}");
                break;
#else
                ChatGui.Print("[MMF] Warm-session retention is only available in debug builds.");
                break;
#endif

            case "probe-bell-warm-replay":
#if DEBUG
                ChatGui.Print($"[MMF] Warm-session retention: {mainWindow.ReplayHeldWarmSession()}");
                break;
#else
                ChatGui.Print("[MMF] Warm-session retention is only available in debug builds.");
                break;
#endif

            case "probe-bell-warm-status":
#if DEBUG
                ChatGui.Print($"[MMF] Warm-session retention: {mainWindow.GetWarmSessionRetentionProbeStatus()}");
                break;
#else
                ChatGui.Print("[MMF] Warm-session retention is only available in debug builds.");
                break;
#endif

            case "probe-bell-warm-cancel":
#if DEBUG
                ChatGui.Print($"[MMF] Warm-session retention: {mainWindow.CancelWarmSessionRetentionProbe()}");
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

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            retainerSaleChatObserver.Tick();
            retainerHistoryObserver.Tick();
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
            mainWindow.MarketListingOverlay.SynchronizePresentationLifetime();
            if (Configuration.AutoAcceptIncomingTrades)
                tradeAutomationCoordinator.SuppressDropboxAutoAccept();
            else
                tradeAutomationCoordinator.RestoreDropboxAutoAccept();
            tradeAutoAcceptController.Tick(
                Configuration.AutoAcceptIncomingTrades && !tradeQueueRunner.IsActive);
            tradeQueueRunner.Tick();
            mainWindow.OnFrameworkUpdate(framework);
            agentBridge.Tick();
        }
        finally
        {
            framePacingGovernor.PaceFrame();
        }
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
        StopTimer();
        marketAcquisitionItemContextMenu.Dispose();
        exactAcquisitionIpc.Dispose();
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
        mainWindow.Dispose();
        framePacingGovernor.Dispose();
        reporter.Dispose();
        remoteMarketAccessProbe.Dispose();
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
