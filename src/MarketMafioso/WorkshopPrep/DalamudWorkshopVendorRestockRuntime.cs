using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Franthropy.Dalamud.Automation.Vendors;
using MarketMafioso.Automation.Runtime;
using MarketMafioso.Automation.Travel;
using MarketMafioso.Quartermaster;
using ClientGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace MarketMafioso.WorkshopPrep;

public sealed class DalamudWorkshopVendorRestockRuntime : IWorkshopVendorRestockRuntime
{
    private static readonly TimeSpan ApproachTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ActionThrottle = TimeSpan.FromSeconds(2);
    private const float DirectInteractionDistance = 4.25f;
    private const float NavigationStopDistance = 3.5f;
    private readonly Configuration config;
    private readonly InventoryScanner scanner;
    private readonly WorkshopQuartermasterRequestService quartermaster;
    private readonly DalamudGilVendorAccessReader access;
    private readonly DalamudOrdinaryGilShop shop;
    private readonly VNavmeshIpc vnavmesh;
    private readonly ExternalAutomationCoordinator externalAutomation;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly Func<DateTimeOffset> utcNow;
    private DateTimeOffset approachStartedAt;
    private DateTimeOffset nextActionAt;
    private uint activeNpcId;
    private uint? requestedAetheryteId;
    private bool ownsNavigation;

    public DalamudWorkshopVendorRestockRuntime(
        Configuration config,
        InventoryScanner scanner,
        WorkshopQuartermasterRequestService quartermaster,
        DalamudGilVendorAccessReader access,
        DalamudOrdinaryGilShop shop,
        VNavmeshIpc vnavmesh,
        ExternalAutomationCoordinator externalAutomation,
        IClientState clientState,
        IObjectTable objectTable,
        ITargetManager targetManager,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        this.quartermaster = quartermaster ?? throw new ArgumentNullException(nameof(quartermaster));
        this.access = access ?? throw new ArgumentNullException(nameof(access));
        this.shop = shop ?? throw new ArgumentNullException(nameof(shop));
        this.vnavmesh = vnavmesh ?? throw new ArgumentNullException(nameof(vnavmesh));
        this.externalAutomation = externalAutomation ?? throw new ArgumentNullException(nameof(externalAutomation));
        this.clientState = clientState ?? throw new ArgumentNullException(nameof(clientState));
        this.objectTable = objectTable ?? throw new ArgumentNullException(nameof(objectTable));
        this.targetManager = targetManager ?? throw new ArgumentNullException(nameof(targetManager));
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public WorkshopVendorInventorySnapshot CaptureInventory(IReadOnlyCollection<uint> itemIds)
    {
        var bags = scanner.CapturePlayerBagPurchaseState();
        if (!bags.IsComplete)
            return new(false, null, new Dictionary<uint, int>(), bags.Message);
        var counts = itemIds.ToDictionary(itemId => itemId, itemId => bags.ItemCounts.GetValueOrDefault(itemId));
        var gil = scanner.ScanPlayerGil();
        return gil is null
            ? new(false, null, counts, "Player gil is temporarily unavailable.")
            : new(true, gil, counts, "Player inventory and gil are ready.");
    }

    public bool HasCapacity(IReadOnlyDictionary<uint, int> quantities, out string message)
    {
        var bags = scanner.CapturePlayerBagPurchaseState();
        if (!bags.IsComplete)
        {
            message = bags.Message;
            return false;
        }

        var freeSlots = bags.FreeSlots;
        foreach (var (itemId, quantity) in quantities.Where(pair => pair.Value > 0))
        {
            var maxStack = Math.Max(1, scanner.ResolveItemMetadata(itemId).MaxStack);
            var existingSpace = bags.OccupiedSlots
                .Where(slot => slot.ItemId == itemId && !slot.IsHighQuality)
                .Sum(slot => Math.Max(0, maxStack - slot.Quantity));
            var requiringSlots = Math.Max(0, quantity - existingSpace);
            var slotsNeeded = (requiringSlots + maxStack - 1) / maxStack;
            freeSlots -= slotsNeeded;
            if (freeSlots < 0)
            {
                message = $"Not enough player-inventory stack capacity remains for {scanner.ResolveItemName(itemId) ?? $"item {itemId}"}; free a bag slot and resume.";
                return false;
            }
        }

        message = "Player inventory has enough stack capacity for the reviewed vendor quantities.";
        return true;
    }

    public bool TryStartQuartermaster(
        QuartermasterOwnerScope owner,
        IReadOnlyList<WorkshopMaterialAvailability> availability,
        out string error)
    {
        if (quartermaster.Submit(owner, availability))
        {
            error = string.Empty;
            return true;
        }
        error = quartermaster.LastStatus;
        return false;
    }

    public WorkshopQuartermasterProgress GetQuartermasterProgress(QuartermasterOwnerScope owner)
    {
        var key = $"{owner.LocalContentId!.Value.ToString(CultureInfo.InvariantCulture)}:{owner.HomeWorldId!.Value.ToString(CultureInfo.InvariantCulture)}";
        if (!config.QuartermasterWorkshopRequests.TryGetValue(key, out var state))
            return new(WorkshopQuartermasterProgressState.NotStarted, "Waiting for Quartermaster operation identity.");
        var status = state.Status?.Trim() ?? string.Empty;
        if (status.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("succeeded", StringComparison.OrdinalIgnoreCase))
        {
            return new(WorkshopQuartermasterProgressState.Completed, state.Message ?? "Quartermaster retrieval completed.");
        }
        if (status.Equals("partially_succeeded", StringComparison.OrdinalIgnoreCase))
            return new(WorkshopQuartermasterProgressState.PartiallySucceeded, state.Message ?? "Quartermaster retrieval partially succeeded.");
        if (status.Equals("indeterminate", StringComparison.OrdinalIgnoreCase))
            return new(WorkshopQuartermasterProgressState.Indeterminate, state.Message ?? "Quartermaster retrieval outcome is indeterminate.");
        if (status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("cancelled", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("rejected", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("not_found", StringComparison.OrdinalIgnoreCase))
        {
            return new(WorkshopQuartermasterProgressState.Failed, state.Message ?? $"Quartermaster retrieval {status}.");
        }
        return new(WorkshopQuartermasterProgressState.Running, state.Message ?? $"Quartermaster retrieval {status}.");
    }

    public WorkshopVendorReachResult AdvanceToOpenShop(GilVendorOffer offer)
    {
        if (shop.IsOpen)
            return new(WorkshopVendorReachState.ShopOpen, $"Opened {offer.NpcName}'s shop.");
        if (activeNpcId != offer.NpcId)
        {
            ResetVendorApproach();
            activeNpcId = offer.NpcId;
            approachStartedAt = utcNow();
        }

        var assessment = access.Assess(offer);
        if (!assessment.IsEligible)
        {
            return assessment.State == GilVendorAccessState.Unknown
                ? new(WorkshopVendorReachState.Waiting, assessment.Message)
                : new(WorkshopVendorReachState.Unavailable, assessment.Message);
        }

        if (clientState.TerritoryType != offer.TerritoryId)
        {
            if (assessment.RouteAetheryteId is not { } route)
                return new(WorkshopVendorReachState.Unavailable, "No live owner-accessible route reaches this vendor.");
            if (requestedAetheryteId != route && utcNow() >= nextActionAt)
            {
                if (!access.TryTeleport(route))
                    return new(WorkshopVendorReachState.Failed, "The verified vendor teleport could not be started.");
                requestedAetheryteId = route;
                nextActionAt = utcNow().Add(ActionThrottle);
            }
            return new(WorkshopVendorReachState.Waiting, $"Traveling to {offer.NpcName}.");
        }

        if (requestedAetheryteId is not null)
        {
            requestedAetheryteId = null;
            approachStartedAt = utcNow();
            nextActionAt = DateTimeOffset.MinValue;
        }
        if (utcNow() - approachStartedAt > ApproachTimeout)
            return new(WorkshopVendorReachState.Unavailable, $"Could not reach {offer.NpcName} within two minutes.");

        var npc = access.FindLiveNpc(offer);
        var playerPosition = objectTable.LocalPlayer?.Position;
        if (playerPosition is null)
            return new(WorkshopVendorReachState.Waiting, "Waiting for the player's position after travel.");
        var destination = npc?.Position ?? offer.Position;
        var distance = HorizontalDistance(playerPosition.Value, destination);
        var decision = DecideApproach(
            distance,
            npc is not null,
            vnavmesh.IsReady,
            vnavmesh.IsRunning,
            ownsNavigation);
        switch (decision)
        {
            case WorkshopVendorApproachDecision.Interact:
                StopOwnedNavigation();
                if (utcNow() < nextActionAt)
                    return new(WorkshopVendorReachState.Waiting, $"Opening {offer.NpcName}'s shop.");
                return InteractWithVendor(npc!, offer);
            case WorkshopVendorApproachDecision.WaitForNpc:
                return new(WorkshopVendorReachState.Waiting, $"Waiting for {offer.NpcName} to become targetable.");
            case WorkshopVendorApproachDecision.WaitForOwnedRoute:
                return new(WorkshopVendorReachState.Waiting, $"Walking to {offer.NpcName} ({distance:0.0} yalms away).");
            case WorkshopVendorApproachDecision.BlockedByAnotherRoute:
                return new(WorkshopVendorReachState.Failed, "Another vnavmesh route is already active.");
            case WorkshopVendorApproachDecision.NavigationUnavailable:
                return new(WorkshopVendorReachState.Failed, "vnavmesh is required to walk from the aetheryte to this vendor.");
        }

        if (utcNow() >= nextActionAt)
        {
            var movement = vnavmesh.MoveCloseTo(destination, NavigationStopDistance);
            if (!movement.Success)
                return new(WorkshopVendorReachState.Failed, $"Could not start the route to {offer.NpcName}.");
            ownsNavigation = true;
            nextActionAt = utcNow().Add(ActionThrottle);
        }
        return new(WorkshopVendorReachState.Waiting, $"Walking to {offer.NpcName} ({distance:0.0} yalms away).");
    }

    public void ResetVendorApproach()
    {
        StopOwnedNavigation();
        approachStartedAt = utcNow();
        nextActionAt = DateTimeOffset.MinValue;
        activeNpcId = 0;
        requestedAetheryteId = null;
    }

    public GilVendorShopReadResult ReadShopRows() => shop.ReadRows();

    public bool TrySubmitPurchase(GilVendorShopRow row, uint quantity, out string error) =>
        shop.TrySubmitPurchase(row, quantity, out error);

    public bool TryConfirmPurchasePrompt() => shop.TryConfirmOwnedPrompt();

    public int ResolveMaximumBatch(uint itemId) =>
        scanner.ResolveItemMetadata(itemId).MaxStack <= 1 ? 1 : 99;

    public void CloseShop() => shop.Close();

    public void BeginAutomation()
    {
        externalAutomation.SuppressTextAdvance();
        externalAutomation.SuppressTradeAutoConfirm();
    }

    public void EndAutomation()
    {
        StopOwnedNavigation();
        externalAutomation.RestoreTextAdvance();
        externalAutomation.RestoreTradeAutoConfirm();
    }

    private unsafe WorkshopVendorReachResult InteractWithVendor(IGameObject npc, GilVendorOffer offer)
    {
        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null)
            return new(WorkshopVendorReachState.Failed, "The game interaction system is temporarily unavailable.");

        targetManager.Target = npc;
        targetSystem->InteractWithObject((ClientGameObject*)npc.Address, false);
        nextActionAt = utcNow().Add(ActionThrottle);
        return new(WorkshopVendorReachState.Waiting, $"Opening {offer.NpcName}'s shop.");
    }

    private void StopOwnedNavigation()
    {
        if (!ownsNavigation)
            return;
        if (vnavmesh.IsRunning)
            vnavmesh.Stop();
        ownsNavigation = false;
    }

    private static float HorizontalDistance(Vector3 first, Vector3 second)
    {
        var dx = first.X - second.X;
        var dz = first.Z - second.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    internal static WorkshopVendorApproachDecision DecideApproach(
        float distance,
        bool npcAvailable,
        bool navigationReady,
        bool navigationRunning,
        bool ownsNavigation)
    {
        if (distance <= DirectInteractionDistance)
        {
            return npcAvailable
                ? WorkshopVendorApproachDecision.Interact
                : WorkshopVendorApproachDecision.WaitForNpc;
        }
        if (navigationRunning)
        {
            return ownsNavigation
                ? WorkshopVendorApproachDecision.WaitForOwnedRoute
                : WorkshopVendorApproachDecision.BlockedByAnotherRoute;
        }
        return navigationReady
            ? WorkshopVendorApproachDecision.StartNavigation
            : WorkshopVendorApproachDecision.NavigationUnavailable;
    }
}

internal enum WorkshopVendorApproachDecision
{
    Interact,
    WaitForNpc,
    StartNavigation,
    WaitForOwnedRoute,
    BlockedByAnotherRoute,
    NavigationUnavailable,
}
