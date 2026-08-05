using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Franthropy.Dalamud.Automation.Vendors;
using Franthropy.Dalamud.Travel;
using MarketMafioso.Automation.Runtime;
using MarketMafioso.Quartermaster;

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
    private readonly DalamudVNavmeshTravel vnavmesh;
    private readonly DalamudLifestreamAetheryteTravel aetheryteTravel;
    private readonly DalamudLifestreamAethernetTravel aethernetTravel;
    private readonly DalamudLifestreamObjectInteractor objectInteractor;
    private readonly DalamudTravelReadiness travelReadiness;
    private readonly ExternalAutomationCoordinator externalAutomation;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly Func<DateTimeOffset> utcNow;
    private DateTimeOffset approachStartedAt;
    private DateTimeOffset nextActionAt;
    private uint activeNpcId;
    private uint? requestedAetheryteId;
    private uint? requestedAethernetId;
    private bool ownsNavigation;

    public DalamudWorkshopVendorRestockRuntime(
        Configuration config,
        InventoryScanner scanner,
        WorkshopQuartermasterRequestService quartermaster,
        DalamudGilVendorAccessReader access,
        DalamudOrdinaryGilShop shop,
        DalamudVNavmeshTravel vnavmesh,
        DalamudLifestreamAetheryteTravel aetheryteTravel,
        DalamudLifestreamAethernetTravel aethernetTravel,
        DalamudLifestreamObjectInteractor objectInteractor,
        DalamudTravelReadiness travelReadiness,
        ExternalAutomationCoordinator externalAutomation,
        IClientState clientState,
        IObjectTable objectTable,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        this.quartermaster = quartermaster ?? throw new ArgumentNullException(nameof(quartermaster));
        this.access = access ?? throw new ArgumentNullException(nameof(access));
        this.shop = shop ?? throw new ArgumentNullException(nameof(shop));
        this.vnavmesh = vnavmesh ?? throw new ArgumentNullException(nameof(vnavmesh));
        this.aetheryteTravel = aetheryteTravel ?? throw new ArgumentNullException(nameof(aetheryteTravel));
        this.aethernetTravel = aethernetTravel ?? throw new ArgumentNullException(nameof(aethernetTravel));
        this.objectInteractor = objectInteractor ?? throw new ArgumentNullException(nameof(objectInteractor));
        this.travelReadiness = travelReadiness ?? throw new ArgumentNullException(nameof(travelReadiness));
        this.externalAutomation = externalAutomation ?? throw new ArgumentNullException(nameof(externalAutomation));
        this.clientState = clientState ?? throw new ArgumentNullException(nameof(clientState));
        this.objectTable = objectTable ?? throw new ArgumentNullException(nameof(objectTable));
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

        var readiness = travelReadiness.Advance();
        if (readiness.State is TravelReadinessState.Repairing or TravelReadinessState.Waiting)
            return new(WorkshopVendorReachState.Waiting, readiness.Message);
        if (readiness.State == TravelReadinessState.Blocked)
        {
            if (ShouldWaitForPendingTravelUi(
                    readiness,
                    requestedAetheryteId is not null || requestedAethernetId is not null))
            {
                return new(
                    WorkshopVendorReachState.Waiting,
                    "Waiting for the in-progress vendor travel to release the game UI.");
            }
            return new(WorkshopVendorReachState.Failed, readiness.Message);
        }

        if (utcNow() - approachStartedAt > ApproachTimeout)
            return new(WorkshopVendorReachState.Unavailable, $"Could not reach {offer.NpcName} within two minutes.");

        if (clientState.TerritoryType != offer.TerritoryId)
        {
            if (assessment.RouteAetheryteId is not { } route)
                return new(WorkshopVendorReachState.Unavailable, "No live owner-accessible route reaches this vendor.");

            switch (DetermineTravelLeg(
                clientState.TerritoryType,
                offer.TerritoryId,
                route,
                assessment.RouteAethernetId,
                assessment.RouteAetheryteTerritoryId,
                requestedAetheryteId,
                requestedAethernetId))
            {
                case WorkshopVendorTravelLeg.InvalidRoute:
                    return new(
                        WorkshopVendorReachState.Unavailable,
                        "The vendor's aethernet route is missing the main aetheryte territory needed to confirm arrival.");

                case WorkshopVendorTravelLeg.SubmitAetheryte:
                {
                    if (utcNow() >= nextActionAt)
                    {
                        var submission = aetheryteTravel.TrySubmit(route);
                        switch (submission.State)
                        {
                            case AetheryteTravelSubmissionState.Submitted:
                                requestedAetheryteId = route;
                                nextActionAt = utcNow().Add(ActionThrottle);
                                travelReadiness.Reset();
                                break;
                            case AetheryteTravelSubmissionState.Busy:
                                return new(WorkshopVendorReachState.Waiting, submission.Message);
                            case AetheryteTravelSubmissionState.Rejected:
                            case AetheryteTravelSubmissionState.Unavailable:
                            case AetheryteTravelSubmissionState.InvalidRequest:
                                return new(WorkshopVendorReachState.Failed, submission.Message);
                        }
                    }
                    break;
                }

                case WorkshopVendorTravelLeg.AwaitAetheryteArrival:
                    return new(
                        WorkshopVendorReachState.Waiting,
                        "Waiting to arrive at the main aetheryte before entering the destination network.");

                case WorkshopVendorTravelLeg.SubmitAethernet:
                {
                    if (requestedAetheryteId != route)
                        requestedAetheryteId = route;
                    if (assessment.RouteAethernetId is not { } aethernetId || utcNow() < nextActionAt)
                        break;

                    var submission = aethernetTravel.TrySubmit(aethernetId);
                    switch (submission.State)
                    {
                        case AetheryteTravelSubmissionState.Submitted:
                            requestedAethernetId = aethernetId;
                            nextActionAt = utcNow().Add(ActionThrottle);
                            travelReadiness.Reset();
                            break;
                        case AetheryteTravelSubmissionState.Busy:
                            return new(WorkshopVendorReachState.Waiting, submission.Message);
                        case AetheryteTravelSubmissionState.Rejected:
                            nextActionAt = utcNow().Add(ActionThrottle);
                            return new(
                                WorkshopVendorReachState.Waiting,
                                "Waiting for the destination aethernet network to accept travel.");
                        case AetheryteTravelSubmissionState.Unavailable:
                        case AetheryteTravelSubmissionState.InvalidRequest:
                            return new(WorkshopVendorReachState.Failed, submission.Message);
                    }
                    break;
                }

                case WorkshopVendorTravelLeg.AwaitDestination:
                    break;
            }
            return new(WorkshopVendorReachState.Waiting, $"Traveling to {offer.NpcName}.");
        }

        if (requestedAetheryteId is not null)
        {
            requestedAetheryteId = null;
            requestedAethernetId = null;
            approachStartedAt = utcNow();
            nextActionAt = DateTimeOffset.MinValue;
        }
        var npc = access.FindLiveNpc(offer);
        var playerPosition = objectTable.LocalPlayer?.Position;
        if (playerPosition is null)
            return new(WorkshopVendorReachState.Waiting, "Waiting for the player's position after travel.");
        var destination = npc?.Position ?? offer.Position;
        var distance = HorizontalDistance(playerPosition.Value, destination);
        if (distance <= DirectInteractionDistance)
        {
            if (npc is null)
                return new(WorkshopVendorReachState.Waiting, $"Waiting for {offer.NpcName} to become targetable.");
            StopOwnedNavigation();
            if (utcNow() < nextActionAt)
                return new(WorkshopVendorReachState.Waiting, $"Opening {offer.NpcName}'s shop.");
            return InteractWithVendor(npc, offer);
        }

        var navigation = vnavmesh.Observe();
        if (navigation.State == VNavmeshLifecycleState.Loading)
            return new(WorkshopVendorReachState.Waiting, navigation.Message);
        if (navigation.State is VNavmeshLifecycleState.Unavailable or VNavmeshLifecycleState.IpcFailure)
            return new(WorkshopVendorReachState.Failed, navigation.Message);
        var decision = DecideApproach(
            distance,
            npc is not null,
            navigation.State == VNavmeshLifecycleState.Ready,
            navigation.State == VNavmeshLifecycleState.Running,
            ownsNavigation);
        switch (decision)
        {
            case WorkshopVendorApproachDecision.WaitForOwnedRoute:
                return new(WorkshopVendorReachState.Waiting, $"Walking to {offer.NpcName} ({distance:0.0} yalms away).");
            case WorkshopVendorApproachDecision.BlockedByAnotherRoute:
                return new(WorkshopVendorReachState.Failed, "Another vnavmesh route is already active.");
            case WorkshopVendorApproachDecision.NavigationUnavailable:
                return new(WorkshopVendorReachState.Waiting, navigation.Message);
        }

        if (utcNow() >= nextActionAt)
        {
            var movement = vnavmesh.TryMoveCloseTo(destination, NavigationStopDistance);
            if (movement.State == VNavmeshPathSubmissionState.Loading)
                return new(WorkshopVendorReachState.Waiting, movement.Message);
            if (!movement.Submitted)
                return new(WorkshopVendorReachState.Failed, movement.Message);
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
        requestedAethernetId = null;
        travelReadiness.Reset();
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

    private WorkshopVendorReachResult InteractWithVendor(IGameObject npc, GilVendorOffer offer)
    {
        var menu = shop.TryAdvanceOfferMenu(offer);
        if (menu.MenuPresented)
        {
            if (!menu.Advanced)
                return new(WorkshopVendorReachState.Unavailable, menu.Message);
            nextActionAt = utcNow().Add(ActionThrottle);
            return new(WorkshopVendorReachState.Waiting, $"Choosing {offer.NpcName}'s reviewed shop.");
        }
        var interaction = objectInteractor.TryEnqueue(
            offer.NpcId,
            approachDistance: NavigationStopDistance,
            exportedName: "MarketMafioso vendor interaction");
        if (!interaction.Success)
            return new(WorkshopVendorReachState.Failed, interaction.Message);
        nextActionAt = utcNow().Add(ActionThrottle);
        return new(WorkshopVendorReachState.Waiting, $"Opening {offer.NpcName}'s shop.");
    }

    private void StopOwnedNavigation()
    {
        if (!ownsNavigation)
            return;
        if (vnavmesh.Observe().IsRunning)
            vnavmesh.TryStop();
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

    internal static WorkshopVendorTravelLeg DetermineTravelLeg(
        uint currentTerritoryId,
        uint targetTerritoryId,
        uint routeAetheryteId,
        uint? routeAethernetId,
        uint? routeAetheryteTerritoryId,
        uint? requestedAetheryteId,
        uint? requestedAethernetId)
    {
        if (currentTerritoryId == targetTerritoryId)
            return WorkshopVendorTravelLeg.AwaitDestination;

        if (routeAethernetId is null)
        {
            return requestedAetheryteId == routeAetheryteId
                ? WorkshopVendorTravelLeg.AwaitDestination
                : WorkshopVendorTravelLeg.SubmitAetheryte;
        }

        if (routeAetheryteTerritoryId is not { } aetheryteTerritoryId)
            return WorkshopVendorTravelLeg.InvalidRoute;

        if (currentTerritoryId != aetheryteTerritoryId)
        {
            return requestedAetheryteId == routeAetheryteId
                ? WorkshopVendorTravelLeg.AwaitAetheryteArrival
                : WorkshopVendorTravelLeg.SubmitAetheryte;
        }

        return requestedAethernetId == routeAethernetId
            ? WorkshopVendorTravelLeg.AwaitDestination
            : WorkshopVendorTravelLeg.SubmitAethernet;
    }

    internal static bool ShouldWaitForPendingTravelUi(
        TravelReadinessResult readiness,
        bool travelRequestPending) =>
        readiness.State == TravelReadinessState.Blocked &&
        readiness.Code == "UnknownUiOwner" &&
        travelRequestPending;
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

internal enum WorkshopVendorTravelLeg
{
    InvalidRoute,
    SubmitAetheryte,
    AwaitAetheryteArrival,
    SubmitAethernet,
    AwaitDestination,
}
