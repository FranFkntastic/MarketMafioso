using System;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using ECommons.Automation;
using Franthropy.Dalamud.AgentBridge;
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
    private readonly DalamudRenderedUiTextActionDispatcher renderedUiActions;
    private readonly Func<AgentBridge.AgentBridgeRenderedUiSnapshot> captureRetainerUi;
    private readonly RenderedRetainerUiPreparationCoordinator coordinator = new();
    private string? lastSemanticActionDiagnostic;

    public DalamudRetainerUiPreparation(
        ICommandManager commandManager,
        IDataManager dataManager,
        LifestreamIpc lifestream,
        VNavmeshIpc vnavmesh,
        DalamudRenderedUiTextActionDispatcher renderedUiActions,
        Func<AgentBridge.AgentBridgeRenderedUiSnapshot> captureRetainerUi)
    {
        this.commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
        this.dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        this.lifestream = lifestream ?? throw new ArgumentNullException(nameof(lifestream));
        this.vnavmesh = vnavmesh ?? throw new ArgumentNullException(nameof(vnavmesh));
        this.renderedUiActions = renderedUiActions ?? throw new ArgumentNullException(nameof(renderedUiActions));
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
        var localizedBellName = ResolveBellName();
        var bellTargetVisible = renderedUi.Addons
            .Where(value => value is { Present: true, Ready: true, Visible: true } &&
                            value.Name.StartsWith("_TargetInfo", StringComparison.Ordinal))
            .SelectMany(value => value.TextNodes)
            .Any(value => string.Equals(value.Text.Trim(), localizedBellName, StringComparison.OrdinalIgnoreCase));
        if (marketBoardUiVisible)
            CloseRenderedMarketBoardUi();
        var stateAvailable = lifestream.TryIsBusy(out var busy);
        var progress = coordinator.Advance(
            DateTimeOffset.UtcNow,
            AddonVisible(renderedUi, "RetainerList"),
            stateAvailable,
            busy,
            marketBoardUiVisible,
            bellTargetVisible,
            vnavmesh.IsReady,
            vnavmesh.IsRunning,
            localizedBellName,
            ProcessSemanticCommand);
        return progress.Status == RenderedRetainerUiPreparationStatus.Failed &&
               !string.IsNullOrWhiteSpace(lastSemanticActionDiagnostic)
            ? progress with { Diagnostic = $"{progress.Diagnostic} {lastSemanticActionDiagnostic}" }
            : progress;
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
        lastSemanticActionDiagnostic = null;
        if (string.Equals(command, "/li mb", StringComparison.Ordinal))
            return commandManager.ProcessCommand(command);
        if (string.Equals(command, "/vnav movetarget", StringComparison.Ordinal))
            return commandManager.ProcessCommand(command);
        const string nameplatePrefix = "rendered-ui:rollover-nameplate:";
        if (command.StartsWith(nameplatePrefix, StringComparison.Ordinal))
        {
            var result = renderedUiActions.TryRollOverUniqueText("NamePlate", command[nameplatePrefix.Length..]);
            if (!result.Success)
                lastSemanticActionDiagnostic = $"Rendered UI action {result.Code}: {result.Message}";
            return result.Success;
        }
        if (!string.Equals(command, "/confirm", StringComparison.Ordinal))
            return false;
        try
        {
            Chat.ExecuteCommand(command);
            return true;
        }
        catch (Exception)
        {
            lastSemanticActionDiagnostic = "The native confirm UI command threw before dispatch.";
            return false;
        }
    }
}
