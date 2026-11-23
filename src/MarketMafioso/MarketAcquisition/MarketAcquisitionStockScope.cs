using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

/// <summary>Which of one character's retainers count: every one (including later ones), these ones, or none.</summary>
public sealed record MarketAcquisitionRetainerScope(bool All, ImmutableHashSet<ulong> Ids)
{
    public static MarketAcquisitionRetainerScope None { get; } = new(false, []);
    public static MarketAcquisitionRetainerScope Every { get; } = new(true, []);

    public bool IsNone => !All && Ids.IsEmpty;

    public bool Includes(ulong retainerId) => All || Ids.Contains(retainerId);
}

/// <summary>
/// Reads and edits the "Count stock from" scope a Workbench document holds. The current character's
/// retainers live in <see cref="MarketAcquisitionRequestDocument.CountRetainerStock"/>, narrowed by
/// per-retainer keys naming that character; other characters live only in keys.
/// </summary>
public static class MarketAcquisitionStockScope
{
    /// <summary>The current character's counted retainers.</summary>
    public static MarketAcquisitionRetainerScope OwnRetainers(
        MarketAcquisitionRequestDocument document,
        ulong localContentId,
        uint homeWorldId)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.CountRetainerStock)
            return MarketAcquisitionRetainerScope.None;
        var ids = document.AdditionalStockSources
            .Where(key => key.IsSingleRetainer && key.IsCharacter(localContentId, homeWorldId))
            .Select(key => key.RetainerId)
            .ToImmutableHashSet();
        return ids.IsEmpty ? MarketAcquisitionRetainerScope.Every : new(false, ids);
    }

    /// <summary>Another character's counted retainers.</summary>
    public static MarketAcquisitionRetainerScope CharacterRetainers(
        MarketAcquisitionRequestDocument document,
        ulong localContentId,
        uint homeWorldId) =>
        CharacterRetainers(document.AdditionalStockSources, localContentId, homeWorldId);

    public static MarketAcquisitionRetainerScope CharacterRetainers(
        IEnumerable<MarketAcquisitionStockSourceKey> sources,
        ulong localContentId,
        uint homeWorldId)
    {
        var keys = sources
            .Where(key => key.Container == MarketAcquisitionStockSourceKey.Retainers && key.IsCharacter(localContentId, homeWorldId))
            .ToList();
        if (keys.Any(key => key.IsAllRetainers))
            return MarketAcquisitionRetainerScope.Every;
        var ids = keys.Select(key => key.RetainerId).ToImmutableHashSet();
        return ids.IsEmpty ? MarketAcquisitionRetainerScope.None : new(false, ids);
    }

    public static bool CharacterBags(MarketAcquisitionRequestDocument document, ulong localContentId, uint homeWorldId) =>
        document.AdditionalStockSources.Any(key =>
            key.Container == MarketAcquisitionStockSourceKey.Bags && key.IsCharacter(localContentId, homeWorldId));

    /// <summary>
    /// The scope after ticking or unticking one retainer. Ticking the last unticked known retainer becomes
    /// "every retainer" again, so retainers that appear later keep counting.
    /// </summary>
    public static MarketAcquisitionRetainerScope Toggle(
        MarketAcquisitionRetainerScope current,
        ulong retainerId,
        bool include,
        IReadOnlyCollection<ulong> knownRetainerIds)
    {
        var ids = current.All ? knownRetainerIds.ToHashSet() : current.Ids.ToHashSet();
        if (include)
            ids.Add(retainerId);
        else
            ids.Remove(retainerId);
        if (ids.Count == 0)
            return MarketAcquisitionRetainerScope.None;
        if (knownRetainerIds.Count > 0 && knownRetainerIds.All(ids.Contains))
            return MarketAcquisitionRetainerScope.Every;
        return new(false, ids.ToImmutableHashSet());
    }

    /// <summary>Writes one character's retainer scope into the document.</summary>
    /// <param name="character">The character; its container and retainer fields are ignored.</param>
    /// <param name="retainerName">Display name for a retainer written as its own key.</param>
    public static MarketAcquisitionRequestDocument WithRetainers(
        MarketAcquisitionRequestDocument document,
        MarketAcquisitionStockSourceKey character,
        bool isCurrentCharacter,
        MarketAcquisitionRetainerScope scope,
        Func<ulong, string> retainerName)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(character);
        var sources = document.AdditionalStockSources
            .Where(key => !(key.Container == MarketAcquisitionStockSourceKey.Retainers &&
                            key.IsCharacter(character.LocalContentId, character.HomeWorldId)))
            .ToList();
        var whole = character with { Container = MarketAcquisitionStockSourceKey.Retainers, RetainerId = 0, RetainerName = string.Empty };
        if (!scope.All)
            sources.AddRange(scope.Ids.Order().Select(id => whole.ForRetainer(id, retainerName(id))));
        else if (!isCurrentCharacter)
            sources.Add(whole);
        return document with
        {
            CountRetainerStock = isCurrentCharacter ? !scope.IsNone : document.CountRetainerStock,
            AdditionalStockSources = sources,
        };
    }

    /// <summary>Adds or removes another character's bags.</summary>
    public static MarketAcquisitionRequestDocument WithBags(
        MarketAcquisitionRequestDocument document,
        MarketAcquisitionStockSourceKey character,
        bool include)
    {
        ArgumentNullException.ThrowIfNull(document);
        var bags = character with { Container = MarketAcquisitionStockSourceKey.Bags, RetainerId = 0, RetainerName = string.Empty };
        var sources = document.AdditionalStockSources.Where(key => !key.SameSource(bags)).ToList();
        if (include)
            sources.Add(bags);
        return document with { AdditionalStockSources = sources };
    }

    // ---- Persistence ---------------------------------------------------------------------------

    /// <summary>
    /// Saved sources as keys. Entries saved before single retainers could be chosen have no retainer id and
    /// keep meaning every retainer of that character.
    /// </summary>
    internal static List<MarketAcquisitionStockSourceKey> Restore(IEnumerable<PersistedMarketAcquisitionStockSource>? stored) =>
        (stored ?? [])
            .Where(source => source.LocalContentId != 0 &&
                             source.Container is MarketAcquisitionStockSourceKey.Bags or MarketAcquisitionStockSourceKey.Retainers)
            .Select(source => new MarketAcquisitionStockSourceKey(
                source.LocalContentId,
                source.HomeWorldId,
                source.CharacterName ?? string.Empty,
                source.HomeWorldName ?? string.Empty,
                source.Container,
                source.Container == MarketAcquisitionStockSourceKey.Retainers ? source.RetainerId : 0,
                source.Container == MarketAcquisitionStockSourceKey.Retainers ? source.RetainerName ?? string.Empty : string.Empty))
            .ToList();

    internal static List<PersistedMarketAcquisitionStockSource> Persist(IEnumerable<MarketAcquisitionStockSourceKey> sources) =>
        sources
            .Select(source => new PersistedMarketAcquisitionStockSource
            {
                LocalContentId = source.LocalContentId,
                HomeWorldId = source.HomeWorldId,
                CharacterName = source.CharacterName,
                HomeWorldName = source.HomeWorldName,
                Container = source.Container,
                RetainerId = source.RetainerId,
                RetainerName = source.IsSingleRetainer ? source.RetainerName : null,
            })
            .ToList();
}
