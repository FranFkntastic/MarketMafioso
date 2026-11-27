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
    /// <summary>A haul landed and bag space was what ended the cycle; switching back to the shopper for the rest.</summary>
    SwitchingToShopperForNextCycle,
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
/// duplicates a gil transfer or forgets what the shopper bought. A completed haul is settled into
/// the cumulative manifests in the same write that leaves the haul phase, so a restart never
/// counts a delivery twice or sends the same cycle out again.
/// </summary>
public sealed class CrossRegionAcquisitionSession
{
    /// <summary>2 added multi-cycle fields; every one defaults so a version 1 file loads as a single-cycle session.</summary>
    public int SchemaVersion { get; set; } = 2;
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
    /// <summary>Confirmed purchases of the current cycle only: what the shopper holds and has not hauled.</summary>
    public List<CrossRegionManifestLine> PurchasedManifest { get; set; } = [];
    /// <summary>1-based shop-then-haul cycle; a session saved before multi-cycle reads as cycle 1.</summary>
    public int CycleNumber { get; set; } = 1;
    /// <summary>When bags end a cycle, haul, stow into the primary's retainers, and go back for the rest.</summary>
    public bool MultiCycleEnabled { get; set; }
    /// <summary>Shopping stopped for bag space, so the pending haul is followed by another cycle unless something forbids it.</summary>
    public bool NeedsContinueAfterHaul { get; set; }
    /// <summary>The pending haul asks Quartermaster to stow the delivery into the primary's retainers.</summary>
    public bool DepositToRetainers { get; set; }
    /// <summary>Units completed hauls delivered to the primary, across all cycles; they count toward targets and are never bought again.</summary>
    public List<CrossRegionManifestLine> DeliveredManifest { get; set; } = [];
    /// <summary>Confirmed purchases of the cycles whose haul already completed.</summary>
    public List<CrossRegionManifestLine> EarlierPurchasedManifest { get; set; } = [];
    /// <summary>Shopper's carried gil read on the shopper just before the latest haul; unreadable once the primary is logged in.</summary>
    public ulong? ShopperGilAtHaul { get; set; }
    /// <summary>Why a later cycle ended without a haul, kept across the switch back to the primary.</summary>
    public string? CycleStopReason { get; set; }
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

    /// <summary>Units bought in the current cycle.</summary>
    [JsonIgnore]
    public uint PurchasedUnits => PurchasedManifest.Aggregate(0u, (total, line) => total + line.Quantity);

    /// <summary>Units bought across every cycle.</summary>
    [JsonIgnore]
    public ulong TotalPurchasedUnits =>
        EarlierPurchasedManifest.Aggregate((ulong)PurchasedUnits, (total, line) => total + line.Quantity);

    [JsonIgnore]
    public ulong DeliveredUnits => DeliveredManifest.Aggregate(0UL, (total, line) => total + line.Quantity);

    /// <summary>Every confirmed purchase of the session, earlier cycles first; an (item, quality) may appear twice.</summary>
    public IReadOnlyList<CrossRegionManifestLine> AllPurchases() => [.. EarlierPurchasedManifest, .. PurchasedManifest];

    /// <summary>Adds <paramref name="lines"/> into <paramref name="target"/>, summing per (item, quality).</summary>
    public static void Accumulate(List<CrossRegionManifestLine> target, IEnumerable<CrossRegionManifestLine> lines)
    {
        foreach (var line in lines)
        {
            var index = target.FindIndex(existing => existing.ItemId == line.ItemId && existing.IsHq == line.IsHq);
            if (index < 0)
                target.Add(line);
            else
                target[index] = target[index] with
                {
                    Quantity = checked(target[index].Quantity + line.Quantity),
                    SpentGil = checked(target[index].SpentGil + line.SpentGil),
                };
        }
    }

    [JsonIgnore]
    public bool IsTerminal => Phase is CrossRegionAcquisitionPhase.Completed or CrossRegionAcquisitionPhase.Failed or CrossRegionAcquisitionPhase.Stopped;

    [JsonIgnore]
    public bool IsActive => Phase != CrossRegionAcquisitionPhase.Idle && !IsTerminal;

    public static string FundRequestIdFor(Guid sessionId) => $"mmf-xr-{sessionId:N}-fund";

    /// <summary>
    /// Quartermaster deduplicates by request id, so each cycle's haul needs its own: a repeated id is
    /// acknowledged as already completed and moves nothing. Cycle 1 keeps the original single-cycle form.
    /// </summary>
    public static string HaulRequestIdFor(Guid sessionId, int cycle, int attempt) => cycle <= 1
        ? $"mmf-xr-{sessionId:N}-haul-{attempt}"
        : $"mmf-xr-{sessionId:N}-c{cycle}-haul-{attempt}";
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
