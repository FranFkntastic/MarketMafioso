using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game;
using Dalamud.Game.ClientState.Objects.Enums;
using Franthropy.Dalamud.Travel;
using Lumina.Excel.Sheets;
using MarketMafioso.Automation.Travel;

namespace MarketMafioso.MarketAcquisition;

// Route-local policy; Franthropy owns the structured aethernet transport.
public sealed class MarketAcquisitionAethernetDeparture
{
    private const float MaximumWalkDistance = 80f;
    private const float ShardRange = 3.5f;
    private const float PlazaRange = 20f;
    private readonly VNavmeshIpc navigation;
    private readonly LifestreamIpc lifestream;
    private readonly DalamudLifestreamAethernetTravel aethernet;
    private readonly Func<bool> enabled;
    private uint shardId;
    private uint boardTerritory;
    private uint plazaId;
    private uint plazaTerritory;
    private bool ownsNavigation;
    private bool transferPending;
    private bool resetting;
    private string? targetWorld;
    public string Stage { get; private set; } = "NotStarted";
    public string Message { get; private set; } = string.Empty;
    public string NetworkIdentity => $"Aetheryte shard={shardId}, plaza={plazaId}; TerritoryType board={boardTerritory}, plaza={plazaTerritory}";
    public bool IsInProgress => ownsNavigation || transferPending || Stage is "WalkToAethernetShard" or "AethernetTransferSubmitted" or "ArrivedAtPlaza" or "FallbackDraining";
    public bool HasPendingCleanup => resetting && (ownsNavigation || transferPending);

    public MarketAcquisitionAethernetDeparture(VNavmeshIpc navigation, LifestreamIpc lifestream, Func<bool> enabled)
    {
        this.navigation = navigation;
        this.lifestream = lifestream;
        this.enabled = enabled;
        aethernet = new(Plugin.PluginInterface);
    }

    // False means keep the preparation operation pending; true permits normal preflight.
    public bool Tick(string nextWorld, string currentWorld)
    {
        try
        {
            return TickCore(nextWorld, currentWorld);
        }
        catch (Exception ex)
        {
            FallBack($"Aethernet departure unavailable: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private bool TickCore(string nextWorld, string currentWorld)
    {
        if (resetting || targetWorld != nextWorld)
        {
            // Cancellation cannot cancel Lifestream's queue. Drain it before observing
            // the new starting position, and never lose a rejected navigation stop.
            if (!StopNavigation() || (transferPending && !IsLifestreamIdle()))
                return Waiting("FallbackDraining", "Waiting for interrupted local departure to become idle.");
            resetting = false;
            transferPending = false;
            targetWorld = nextWorld;
            Stage = "NotStarted";
        }

        if (Stage == "FallbackDraining")
        {
            if (!StopNavigation() || !IsLifestreamIdle())
                return false;
            transferPending = false;
            Stage = "DirectLifestream";
            return true;
        }
        if (Stage is "DirectLifestream" or "ArrivedAtPlaza")
            return true;

        if (Stage == "NotStarted")
        {
            if (!enabled() || nextWorld.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
                return Skip("Aethernet departure disabled or next stop is on the current world.");
            if (!ResolveNetwork())
                return Skip("Ul'dah aethernet sheet data could not be resolved uniquely.");
            if (!IsLifestreamIdle())
                return Skip("Lifestream is unavailable or busy; using normal world travel preflight.");
            if (AtPlaza())
                return Waiting("ArrivedAtPlaza", "Already at Ul'dah Aetheryte Plaza; preparing world travel.");
            if (Plugin.ClientState.TerritoryType != boardTerritory || !navigation.IsReady ||
                !navigation.TryIsIdle(out var idle) || !idle)
                return Skip("Ul'dah board territory or idle, ready navigation was not proven.");
            var shard = FindNode(shardId, MaximumWalkDistance);
            if (shard == null)
                return Skip("Sapphire Avenue Exchange shard not observed within walking range.");
            // Own even a rejected submission until Stop acknowledges it: an IPC
            // exception does not prove that the remote side did nothing.
            ownsNavigation = true;
            var move = navigation.MoveCloseTo(shard.Value, ShardRange);
            if (!move.Success)
            {
                FallBack(move.Message);
                return false;
            }
            return Waiting("WalkToAethernetShard", "Walking to Sapphire Avenue Exchange before world travel.");
        }

        if (Stage == "WalkToAethernetShard")
        {
            if (Plugin.ClientState.TerritoryType != boardTerritory || !navigation.IsReady)
            {
                FallBack("Territory or navigation readiness changed during shard approach.");
                return false;
            }
            if (FindNode(shardId, ShardRange + 0.75f) == null)
                return false;
            if (!StopNavigation() || !IsLifestreamIdle())
                return false;
            // Mark before IPC so an ambiguous submission is drained, never retried.
            transferPending = true;
            var submission = aethernet.TrySubmit(plazaId);
            if (submission.State != AetheryteTravelSubmissionState.Submitted)
            {
                FallBack(submission.Message);
                return false;
            }
            return Waiting("AethernetTransferSubmitted", "Aethernet transfer submitted; verifying arrival at Ul'dah Aetheryte Plaza.");
        }

        if (Stage == "AethernetTransferSubmitted" && AtPlaza() && IsLifestreamIdle())
        {
            transferPending = false;
            return Waiting("ArrivedAtPlaza", "Verified arrival at Ul'dah Aetheryte Plaza; preparing world travel.");
        }
        return false;
    }

    public void FallBack(string reason)
    {
        Message = $"{reason} Falling back to direct Lifestream world travel after owned movement and aethernet are idle.";
        Stage = "FallbackDraining";
        StopNavigation();
    }

    public bool Cleanup()
    {
        resetting = true;
        return StopNavigation();
    }

    public bool ReconcileCleanup()
    {
        if (!resetting)
            return true;
        if (!StopNavigation() || (transferPending && !IsLifestreamIdle()))
            return false;
        transferPending = false;
        return true;
    }

    private bool StopNavigation()
    {
        if (!ownsNavigation)
            return true;
        if (navigation.IsAvailable)
        {
            if (!navigation.Stop().Success || !navigation.TryIsIdle(out var idle) || !idle)
                return false;
        }
        ownsNavigation = false;
        return true;
    }

    private bool IsLifestreamIdle() => lifestream.IsAvailable && lifestream.TryIsBusy(out var busy) && !busy;

    private bool AtPlaza() => Plugin.ClientState.TerritoryType == plazaTerritory && FindNode(plazaId, PlazaRange) != null;

    private Vector3? FindNode(uint id, float range)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
            return null;
        return Plugin.ObjectTable
            .Where(obj => obj.ObjectKind == ObjectKind.Aetheryte && obj.BaseId == id && obj.IsTargetable &&
                          Vector3.Distance(player.Position, obj.Position) <= range)
            .Select(obj => (Vector3?)obj.Position).FirstOrDefault();
    }

    private bool ResolveNetwork()
    {
        // Resolve localized-client-independent identities from English Aetheryte /
        // AethernetName rows, then validate both TerritoryType references. No
        // guessed aethernet ids, object names, coordinates or territory ids.
        var rows = Plugin.DataManager.GetExcelSheet<Aetheryte>(ClientLanguage.English);
        var shards = rows.Where(row => !row.IsAetheryte && row.AethernetGroup != 0 &&
            row.AethernetName.Value.Name.ToString() == "Sapphire Avenue Exchange").ToArray();
        if (shards.Length != 1)
            return false;
        var shard = shards[0];
        var plazas = rows.Where(row => row.IsAetheryte && row.AethernetGroup == shard.AethernetGroup).ToArray();
        if (plazas.Length != 1)
            return false;
        var plaza = plazas[0];
        var territories = Plugin.DataManager.GetExcelSheet<TerritoryType>();
        if (shard.Territory.RowId == 0 || plaza.Territory.RowId == 0 ||
            !territories.HasRow(shard.Territory.RowId) || !territories.HasRow(plaza.Territory.RowId))
            return false;
        shardId = shard.RowId;
        boardTerritory = shard.Territory.RowId;
        plazaId = plaza.RowId;
        plazaTerritory = plaza.Territory.RowId;
        return true;
    }

    private bool Skip(string reason)
    {
        Stage = "DirectLifestream";
        Message = reason;
        return true;
    }

    private bool Waiting(string stage, string message)
    {
        Stage = stage;
        Message = message;
        return false;
    }
}
