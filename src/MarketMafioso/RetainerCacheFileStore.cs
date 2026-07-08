using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

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

        var json = File.ReadAllText(path, Encoding.UTF8);
        return JsonSerializer.Deserialize<Dictionary<ulong, CachedRetainer>>(json, JsonOptions)
            ?? new Dictionary<ulong, CachedRetainer>();
    }

    public void Save(IReadOnlyDictionary<ulong, CachedRetainer> cache)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var tempPath = path + ".tmp";
        var json = JsonSerializer.Serialize(cache, JsonOptions);
        File.WriteAllText(tempPath, json, Encoding.UTF8);
        File.Move(tempPath, path, overwrite: true);
    }
}
