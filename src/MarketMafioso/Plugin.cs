using System;
using System.IO;
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
using MarketMafioso.WorkshopPrep;
using MarketMafioso.Squire;
using MarketMafioso.Squire.Observation;
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

    internal static Plugin Instance { get; private set; } = null!;

    private const string CmdMain = "/mmf";

    public Configuration Configuration { get; init; }

    private readonly InventoryScanner scanner;
    private readonly HttpReporter reporter;
    private readonly RetainerCacheFileStore retainerCacheStore;
    private readonly RetainerCacheManager retainerCache;
    private readonly AutoRetainerRefreshService autoRetainerRefresh;
    private readonly WorkshopProjectCatalog workshopCatalog;
    private readonly VIWIWorkshoppaIpc viwiWorkshoppaIpc;
    private readonly WorkshopRetainerRestockService workshopRetainerRestock;
    private readonly WorkshopAssemblyRunner workshopAssemblyRunner;
    private readonly WorkshopMaterialManifestExportService workshopMaterialManifestExport;
    private readonly WindowSystem windowSystem = new("MarketMafioso");
    private readonly MainWindow mainWindow;
    private readonly AgentBridgeProofStore agentBridgeProofStore;
    private readonly AgentBridgeProofWindow agentBridgeProofWindow;
    private readonly AgentBridgeHost agentBridge;
    private readonly AgentBridgeViewportCaptureService agentBridgeViewportCapture;
    private readonly DalamudRenderedCharacterUiProbe renderedCharacterUiProbe;
    private readonly DalamudRetainerUiPreparation retainerUiPreparation;

    private CancellationTokenSource? timerCancellation;

    public Plugin()
    {
        Instance = this;
        ECommonsMain.ReducedLogging = true;
        ECommonsMain.Init(PluginInterface, this);

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        var squireConfigurationChanged = SquireRuleMigration.Migrate(Configuration);
        squireConfigurationChanged |= SquireCleanupRuleMigration.Migrate(Configuration);
        squireConfigurationChanged |= SquireAdvisorConfigurationMigration.Migrate(Configuration);
        if (squireConfigurationChanged)
            Configuration.Save();
        retainerCacheStore = new RetainerCacheFileStore(
            Path.Combine(PluginInterface.GetPluginConfigDirectory(), "retainer-cache.json"));
        LoadRetainerCache();

        scanner = new InventoryScanner(DataManager, Log);
        var serviceAccountIdentity = new DalamudServiceAccountIdentitySource(PluginInterface, Log);
        reporter = new HttpReporter(Configuration, PlayerState, Log, ChatGui, scanner, serviceAccountIdentity);
        retainerCache = new RetainerCacheManager(
            AddonLifecycle,
            Log,
            Configuration,
            scanner,
            reporter,
            PlayerState,
            retainerCacheStore);
        autoRetainerRefresh = new AutoRetainerRefreshService(
            PluginInterface,
            Log,
            GameGui,
            ObjectTable,
            DataManager,
            retainerCache,
            reporter);
        workshopCatalog = new WorkshopProjectCatalog(DataManager, Log);
        viwiWorkshoppaIpc = new VIWIWorkshoppaIpc(new DalamudVIWIWorkshoppaIpcAdapter(PluginInterface, Log));
        workshopRetainerRestock = new WorkshopRetainerRestockService(Log);
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
        renderedCharacterUiProbe = new DalamudRenderedCharacterUiProbe(GameGui);
        retainerUiPreparation = new(
            CommandManager,
            new LifestreamIpc(PluginInterface, Log),
            renderedCharacterUiProbe.CaptureRetainerUi,
            renderedCharacterUiProbe.TryActivateRenderedSummoningBell);
        mainWindow = new MainWindow(
            Configuration,
            reporter,
            scanner,
            autoRetainerRefresh,
            workshopCatalog,
            viwiWorkshoppaIpc,
            workshopRetainerRestock,
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
            retainerCacheStore,
            Log,
            renderedCharacterUiProbe);

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
                proofId =>
                {
                    agentBridgeProofWindow.RequestedProofId = proofId;
                    agentBridgeProofWindow.IsOpen = true;
                },
                mainWindow.TrySelectAgentBridgeTab,
                mainWindow.AgentCaptureInputState,
                mainWindow.AgentStopRoute,
                () => MarketAcquisitionUnlock.IsUnlocked(Configuration),
                mainWindow.AgentReviewRegistry,
                () => renderedCharacterUiProbe.Open(),
                renderedCharacterUiProbe.TryCloseCharacterUi,
                renderedCharacterUiProbe.Capture,
                renderedCharacterUiProbe.TryCloseBlockingSelectString,
                renderedCharacterUiProbe.TrySwitchCalibrationJob,
                renderedCharacterUiProbe.TrySwitchGearsetSlot,
                renderedCharacterUiProbe.CaptureGatheringStats,
                renderedCharacterUiProbe.TryHoverCharacterNode,
                renderedCharacterUiProbe.RestoreCursor,
                renderedCharacterUiProbe.BeginEquipmentScan,
                renderedCharacterUiProbe.AdvanceEquipmentScan,
                renderedCharacterUiProbe.CancelEquipmentScan,
                () => renderedCharacterUiProbe.Capabilities,
                mainWindow.TryOpenSyntheticAdvisorReview,
                captureRetainerUi: renderedCharacterUiProbe.CaptureRetainerUi,
                beginRetainerObservationUi: retainerUiPreparation.Begin,
                advanceRetainerObservationUi: retainerUiPreparation.Advance,
                cancelRetainerObservationUi: retainerUiPreparation.Cancel,
                tryOpenGearsetListUi: renderedCharacterUiProbe.TryOpenGearsetList,
                trySelectCalibrationGearsetUi: renderedCharacterUiProbe.TrySelectCalibrationGearset,
                tryEquipSelectedGearsetUi: renderedCharacterUiProbe.TryEquipSelectedGearset),
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

        CommandManager.AddHandler(CmdMain, new CommandInfo(OnCommand)
        {
            HelpMessage =
                "Open the MarketMafioso toolbox window. " +
                "Use \"/mmf send\" to send an inventory report immediately.",
        });

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += OpenConfigUi;
        Framework.Update += OnFrameworkUpdate;

        StartTimer();

        Log.Information("[MarketMafioso] Plugin loaded. Use /mmf to open settings.");
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "send":
                Framework.RunOnTick(() => _ = reporter.SendReportAsync());
                break;

            default:
                mainWindow.IsOpen = !mainWindow.IsOpen;
                break;
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
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

    private void LoadRetainerCache()
    {
        try
        {
            if (retainerCacheStore.Exists)
            {
                Configuration.RetainerCache = retainerCacheStore.Load();
                Log.Information(
                    "[MarketMafioso] Loaded {Count} cached retainer(s) from retainer-cache.json.",
                    Configuration.RetainerCache.Count);
                return;
            }

            if (Configuration.RetainerCache.Count > 0)
            {
                retainerCacheStore.Save(Configuration.RetainerCache);
                Log.Information(
                    "[MarketMafioso] Migrated {Count} cached retainer(s) to retainer-cache.json.",
                    Configuration.RetainerCache.Count);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MarketMafioso] Error loading retainer inventory cache");
        }
    }

    public void Dispose()
    {
        StopTimer();
        agentBridge.Dispose();

        PluginInterface.UiBuilder.Draw -= DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenConfigUi;
        Framework.Update -= OnFrameworkUpdate;

        CommandManager.RemoveHandler(CmdMain);

        autoRetainerRefresh.Dispose();
        retainerCache.Dispose();
        workshopAssemblyRunner.Dispose();
        reporter.Dispose();

        windowSystem.RemoveAllWindows();
        mainWindow.ProjectBrowser.Dispose();
        mainWindow.AcquisitionCompositionWindow.Dispose();
        mainWindow.Dispose();
        ECommonsMain.Dispose();
    }

    public void RestartTimer() => StartTimer();

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
