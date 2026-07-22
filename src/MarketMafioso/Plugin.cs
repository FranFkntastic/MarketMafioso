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

    internal static Plugin Instance { get; private set; } = null!;

    private const string CmdMain = "/mmf";

    public Configuration Configuration { get; init; }

    private readonly InventoryScanner scanner;
    private readonly HttpReporter reporter;
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
        workshopCatalog = new WorkshopProjectCatalog(DataManager, Log);
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
                proofId =>
                {
                    agentBridgeProofWindow.RequestedProofId = proofId;
                    agentBridgeProofWindow.IsOpen = true;
                },
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

    public void Dispose()
    {
        StopTimer();
        exactAcquisitionIpc.Dispose();
        agentBridge.Dispose();

        PluginInterface.UiBuilder.Draw -= DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenConfigUi;
        Framework.Update -= OnFrameworkUpdate;

        CommandManager.RemoveHandler(CmdMain);

        workshopAssemblyRunner.Dispose();

        windowSystem.RemoveAllWindows();
        mainWindow.ProjectBrowser.Dispose();
        mainWindow.AcquisitionCompositionWindow.Dispose();
        mainWindow.Dispose();
        reporter.Dispose();
        quartermaster.Dispose();
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
