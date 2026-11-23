using System;
using System.Collections.Generic;
using System.Linq;
using MarketMafioso.MarketAcquisition.ExactAuthority;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketAcquisitionRequestDocument
{
    // Local execution policy, not a hosted work-order limit. Zero means unset.
    public uint RunSpendCap { get; init; }
    public string LocalRequestId { get; init; } = Guid.NewGuid().ToString("N");
    public int LocalRevision { get; init; } = 1;
    public string TargetCharacterName { get; init; } = string.Empty;
    public string TargetWorld { get; init; } = string.Empty;
    public string Region { get; init; } = "North America";
    public string WorldMode { get; init; } = "Recommended";
    public string SweepScope { get; init; } = "Region";
    public List<string> SweepDataCenters { get; init; } = [];
    public List<MarketAcquisitionRequestLineDocument> Lines { get; init; } = [];
    // Local stock policy: whether the current character's retainer stock counts toward target lines.
    // Mirrored onto each target line's TargetBasis when the document is saved.
    public bool CountRetainerStock { get; init; }
    // Local stock policy: narrows CountRetainerStock to these retainers (single-retainer keys of one owner)
    // while that owner is the logged-in character. Empty, or another character logged in: every retainer counts.
    public List<MarketAcquisitionStockSourceKey> OwnRetainerSelection { get; init; } = [];
    // Local stock policy: other characters' bags/retainers counted toward target lines.
    public List<MarketAcquisitionStockSourceKey> AdditionalStockSources { get; init; } = [];
    public bool StowAfterDelivery { get; init; }
    public ExactAcquisitionWorkbenchAuthority? ExactAcquisitionAuthority { get; init; }
    public string? RemoteRequestId { get; init; }
    public int RemoteRevision { get; init; }
    public string? RemoteOrigin { get; init; }
    public string? LastSyncedHash { get; init; }
    public string? RemoteHash { get; init; }
    public string? LastPlanHash { get; init; }
    public string SyncStatus { get; init; } = "NewDraft";
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public static MarketAcquisitionRequestDocument CreateDefault(
        string characterName = "",
        string world = "") =>
        new()
        {
            TargetCharacterName = characterName,
            TargetWorld = world,
        };

    /// <summary>Mirrors the plan-level retainer-stock choice onto every on-hand target line.</summary>
    public MarketAcquisitionRequestDocument WithNormalizedStockScope()
    {
        var basis = CountRetainerStock
            ? MarketAcquisitionTargetBases.OnHandIncludingRetainers
            : MarketAcquisitionTargetBases.OnHandTotal;
        // Exact-acquisition lines buy a required quantity and their lineage pins every field.
        if (ExactAcquisitionAuthority is not null || !Lines.Any(line => NeedsBasis(line, basis)))
            return this;
        return this with { Lines = Lines.Select(line => NeedsBasis(line, basis) ? line with { TargetBasis = basis } : line).ToList() };

        static bool NeedsBasis(MarketAcquisitionRequestLineDocument line, string basis) =>
            line.QuantityMode == "TargetQuantity" &&
            MarketAcquisitionTargetBases.CountsOnHand(line.TargetBasis) &&
            MarketAcquisitionTargetBases.Normalize(line.TargetBasis) != basis;
    }

    public MarketAcquisitionRequestDocument WithNextRevision(string syncStatus) =>
        this with
        {
            LocalRevision = LocalRevision + 1,
            SyncStatus = syncStatus,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

    public MarketAcquisitionRequestDocument WithNewIdentity() =>
        this with
        {
            LocalRequestId = Guid.NewGuid().ToString("N"),
            LocalRevision = 1,
            RemoteRequestId = null,
            RemoteRevision = 0,
            RemoteOrigin = null,
            LastSyncedHash = null,
            RemoteHash = null,
            LastPlanHash = null,
            ExactAcquisitionAuthority = ExactAcquisitionAuthority is null
                ? null
                : ExactAcquisitionAuthority with { FinalizedContract = null },
            SyncStatus = "NewDraft",
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
}

public sealed record MarketAcquisitionRequestLineDocument
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string? ItemKind { get; init; }
    public string QuantityMode { get; init; } = "AllBelowThreshold";
    public uint TargetQuantity { get; init; }
    public string TargetBasis { get; init; } = MarketAcquisitionTargetBases.OnHandTotal;
    public uint MaximumOverage { get; init; }
    public uint MaxQuantity { get; init; }
    public string HqPolicy { get; init; } = "Either";
    public uint MaxUnitPrice { get; init; }
    public uint GilCap { get; init; }
    // Local: the target is the Quartermaster reserve level for this item (a TargetQuantity line).
    public bool RestockReserve { get; init; }
    public IReadOnlyList<MarketAcquisitionCountedStockSource> CountedStock { get; init; } = [];
}
