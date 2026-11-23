using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionRequestDocumentHasher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string ComputeHostedIntentHash(MarketAcquisitionRequestDocument document) =>
        ComputeIntentHash(document with
        {
            RunSpendCap = 0,
            OwnRetainerSelection = [],
            AdditionalStockSources = [],
            StowAfterDelivery = false,
            Lines = document.Lines.Select(line => line with
            {
                TargetBasis = MarketAcquisitionTargetBases.Hosted(line.TargetBasis),
                RestockReserve = false,
            }).ToList(),
        });

    public static string ComputeIntentHash(MarketAcquisitionRequestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var normalized = new
        {
            Region = Normalize(document.Region),
            RunSpendCap = document.RunSpendCap == 0 ? (uint?)null : document.RunSpendCap,
            WorldMode = Normalize(document.WorldMode),
            SweepScope = string.IsNullOrWhiteSpace(document.SweepScope) ? "Region" : document.SweepScope.Trim(),
            SweepDataCenters = document.SweepDataCenters
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            // Local stock policy; omitted when unset so earlier documents keep their hashes.
            AdditionalStockSources = document.AdditionalStockSources.Count == 0
                ? null
                : document.AdditionalStockSources
                    .Select(source => source.RetainerId == 0
                        ? $"{source.LocalContentId}:{source.HomeWorldId}:{source.Container}"
                        : $"{source.LocalContentId}:{source.HomeWorldId}:{source.Container}:{source.RetainerId}")
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray(),
            // Local stock policy; omitted when unset so earlier documents keep their hashes.
            OwnRetainerSelection = document.OwnRetainerSelection.Count == 0
                ? null
                : document.OwnRetainerSelection
                    .Select(source => $"{source.LocalContentId}:{source.HomeWorldId}:{source.RetainerId}")
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray(),
            StowAfterDelivery = document.StowAfterDelivery ? true : (bool?)null,
            Lines = document.Lines
                .Select(line => new
                {
                    line.ItemId,
                    ItemName = Normalize(line.ItemName),
                    ItemKind = Normalize(line.ItemKind),
                    QuantityMode = Normalize(line.QuantityMode),
                    line.TargetQuantity,
                    TargetBasis = MarketAcquisitionTargetBases.Normalize(line.TargetBasis),
                    line.MaximumOverage,
                    line.MaxQuantity,
                    HqPolicy = Normalize(line.HqPolicy),
                    line.MaxUnitPrice,
                    line.GilCap,
                    RestockReserve = line.RestockReserve ? true : (bool?)null,
                })
                .ToArray(),
        };

        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
