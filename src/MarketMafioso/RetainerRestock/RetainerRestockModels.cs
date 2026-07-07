using System;
using System.Collections.Generic;

namespace MarketMafioso.RetainerRestock;

public enum RetainerRestockPlanLineStatus
{
    NoNeed,
    Ready,
    Partial,
    NoCachedStock,
}

[Serializable]
public sealed class RetainerRestockPlanItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public int DesiredPlayerQuantity { get; set; }
    public bool Enabled { get; set; } = true;
    public string Note { get; set; } = string.Empty;
}

public sealed record RetainerRestockCandidate(
    ulong RetainerId,
    string RetainerName,
    DateTime LastUpdatedUtc,
    int CachedQuantity);

public sealed record RetainerRestockPlanLine(
    Guid PlanItemId,
    uint ItemId,
    string ItemName,
    int DesiredPlayerQuantity,
    int PlayerQuantity,
    int NeededQuantity,
    int CachedRetainerQuantity,
    int MissingQuantity,
    IReadOnlyList<RetainerRestockCandidate> Candidates,
    RetainerRestockPlanLineStatus Status,
    TimeSpan? OldestRelevantCacheAge);

public sealed record RetainerRestockPlan(
    DateTime BuiltAtUtc,
    IReadOnlyList<RetainerRestockPlanLine> Lines);

public sealed record RetainerOwnerScope(string? CharacterName, string? HomeWorld)
{
    public bool IsAvailable =>
        !string.IsNullOrWhiteSpace(CharacterName) &&
        !string.IsNullOrWhiteSpace(HomeWorld);

    public bool Matches(string? ownerCharacterName, string? ownerHomeWorld) =>
        IsAvailable &&
        string.Equals(ownerCharacterName, CharacterName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(ownerHomeWorld, HomeWorld, StringComparison.OrdinalIgnoreCase);
}
