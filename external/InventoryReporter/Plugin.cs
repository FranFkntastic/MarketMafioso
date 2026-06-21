using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using InventoryReporter2.Windows;

namespace InventoryReporter2;

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

    internal static Plugin Instance { get; private set; } = null!;

    private const string CmdMain = "/invreport";

    public Configuration Configuration { get; init; }

    private readonly InventoryScanner scanner;
    private readonly HttpReporter reporter;
    private readonly RetainerCacheManager retainerCache;
    private readonly WindowSystem windowSystem = new("InventoryReporter2");
    private readonly MainWindow mainWindow;

    private CancellationTokenSource? timerCancellation;

    public Plugin()
    {
        Instance = this;

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        scanner = new InventoryScanner(DataManager, Log);
        reporter = new HttpReporter(Configuration, PlayerState, Log, ChatGui, scanner);
        retainerCache = new RetainerCacheManager(AddonLifecycle, Log, Configuration, scanner, reporter);
        mainWindow = new MainWindow(Configuration, reporter, scanner, Log);

        windowSystem.AddWindow(mainWindow);

        CommandManager.AddHandler(CmdMain, new CommandInfo(OnCommand)
        {
            HelpMessage =
                "Open the Inventory Reporter settings window. " +
                "Use \"/invreport send\" to send a report immediately.",
        });

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += OpenConfigUi;

        StartTimer();

        Log.Information("[InventoryReporter2] Plugin loaded. Use /invreport to open settings.");
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
                Log.Error(ex, "[InventoryReporter2] Error in auto-send timer loop");
            }
        }, token);

        Log.Debug($"[InventoryReporter2] Auto-send timer started (every {Configuration.AutoSendIntervalMinutes} minute(s))");
    }

    private void StopTimer()
    {
        if (timerCancellation != null)
        {
            timerCancellation.Cancel();
            timerCancellation.Dispose();
            timerCancellation = null;
            Log.Debug("[InventoryReporter2] Auto-send timer stopped");
        }
    }
}
