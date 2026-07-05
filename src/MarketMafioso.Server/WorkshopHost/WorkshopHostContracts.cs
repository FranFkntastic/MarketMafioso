namespace MarketMafioso.Server.WorkshopHost;

public sealed record WorkshopHostCapabilitiesResponse
{
    public string Service { get; init; } = "WorkshopHost";
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset ServerTimeUtc { get; init; }
    public IReadOnlyList<WorkshopHostCapability> Capabilities { get; init; } = [];
}

public sealed record WorkshopHostCapability
{
    public string Id { get; init; } = string.Empty;
    public string Status { get; init; } = "available";
    public IReadOnlyList<int> SupportedSchemaVersions { get; init; } = [];
    public IReadOnlyList<string> RequiredScopes { get; init; } = [];
}

public sealed record CraftAppraisalRequest
{
    public int SchemaVersion { get; init; } = 1;
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public uint Quantity { get; init; }
    public CraftAppraisalScope Scope { get; init; } = new();
    public CraftAppraisalOptions Options { get; init; } = new();
}

public sealed record CraftAppraisalScope
{
    public string Region { get; init; } = "North America";
    public string? DataCenter { get; init; }
    public string? World { get; init; }
}

public sealed record CraftAppraisalOptions
{
    public string HqPolicy { get; init; } = "Either";
    public string PricingMode { get; init; } = "CurrentMarketEvidence";
}

public sealed record CraftAppraisalQuote
{
    public int SchemaVersion { get; init; } = 1;
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public uint RequestedQuantity { get; init; }
    public uint OutputQuantity { get; init; } = 1;
    public decimal EstimatedUnitCost { get; init; }
    public decimal EstimatedTotalCost { get; init; }
    public string Currency { get; init; } = "gil";
    public DateTimeOffset QuotedAtUtc { get; init; }
    public string Source { get; init; } = "WorkshopHostCraftArchitect";
    public string Confidence { get; init; } = "Unknown";
    public IReadOnlyList<CraftAppraisalMaterialQuote> Materials { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record CraftAppraisalMaterialQuote
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public decimal QuantityPerCraft { get; init; }
    public decimal TotalQuantity { get; init; }
    public decimal UnitCost { get; init; }
    public decimal TotalCost { get; init; }
    public string CostSource { get; init; } = "Unknown";
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
