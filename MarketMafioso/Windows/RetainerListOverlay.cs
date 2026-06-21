using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using MarketMafioso.Services;
using System;
using System.Numerics;

namespace MarketMafioso.Windows;

public sealed class RetainerMenuOverlay : Window
{
    private readonly ICondition _condition;
    private readonly IPlayerState _playerState;
    private readonly IGameGui _gameGui;
    private readonly IPluginLog _pluginLog;
    private readonly RetainerSnapshotStore _snapshotStore;
    private readonly RetainerMarketSnapshotStore _marketSnapshotStore;
    private readonly RetainerMarketCaptureService _retainerMarketCaptureService;
    private readonly Action _openMasterWindow;

    private ulong _sessionContentId;
    private string _statusMessage = "Idle";
    private ulong _activeRetainerId;
    private string _activeRetainerName = string.Empty;
    private int _totalRetainers;
    private Vector2 _position;
    private bool? _pendingCollapsed;

    public RetainerMenuOverlay(
        ICondition condition,
        IPlayerState playerState,
        IGameGui gameGui,
        IPluginLog pluginLog,
        RetainerSnapshotStore snapshotStore,
        RetainerMarketSnapshotStore marketSnapshotStore,
        RetainerMarketCaptureService retainerMarketCaptureService,
        PluginConfiguration configuration,
        Action openMasterWindow)
        : base(
            "MarketMafioso###MmfRetainerMenuOverlay",
            ImGuiWindowFlags.AlwaysAutoResize
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoNav)
    {
        _condition = condition;
        _playerState = playerState;
        _gameGui = gameGui;
        _pluginLog = pluginLog;
        _snapshotStore = snapshotStore;
        _marketSnapshotStore = marketSnapshotStore;
        _retainerMarketCaptureService = retainerMarketCaptureService;
        _openMasterWindow = openMasterWindow;

        IsOpen = true;
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        AllowPinning = false;
        AllowClickthrough = false;

        _pendingCollapsed = configuration.OverlayCollapsed;
    }

    public void SetCollapsedPreference(bool collapsed)
    {
        _pendingCollapsed = collapsed;
    }

    public unsafe override bool DrawConditions()
    {
        SyncSessionScope();

        if (!_condition[ConditionFlag.OccupiedSummoningBell])
        {
            return false;
        }

        var manager = RetainerManager.Instance();
        if (manager == null || !manager->IsReady)
        {
            return false;
        }

        var activeRetainer = manager->GetActiveRetainer();
        if (activeRetainer == null || activeRetainer->RetainerId == 0)
        {
            return false;
        }

        _totalRetainers = manager->GetRetainerCount();
        _activeRetainerId = activeRetainer->RetainerId;
        _activeRetainerName = activeRetainer->NameString;

        var selectString = _gameGui.GetAddonByName("SelectString", 1);
        if (selectString.IsNull || !selectString.IsVisible || !selectString.IsReady)
        {
            return false;
        }

        _position = new Vector2(selectString.X + selectString.ScaledWidth + 7f, selectString.Y + 7f);
        return true;
    }

    public override void PreDraw()
    {
        Position = _position;
        PositionCondition = ImGuiCond.Always;

        if (_pendingCollapsed.HasValue)
        {
            Collapsed = _pendingCollapsed.Value;
            CollapsedCondition = ImGuiCond.Always;
            _pendingCollapsed = null;
            return;
        }

        Collapsed = null;
    }

    public override void Draw()
    {
        var capturedRetainers = _snapshotStore.Count;

        if (ImGui.SmallButton("Manage"))
        {
            _openMasterWindow();
        }

        ImGui.Separator();

        ImGui.BeginDisabled(_retainerMarketCaptureService.IsRunning);
        if (ImGui.Button("Refresh Active + Market"))
        {
            StartMarketCaptureCycle();
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Dump Last Capture"))
        {
            DumpLastCapture(_activeRetainerId, _activeRetainerName);
        }

        var captureStatus = _retainerMarketCaptureService.StatusMessage;
        ImGui.TextDisabled(captureStatus == "Idle" ? _statusMessage : captureStatus);

        if (_snapshotStore.TryGet(_activeRetainerId, out var activeSnapshot))
        {
            ImGui.Separator();
            ImGui.TextUnformatted($"Retainer: {activeSnapshot.RetainerName}");
            ImGui.TextUnformatted($"Listings: {activeSnapshot.Listings.Count}");
            ImGui.TextUnformatted($"Captured: {activeSnapshot.CapturedAt:HH:mm:ss}");

            if (_marketSnapshotStore.TryGet(_activeRetainerId, out var marketSnapshot))
            {
                ImGui.TextUnformatted($"Market Items: {marketSnapshot.Items.Count}");
                ImGui.TextUnformatted($"Market Captured: {marketSnapshot.CapturedAt:HH:mm:ss}");
            }
        }
        else
        {
            ImGui.Separator();
            ImGui.TextUnformatted($"Retainer: {_activeRetainerName}");
            ImGui.TextUnformatted("No capture for this retainer yet.");
        }

        ImGui.TextUnformatted($"Session: {capturedRetainers}/{_totalRetainers} retainers captured");
    }

    private void DumpLastCapture(ulong activeRetainerId, string activeRetainerName)
    {
        if (!_snapshotStore.TryGet(activeRetainerId, out var snapshot))
        {
            _statusMessage = "No capture available for selected retainer.";
            _pluginLog.Warning($"[MarketMafioso] Dump requested, but no capture exists for {activeRetainerName} ({activeRetainerId}).");
            return;
        }

        _pluginLog.Information($"[MarketMafioso] Capture: {snapshot.RetainerName} ({snapshot.RetainerId}), listings={snapshot.Listings.Count}, captured={snapshot.CapturedAt:O}");

        foreach (var listing in snapshot.Listings)
        {
            var hq = listing.IsHq ? "HQ" : "NQ";
            _pluginLog.Information($"[MarketMafioso] slot={listing.Slot} item={listing.ItemId} name=\"{listing.ItemName}\" {hq} qty={listing.Quantity} price={listing.UnitPrice}");
        }

        _statusMessage = $"Dumped {snapshot.Listings.Count} listings to log.";
    }

    private void SyncSessionScope()
    {
        var contentId = _playerState.ContentId;
        if (contentId == _sessionContentId)
        {
            return;
        }

        _sessionContentId = contentId;
        _snapshotStore.Clear();
        _marketSnapshotStore.Clear();
        _retainerMarketCaptureService.Reset();
    }

    private void StartMarketCaptureCycle()
    {
        try
        {
            _retainerMarketCaptureService.StartActiveRetainerCaptureCycle();
            _statusMessage = _retainerMarketCaptureService.StatusMessage;
        }
        catch (Exception ex)
        {
            _statusMessage = ex.Message;
            _pluginLog.Error(ex, "[MarketMafioso] Failed to start market capture cycle.");
        }
    }
}
