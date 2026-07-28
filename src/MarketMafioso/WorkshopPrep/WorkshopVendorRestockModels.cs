using System;
using System.Collections.Generic;
using System.Numerics;
using Franthropy.Dalamud.Automation.Vendors;

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
}

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

    public static PersistedGilVendorOffer From(GilVendorOffer offer) => new()
    {
        ItemId = offer.ItemId,
        ItemName = offer.ItemName,
        IconId = offer.IconId,
        UnitPriceGil = offer.UnitPriceGil,
        ShopId = offer.ShopId,
        ShopRowIndex = offer.ShopRowIndex,
        NpcId = offer.NpcId,
        NpcName = offer.NpcName,
        TerritoryId = offer.TerritoryId,
        PositionX = offer.Position.X,
        PositionY = offer.Position.Y,
        PositionZ = offer.Position.Z,
        RouteAetheryteIds = [.. offer.RouteAetheryteIds],
    };

    public GilVendorOffer ToOffer() => new(
        ItemId,
        ItemName,
        IconId,
        UnitPriceGil,
        ShopId,
        ShopRowIndex,
        NpcId,
        NpcName,
        TerritoryId,
        new Vector3(PositionX, PositionY, PositionZ),
        RouteAetheryteIds);
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

public sealed record WorkshopVendorInventorySnapshot(
    bool IsComplete,
    ulong? Gil,
    IReadOnlyDictionary<uint, int> ItemCounts,
    string Message);

public enum WorkshopVendorReachState
{
    Waiting,
    ShopOpen,
    Unavailable,
    Failed,
}

public sealed record WorkshopVendorReachResult(
    WorkshopVendorReachState State,
    string Message);

public enum WorkshopQuartermasterProgressState
{
    NotStarted,
    Running,
    Completed,
    PartiallySucceeded,
    Failed,
    Indeterminate,
}

public sealed record WorkshopQuartermasterProgress(
    WorkshopQuartermasterProgressState State,
    string Message);

public interface IWorkshopVendorRestockRuntime
{
    WorkshopVendorInventorySnapshot CaptureInventory(IReadOnlyCollection<uint> itemIds);
    bool HasCapacity(IReadOnlyDictionary<uint, int> quantities, out string message);
    bool TryStartQuartermaster(
        MarketMafioso.Quartermaster.QuartermasterOwnerScope owner,
        IReadOnlyList<WorkshopMaterialAvailability> availability,
        out string error);
    WorkshopQuartermasterProgress GetQuartermasterProgress(
        MarketMafioso.Quartermaster.QuartermasterOwnerScope owner);
    WorkshopVendorReachResult AdvanceToOpenShop(GilVendorOffer offer);
    void ResetVendorApproach();
    GilVendorShopReadResult ReadShopRows();
    bool TrySubmitPurchase(GilVendorShopRow row, uint quantity, out string error);
    bool TryConfirmPurchasePrompt();
    int ResolveMaximumBatch(uint itemId);
    void CloseShop();
    void BeginAutomation();
    void EndAutomation();
}
