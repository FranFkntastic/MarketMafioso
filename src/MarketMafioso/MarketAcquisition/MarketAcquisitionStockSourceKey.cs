using System;

namespace MarketMafioso.MarketAcquisition;

/// <summary>
/// A character's bags, all of its retainers, or one of its retainers, selected under "Count stock from".
/// The current character's own sources are expressed through
/// <see cref="MarketAcquisitionRequestDocument.CountRetainerStock"/> (its bags always count); per-retainer
/// entries naming the current character narrow that choice to those retainers. Other entries name other
/// characters known through Quartermaster.
/// </summary>
/// <remarks>
/// A retainers entry with <see cref="RetainerId"/> 0 means every retainer of the character, including
/// ones that appear later; entries saved before single retainers could be chosen read that way.
/// </remarks>
public sealed record MarketAcquisitionStockSourceKey(
    ulong LocalContentId,
    uint HomeWorldId,
    string CharacterName,
    string HomeWorldName,
    string Container,
    ulong RetainerId = 0,
    string RetainerName = "")
{
    public const string Bags = "bags";
    public const string Retainers = "retainers";

    /// <summary>One named retainer rather than a whole container.</summary>
    public bool IsSingleRetainer => RetainerId != 0 && Container == Retainers;

    /// <summary>Every retainer of the character.</summary>
    public bool IsAllRetainers => RetainerId == 0 && Container == Retainers;

    public bool SameSource(MarketAcquisitionStockSourceKey other) =>
        LocalContentId == other.LocalContentId &&
        HomeWorldId == other.HomeWorldId &&
        string.Equals(Container, other.Container, StringComparison.Ordinal) &&
        RetainerId == other.RetainerId;

    public bool IsCharacter(ulong localContentId, uint homeWorldId) =>
        LocalContentId == localContentId && HomeWorldId == homeWorldId;

    public MarketAcquisitionStockSourceKey ForRetainer(ulong retainerId, string retainerName) =>
        this with { Container = Retainers, RetainerId = retainerId, RetainerName = retainerName ?? string.Empty };

    /// <summary>"Name · bags", "Name · retainers" or "Name · Retainer".</summary>
    public string Label => IsSingleRetainer
        ? $"{CharacterName} · {(string.IsNullOrWhiteSpace(RetainerName) ? $"Retainer {RetainerId}" : RetainerName)}"
        : $"{CharacterName} · {Container}";
}
