using Dalamud.Configuration;
using MarketMafioso.WorkshopPrep;
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

    public bool IncludeArmoury { get; set; } = false;
    public bool IncludeCrystals { get; set; } = true;
    public bool IncludeEquipped { get; set; } = false;
    public bool IncludeSaddlebag { get; set; } = false;

    public bool IncludeItemNames { get; set; } = true;
    public bool IncludeCharacterInfo { get; set; } = true;

    public bool AutoSendOnRetainerClose { get; set; } = false;
    public bool EnableAutoSendTimer { get; set; } = false;
    public int AutoSendIntervalMinutes { get; set; } = 5;

    public Dictionary<ulong, CachedRetainer> RetainerCache { get; set; } = new();
    public List<WorkshopPrepQueueItem> WorkshopPrepQueue { get; set; } = new();
    public List<WorkshopFrozenQueue> FrozenWorkshopQueues { get; set; } = new();
    public Guid? ActiveFrozenWorkshopQueueId { get; set; }
    public List<uint> FavoriteWorkshopProjectIds { get; set; } = new();

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}

[Serializable]
public class CachedRetainer
{
    public ulong RetainerId { get; set; }
    public string RetainerName { get; set; } = string.Empty;
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
    public string Status { get; set; } = string.Empty;
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
