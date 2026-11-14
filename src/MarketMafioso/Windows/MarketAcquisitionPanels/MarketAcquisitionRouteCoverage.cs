using System;
using System.Collections.Generic;
using System.Linq;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Windows.MarketAcquisitionPanels;

/// <summary>One target line's coverage at one stop, with what earlier stops already planned.</summary>
/// <remarks>For a skipped stop, <see cref="PriorQuantity"/> is the whole route's plan for the line.</remarks>
internal sealed record RouteCoverageBar(
    string LineId,
    uint ItemId,
    string ItemName,
    uint Need,
    ulong StopQuantity,
    double Share,
    ulong PriorQuantity)
{
    public ulong CumulativeQuantity => PriorQuantity + StopQuantity;
    public double PriorFraction => Fraction(PriorQuantity);
    public double CumulativeFraction => Fraction(CumulativeQuantity);
    public bool Complete => CumulativeQuantity >= Need;
    public bool Overflow => CumulativeQuantity > Need;

    private double Fraction(ulong quantity) => Need == 0 ? 0 : Math.Min(1d, (double)quantity / Need);
}

/// <summary>A target line still under its need after the whole route.</summary>
internal sealed record RouteCoverageShortfall(string LineId, uint ItemId, string ItemName, uint Need, ulong Planned)
{
    public double Fraction => Need == 0 ? 1 : Math.Min(1d, (double)Planned / Need);
    public ulong Gap => Need > Planned ? Need - Planned : 0;
}

/// <summary>Ranked per-stop coverage for the plan's route-stop table, built once per plan instance.</summary>
internal sealed class MarketAcquisitionRouteCoverage
{
    private const string BuyBelowMode = "AllBelowThreshold";

    private MarketAcquisitionRouteCoverage(
        IReadOnlyList<IReadOnlyList<RouteCoverageBar>> stops,
        IReadOnlyList<MarketAcquisitionStopDecision> skipped,
        IReadOnlyList<IReadOnlyList<RouteCoverageBar>> skippedBars,
        IReadOnlyList<RouteCoverageShortfall> shortfalls,
        IReadOnlyDictionary<string, MarketAcquisitionPlanLine> lines)
    {
        Stops = stops;
        Skipped = skipped;
        SkippedBars = skippedBars;
        Shortfalls = shortfalls;
        Lines = lines;
        RequiredSlots = stops.Concat(skippedBars).Select(bars => bars.Count)
            .Append(shortfalls.Count).Max();
    }

    /// <summary>Ranked bars per route stop, aligned with <see cref="MarketAcquisitionPlan.WorldBatches"/>.</summary>
    public IReadOnlyList<IReadOnlyList<RouteCoverageBar>> Stops { get; }

    /// <summary>Skipped stop decisions in display order.</summary>
    public IReadOnlyList<MarketAcquisitionStopDecision> Skipped { get; }

    /// <summary>Ranked bars per skipped stop, aligned with <see cref="Skipped"/>.</summary>
    public IReadOnlyList<IReadOnlyList<RouteCoverageBar>> SkippedBars { get; }

    /// <summary>Lines still short after the route, biggest gap first.</summary>
    public IReadOnlyList<RouteCoverageShortfall> Shortfalls { get; }

    public IReadOnlyDictionary<string, MarketAcquisitionPlanLine> Lines { get; }

    /// <summary>The most ranked entries any row has.</summary>
    public int RequiredSlots { get; }

    public bool IsBuyBelow(string lineId) =>
        Lines.TryGetValue(lineId, out var line) && line.QuantityMode == BuyBelowMode;

    public static MarketAcquisitionRouteCoverage Build(MarketAcquisitionPlan plan)
    {
        var lines = plan.Lines
            .GroupBy(line => line.LineId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var planned = new Dictionary<string, ulong>(StringComparer.Ordinal);

        // Batches are already in route order, so a running total is "covered by earlier stops".
        var stops = plan.WorldBatches.Select(batch =>
        {
            var bars = Covered(batch.LineCoverage, lines, lineId => planned.GetValueOrDefault(lineId));
            foreach (var bar in bars)
                planned[bar.LineId] = bar.CumulativeQuantity;
            return Rank(bars);
        }).ToArray();

        var skipped = plan.Diagnostics.StopDecisions
            .Where(decision => decision.Skipped)
            .OrderByDescending(decision => decision.CoverageScore)
            .ThenByDescending(decision => decision.BuyBelowSavingsGil)
            .ToArray();
        var skippedBars = skipped
            .Select(decision => Rank(Covered(decision.LineCoverage, lines, lineId => planned.GetValueOrDefault(lineId))))
            .ToArray();

        // Older persisted plans lack per-stop coverage; without it every line would look short.
        var coverageKnown = plan.WorldBatches.Count == 0 || plan.WorldBatches.Any(batch => batch.LineCoverage.Count > 0);
        var shortfalls = !coverageKnown
            ? []
            : lines.Values
                .Where(line => line.QuantityMode != BuyBelowMode)
                .Select(line => new RouteCoverageShortfall(
                    line.LineId,
                    line.ItemId,
                    ItemName(line.ItemName),
                    MarketAcquisitionPlanner.RemainingNeed(line),
                    planned.GetValueOrDefault(line.LineId)))
                .Where(shortfall => shortfall.Need > 0 && shortfall.Planned < shortfall.Need)
                .OrderBy(shortfall => shortfall.Fraction)
                .ThenByDescending(shortfall => shortfall.Gap)
                .ThenBy(shortfall => shortfall.ItemName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        return new MarketAcquisitionRouteCoverage(stops, skipped, skippedBars, shortfalls, lines);
    }

    private static RouteCoverageBar[] Covered(
        IReadOnlyList<MarketAcquisitionStopLineCoverage> coverage,
        IReadOnlyDictionary<string, MarketAcquisitionPlanLine> lines,
        Func<string, ulong> prior) =>
        coverage
            .Where(line => line.PlannedQuantity > 0 && line.RemainingNeed > 0)
            .GroupBy(line => line.LineId, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                var quantity = group.Aggregate(0UL, (total, line) => total + line.PlannedQuantity);
                lines.TryGetValue(group.Key, out var line);
                return new RouteCoverageBar(
                    group.Key,
                    line?.ItemId ?? 0,
                    ItemName(first.ItemName ?? line?.ItemName),
                    first.RemainingNeed,
                    quantity,
                    Math.Min(1d, (double)quantity / first.RemainingNeed),
                    prior(group.Key));
            })
            .ToArray();

    private static IReadOnlyList<RouteCoverageBar> Rank(IEnumerable<RouteCoverageBar> bars) =>
        bars.OrderByDescending(bar => bar.Share)
            .ThenByDescending(bar => bar.StopQuantity)
            .ThenBy(bar => bar.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string ItemName(string? name) => string.IsNullOrWhiteSpace(name) ? "Unnamed item" : name;
}
