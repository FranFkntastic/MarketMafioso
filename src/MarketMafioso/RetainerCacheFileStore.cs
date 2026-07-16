using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Franthropy.Dalamud.Persistence;

namespace MarketMafioso;

public sealed class RetainerCacheFileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string path;

    public RetainerCacheFileStore(string path)
    {
        this.path = path;
    }

    public bool Exists => File.Exists(path);

    public Dictionary<ulong, CachedRetainer> Load()
    {
        if (!File.Exists(path))
            return new Dictionary<ulong, CachedRetainer>();

        return AtomicJsonFile.Read<Dictionary<ulong, CachedRetainer>>(path, JsonOptions)
            ?? new Dictionary<ulong, CachedRetainer>();
    }

    public void Save(IReadOnlyDictionary<ulong, CachedRetainer> cache)
    {
        AtomicJsonFile.Write(path, cache, JsonOptions);
    }
}
