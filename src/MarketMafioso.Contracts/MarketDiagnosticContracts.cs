namespace MarketMafioso.Contracts;

public sealed record RetainerSaleEvidenceCreateRequest
{
    public string Source { get; init; } = "RetainerSaleChat";
    public string EvidenceId { get; init; } = string.Empty;
    public ulong? RetainerId { get; init; }
    public string? RetainerName { get; init; }
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public bool IsHq { get; init; }
    public uint? Quantity { get; init; }
    public uint? UnitPrice { get; init; }
    public ulong TotalGil { get; init; }
    public DateTimeOffset EventAtUtc { get; init; }
    public DateTimeOffset? EarliestEventAtUtc { get; init; }
    public DateTimeOffset? LatestEventAtUtc { get; init; }
    public string? CharacterName { get; init; }
    public string? HomeWorld { get; init; }
    public string? RawMessage { get; init; }
}

public sealed record RetainerSaleEvidenceCreateResponse
{
    public long Id { get; init; }
    public bool Duplicate { get; init; }
    public long? OwnedListingVersionId { get; init; }
}
