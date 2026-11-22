using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketAcquisitionWorkbenchCompositionStoreSnapshot(
    IReadOnlyList<MarketAcquisitionWorkbenchComposition> Compositions,
    string? SelectedCompositionId,
    MarketAcquisitionWorkbenchCompositionSource? Source = null);

/// <summary>
/// The composition the current Workbench came from (or was last saved into), tied to that
/// Workbench's local identity so a new blank, restore or hosted adoption naturally unlinks it.
/// </summary>
public sealed record MarketAcquisitionWorkbenchCompositionSource(
    string CompositionId,
    string WorkbenchRequestId,
    bool LoadedFromComposition);

public interface IMarketAcquisitionWorkbenchCompositionStore
{
    MarketAcquisitionWorkbenchCompositionStoreSnapshot Load();

    void Save(MarketAcquisitionWorkbenchCompositionStoreSnapshot snapshot);
}

public sealed class ConfigurationMarketAcquisitionWorkbenchCompositionStore : IMarketAcquisitionWorkbenchCompositionStore
{
    private readonly Configuration config;
    private readonly Action saveConfig;

    public ConfigurationMarketAcquisitionWorkbenchCompositionStore(Configuration config, Action saveConfig)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.saveConfig = saveConfig ?? throw new ArgumentNullException(nameof(saveConfig));
    }

    public MarketAcquisitionWorkbenchCompositionStoreSnapshot Load()
    {
        var compositions = (config.MarketAcquisitionWorkbenchCompositions ?? [])
            .Where(stored => !string.IsNullOrWhiteSpace(stored.Id) && !string.IsNullOrWhiteSpace(stored.Name))
            .Select(FromPersisted)
            .OrderBy(composition => composition.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var selectedId = compositions.Any(composition => composition.Id == config.SelectedMarketAcquisitionWorkbenchCompositionId)
            ? config.SelectedMarketAcquisitionWorkbenchCompositionId
            : compositions.FirstOrDefault()?.Id;
        var source = config.MarketAcquisitionWorkbenchCompositionSource is { } stored &&
                     compositions.Any(composition => composition.Id == stored.CompositionId) &&
                     !string.IsNullOrWhiteSpace(stored.WorkbenchRequestId)
            ? new MarketAcquisitionWorkbenchCompositionSource(stored.CompositionId, stored.WorkbenchRequestId, stored.LoadedFromComposition)
            : null;
        return new MarketAcquisitionWorkbenchCompositionStoreSnapshot(compositions, selectedId, source);
    }

    public void Save(MarketAcquisitionWorkbenchCompositionStoreSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        config.MarketAcquisitionWorkbenchCompositions = snapshot.Compositions.Select(ToPersisted).ToList();
        config.SelectedMarketAcquisitionWorkbenchCompositionId = snapshot.SelectedCompositionId;
        config.MarketAcquisitionWorkbenchCompositionSource = snapshot.Source is { } source
            ? new PersistedMarketAcquisitionWorkbenchCompositionSource
            {
                CompositionId = source.CompositionId,
                WorkbenchRequestId = source.WorkbenchRequestId,
                LoadedFromComposition = source.LoadedFromComposition,
            }
            : null;
        saveConfig();
    }

    private static MarketAcquisitionWorkbenchComposition FromPersisted(
        PersistedMarketAcquisitionWorkbenchComposition stored) =>
        new()
        {
            Id = stored.Id,
            Name = stored.Name.Trim(),
            Region = string.IsNullOrWhiteSpace(stored.Region) ? "North America" : stored.Region,
            WorldMode = MarketAcquisitionRequestDocumentMapper.NormalizeBuilderWorldMode(stored.WorldMode),
            SweepScope = string.IsNullOrWhiteSpace(stored.SweepScope) ? "Region" : stored.SweepScope,
            SweepDataCenters = stored.SweepDataCenters?.ToList() ?? [],
            Lines = (stored.Lines ?? []).Select(FromPersistedLine).ToList(),
            StockScope = stored.CountRetainerStock is { } countRetainers
                ? new(
                    countRetainers,
                    (stored.AdditionalStockSources ?? [])
                        .Where(source => source.LocalContentId != 0 &&
                                         source.Container is MarketAcquisitionStockSourceKey.Bags or MarketAcquisitionStockSourceKey.Retainers)
                        .Select(source => new MarketAcquisitionStockSourceKey(source.LocalContentId, source.HomeWorldId,
                            source.CharacterName ?? string.Empty, source.HomeWorldName ?? string.Empty, source.Container))
                        .ToList(),
                    stored.StowAfterDelivery)
                : null,
            CreatedAtUtc = ToUtc(stored.CreatedAtUtc),
            UpdatedAtUtc = ToUtc(stored.UpdatedAtUtc),
        };

    private static PersistedMarketAcquisitionWorkbenchComposition ToPersisted(
        MarketAcquisitionWorkbenchComposition composition) =>
        new()
        {
            Id = composition.Id,
            Name = composition.Name,
            Region = composition.Region,
            WorldMode = composition.WorldMode,
            SweepScope = composition.SweepScope,
            SweepDataCenters = composition.SweepDataCenters.ToList(),
            Lines = composition.Lines.Select(ToPersistedLine).ToList(),
            CountRetainerStock = composition.StockScope?.CountRetainerStock,
            AdditionalStockSources = composition.StockScope?.AdditionalStockSources
                .Select(source => new PersistedMarketAcquisitionStockSource
                {
                    LocalContentId = source.LocalContentId,
                    HomeWorldId = source.HomeWorldId,
                    CharacterName = source.CharacterName,
                    HomeWorldName = source.HomeWorldName,
                    Container = source.Container,
                })
                .ToList(),
            StowAfterDelivery = composition.StockScope?.StowAfterDelivery ?? false,
            CreatedAtUtc = composition.CreatedAtUtc.UtcDateTime,
            UpdatedAtUtc = composition.UpdatedAtUtc.UtcDateTime,
        };

    private static MarketAcquisitionRequestLineDocument FromPersistedLine(
        PersistedMarketAcquisitionRequestLineDocument line) =>
        new()
        {
            ItemId = line.ItemId,
            ItemName = line.ItemName,
            ItemKind = line.ItemKind,
            QuantityMode = string.IsNullOrWhiteSpace(line.QuantityMode) ? "AllBelowThreshold" : line.QuantityMode,
            TargetQuantity = line.TargetQuantity,
            TargetBasis = MarketAcquisitionTargetBases.Normalize(line.TargetBasis),
            MaximumOverage = line.MaximumOverage,
            MaxQuantity = line.MaxQuantity,
            HqPolicy = string.IsNullOrWhiteSpace(line.HqPolicy) ? "Either" : line.HqPolicy,
            MaxUnitPrice = line.MaxUnitPrice,
            GilCap = line.GilCap,
        };

    private static PersistedMarketAcquisitionRequestLineDocument ToPersistedLine(
        MarketAcquisitionRequestLineDocument line) =>
        new()
        {
            ItemId = line.ItemId,
            ItemName = line.ItemName,
            ItemKind = line.ItemKind,
            QuantityMode = line.QuantityMode,
            TargetQuantity = line.TargetQuantity,
            TargetBasis = MarketAcquisitionTargetBases.Normalize(line.TargetBasis),
            MaximumOverage = line.MaximumOverage,
            MaxQuantity = line.MaxQuantity,
            HqPolicy = line.HqPolicy,
            MaxUnitPrice = line.MaxUnitPrice,
            GilCap = line.GilCap,
        };

    private static DateTimeOffset ToUtc(DateTime value) =>
        value == default
            ? DateTimeOffset.UtcNow
            : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
