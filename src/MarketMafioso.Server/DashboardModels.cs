namespace MarketMafioso.Server;

public sealed record DashboardCharacterOption(
    long Id,
    string CharacterName,
    string? HomeWorld,
    DateTimeOffset LastSeenAt)
{
    public string DisplayName => string.IsNullOrWhiteSpace(HomeWorld)
        ? CharacterName
        : $"{CharacterName} @ {HomeWorld}";
}

public sealed record DashboardSettingsView
{
    public int SchemaVersion { get; init; } = 1;
    public long? DefaultCharacterId { get; init; }
    public string DefaultRegion { get; init; } = "North America";
    public string DefaultWorldMode { get; init; } = "Recommended";
    public int DefaultPickupExpiresSeconds { get; init; } = 300;
    public DateTimeOffset? UpdatedAtUtc { get; init; }
}

public sealed record DashboardSettingsUpdate
{
    public long? DefaultCharacterId { get; init; }
    public string DefaultRegion { get; init; } = "North America";
    public string DefaultWorldMode { get; init; } = "Recommended";
    public int DefaultPickupExpiresSeconds { get; init; } = 300;
}

public sealed record DashboardFeatureFlagsView
{
    public bool EnableMarketAcquisition { get; init; }
}

public record ClientCredentialView
{
    public long Id { get; init; }
    public string Label { get; init; } = string.Empty;
    public string Purpose { get; init; } = string.Empty;
    public string KeyPrefix { get; init; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? LastUsedAtUtc { get; init; }
    public DateTimeOffset? RevokedAtUtc { get; init; }
}

public sealed record ClientCredentialCreatedView : ClientCredentialView
{
    public string Secret { get; init; } = string.Empty;
}

public sealed record ClientCredentialCreateRequest
{
    public string Label { get; init; } = string.Empty;
    public string Purpose { get; init; } = string.Empty;
}
