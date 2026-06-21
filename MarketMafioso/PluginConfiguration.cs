using Dalamud.Configuration;
using Dalamud.Plugin;
using System;

namespace MarketMafioso;

[Serializable]
public sealed class PluginConfiguration : IPluginConfiguration
{
    [NonSerialized]
    private IDalamudPluginInterface? _pluginInterface;

    public int Version { get; set; } = 1;

    public bool OverlayCollapsed { get; set; }

    public static PluginConfiguration Load(IDalamudPluginInterface pluginInterface)
    {
        var configuration = pluginInterface.GetPluginConfig() as PluginConfiguration ?? new PluginConfiguration();
        configuration.Initialize(pluginInterface);
        return configuration;
    }

    public void Initialize(IDalamudPluginInterface pluginInterface)
    {
        _pluginInterface = pluginInterface;
    }

    public void Save()
    {
        _pluginInterface?.SavePluginConfig(this);
    }
}
