using System;

namespace MarketMafioso.MarketAcquisition;

/// <summary>
/// A character's bags or retainers selected under "Count stock from". The current character's own
/// sources are expressed through <see cref="MarketAcquisitionRequestDocument.CountRetainerStock"/>
/// (its bags always count); entries here name other characters known through Quartermaster.
/// </summary>
public sealed record MarketAcquisitionStockSourceKey(
    ulong LocalContentId,
    uint HomeWorldId,
    string CharacterName,
    string HomeWorldName,
    string Container)
{
    public const string Bags = "bags";
    public const string Retainers = "retainers";

    public bool SameSource(MarketAcquisitionStockSourceKey other) =>
        LocalContentId == other.LocalContentId &&
        HomeWorldId == other.HomeWorldId &&
        string.Equals(Container, other.Container, StringComparison.Ordinal);

    public bool IsCharacter(ulong localContentId, uint homeWorldId) =>
        LocalContentId == localContentId && HomeWorldId == homeWorldId;
}
