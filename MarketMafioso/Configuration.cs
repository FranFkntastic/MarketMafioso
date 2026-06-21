using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace MarketMafioso;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public string ServerUrl { get; set; } = "http://localhost:8080/inventory";
    public string ApiKey { get; set; } = string.Empty;

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

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}

[Serializable]
public class CachedRetainer
{
    public ulong RetainerId { get; set; }
    public string RetainerName { get; set; } = string.Empty;
    public DateTime LastUpdated { get; set; }
    public List<CachedBag> Bags { get; set; } = new();
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
    public uint Quantity { get; set; }
    public bool IsHQ { get; set; }
    public float Condition { get; set; }
}
