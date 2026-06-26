using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ClientGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketBoardApproachService
{
    private const string ItemSearchAddon = "ItemSearch";
    private const string ItemSearchResultAddon = "ItemSearchResult";
    private const string MarketBoardObjectName = "Market Board";

    public const float DirectInteractionDistance = 6.5f;
    public const float MaximumApproachDistance = 80f;
    public const float NavigationStopDistance = 5.5f;

    private readonly IGameGui gameGui;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly VNavmeshIpc vnavmesh;
    private readonly IPluginLog log;

    public MarketBoardApproachService(
        IGameGui gameGui,
        IObjectTable objectTable,
        ITargetManager targetManager,
        VNavmeshIpc vnavmesh,
        IPluginLog log)
    {
        this.gameGui = gameGui;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.vnavmesh = vnavmesh;
        this.log = log;
    }

    public unsafe MarketBoardApproachResult OpenOrApproach()
    {
        if (IsMarketBoardUiOpen())
            return MarketBoardApproachResult.Ready("Market board UI is already open.");

        var board = FindMarketBoard();
        var vnavmeshRunning = vnavmesh.IsRunning;
        var decision = Decide(
            marketBoardUiOpen: false,
            boardDistance: board == null ? null : GetDistance(board),
            vnavmeshAvailable: vnavmesh.IsReady,
            vnavmeshRunning: vnavmeshRunning);

        return decision.Kind switch
        {
            MarketBoardApproachDecisionKind.InteractDirectly => InteractWithBoard(board),
            MarketBoardApproachDecisionKind.StartNavigation => StartNavigation(board),
            MarketBoardApproachDecisionKind.WaitForMovement => MarketBoardApproachResult.Wait(
                "vnavmesh is moving toward the nearby market board."),
            MarketBoardApproachDecisionKind.ReadyToSearch => MarketBoardApproachResult.Ready(
                "Market board UI is already open."),
            _ => MarketBoardApproachResult.Wait(DescribeManualWait(board, vnavmeshRunning)),
        };
    }

    public void StopNavigation()
    {
        vnavmesh.Stop();
    }

    internal static MarketBoardApproachDecision Decide(
        bool marketBoardUiOpen,
        float? boardDistance,
        bool vnavmeshAvailable,
        bool vnavmeshRunning)
    {
        if (marketBoardUiOpen)
            return new(MarketBoardApproachDecisionKind.ReadyToSearch);

        if (boardDistance == null || boardDistance > MaximumApproachDistance)
            return new(MarketBoardApproachDecisionKind.WaitForManualOpen);

        if (boardDistance <= DirectInteractionDistance)
            return new(MarketBoardApproachDecisionKind.InteractDirectly);

        if (vnavmeshRunning)
            return new(MarketBoardApproachDecisionKind.WaitForMovement);

        return vnavmeshAvailable
            ? new(MarketBoardApproachDecisionKind.StartNavigation)
            : new(MarketBoardApproachDecisionKind.WaitForManualOpen);
    }

    private unsafe bool IsMarketBoardUiOpen()
    {
        return IsAddonReady(gameGui.GetAddonByName<AtkUnitBase>(ItemSearchAddon, 1)) ||
               IsAddonReady(gameGui.GetAddonByName<AtkUnitBase>(ItemSearchResultAddon, 1));
    }

    private IGameObject? FindMarketBoard()
    {
        if (IsMarketBoardObject(targetManager.Target))
            return targetManager.Target;

        return objectTable
            .Where(IsMarketBoardObject)
            .OrderBy(GetDistance)
            .FirstOrDefault();
    }

    private static bool IsMarketBoardObject(IGameObject? gameObject)
    {
        if (gameObject == null || !gameObject.IsTargetable)
            return false;

        if (gameObject.ObjectKind is not (ObjectKind.EventObj or ObjectKind.HousingEventObject or ObjectKind.ReactionEventObject))
            return false;

        return gameObject.Name.TextValue.Equals(MarketBoardObjectName, StringComparison.OrdinalIgnoreCase);
    }

    private unsafe MarketBoardApproachResult InteractWithBoard(IGameObject? board)
    {
        if (board == null)
            return MarketBoardApproachResult.Wait("No nearby market board target was found.");

        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
            return MarketBoardApproachResult.Wait("Target system is unavailable; open the market board manually.");

        targetManager.Target = board;
        var result = targetSystem->InteractWithObject((ClientGameObject*)board.Address, true);
        log.Verbose($"[MarketMafioso] Interacted with market board {board.Name.TextValue} ({board.GameObjectId:X}).");
        return MarketBoardApproachResult.Action(
            $"Interacted with nearby market board ({GetDistance(board):0.0}y).",
            new Dictionary<string, string?>
            {
                ["name"] = board.Name.TextValue,
                ["objectKind"] = board.ObjectKind.ToString(),
                ["gameObjectId"] = board.GameObjectId.ToString("X"),
                ["baseId"] = board.BaseId.ToString(),
                ["distance"] = GetDistance(board).ToString("0.00"),
                ["result"] = result.ToString(),
            });
    }

    private MarketBoardApproachResult StartNavigation(IGameObject? board)
    {
        if (board == null)
            return MarketBoardApproachResult.Wait("No nearby market board target was found.");

        var result = vnavmesh.MoveCloseTo(board.Position, NavigationStopDistance);
        if (!result.Success)
            return MarketBoardApproachResult.Wait(result.Message);

        return MarketBoardApproachResult.Action(
            $"vnavmesh is approaching nearby market board ({GetDistance(board):0.0}y).",
            new Dictionary<string, string?>
            {
                ["name"] = board.Name.TextValue,
                ["gameObjectId"] = board.GameObjectId.ToString("X"),
                ["distance"] = GetDistance(board).ToString("0.00"),
                ["destination"] = board.Position.ToString(),
            });
    }

    private string DescribeManualWait(IGameObject? board, bool vnavmeshRunning)
    {
        if (vnavmeshRunning)
            return "vnavmesh is moving toward the market board.";

        if (board == null)
            return "Open a market board manually; no nearby market board target was found.";

        var distance = GetDistance(board);
        if (distance > MaximumApproachDistance)
            return $"Open a market board manually; nearest board is {distance:0.0}y away.";

        return "Open a market board manually; vnavmesh is unavailable for approach movement.";
    }

    private static unsafe bool IsAddonReady(AtkUnitBase* addon)
    {
        return addon != null && addon->IsReady && addon->IsVisible;
    }

    private static float GetDistance(IGameObject gameObject)
    {
        return MathF.Sqrt(
            gameObject.YalmDistanceX * gameObject.YalmDistanceX +
            gameObject.YalmDistanceZ * gameObject.YalmDistanceZ);
    }
}

public enum MarketBoardApproachDecisionKind
{
    ReadyToSearch,
    InteractDirectly,
    StartNavigation,
    WaitForMovement,
    WaitForManualOpen,
}

public sealed record MarketBoardApproachDecision(MarketBoardApproachDecisionKind Kind);

public sealed record MarketBoardApproachResult
{
    public string Status { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public bool ReadyToSearch => string.Equals(Status, "ReadyToSearch", StringComparison.OrdinalIgnoreCase);
    public bool ActionTaken => string.Equals(Status, "ActionTaken", StringComparison.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string?> Details { get; init; } = new Dictionary<string, string?>();

    public static MarketBoardApproachResult Ready(string message)
    {
        return new() { Status = "ReadyToSearch", Message = message };
    }

    public static MarketBoardApproachResult Action(string message, IReadOnlyDictionary<string, string?>? details = null)
    {
        return new()
        {
            Status = "ActionTaken",
            Message = message,
            Details = details ?? new Dictionary<string, string?>(),
        };
    }

    public static MarketBoardApproachResult Wait(string message)
    {
        return new() { Status = "Waiting", Message = message };
    }
}
