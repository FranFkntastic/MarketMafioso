using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MarketMafioso.Services;
using MarketMafioso.Windows;

namespace MarketMafioso;

public sealed class MarketMafioso : IDalamudPlugin
{
    public string Name => "MarketMafioso";

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IPluginLog _pluginLog;
    private readonly PluginConfiguration _configuration;
    private readonly WindowSystem _windowSystem;
    private readonly RetainerMarketCaptureService _retainerMarketCaptureService;
    private readonly MasterWindow _masterWindow;
    private readonly RetainerMenuOverlay _retainerMenuOverlay;

    public MarketMafioso(
        IDalamudPluginInterface pluginInterface,
        IPluginLog pluginLog,
        ICondition condition,
        IGameInventory gameInventory,
        IMarketBoard marketBoard,
        IDataManager dataManager,
        IGameGui gameGui,
        IPlayerState playerState)
    {
        _pluginInterface = pluginInterface;
        _pluginLog = pluginLog;
        _configuration = PluginConfiguration.Load(pluginInterface);
        _windowSystem = new WindowSystem("MarketMafioso");

        var snapshotStore = new RetainerSnapshotStore();
        var marketSnapshotStore = new RetainerMarketSnapshotStore();
        var itemNameResolver = new ItemNameResolver(dataManager);
        var activeRetainerCaptureService = new ActiveRetainerCaptureService(condition, gameInventory, itemNameResolver);
        _retainerMarketCaptureService = new RetainerMarketCaptureService(
            activeRetainerCaptureService,
            snapshotStore,
            marketSnapshotStore,
            marketBoard,
            pluginLog);

        _retainerMenuOverlay = new RetainerMenuOverlay(
            condition,
            playerState,
            gameGui,
            pluginLog,
            snapshotStore,
            marketSnapshotStore,
            _retainerMarketCaptureService,
            _configuration,
            OpenMainUi);

        _masterWindow = new MasterWindow(
            pluginLog,
            snapshotStore,
            marketSnapshotStore,
            _retainerMarketCaptureService,
            _configuration,
            _retainerMenuOverlay.SetCollapsedPreference);

        _windowSystem.AddWindow(_retainerMenuOverlay);
        _windowSystem.AddWindow(_masterWindow);

        _pluginInterface.UiBuilder.Draw += Draw;
        _pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        _pluginInterface.UiBuilder.OpenConfigUi += OpenMainUi;

        _pluginLog.Information("[MarketMafioso] Plugin loaded.");
    }

    private void Draw()
    {
        _retainerMarketCaptureService.Tick();
        _windowSystem.Draw();
    }

    private void OpenMainUi()
    {
        _masterWindow.Open();
    }

    public void Dispose()
    {
        _pluginInterface.UiBuilder.Draw -= Draw;
        _pluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        _pluginInterface.UiBuilder.OpenConfigUi -= OpenMainUi;
        _windowSystem.RemoveAllWindows();
        _retainerMarketCaptureService.Dispose();
        _configuration.Save();
        _pluginLog.Information("[MarketMafioso] Plugin disposed.");
    }
}
