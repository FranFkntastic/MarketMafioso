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
    /// <summary>Couriering counted stock from other characters' bags to the primary, one source at a time.</summary>
    CollectingCountedStock,
    /// <summary>Asking Quartermaster to stow what this run delivered into each item's home retainers.</summary>
    StowingDelivered,
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
    /// <summary>True once Start planned counted-stock collection; older sessions never collect.</summary>
    public bool CountedStockPlanned { get; set; }
    /// <summary>Counted stock on the shopper: rides home with the haul (bags) after an optional retrieval (retainers).</summary>
    public CrossRegionCollection? ShopperCounted { get; set; }
    /// <summary>One entry per other source character, collected in order after the haul.</summary>
    public List<CrossRegionCollection> Collections { get; set; } = [];
    /// <summary>Counted stock the run never attempts (for example held by the courier carrier).</summary>
    public List<CrossRegionLineOutcome> NotCollected { get; set; } = [];
    public CrossRegionStow? Stow { get; set; }
    /// <summary>True once the return haul's courier run completed (its manifest reached the primary).</summary>
    public bool HaulDelivered { get; set; }
    /// <summary>The haul/return outcome, kept while collection and stow run so the final summary can lead with it.</summary>
    public string? DeliverySummary { get; set; }
    /// <summary>
    /// The primary's carried bags per item variant at Start, before anything was delivered to it. Delivered stow
    /// asks only for what the bags gained since. Null for older sessions or when the bags were unreadable at Start.
    /// </summary>
    public List<CrossRegionManifestLine>? PrimaryBagBaseline { get; set; }

    [JsonIgnore]
    public uint PurchasedUnits => PurchasedManifest.Aggregate(0u, (total, line) => total + line.Quantity);

    [JsonIgnore]
    public bool IsTerminal => Phase is CrossRegionAcquisitionPhase.Completed or CrossRegionAcquisitionPhase.Failed or CrossRegionAcquisitionPhase.Stopped;

    [JsonIgnore]
    public bool IsActive => Phase != CrossRegionAcquisitionPhase.Idle && !IsTerminal;

    public static string FundRequestIdFor(Guid sessionId) => $"mmf-xr-{sessionId:N}-fund";

    public static string HaulRequestIdFor(Guid sessionId, int attempt) => $"mmf-xr-{sessionId:N}-haul-{attempt}";

    public static string CollectRequestIdFor(Guid sessionId, int index, int attempt) => $"mmf-xr-{sessionId:N}-collect-{index}-{attempt}";

    /// <summary>index -1 is the shopper's own retainers.</summary>
    public static string RetrievalRequestIdFor(Guid sessionId, int index) =>
        index < 0 ? $"mmf-xr-{sessionId:N}-retrieve-shopper" : $"mmf-xr-{sessionId:N}-retrieve-{index}";

    public static string StowRequestIdFor(Guid sessionId) => $"mmf-xr-{sessionId:N}-stow";
}

/// <summary>One plan line's counted stock on one source, split by container.</summary>
public sealed record CrossRegionCountedLine(uint ItemId, string ItemName, string HqPolicy, uint BagQuantity, uint RetainerQuantity)
{
    [JsonIgnore]
    public uint Total => BagQuantity + RetainerQuantity;
}

public sealed record CrossRegionLineOutcome(uint ItemId, string ItemName, string Source, uint Quantity, string Code);

public enum CrossRegionCollectionState
{
    Pending,
    Retrieving,
    Shipping,
    Completed,
    NotCollected,
}

/// <summary>
/// Counted stock on one source character. Retrieval and courier request ids are persisted before they are
/// submitted; a settled collection is never resubmitted.
/// </summary>
public sealed class CrossRegionCollection
{
    public CrossRegionCharacter Source { get; set; } = new(0, 0, string.Empty, string.Empty);
    public List<CrossRegionCountedLine> Lines { get; set; } = [];
    public CrossRegionCollectionState State { get; set; }
    public string? RetrievalRequestId { get; set; }
    public string? RetrievalOperationId { get; set; }
    public DateTimeOffset? RetrievalSubmittedAtUtc { get; set; }
    public List<CrossRegionRetrievalItem> RetrievalItems { get; set; } = [];
    public bool RetrievalSettled { get; set; }
    /// <summary>True when retrieval ran to a terminal Quartermaster status, so retainer quantities count toward the cap.</summary>
    public bool RetrievalAttempted { get; set; }
    /// <summary>Why retainer-held counted stock was not (fully) retrieved; null when retrieval succeeded or was not needed.</summary>
    public string? RetrievalOutcomeCode { get; set; }
    public string? RequestId { get; set; }
    public Guid? RunId { get; set; }
    public int Attempt { get; set; }
    public List<CrossRegionManifestLine> Manifest { get; set; } = [];
    /// <summary>Per-line shortfalls and skipped retainer stock; Code names why.</summary>
    public List<CrossRegionLineOutcome> Outcomes { get; set; } = [];
    public string? OutcomeCode { get; set; }
    public string? Detail { get; set; }

    [JsonIgnore]
    public bool IsSettled => State is CrossRegionCollectionState.Completed or CrossRegionCollectionState.NotCollected;
}

public sealed record CrossRegionRetrievalItem(uint ItemId, string ItemName, int TargetQuantity, int ShortageQuantity);

/// <summary>The single delivered-stow request of a run.</summary>
public sealed class CrossRegionStow
{
    public string? RequestId { get; set; }
    /// <summary>What the run delivered to the primary (completed haul and collection manifests).</summary>
    public List<CrossRegionManifestLine> Claimed { get; set; } = [];
    /// <summary>What was asked of Quartermaster: per item, the claim capped by what the bags gained since Start.</summary>
    public List<CrossRegionManifestLine> Items { get; set; } = [];
    /// <summary>When the request was first persisted for submission; the stow deadline runs from here.</summary>
    public DateTimeOffset? SubmittedAtUtc { get; set; }
    public bool Submitted { get; set; }
    public string? OperationId { get; set; }
    public List<QuartermasterDeliveredStowLine> Lines { get; set; } = [];
    public bool Settled { get; set; }
    public string? Status { get; set; }
    /// <summary>Null when everything with a home was stowed.</summary>
    public string? OutcomeCode { get; set; }
    public string? Detail { get; set; }
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
