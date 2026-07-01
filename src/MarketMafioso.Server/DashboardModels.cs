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
