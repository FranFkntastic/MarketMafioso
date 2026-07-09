using Dalamud.Configuration;
using MarketMafioso.RetainerRestock;
using MarketMafioso.WorkshopPrep;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace MarketMafioso;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public string ServerUrl { get; set; } = "http://localhost:8080/inventory";
    public string ApiKey { get; set; } = string.Empty;
    public string CommandPickupApiKey { get; set; } = string.Empty;
    public string PluginInstanceId { get; set; } = Guid.NewGuid().ToString("N");
    public PersistedMarketAcquisitionClaim? ActiveMarketAcquisitionClaim { get; set; }
    public PersistedMarketAcquisitionRequestDocument? ActiveMarketAcquisitionRequestDocument { get; set; }
    public bool EnableMarketAcquisition { get; set; } = false;
    public DateTime? MarketAcquisitionUnlockedAtUtc { get; set; }
    public bool EnableOpportunisticWorldChecks { get; set; } = true;
    public bool CreateMarketAcquisitionRouteDiagnosticPackages { get; set; } = false;
    public int MarketAcquisitionRecentWorldTtlHours { get; set; } = 18;
    public bool MarketAcquisitionIgnoreRecentWorldVisitsForSweep { get; set; } = false;
    public List<PersistedMarketAcquisitionWorldVisit> MarketAcquisitionWorldVisits { get; set; } = [];
    public bool EnableWorkshopHostCraftQuotes { get; set; } = false;
    public bool EnableCraftArchitectManualFallback { get; set; } = false;
    public string CraftArchitectQuoteFilePath { get; set; } = string.Empty;

    public bool IncludeArmoury { get; set; } = false;
    public bool IncludeCrystals { get; set; } = true;
    public bool IncludeEquipped { get; set; } = false;
    public bool IncludeSaddlebag { get; set; } = false;

    public bool IncludeItemNames { get; set; } = true;
    public bool IncludeCharacterInfo { get; set; } = true;

    public bool AutoSendOnRetainerClose { get; set; } = false;
    public bool EnableAutoSendTimer { get; set; } = false;
    public int AutoSendIntervalMinutes { get; set; } = 5;

    [JsonIgnore]
    public Dictionary<ulong, CachedRetainer> RetainerCache { get; set; } = new();

    [JsonProperty("RetainerCache")]
    private Dictionary<ulong, CachedRetainer>? LegacyRetainerCache
    {
        get => null;
        set
        {
            if (value is { Count: > 0 })
                RetainerCache = value;
        }
    }

    public bool ShouldSerializeLegacyRetainerCache() => false;

    public List<WorkshopPrepQueueItem> WorkshopPrepQueue { get; set; } = new();
    public List<WorkshopFrozenQueue> FrozenWorkshopQueues { get; set; } = new();
    public List<RetainerRestockPlanItem> RetainerRestockPlanItems { get; set; } = new();
    public Guid? ActiveFrozenWorkshopQueueId { get; set; }
    public List<uint> FavoriteWorkshopProjectIds { get; set; } = new();

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}

[Serializable]
public class CachedRetainer
{
    public ulong RetainerId { get; set; }
    public string RetainerName { get; set; } = string.Empty;
    public string? OwnerCharacterName { get; set; }
    public string? OwnerHomeWorld { get; set; }
    public DateTime LastUpdated { get; set; }
    public ulong Gil { get; set; }
    public List<CachedBag> Bags { get; set; } = new();
    public List<CachedMarketListing> MarketListings { get; set; } = new();
}

[Serializable]
public class CachedBag
{
    public string BagName { get; set; } = string.Empty;
    public List<CachedItem> Items { get; set; } = new();
}

[Serializable]
public class CachedItem
{
    public uint ItemId { get; set; }
    public string? ItemName { get; set; }
    public string? ItemType { get; set; }
    public uint Quantity { get; set; }
    public bool IsHQ { get; set; }
    public float Condition { get; set; }
}

[Serializable]
public class CachedMarketListing
{
    public uint ItemId { get; set; }
    public string? ItemName { get; set; }
    public string? ItemType { get; set; }
    public uint Quantity { get; set; }
    public bool IsHQ { get; set; }
    public float Condition { get; set; }
    public uint? UnitPrice { get; set; }
    public string? ListedAt { get; set; }
}

[Serializable]
public sealed class PersistedMarketAcquisitionClaim
{
    public string Id { get; set; } = string.Empty;
    public int Revision { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;
    public string? CreatedByPluginInstanceId { get; set; }
    public string TargetCharacterName { get; set; } = string.Empty;
    public string TargetWorld { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public uint ItemId { get; set; }
    public string? ItemName { get; set; }
    public string QuantityMode { get; set; } = string.Empty;
    public uint Quantity { get; set; }
    public string HqPolicy { get; set; } = string.Empty;
    public uint MaxUnitPrice { get; set; }
    public uint MaxTotalGil { get; set; }
    public string WorldMode { get; set; } = string.Empty;
    public string ClaimToken { get; set; } = string.Empty;
    public string? AcceptIdempotencyKey { get; set; }
    public string? RejectIdempotencyKey { get; set; }
    public List<PersistedMarketAcquisitionLine> Lines { get; set; } = [];
}

[Serializable]
public sealed class PersistedMarketAcquisitionLine
{
    public string LineId { get; set; } = string.Empty;
    public string BatchId { get; set; } = string.Empty;
    public int Ordinal { get; set; }
    public uint ItemId { get; set; }
    public string? ItemName { get; set; }
    public string? ItemKind { get; set; }
    public string QuantityMode { get; set; } = string.Empty;
    public uint TargetQuantity { get; set; }
    public uint MaxQuantity { get; set; }
    public string HqPolicy { get; set; } = string.Empty;
    public uint MaxUnitPrice { get; set; }
    public uint GilCap { get; set; }
    public string Status { get; set; } = string.Empty;
    public uint PurchasedQuantity { get; set; }
    public uint SpentGil { get; set; }
    public string? LatestMessage { get; set; }
}

[Serializable]
public sealed class PersistedMarketAcquisitionRequestDocument
{
    public string LocalRequestId { get; set; } = string.Empty;
    public int LocalRevision { get; set; }
    public string TargetCharacterName { get; set; } = string.Empty;
    public string TargetWorld { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string WorldMode { get; set; } = string.Empty;
    public string SweepScope { get; set; } = string.Empty;
    public List<string> SweepDataCenters { get; set; } = [];
    public List<PersistedMarketAcquisitionRequestLineDocument> Lines { get; set; } = [];
    public string? RemoteRequestId { get; set; }
    public int RemoteRevision { get; set; }
    public string? RemoteOrigin { get; set; }
    public string? LastSyncedHash { get; set; }
    public string? RemoteHash { get; set; }
    public string? LastPlanHash { get; set; }
    public string SyncStatus { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; }
}

[Serializable]
public sealed class PersistedMarketAcquisitionRequestLineDocument
{
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public string? ItemKind { get; set; }
    public string QuantityMode { get; set; } = string.Empty;
    public uint TargetQuantity { get; set; }
    public uint MaxQuantity { get; set; }
    public string HqPolicy { get; set; } = string.Empty;
    public uint MaxUnitPrice { get; set; }
    public uint GilCap { get; set; }
}

[Serializable]
public sealed class PersistedMarketAcquisitionWorldVisit
{
    public string WorldName { get; set; } = string.Empty;
    public string DataCenter { get; set; } = string.Empty;
    public uint ItemId { get; set; }
    public string? ItemName { get; set; }
    public string HqPolicy { get; set; } = string.Empty;
    public uint MaxUnitPrice { get; set; }
    public DateTime CheckedAtUtc { get; set; }
    public string Result { get; set; } = string.Empty;
    public uint PurchasedQuantity { get; set; }
    public uint SpentGil { get; set; }
    public int ObservedLegalListingCount { get; set; }
    public uint ObservedLegalQuantity { get; set; }
    public ulong ObservedLegalGil { get; set; }
    public string Source { get; set; } = string.Empty;
    public string? RequestId { get; set; }
    public string? RouteRunId { get; set; }
    public string? RouteStopId { get; set; }
}
