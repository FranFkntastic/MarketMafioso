using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketAcquisitionWorkbenchComposition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "Untitled composition";
    public string Region { get; init; } = "North America";
    public string WorldMode { get; init; } = "Recommended";
    public string SweepScope { get; init; } = "Region";
    public List<string> SweepDataCenters { get; init; } = [];
    public List<MarketAcquisitionRequestLineDocument> Lines { get; init; } = [];
    /// <summary>The plan's stock scope and stow choice; null in a composition saved before they were kept.</summary>
    public MarketAcquisitionCompositionStockScope? StockScope { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public static MarketAcquisitionWorkbenchComposition FromDocument(
        string name,
        MarketAcquisitionRequestDocument document,
        DateTimeOffset nowUtc) =>
        new()
        {
            Name = name.Trim(),
            Region = document.Region,
            WorldMode = document.WorldMode,
            SweepScope = document.SweepScope,
            SweepDataCenters = document.SweepDataCenters.ToList(),
            Lines = CloneLines(document.Lines),
            StockScope = MarketAcquisitionCompositionStockScope.Of(document),
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };

    public MarketAcquisitionWorkbenchComposition WithDocument(
        MarketAcquisitionRequestDocument document,
        DateTimeOffset nowUtc) =>
        this with
        {
            Region = document.Region,
            WorldMode = document.WorldMode,
            SweepScope = document.SweepScope,
            SweepDataCenters = document.SweepDataCenters.ToList(),
            Lines = CloneLines(document.Lines),
            StockScope = MarketAcquisitionCompositionStockScope.Of(document),
            UpdatedAtUtc = nowUtc,
        };

    public MarketAcquisitionRequestDocument CreateDocument(string characterName, string world) =>
        MarketAcquisitionRequestDocument.CreateDefault(characterName, world) with
        {
            Region = Region,
            WorldMode = WorldMode,
            SweepScope = SweepScope,
            SweepDataCenters = SweepDataCenters.ToList(),
            Lines = CloneLines(Lines),
            CountRetainerStock = StockScope?.CountRetainerStock ?? false,
            OwnRetainerSelection = StockScope?.OwnRetainerSelection.ToList() ?? [],
            AdditionalStockSources = StockScope?.AdditionalStockSources.ToList() ?? [],
            StowAfterDelivery = StockScope?.StowAfterDelivery ?? false,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

    internal static List<MarketAcquisitionRequestLineDocument> CloneLines(
        IEnumerable<MarketAcquisitionRequestLineDocument> lines) =>
        lines.Select(line => line with { }).ToList();
}

public sealed record MarketAcquisitionCompositionStockScope(
    bool CountRetainerStock,
    IReadOnlyList<MarketAcquisitionStockSourceKey> AdditionalStockSources,
    bool StowAfterDelivery,
    IReadOnlyList<MarketAcquisitionStockSourceKey>? OwnRetainers = null)
{
    public IReadOnlyList<MarketAcquisitionStockSourceKey> OwnRetainerSelection => OwnRetainers ?? [];

    public static MarketAcquisitionCompositionStockScope Of(MarketAcquisitionRequestDocument document) =>
        new(
            document.CountRetainerStock,
            document.AdditionalStockSources.ToList(),
            document.StowAfterDelivery,
            document.OwnRetainerSelection.ToList());
}
