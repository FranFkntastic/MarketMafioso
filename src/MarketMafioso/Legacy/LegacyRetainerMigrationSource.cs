using System;
using System.IO;
using System.Text.Json;
using Franthropy.Dalamud.Persistence;

namespace MarketMafioso.Legacy;

internal static class LegacyRetainerMigrationSource
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static bool Preserve(Configuration configuration, string path)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (configuration.RetainerCache.Count == 0 || File.Exists(path))
            return false;

        AtomicJsonFile.Write(path, configuration.RetainerCache, JsonOptions);
        return true;
    }
}
