using System.Text.Json.Serialization;

namespace MarketMafioso.Contracts.MarketIntelligence;

public static class MarketEvidenceSources
{
    public const string MarketAcquisition = "MarketAcquisition";
    public const string PassiveMarketBoard = "PassiveMarketBoard";
    public const string LegacyRouteImport = "LegacyRouteImport";
    public const string Universalis = "Universalis";
    public const string SaddlebagExchange = "SaddlebagExchange";
}

public static class MarketEvidenceCoverage
{
    public const string Complete = "Complete";
    public const string Partial = "Partial";
    public const string LegacyMissing = "LegacyMissing";
    public const string Empty = "Empty";
    public const string Unavailable = "Unavailable";
    public const string AggregateOnly = "AggregateOnly";
}

public sealed record MarketEvidenceSourceView
{
    public string SourceKind { get; init; } = string.Empty;
    public IReadOnlyList<string> Capabilities { get; init; } = [];
}

public sealed record MarketEvidenceSourceRegistryView
{
    public string RegistryVersion { get; init; } = string.Empty;
    public IReadOnlyList<MarketEvidenceSourceView> Sources { get; init; } = [];
}

public sealed record MarketEvidenceUploadRequest
{
    public int SchemaVersion { get; init; } = 1;
    public string IdempotencyKey { get; init; } = string.Empty;
    public string OccurrenceId { get; init; } = string.Empty;
    public string SourceKind { get; init; } = string.Empty;
    public string SourceVersion { get; init; } = "1";
    public string? SourceInstanceId { get; init; }
    public string? SourceBuild { get; init; }
    public string? CaptureMode { get; init; }
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public string DataCenter { get; init; } = string.Empty;
    public string WorldName { get; init; } = string.Empty;
    public DateTimeOffset ObservedAtUtc { get; init; }
    public string Coverage { get; init; } = MarketEvidenceCoverage.Unavailable;
    public int? ReportedListingCount { get; init; }
    public int? ListingCapacity { get; init; }
    public bool? IsTruncated { get; init; }
    public string? SourceFreshness { get; init; }
    public string? ProvenanceJson { get; init; }
    public MarketEvidenceAggregate? Aggregate { get; init; }
    public IReadOnlyList<MarketEvidenceListing> Listings { get; init; } = [];
}

public sealed record MarketEvidenceAggregate
{
    public int? VisibleListingCount { get; init; }
    public long? VisibleQuantity { get; init; }
    public uint? LowestUnitPrice { get; init; }
    public uint? HighestUnitPrice { get; init; }
}

public sealed record MarketEvidenceListing
{
    public string ListingId { get; init; } = string.Empty;
    public string RetainerId { get; init; } = string.Empty;
    public string? RetainerName { get; init; }
    public uint Quantity { get; init; }
    public uint UnitPrice { get; init; }
    public bool IsHq { get; init; }
}

public sealed record MarketEvidenceReceipt
{
    public string ObservationId { get; init; } = string.Empty;
    public string PayloadHash { get; init; } = string.Empty;
    public bool Duplicate { get; init; }
    public long ProjectionRevision { get; init; }
}

public sealed record MarketIntelligenceLedgerView
{
    public long Revision { get; init; }
    public string ClassifierVersion { get; init; } = string.Empty;
    public DateTimeOffset? UpdatedAtUtc { get; init; }
    public IReadOnlyList<MarketIntelligenceMarketRow> Rows { get; init; } = [];
}

public sealed record MarketIntelligenceMarketRow
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string WorldName { get; init; } = string.Empty;
    public string DataCenter { get; init; } = string.Empty;
    public int ObservationCount { get; init; }
    public int DistinctDays { get; init; }
    public DateTimeOffset FirstObservedAtUtc { get; init; }
    public DateTimeOffset LastObservedAtUtc { get; init; }
    public string LatestCoverage { get; init; } = string.Empty;
    public IReadOnlyList<string> SourceKinds { get; init; } = [];
    public int VisibleListings { get; init; }
    public long VisibleQuantity { get; init; }
    public uint? LowestUnitPrice { get; init; }
    public uint? HighestUnitPrice { get; init; }
    public int DistinctSellers { get; init; }
    public double FullStackShare { get; init; }
    public double TopTwoSellerShare { get; init; }
    public double DominantPriceShelfShare { get; init; }
    public uint? DominantPriceShelf { get; init; }
    public int AddedListings { get; init; }
    public int RemovedListings { get; init; }
    public long VisibleQuantityChange { get; init; }
    public IReadOnlyList<MarketIntelligenceFinding> Findings { get; init; } = [];
    public string? Note { get; init; }
    public bool Reviewed { get; init; }
}

public sealed record MarketIntelligenceFinding
{
    public string Kind { get; init; } = string.Empty;
    public string ClassifierVersion { get; init; } = string.Empty;
    public string ObservationId { get; init; } = string.Empty;
    public IReadOnlyList<string> ObservationIds { get; init; } = [];
    public string Summary { get; init; } = string.Empty;
}

public sealed record MarketIntelligenceObservationView
{
    public string ObservationId { get; init; } = string.Empty;
    public string OccurrenceId { get; init; } = string.Empty;
    public string SourceKind { get; init; } = string.Empty;
    public string SourceVersion { get; init; } = string.Empty;
    public string? SourceBuild { get; init; }
    public string? CaptureMode { get; init; }
    public DateTimeOffset ObservedAtUtc { get; init; }
    public DateTimeOffset ReceivedAtUtc { get; init; }
    public string Coverage { get; init; } = string.Empty;
    public int? ReportedListingCount { get; init; }
    public int? ListingCapacity { get; init; }
    public bool? IsTruncated { get; init; }
    public string PayloadHash { get; init; } = string.Empty;
    public MarketEvidenceAggregate? Aggregate { get; init; }
    public IReadOnlyList<MarketEvidenceListing> Listings { get; init; } = [];
}

public sealed record MarketIntelligenceMarketDetailView
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string WorldName { get; init; } = string.Empty;
    public IReadOnlyList<MarketIntelligenceObservationView> Observations { get; init; } = [];
}

public sealed record MarketIntelligenceAnnotationUpdate
{
    public string? Note { get; init; }
    public bool Reviewed { get; init; }
}

public sealed record MarketIntelligenceImportReceiptRequest
{
    public string SourcePathHash { get; init; } = string.Empty;
    public string SourceFingerprint { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int ImportedObservations { get; init; }
    public string? Error { get; init; }
}
