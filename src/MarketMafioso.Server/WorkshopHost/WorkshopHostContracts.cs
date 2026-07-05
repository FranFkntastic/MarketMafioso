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
