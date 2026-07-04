using System;
using System.Collections.Generic;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketAcquisitionQuickShopDraft
{
    public string DraftId { get; init; } = Guid.NewGuid().ToString("N");
    public int DraftRevision { get; init; } = 1;
    public string Region { get; init; } = "North America";
    public string WorldMode { get; init; } = "Recommended";
    public string SweepScope { get; init; } = "Region";
    public List<string> SweepDataCenters { get; init; } = new();
    public List<MarketAcquisitionQuickShopLineDraft> Lines { get; init; } = new();

    public static MarketAcquisitionQuickShopDraft CreateDefault() => new();

    public MarketAcquisitionQuickShopDraft WithNextRevision() =>
        this with { DraftRevision = DraftRevision + 1 };

    public MarketAcquisitionQuickShopDraft WithNewIdentity() =>
        this with { DraftId = Guid.NewGuid().ToString("N"), DraftRevision = 1 };
}

public sealed record MarketAcquisitionQuickShopLineDraft
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string QuantityMode { get; init; } = "AllBelowThreshold";
    public uint TargetQuantity { get; init; }
    public uint MaxQuantity { get; init; }
    public string HqPolicy { get; init; } = "Either";
    public uint MaxUnitPrice { get; init; }
    public uint GilCap { get; init; }
}
