using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using MarketMafioso.Automation.Runtime;
using MarketMafioso.Automation.Travel;
using MarketMafioso.Quartermaster;

namespace MarketMafioso.MarketAcquisition;

public sealed class DalamudMarketAcquisitionDepositCheckpointRuntime : IMarketAcquisitionDepositCheckpointRuntime
{
    private readonly IPlayerState playerState;
    private readonly Func<IReadOnlyList<CrossRegionBagStack>?> readCarriedBags;
    private readonly IMarketAcquisitionRouteUiAutomation uiAutomation;
    private readonly LifestreamIpc lifestream;
    private readonly QuartermasterIpcClient quartermaster;
    private readonly ICondition? condition;

    public DalamudMarketAcquisitionDepositCheckpointRuntime(
        IPlayerState playerState,
        Func<IReadOnlyList<CrossRegionBagStack>?> readCarriedBags,
        IMarketAcquisitionRouteUiAutomation uiAutomation,
        LifestreamIpc lifestream,
        QuartermasterIpcClient quartermaster,
        ICondition? condition = null)
    {
        this.playerState = playerState;
        this.readCarriedBags = readCarriedBags;
        this.uiAutomation = uiAutomation;
        this.lifestream = lifestream;
        this.quartermaster = quartermaster;
        this.condition = condition;
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public string? CurrentWorldName => playerState.CurrentWorld.IsValid ? playerState.CurrentWorld.Value.Name.ToString() : null;
    public string? ActionBlocker => condition is null ? null : PlayerActionReadiness.Blocker(condition);

    public bool CanForward =>
        quartermaster.TryGetCapabilities(out var capabilities, out _) &&
        capabilities!.Capabilities.Contains(QuartermasterIpcClient.DeliveredStowForwardCapability, StringComparer.Ordinal);

    public bool TryGetOwner(out QuartermasterOwner owner)
    {
        owner = new(
            playerState.ContentId,
            playerState.HomeWorld.IsValid ? playerState.HomeWorld.RowId : 0,
            playerState.CharacterName ?? string.Empty,
            playerState.HomeWorld.IsValid ? playerState.HomeWorld.Value.Name.ToString() : null);
        return owner.LocalContentId != 0 &&
               owner.HomeWorldId != 0 &&
               !string.IsNullOrWhiteSpace(owner.CharacterName) &&
               !string.IsNullOrWhiteSpace(owner.HomeWorldName);
    }

    public IReadOnlyList<CrossRegionBagStack>? ReadCarriedBags() => readCarriedBags();
    public bool TryCloseMarketBoardWindows() => uiAutomation.TryCloseMarketBoardWindows();
    public bool ProcessCommand(string command) => uiAutomation.ProcessCommand(command);
    public bool TryIsLifestreamBusy(out bool busy) => lifestream.TryIsBusy(out busy);
}
