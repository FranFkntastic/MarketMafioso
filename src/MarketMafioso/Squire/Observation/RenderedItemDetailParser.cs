using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using MarketMafioso.AgentBridge;

namespace MarketMafioso.Squire.Observation;

public enum RenderedItemDetailStatus
{
    Complete,
    Unavailable,
    Incomplete,
}

public enum RenderedItemQuality
{
    Normal,
    High,
}

public sealed record RenderedItemDetailObservation(
    RenderedItemDetailStatus Status,
    string? Name,
    RenderedItemQuality? Quality,
    int? ItemLevel,
    int? EquipLevel,
    string? JobCategory,
    IReadOnlyDictionary<string, int> Stats,
    string Diagnostic);

/// <summary>
/// Parses only text rendered by the ItemDetail addon. Static sheet data may later resolve the
/// observed name, but it never substitutes for a missing rendered identity or quality marker.
/// </summary>
public static partial class RenderedItemDetailParser
{
    private const char HighQualityGlyph = '\uE03C';

    public static RenderedItemDetailObservation Parse(AgentBridgeRenderedUiSnapshot snapshot)
    {
        var addon = snapshot.Addons.FirstOrDefault(value => string.Equals(value.Name, "ItemDetail", StringComparison.Ordinal));
        if (addon is not { Present: true, Ready: true, Visible: true })
            return Unavailable("No rendered Item Detail tooltip is visible.");

        var texts = addon.TextNodes
            .GroupBy(value => value.NodePath, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Text.Trim(), StringComparer.Ordinal);
        if (!texts.TryGetValue("ItemDetail/33", out var renderedName) || string.IsNullOrWhiteSpace(renderedName))
            return Incomplete("The rendered tooltip has no item-name field.");

        var quality = renderedName.Contains(HighQualityGlyph)
            ? RenderedItemQuality.High
            : RenderedItemQuality.Normal;
        var name = renderedName.Replace(HighQualityGlyph.ToString(), string.Empty, StringComparison.Ordinal).Trim();

        var itemLevel = FindInt(texts.Values, ItemLevelPattern());
        var equipLevel = texts.TryGetValue("ItemDetail/66/2", out var requirements)
            ? FindInt(requirements.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries), EquipLevelPattern())
            : null;
        texts.TryGetValue("ItemDetail/65/2", out var jobCategory);
        if (itemLevel == null || equipLevel == null || string.IsNullOrWhiteSpace(jobCategory))
            return Incomplete("Rendered item name, item level, equip level, and job category did not form a complete tuple.");

        var stats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, text) in texts.Where(value => value.Key.StartsWith("ItemDetail/100/", StringComparison.Ordinal)))
        {
            _ = path;
            var match = StatPattern().Match(text);
            if (!match.Success || !int.TryParse(match.Groups[2].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value))
                continue;
            stats[match.Groups[1].Value] = value;
        }

        return new(
            RenderedItemDetailStatus.Complete,
            name,
            quality,
            itemLevel,
            equipLevel,
            jobCategory.Trim(),
            stats,
            "Rendered Item Detail observation is complete.");
    }

    private static int? FindInt(IEnumerable<string> values, Regex pattern)
    {
        foreach (var value in values)
        {
            var match = pattern.Match(value);
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var result))
                return result;
        }
        return null;
    }

    private static RenderedItemDetailObservation Unavailable(string diagnostic) =>
        new(RenderedItemDetailStatus.Unavailable, null, null, null, null, null, new Dictionary<string, int>(), diagnostic);

    private static RenderedItemDetailObservation Incomplete(string diagnostic) =>
        new(RenderedItemDetailStatus.Incomplete, null, null, null, null, null, new Dictionary<string, int>(), diagnostic);

    [GeneratedRegex(@"^Item Level\s+(\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ItemLevelPattern();

    [GeneratedRegex(@"^Lv\.\s*(\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex EquipLevelPattern();

    [GeneratedRegex(@"^(.+?)\s+\+([\d,]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex StatPattern();
}
