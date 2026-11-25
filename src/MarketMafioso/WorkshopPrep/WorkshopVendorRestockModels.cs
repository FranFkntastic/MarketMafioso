using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Automation.Vendors.Coordination;

namespace MarketMafioso.WorkshopPrep;

public enum WorkshopVendorRestockPhase
{
    Idle,
    RetrieveFromQuartermaster,
    RefreshInventory,
    ReachVendor,
    ValidateShop,
    PurchaseLine,
    VerifyReceipt,
    Paused,
    Completed,
    Stopped,
    Failed,
    Indeterminate,
    ReconcileReceipt,
}

// C1 compatibility schema. New runs serialize WorkshopVendorRestockState plus
// Franthropy's GilVendorBuyRunSnapshot; these types exist only to read pre-extraction data.
[Serializable]
public sealed class PersistedWorkshopVendorRestockRun
{
    public string RunId { get; set; } = string.Empty;
    public ulong LocalContentId { get; set; }
    public uint HomeWorldId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public string QueueSignature { get; set; } = string.Empty;
    public bool AutomaticallyBuyVendorMaterials { get; set; }
    public ulong MaximumApprovedGil { get; set; }
    public WorkshopVendorRestockPhase Phase { get; set; }
    public WorkshopVendorRestockPhase ResumePhase { get; set; }
    public bool QuartermasterSubmitted { get; set; }
    public bool StopRequested { get; set; }
    public int StopIndex { get; set; }
    public int LineIndex { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<PersistedWorkshopVendorRestockLine> Lines { get; set; } = [];
    public List<PersistedWorkshopVendorStop> Stops { get; set; } = [];
    public PersistedWorkshopVendorPurchaseIntent? ArmedPurchase { get; set; }
    public List<PersistedWorkshopVendorPurchaseReceipt> Receipts { get; set; } = [];
}

[Serializable]
public sealed class PersistedWorkshopVendorRestockLine
{
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int RequiredQuantity { get; set; }
    public int ReviewedRetainerQuantity { get; set; }
    public int ApprovedVendorQuantity { get; set; }
    public int PurchasedQuantity { get; set; }
    public int PurchaseRetryCount { get; set; }
    public uint UnitPriceGil { get; set; }
    public ulong ApprovedGilCeiling { get; set; }
    public int LivePlayerQuantity { get; set; }
    public bool VendorUnavailable { get; set; }
    public string Status { get; set; } = "Waiting";
    public PersistedGilVendorOffer? Offer { get; set; }
    public List<PersistedGilVendorOffer> AlternativeOffers { get; set; } = [];
}

[Serializable]
public sealed class PersistedWorkshopVendorStop
{
    public uint NpcId { get; set; }
    public uint ShopId { get; set; }
    public uint TerritoryId { get; set; }
    public string NpcName { get; set; } = string.Empty;
    public List<uint> ItemIds { get; set; } = [];
    public Dictionary<uint, int> MatchedShopRows { get; set; } = [];
    public bool ShopValidated { get; set; }
}

[Serializable]
public sealed class PersistedGilVendorOffer
{
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public uint IconId { get; set; }
    public uint UnitPriceGil { get; set; }
    public uint ShopId { get; set; }
    public uint ShopRowIndex { get; set; }
    public uint NpcId { get; set; }
    public string NpcName { get; set; } = string.Empty;
    public uint TerritoryId { get; set; }
    public float PositionX { get; set; }
    public float PositionY { get; set; }
    public float PositionZ { get; set; }
    public List<uint> RouteAetheryteIds { get; set; } = [];
    public List<PersistedGilVendorTravelRoute> TravelRoutes { get; set; } = [];

    public GilVendorBuyOfferSnapshot ToSnapshot() => new()
    {
        ItemId = ItemId,
        ItemName = ItemName,
        IconId = IconId,
        UnitPriceGil = UnitPriceGil,
        ShopId = ShopId,
        ShopRowIndex = ShopRowIndex,
        NpcId = NpcId,
        NpcName = NpcName,
        TerritoryId = TerritoryId,
        PositionX = PositionX,
        PositionY = PositionY,
        PositionZ = PositionZ,
        RouteAetheryteIds = [.. RouteAetheryteIds],
        TravelRoutes = TravelRoutes.Select(route => new GilVendorBuyRouteSnapshot
        {
            AetheryteId = route.AetheryteId,
            AethernetId = route.AethernetId,
            AetheryteTerritoryId = route.AetheryteTerritoryId,
        }).ToList(),
    };
}

[Serializable]
public sealed class PersistedGilVendorTravelRoute
{
    public uint AetheryteId { get; set; }
    public uint? AethernetId { get; set; }
    public uint? AetheryteTerritoryId { get; set; }
}

[Serializable]
public sealed class PersistedWorkshopVendorPurchaseIntent
{
    public uint ItemId { get; set; }
    public int Quantity { get; set; }
    public ulong ExpectedGil { get; set; }
    public int ShopRowIndex { get; set; }
    public int BeforeItemCount { get; set; }
    public ulong BeforeGil { get; set; }
    public int RetryCount { get; set; }
    public DateTime ArmedAtUtc { get; set; }
}

[Serializable]
public sealed class PersistedWorkshopVendorPurchaseReceipt
{
    public uint ItemId { get; set; }
    public int Quantity { get; set; }
    public ulong SpentGil { get; set; }
    public int BeforeItemCount { get; set; }
    public int AfterItemCount { get; set; }
    public ulong BeforeGil { get; set; }
    public ulong AfterGil { get; set; }
    public DateTime VerifiedAtUtc { get; set; }
}

[Serializable]
public sealed class WorkshopVendorRestockState
{
    public string? QuartermasterPartialResult { get; set; }
    // Set when Quartermaster reports an indeterminate retrieval; bounds the bag reconcile.
    public DateTime? RetrievalReconcileStartedAtUtc { get; set; }
    public ulong LocalContentId { get; set; }
    public uint HomeWorldId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public string QueueSignature { get; set; } = string.Empty;
    public bool AutomaticallyBuyVendorMaterials { get; set; }
    public bool QuartermasterSubmitted { get; set; }
    public WorkshopVendorRestockPhase Phase { get; set; }
    public WorkshopVendorRestockPhase ResumePhase { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<WorkshopVendorRestockPolicyLine> Lines { get; set; } = [];
    public List<GilVendorBuyStopSnapshot> Stops { get; set; } = [];
    // Share runs only: the split share this run restocks for and its raw-terms demand, frozen at
    // start. Null on prep-queue runs (and on every run saved before share restock existed).
    public Guid? DemandShareJobId { get; set; }
    public string? DemandShareName { get; set; }
    public List<WorkshopRestockDemandLine>? FrozenRequirements { get; set; }
}

/// <summary>One frozen raw-terms demand line: Quantity is the total bag target (owed + kept for the active plan).</summary>
[Serializable]
public sealed class WorkshopRestockDemandLine
{
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public ushort IconId { get; set; }
    public int Quantity { get; set; }
}

[Serializable]
public sealed class WorkshopVendorRestockPolicyLine
{
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int RequiredQuantity { get; set; }
    public int ReviewedRetainerQuantity { get; set; }
    public int ApprovedVendorQuantity { get; set; }
    public int LivePlayerQuantity { get; set; }
    public uint UnitPriceGil { get; set; }
    public ulong ApprovedGilCeiling { get; set; }
    public GilVendorBuyOfferSnapshot? Offer { get; set; }
    public List<GilVendorBuyOfferSnapshot> AlternativeOffers { get; set; } = [];
}

public sealed class WorkshopVendorRestockRunView
{
    public string RunId { get; init; } = string.Empty;
    public string QueueSignature { get; init; } = string.Empty;
    public Guid? DemandShareJobId { get; init; }
    public string? DemandShareName { get; init; }
    public bool AutomaticallyBuyVendorMaterials { get; init; }
    public WorkshopVendorRestockPhase Phase { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<WorkshopVendorRestockLineView> Lines { get; init; } = [];
    public IReadOnlyList<GilVendorBuyStopSnapshot> Stops { get; init; } = [];
    public IReadOnlyList<GilVendorBuyReceiptSnapshot> Receipts { get; init; } = [];
    public GilVendorBuyArmedIntentSnapshot? ArmedPurchase { get; init; }
    public bool StopRequested { get; init; }
}

public sealed class WorkshopVendorRestockLineView
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public int RequiredQuantity { get; init; }
    public int ApprovedVendorQuantity { get; init; }
    public int PurchasedQuantity { get; init; }
    public int LivePlayerQuantity { get; init; }
    public bool VendorUnavailable { get; init; }
    public string Status { get; init; } = "Waiting";
    public GilVendorBuyOfferSnapshot? Offer { get; init; }
}

public enum WorkshopQuartermasterProgressState
{
    NotStarted,
    Running,
    Completed,
    PartiallySucceeded,
    Failed,
    Indeterminate,
}

public sealed record WorkshopQuartermasterProgress(WorkshopQuartermasterProgressState State, string Message);

public interface IWorkshopQuartermasterRestockService
{
    bool Submit(
        MarketMafioso.Quartermaster.QuartermasterOwnerScope owner,
        IReadOnlyList<WorkshopMaterialAvailability> availability);
    string LastStatus { get; }

    /// <summary>
    /// True when the last <see cref="Submit"/> failed on something that clears on its own (no owner-matched snapshot
    /// yet, capabilities unreadable, a transport error under the persisted request id); calling it again is safe.
    /// </summary>
    bool LastFailureTransient => false;

    WorkshopQuartermasterProgress GetProgress(MarketMafioso.Quartermaster.QuartermasterOwnerScope owner);

    /// <summary>The owner's persisted operation that has not reached a terminal status, or null.</summary>
    string? OpenOperationId(MarketMafioso.Quartermaster.QuartermasterOwnerScope owner);

    /// <summary>
    /// Drops the owner's persisted operation record. Its open operation is withdrawn in Quartermaster first (a cancel
    /// Quartermaster does not confirm is retried); forgetting itself never waits on that.
    /// </summary>
    bool ForgetOperation(MarketMafioso.Quartermaster.QuartermasterOwnerScope owner);

    /// <summary>
    /// Withdraws the owner's open retrieval in Quartermaster (requestedOperationCancel.v1). A new retrieval for the
    /// same shortage may start only on <see cref="WorkshopQuartermasterCancelState.Settled"/>,
    /// <see cref="WorkshopQuartermasterCancelState.NoOperation"/> or <see cref="WorkshopQuartermasterCancelState.Unsupported"/>.
    /// </summary>
    WorkshopQuartermasterCancelResult Cancel(MarketMafioso.Quartermaster.QuartermasterOwnerScope owner, string reason) =>
        new(WorkshopQuartermasterCancelState.Unsupported, "This retrieval service cannot cancel in Quartermaster.");
}

public enum WorkshopQuartermasterCancelState
{
    /// <summary>No open operation is recorded for the owner.</summary>
    NoOperation,
    /// <summary>Quartermaster answered cancelled, already_terminal or not_found: nothing of it runs any more.</summary>
    Settled,
    /// <summary>Quartermaster is stopping it after the current retainer; the record is kept until it is terminal.</summary>
    Stopping,
    /// <summary>Quartermaster could not be asked; the cancel is kept and retried.</summary>
    Unconfirmed,
    /// <summary>Quartermaster does not advertise the cancel capability: today's behaviour, nothing was withdrawn.</summary>
    Unsupported,
}

public sealed record WorkshopQuartermasterCancelResult(WorkshopQuartermasterCancelState State, string Message)
{
    /// <summary>A new retrieval for the same shortage cannot double the old one.</summary>
    public bool AllowsNewRetrieval => State is WorkshopQuartermasterCancelState.NoOperation or WorkshopQuartermasterCancelState.Settled or
        WorkshopQuartermasterCancelState.Unsupported;
}

public static class WorkshopVendorRestockPresentation
{
    public static string Describe(WorkshopVendorRestockRunView run, WorkshopVendorRestockReview review)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(review);
        if (run.DemandShareName is { } share)
            return IsActive(run)
                ? $"Restocking for {share} · {run.Message}"
                : $"Restock for {share} · {run.Message}";
        if (run.Phase == WorkshopVendorRestockPhase.Completed)
        {
            var remainingLines = review.Materials.Count(line => line.Availability.Shortage > 0);
            return remainingLines == 0
                ? "Workshop materials are ready."
                : $"Vendor purchases complete. {remainingLines:N0} material line(s) still need another source.";
        }
        return run.Message;
    }

    /// <summary>Running or paused: the run still owns the restock slot.</summary>
    public static bool IsActive(WorkshopVendorRestockRunView run) => run.Phase is not (
        WorkshopVendorRestockPhase.Completed or WorkshopVendorRestockPhase.Stopped or
        WorkshopVendorRestockPhase.Failed or WorkshopVendorRestockPhase.Indeterminate or
        WorkshopVendorRestockPhase.Idle);
}
