using System;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using MarketMafioso.Automation.Travel;

namespace MarketMafioso.Squire.Observation;

public sealed class DalamudRetainerUiPreparation
{
    private const uint SummoningBellNameRowId = 2000401;
    private readonly ICommandManager commandManager;
    private readonly IDataManager dataManager;
    private readonly LifestreamIpc lifestream;
    private readonly VNavmeshIpc vnavmesh;
    private readonly Func<AgentBridge.AgentBridgeRenderedUiSnapshot> captureRetainerUi;
    private readonly RenderedRetainerUiPreparationCoordinator coordinator = new();

    public DalamudRetainerUiPreparation(
        ICommandManager commandManager,
        IDataManager dataManager,
        LifestreamIpc lifestream,
        VNavmeshIpc vnavmesh,
        Func<AgentBridge.AgentBridgeRenderedUiSnapshot> captureRetainerUi)
    {
        this.commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
        this.dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        this.lifestream = lifestream ?? throw new ArgumentNullException(nameof(lifestream));
        this.vnavmesh = vnavmesh ?? throw new ArgumentNullException(nameof(vnavmesh));
        this.captureRetainerUi = captureRetainerUi ?? throw new ArgumentNullException(nameof(captureRetainerUi));
    }

    public RenderedRetainerUiPreparationProgress Begin() => coordinator.Begin(
        DateTimeOffset.UtcNow,
        RetainerListVisible(),
        lifestream.IsAvailable,
        ProcessSemanticCommand);

    public RenderedRetainerUiPreparationProgress Advance()
    {
        var renderedUi = captureRetainerUi();
        var marketBoardUiVisible = AddonVisible(renderedUi, "ItemSearch") ||
                                   AddonVisible(renderedUi, "ItemSearchResult");
        if (marketBoardUiVisible)
            CloseRenderedMarketBoardUi();
        var stateAvailable = lifestream.TryIsBusy(out var busy);
        return coordinator.Advance(
            DateTimeOffset.UtcNow,
            AddonVisible(renderedUi, "RetainerList"),
            stateAvailable,
            busy,
            marketBoardUiVisible,
            vnavmesh.IsReady,
            vnavmesh.IsRunning,
            ResolveBellName(),
            ProcessSemanticCommand);
    }

    public RenderedRetainerUiPreparationProgress Cancel() => coordinator.Cancel();

    private bool RetainerListVisible() => AddonVisible(captureRetainerUi(), "RetainerList");

    private static bool AddonVisible(AgentBridge.AgentBridgeRenderedUiSnapshot snapshot, string name) =>
        snapshot.Addons.Any(value =>
        string.Equals(value.Name, name, StringComparison.Ordinal) &&
        value is { Present: true, Ready: true, Visible: true });

    private static unsafe void CloseRenderedMarketBoardUi()
    {
        CloseRenderedAddon("ItemSearchResult");
        CloseRenderedAddon("ItemSearch");
    }

    private static unsafe void CloseRenderedAddon(string addonName)
    {
        var addon = Plugin.GameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
        if (addon != null && addon->IsReady && addon->IsVisible)
            addon->Close(true);
    }

    private string ResolveBellName()
    {
        var localized = dataManager.GetExcelSheet<EObjName>()?
            .GetRowOrDefault(SummoningBellNameRowId)?
            .Singular.ToString();
        return string.IsNullOrWhiteSpace(localized) ? "Summoning Bell" : localized;
    }

    private bool ProcessSemanticCommand(string command)
    {
        if (string.Equals(command, "/li mb", StringComparison.Ordinal))
            return commandManager.ProcessCommand(command);
        if (string.Equals(command, "/vnav movetarget", StringComparison.Ordinal))
            return commandManager.ProcessCommand(command);
        if (!string.Equals(command, "/interact", StringComparison.Ordinal) &&
            !command.StartsWith("/target \"", StringComparison.Ordinal))
            return false;
        try
        {
            Chat.ExecuteCommand(command);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
