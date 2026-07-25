using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using Dalamud.Interface.Windowing;
using MarketMafioso.Automation.Runtime;
using MarketMafioso.Automation.Travel;
using MarketMafioso.AgentBridge;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.MarketDiagnostics;
using MarketMafioso.Quartermaster;
using MarketMafioso.WorkshopPrep;
using MarketMafioso.SquireIntegration;
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
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] internal static IMarketBoard MarketBoard { get; private set; } = null!;

    internal static Plugin Instance { get; private set; } = null!;

    private const string CmdMain = "/mmf";

    public Configuration Configuration { get; init; }

    private readonly InventoryScanner scanner;
    private readonly HttpReporter reporter;
    private readonly RetainerSaleChatObserver retainerSaleChatObserver;
    private readonly RetainerHistoryObserver retainerHistoryObserver;
    private readonly RemoteMarketAccessProbe remoteMarketAccessProbe;
    private readonly RemoteMarketProbeWindow remoteMarketProbeWindow;
    private readonly QuartermasterIpcClient quartermaster;
    private readonly StandaloneSquireIpcClient standaloneSquire;
    private readonly ExactAcquisitionIpcProvider exactAcquisitionIpc;
    private readonly WorkshopProjectCatalog workshopCatalog;
    private readonly VIWIWorkshoppaIpc viwiWorkshoppaIpc;
    private readonly WorkshopAssemblyRunner workshopAssemblyRunner;
    private readonly WorkshopMaterialManifestExportService workshopMaterialManifestExport;
    private readonly WindowSystem windowSystem = new("MarketMafioso");
    private readonly MainWindow mainWindow;
    private readonly AgentBridgeProofStore agentBridgeProofStore;
    private readonly AgentBridgeProofWindow agentBridgeProofWindow;
    private readonly AgentBridgeHost agentBridge;
    private readonly AgentBridgeViewportCaptureService agentBridgeViewportCapture;

    private CancellationTokenSource? timerCancellation;
    private CancellationTokenSource? marketDiagnosticReportCancellation;

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
        standaloneSquire = new StandaloneSquireIpcClient(new DalamudStandaloneSquireIpcAdapter(PluginInterface));
        var serviceAccountIdentity = new DalamudServiceAccountIdentitySource(PluginInterface, Log);
        reporter = new HttpReporter(Configuration, PlayerState, Log, ChatGui, scanner, serviceAccountIdentity, quartermaster);
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
            PluginInterface.GetPluginConfigDirectory());
        remoteMarketProbeWindow = new RemoteMarketProbeWindow(remoteMarketAccessProbe);
        viwiWorkshoppaIpc = new VIWIWorkshoppaIpc(new DalamudVIWIWorkshoppaIpcAdapter(PluginInterface, Log));
        workshopAssemblyRunner = new WorkshopAssemblyRunner(
            Framework,
            Log,
            new WorkshopAssemblyUiAutomation(
                GameGui,
                AddonLifecycle,
                Log,
                ObjectTable,
                TargetManager,
                Condition,
                new ExternalAutomationCoordinator(new DalamudPluginDataStore(PluginInterface), Log)),
            Path.Combine(PluginInterface.GetPluginConfigDirectory(), "workshop-assembly-logs"),
            entry =>
            {
                var result = WorkshopQueueService.DecrementActiveQueue(Configuration, entry.WorkshopItemId);
                if (!result.Success)
                    Log.Warning("[MarketMafioso] {Message}", result.Message);

                Configuration.Save();
            });
        workshopMaterialManifestExport = new WorkshopMaterialManifestExportService(
            new LuminaWorkshopMaterialCraftRecipeResolver(DataManager));
        mainWindow = new MainWindow(
            Configuration,
            reporter,
            scanner,
            quartermaster,
            standaloneSquire,
            workshopCatalog,
            viwiWorkshoppaIpc,
            workshopAssemblyRunner,
            workshopMaterialManifestExport,
            DataManager,
            PlayerState,
            new MarketBoardApproachService(
                GameGui,
                ObjectTable,
                TargetManager,
                new VNavmeshIpc(new DalamudVNavmeshIpcAdapter(PluginInterface, Log)),
                Log),
            Path.Combine(PluginInterface.GetPluginConfigDirectory(), "market-acquisition-route-logs"),
            Log);
        exactAcquisitionIpc = new ExactAcquisitionIpcProvider(PluginInterface, mainWindow.StageExternalExactAcquisition);

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
                () => MarketAcquisitionUnlock.IsUnlocked(Configuration),
                mainWindow.AgentReviewRegistry),
            agentBridgeProofStore,
            agentBridgeViewportCapture.CaptureAsync,
            () => Configuration.EnableAgentBridgeScreenshots,
            mainWindow.AgentCaptureTransactions.Begin,
            mainWindow.AgentCaptureTransactions.Complete,
            mainWindow.AgentCaptureTransactions.Cancel);

        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(mainWindow.ProjectBrowser);
        windowSystem.AddWindow(mainWindow.FrozenQueueBrowser);
        windowSystem.AddWindow(mainWindow.AcquisitionCompositionWindow);
        windowSystem.AddWindow(agentBridgeProofWindow);
        windowSystem.AddWindow(remoteMarketProbeWindow);
        windowSystem.AddWindow(mainWindow.RemoteMarketOverlay);

        CommandManager.AddHandler(CmdMain, new CommandInfo(OnCommand)
        {
            HelpMessage =
                "Open the MarketMafioso toolbox window. " +
                "Use \"/mmf send\" to send an inventory report immediately, or " +
                "\"/mmf capture-bell\" to arm the passive normal-bell flight recorder. " +
                "Use \"/mmf capture-bell-lifecycle\" for the complete open/select/return/close trace. " +
                "Yield tests: \"/mmf probe-bell-yield-control\" then \"/mmf probe-bell-yield-direct\". " +
                "Warm retention: \"/mmf probe-bell-warm\", \"/mmf probe-bell-warm-delay <seconds>\", or \"/mmf probe-bell-warm-manual\". " +
                "Use \"/mmf probe-bell-warm-ui\" only for the old manual select/Quit bootstrap.",
        });

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += OpenConfigUi;
        Framework.Update += OnFrameworkUpdate;
        quartermaster.Changed += OnQuartermasterChanged;

        StartTimer();

        Log.Information("[MarketMafioso] Plugin loaded. Use /mmf to open settings.");
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
                ChatGui.Print($"[MMF] Remote market: {mainWindow.OpenRemoteMarketBoard()}");
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

            case "probe-bell-yield-cancel":
#if DEBUG
                ChatGui.Print($"[MMF] YieldEventScene2 probe: {mainWindow.CancelYieldEventSceneProbe()}");
                break;
#else
                ChatGui.Print("[MMF] YieldEventScene2 probes are only available in debug builds.");
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

            case "probe-bell-warm-manual":
#if DEBUG
                ChatGui.Print($"[MMF] Warm-session retention: {mainWindow.BeginManualWarmSessionRetentionProbe()}");
                break;
#else
                ChatGui.Print("[MMF] Warm-session retention is only available in debug builds.");
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
        retainerSaleChatObserver.Tick();
        retainerHistoryObserver.Tick();
        mainWindow.RemoteMarketOverlay.IsOpen = true;
        mainWindow.OnFrameworkUpdate(framework);
        agentBridge.Tick();
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
        exactAcquisitionIpc.Dispose();
        agentBridge.Dispose();

        PluginInterface.UiBuilder.Draw -= DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenConfigUi;
        Framework.Update -= OnFrameworkUpdate;
        quartermaster.Changed -= OnQuartermasterChanged;

        CommandManager.RemoveHandler(CmdMain);

        workshopAssemblyRunner.Dispose();

        windowSystem.RemoveAllWindows();
        mainWindow.ProjectBrowser.Dispose();
        mainWindow.AcquisitionCompositionWindow.Dispose();
        mainWindow.Dispose();
        reporter.Dispose();
        remoteMarketAccessProbe.Dispose();
        retainerHistoryObserver.Dispose();
        retainerSaleChatObserver.Dispose();
        quartermaster.Dispose();
        ECommonsMain.Dispose();
    }

    public void RestartTimer() => StartTimer();

    private void OnQuartermasterChanged(QuartermasterChanged changed)
    {
        if (!Configuration.EnableMarketDiagnostics)
            return;

        marketDiagnosticReportCancellation?.Cancel();
        marketDiagnosticReportCancellation?.Dispose();
        marketDiagnosticReportCancellation = new CancellationTokenSource();
        _ = SendMarketDiagnosticReportAfterDebounceAsync(
            changed.Revision,
            marketDiagnosticReportCancellation.Token);
    }

    private async Task SendMarketDiagnosticReportAfterDebounceAsync(
        long revision,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            await Framework.RunOnTick(
                () => reporter.SendReportAsync(quiet: true),
                cancellationToken: cancellationToken);
            Log.Information(
                "[MarketMafioso] Shipped inventory report after Quartermaster revision {Revision}.",
                revision);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Log.Warning(
                exception,
                "[MarketMafioso] Failed to ship the inventory report for Quartermaster revision {Revision}.",
                revision);
        }
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

        marketDiagnosticReportCancellation?.Cancel();
        marketDiagnosticReportCancellation?.Dispose();
        marketDiagnosticReportCancellation = null;
    }
}
