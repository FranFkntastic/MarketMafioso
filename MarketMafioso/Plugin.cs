using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using MarketMafioso.WorkshopPrep;
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

    internal static Plugin Instance { get; private set; } = null!;

    private const string CmdMain = "/mmf";

    public Configuration Configuration { get; init; }

    private readonly InventoryScanner scanner;
    private readonly HttpReporter reporter;
    private readonly RetainerCacheManager retainerCache;
    private readonly AutoRetainerRefreshService autoRetainerRefresh;
    private readonly WorkshopProjectCatalog workshopCatalog;
    private readonly VIWIWorkshoppaIpc viwiWorkshoppaIpc;
    private readonly WorkshopRetainerRestockService workshopRetainerRestock;
    private readonly WindowSystem windowSystem = new("MarketMafioso");
    private readonly MainWindow mainWindow;

    private CancellationTokenSource? timerCancellation;

    public Plugin()
    {
        Instance = this;

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        scanner = new InventoryScanner(DataManager, Log);
        reporter = new HttpReporter(Configuration, PlayerState, Log, ChatGui, scanner);
        retainerCache = new RetainerCacheManager(AddonLifecycle, Log, Configuration, scanner, reporter);
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
        mainWindow = new MainWindow(
            Configuration,
            reporter,
            scanner,
            autoRetainerRefresh,
            workshopCatalog,
            viwiWorkshoppaIpc,
            workshopRetainerRestock,
            Log);

        windowSystem.AddWindow(mainWindow);

        CommandManager.AddHandler(CmdMain, new CommandInfo(OnCommand)
        {
            HelpMessage =
                "Open the MarketMafioso toolbox window. " +
                "Use \"/mmf send\" to send an inventory report immediately.",
        });

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += OpenConfigUi;

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

    private void DrawUI() => windowSystem.Draw();
    private void OpenConfigUi() => mainWindow.IsOpen = true;

    public void Dispose()
    {
        StopTimer();

        PluginInterface.UiBuilder.Draw -= DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenConfigUi;

        CommandManager.RemoveHandler(CmdMain);

        autoRetainerRefresh.Dispose();
        retainerCache.Dispose();
        reporter.Dispose();

        windowSystem.RemoveAllWindows();
        mainWindow.Dispose();
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
