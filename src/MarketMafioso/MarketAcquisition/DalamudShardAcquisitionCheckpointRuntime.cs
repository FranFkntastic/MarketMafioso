using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Automation.Retainers;
using Franthropy.Dalamud.Travel;
using MarketMafioso.Automation.Travel;
using MarketMafioso.Quartermaster;

namespace MarketMafioso.MarketAcquisition;

public sealed class DalamudShardAcquisitionCheckpointRuntime : IShardAcquisitionCheckpointRuntime
{
    private readonly IPlayerState playerState;
    private readonly InventoryScanner scanner;
    private readonly IMarketAcquisitionRouteUiAutomation uiAutomation;
    private readonly DalamudLifestreamPropertyTravel propertyTravel;
    private readonly LifestreamIpc lifestream;

    public DalamudShardAcquisitionCheckpointRuntime(
        IPlayerState playerState,
        InventoryScanner scanner,
        IMarketAcquisitionRouteUiAutomation uiAutomation,
        DalamudLifestreamPropertyTravel propertyTravel,
        LifestreamIpc lifestream)
    {
        this.playerState = playerState;
        this.scanner = scanner;
        this.uiAutomation = uiAutomation;
        this.propertyTravel = propertyTravel;
        this.lifestream = lifestream;
    }

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public string? CurrentWorldName => playerState.CurrentWorld.IsValid ? playerState.CurrentWorld.Value.Name.ToString() : null;

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

    public IReadOnlyDictionary<uint, int> CountPlayerShards() =>
        scanner.CountPlayerCrystals()
            .Where(entry => ElementalCurrencyCatalog.IsShard(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value);

    public bool TryCloseMarketBoardWindows() => uiAutomation.TryCloseMarketBoardWindows();
    public bool ProcessCommand(string command) => uiAutomation.ProcessCommand(command);
    public bool TryIsLifestreamBusy(out bool busy) => lifestream.TryIsBusy(out busy);
    public PrivateEstateTravelResult TryTravelToPrivateEstate() => propertyTravel.TrySubmit();
    public bool TryOpenSummoningBell() => lifestream.TryEnqueueObjectInteraction(DalamudSummoningBellInteractor.SummoningBellNameRowId);
}
