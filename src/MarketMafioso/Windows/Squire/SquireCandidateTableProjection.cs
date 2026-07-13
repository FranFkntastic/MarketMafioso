using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire;

namespace MarketMafioso.Windows.Squire;

internal static class SquireCandidateTableProjection
{
    public const int ColumnCount = 14;

    public static SquireCandidate[] Filter(IEnumerable<SquireCandidate> rows, IReadOnlyList<string> filters) =>
        Filter(rows, filters, null);

    public static SquireCandidate[] Filter(
        IEnumerable<SquireCandidate> rows,
        IReadOnlyList<string> filters,
        Func<SquireCandidate, string>? rowState)
    {
        if (filters.Count != ColumnCount)
            throw new ArgumentException($"Expected {ColumnCount} Squire table filters, but received {filters.Count}.", nameof(filters));

        return rows.Where(candidate =>
            Matches(candidate.Definition.Name, filters[0]) &&
            Matches(SquirePresentation.FormatLocation(candidate.Instance.Fingerprint), filters[1]) &&
            Matches(candidate.Definition.EquipLevel.ToString(), filters[2]) &&
            Matches(candidate.Definition.ItemLevel.ToString(), filters[3]) &&
            Matches(FormatRarity(candidate.Definition.NormalizedRarity), filters[4]) &&
            Matches(candidate.Instance.Fingerprint.IsHighQuality ? "HQ" : "Normal", filters[5]) &&
            Matches(candidate.Instance.Fingerprint.MateriaIds.Count.ToString(), filters[6]) &&
            Matches(FormatCondition(candidate.Instance.Fingerprint.Condition), filters[7]) &&
            Matches(EquipmentWearerInference.Infer(candidate.Definition).Label, filters[8]) &&
            Matches(SquirePresentation.FormatAssessment(candidate.Assessment), filters[9]) &&
            Matches(SquirePresentation.FormatDisposition(candidate.RecommendedDisposition), filters[10]) &&
            Matches(rowState?.Invoke(candidate) ?? string.Empty, filters[11]) &&
            Matches(SquirePresentation.FormatReasons(candidate), filters[12]) &&
            Matches(candidate.Definition.ItemId.ToString(), filters[13])).ToArray();
    }

    public static SquireCandidate[] Sort(SquireCandidate[] rows, ImGuiTableSortSpecsPtr sortSpecs)
        => Sort(rows, sortSpecs, null);

    public static SquireCandidate[] Sort(
        SquireCandidate[] rows,
        ImGuiTableSortSpecsPtr sortSpecs,
        Func<SquireCandidate, string>? rowState)
    {
        if (sortSpecs.SpecsCount == 0)
            return rows;
        var spec = sortSpecs.Specs;
        return spec.ColumnIndex switch
        {
            0 => SortBy(rows, candidate => candidate.Definition.Name, spec.SortDirection),
            1 => SortBy(rows, candidate => $"{SquirePresentation.FormatContainer(candidate.Instance.Fingerprint.Container)}:{candidate.Instance.Fingerprint.SlotIndex:D3}", spec.SortDirection),
            2 => SortBy(rows, candidate => candidate.Definition.EquipLevel, spec.SortDirection),
            3 => SortBy(rows, candidate => candidate.Definition.ItemLevel, spec.SortDirection),
            4 => SortBy(rows, candidate => candidate.Definition.NormalizedRarity, spec.SortDirection),
            5 => SortBy(rows, candidate => candidate.Instance.Fingerprint.IsHighQuality, spec.SortDirection),
            6 => SortBy(rows, candidate => candidate.Instance.Fingerprint.MateriaIds.Count, spec.SortDirection),
            7 => SortBy(rows, candidate => candidate.Instance.Fingerprint.Condition, spec.SortDirection),
            8 => SortBy(rows, candidate => EquipmentWearerInference.Infer(candidate.Definition).Label, spec.SortDirection),
            9 => SortBy(rows, candidate => SquirePresentation.FormatAssessment(candidate.Assessment), spec.SortDirection),
            10 => SortBy(rows, candidate => SquirePresentation.FormatDisposition(candidate.RecommendedDisposition), spec.SortDirection),
            11 when rowState is not null => SortBy(rows, rowState, spec.SortDirection),
            11 => rows,
            12 => SortBy(rows, SquirePresentation.FormatReasons, spec.SortDirection),
            13 => SortBy(rows, candidate => candidate.Definition.ItemId, spec.SortDirection),
            _ => rows,
        };
    }

    public static string FormatRarity(EquipmentRarity rarity) => rarity switch
    {
        EquipmentRarity.Normal => "Common (white)",
        EquipmentRarity.Uncommon => "Uncommon (green)",
        EquipmentRarity.Rare => "Rare (blue)",
        EquipmentRarity.Relic => "Relic (purple)",
        _ => "Unknown",
    };

    public static string FormatCondition(ushort condition) => $"{condition / 300f:0.#}%";

    private static bool Matches(string value, string filter) =>
        string.IsNullOrWhiteSpace(filter) || value.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);

    private static SquireCandidate[] SortBy<TKey>(
        SquireCandidate[] rows,
        Func<SquireCandidate, TKey> keySelector,
        ImGuiSortDirection direction)
    {
        var ordered = direction == ImGuiSortDirection.Descending
            ? rows.OrderByDescending(keySelector)
            : rows.OrderBy(keySelector);
        return ordered
            .ThenBy(candidate => candidate.Instance.Fingerprint.Container)
            .ThenBy(candidate => candidate.Instance.Fingerprint.SlotIndex)
            .ToArray();
    }
}
