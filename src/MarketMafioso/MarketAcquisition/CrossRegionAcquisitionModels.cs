using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarketMafioso.Quartermaster;

namespace MarketMafioso.MarketAcquisition;

public enum CrossRegionAcquisitionPhase
{
    Idle,
    FundingShopper,
    SwitchingToShopper,
    PreparingShopRoute,
    Shopping,
    Paused,
    ClosingMarketBoardForHaul,
    HaulingToPrimary,
    Completed,
    Failed,
    Stopped,
    /// <summary>Nothing to haul; switching back from the shopper to the primary before completing.</summary>
    ReturningToPrimary,
}

public sealed record CrossRegionCharacter(
    ulong LocalContentId,
    uint HomeWorldId,
    string CharacterName,
    string HomeWorldName)
{
    public bool Matches(CrossRegionCharacter? other) =>
        other is not null && LocalContentId != 0 && other.LocalContentId == LocalContentId && other.HomeWorldId == HomeWorldId;

    public QuartermasterCourierCharacter ToCourier() => new(LocalContentId, HomeWorldId, CharacterName, HomeWorldName);

    public string Label => $"{CharacterName} · {HomeWorldName}";
}

/// <summary>SpentGil is the confirmed wallet gil (tax included) for this (item, quality); 0 on older sessions.</summary>
public sealed record CrossRegionManifestLine(uint ItemId, string ItemName, bool IsHq, uint Quantity, ulong SpentGil = 0);

/// <summary>A server-confirmed live purchase; WalletGil includes market tax.</summary>
public sealed record MarketAcquisitionConfirmedPurchase(
    uint ItemId,
    string? ItemName,
    bool IsHq,
    uint Quantity,
    ulong WalletGil,
    string WorldName,
    string ListingId);

/// <summary>
/// Stock baselines for a route run on a shopper on behalf of a primary character, keyed by plan line id.
/// </summary>
public sealed record MarketAcquisitionCrossRegionBaseline(
    IReadOnlyDictionary<string, uint> PrimaryStockBaselines,
    IReadOnlyDictionary<string, uint> ShopperStartingStock);

/// <summary>One carried stack (or summed stacks) of a live bag read: Inventory1..4 and the crystal pouch.</summary>
public readonly record struct CrossRegionBagStack(uint ItemId, bool IsHq, uint Quantity);

/// <summary>
/// The durable cross-regional acquisition session. Every courier request id is persisted before it
/// is submitted, and every confirmed purchase is persisted when recorded, so a crash or relog never
/// duplicates a gil transfer or forgets what the shopper bought.
/// </summary>
public sealed class CrossRegionAcquisitionSession
{
    public int SchemaVersion { get; set; } = 1;
    public Guid SessionId { get; set; }
    public CrossRegionAcquisitionPhase Phase { get; set; }
    public CrossRegionCharacter? Primary { get; set; }
    public CrossRegionCharacter? Shopper { get; set; }
    public string TargetRegion { get; set; } = string.Empty;
    public int FundGil { get; set; }
    public string? FundRequestId { get; set; }
    public Guid? FundRunId { get; set; }
    public bool FundCompleted { get; set; }
    public string? HaulRequestId { get; set; }
    public Guid? HaulRunId { get; set; }
    public int HaulAttempt { get; set; }
    public List<CrossRegionManifestLine> HaulManifest { get; set; } = [];
    public Dictionary<string, uint> PrimaryStockBaselines { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Shopper's own carried stock per line when shopping first started; purchases are the delta above it.</summary>
    public Dictionary<string, uint>? ShopperStartingStock { get; set; }
    public List<CrossRegionManifestLine> PurchasedManifest { get; set; } = [];
    public List<string> RecordedPurchaseKeys { get; set; } = [];
    public ulong TotalSpentGil { get; set; }
    public string LastMessage { get; set; } = string.Empty;
    public string? LastErrorCode { get; set; }
    public string? CourierPhaseSummary { get; set; }
    /// <summary>The phase a Failed/Stopped session was in, so Resume knows what to continue.</summary>
    public CrossRegionAcquisitionPhase? InterruptedPhase { get; set; }
    public bool CanResumeCourier { get; set; }
    public bool ShoppingPausedByUser { get; set; }
    public int CharacterSwitchAttempts { get; set; }
    public MarketAcquisitionRequestDocument? RequestDocumentSnapshot { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }

    [JsonIgnore]
    public uint PurchasedUnits => PurchasedManifest.Aggregate(0u, (total, line) => total + line.Quantity);

    [JsonIgnore]
    public bool IsTerminal => Phase is CrossRegionAcquisitionPhase.Completed or CrossRegionAcquisitionPhase.Failed or CrossRegionAcquisitionPhase.Stopped;

    [JsonIgnore]
    public bool IsActive => Phase != CrossRegionAcquisitionPhase.Idle && !IsTerminal;

    public static string FundRequestIdFor(Guid sessionId) => $"mmf-xr-{sessionId:N}-fund";

    public static string HaulRequestIdFor(Guid sessionId, int attempt) => $"mmf-xr-{sessionId:N}-haul-{attempt}";
}

public interface ICrossRegionAcquisitionStore
{
    CrossRegionAcquisitionSession? Load();
    void Save(CrossRegionAcquisitionSession session);
    void Clear();
}

/// <summary>Plugin-config file store; writes go to a temp file that atomically replaces the previous copy.</summary>
public sealed class FileCrossRegionAcquisitionStore : ICrossRegionAcquisitionStore
{
    public const string FileName = "cross-region-acquisition.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string path;

    public FileCrossRegionAcquisitionStore(string pluginConfigDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginConfigDirectory);
        path = Path.Combine(pluginConfigDirectory, FileName);
    }

    public string FilePath => path;

    public CrossRegionAcquisitionSession? Load()
    {
        if (!File.Exists(path))
            return null;
        var json = File.ReadAllText(path);
        return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<CrossRegionAcquisitionSession>(json, JsonOptions);
    }

    public void Save(CrossRegionAcquisitionSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, session, JsonOptions);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    public void Clear()
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
