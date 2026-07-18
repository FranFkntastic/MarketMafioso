using System;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using ECommons.Automation;
using Lumina.Excel.Sheets;
using MarketMafioso.Automation.Travel;

namespace MarketMafioso.Squire.Observation;

public sealed class DalamudRetainerUiPreparation
{
    private const uint SummoningBellNameRowId = 2000401;
    private readonly ICommandManager commandManager;
    private readonly IDataManager dataManager;
    private readonly LifestreamIpc lifestream;
    private readonly Func<AgentBridge.AgentBridgeRenderedUiSnapshot> captureRetainerUi;
    private readonly RenderedRetainerUiPreparationCoordinator coordinator = new();

    public DalamudRetainerUiPreparation(
        ICommandManager commandManager,
        IDataManager dataManager,
        LifestreamIpc lifestream,
        Func<AgentBridge.AgentBridgeRenderedUiSnapshot> captureRetainerUi)
    {
        this.commandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
        this.dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        this.lifestream = lifestream ?? throw new ArgumentNullException(nameof(lifestream));
        this.captureRetainerUi = captureRetainerUi ?? throw new ArgumentNullException(nameof(captureRetainerUi));
    }

    public RenderedRetainerUiPreparationProgress Begin() => coordinator.Begin(
        DateTimeOffset.UtcNow,
        RetainerListVisible(),
        lifestream.IsAvailable,
        ProcessSemanticCommand);

    public RenderedRetainerUiPreparationProgress Advance()
    {
        var stateAvailable = lifestream.TryIsBusy(out var busy);
        return coordinator.Advance(
            DateTimeOffset.UtcNow,
            RetainerListVisible(),
            stateAvailable,
            busy,
            ResolveBellName(),
            ProcessSemanticCommand);
    }

    public RenderedRetainerUiPreparationProgress Cancel() => coordinator.Cancel();

    private bool RetainerListVisible() => captureRetainerUi().Addons.Any(value =>
        string.Equals(value.Name, "RetainerList", StringComparison.Ordinal) &&
        value is { Present: true, Ready: true, Visible: true });

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
