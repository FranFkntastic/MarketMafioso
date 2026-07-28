using System;
using System.Collections.Generic;
using System.Linq;
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

public static class WorkshopVendorRestockPresentation
{
    public static string? BuildStartActionLabel(
        WorkshopVendorRestockReview review,
        bool automaticallyBuyVendorMaterials)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (review.RetainerUnits > 0 &&
            automaticallyBuyVendorMaterials &&
            review.VendorUnits > 0)
        {
            return $"Retrieve {review.RetainerUnits:N0} + buy {review.VendorUnits:N0} · up to {review.MaximumGil:N0} gil";
        }

        if (automaticallyBuyVendorMaterials && review.VendorUnits > 0)
            return $"Buy {review.VendorUnits:N0} items · up to {review.MaximumGil:N0} gil";
        if (review.RetainerUnits > 0)
            return $"Retrieve {review.RetainerUnits:N0} from retainers";
        return null;
    }

    public static string DescribeRemaining(WorkshopVendorRestockReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        var lines = review.Materials.Count(line => line.VendorNeed > 0);
        var units = review.Materials.Sum(line => line.VendorNeed);
        if (lines == 0)
            return "All workshop materials are covered.";

        return $"{lines:N0} {(lines == 1 ? "material" : "materials")} · {units:N0} units still need another source";
    }

    public static string DescribeReceiptDetails(PersistedWorkshopVendorRestockRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return string.Join(
            " · ",
            run.Receipts
                .GroupBy(receipt => receipt.ItemId)
                .OrderBy(group =>
                    run.Lines.FirstOrDefault(line => line.ItemId == group.Key)?.ItemName ??
                    group.Key.ToString())
                .Select(group =>
                {
                    var name = run.Lines.FirstOrDefault(line => line.ItemId == group.Key)?.ItemName ??
                               $"Item {group.Key}";
                    return $"{name} ×{group.Sum(receipt => receipt.Quantity):N0} · " +
                           $"{group.Aggregate(0UL, (total, receipt) => checked(total + receipt.SpentGil)):N0} gil";
                }));
    }

    public static string Describe(
        PersistedWorkshopVendorRestockRun run,
        WorkshopVendorRestockReview review)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(review);
        if (run.Phase == WorkshopVendorRestockPhase.Completed)
        {
            if (run.Receipts.Count == 0)
                return review.Materials.Any(line => line.VendorNeed > 0)
                    ? "Restock complete."
                    : "Workshop materials are ready.";

            var quantity = run.Receipts.Sum(receipt => receipt.Quantity);
            var spentGil = run.Receipts.Aggregate(
                0UL,
                (total, receipt) => checked(total + receipt.SpentGil));
            var vendorNames = run.Receipts
                .Select(receipt =>
                    run.Lines.FirstOrDefault(line => line.ItemId == receipt.ItemId)?.Offer?.NpcName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var source = vendorNames.Length switch
            {
                1 => $" from {vendorNames[0]}",
                > 1 => $" across {vendorNames.Length:N0} vendors",
                _ => string.Empty,
            };
            return $"Bought {quantity:N0} items for {spentGil:N0} gil{source}.";
        }
        if (run.Phase != WorkshopVendorRestockPhase.Failed ||
            !run.Message.Contains("expected shop did not become available", StringComparison.OrdinalIgnoreCase))
        {
            return run.Message;
        }

        var vendor = run.Stops.FirstOrDefault()?.NpcName ?? "the vendor";
        return run.Receipts.Count == 0
            ? $"Couldn't reach {vendor}. No gil was spent. Start again to rebuild the route."
            : $"Couldn't reach {vendor}. Earlier verified purchases were preserved. Start again to rebuild the route.";
    }
}

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
