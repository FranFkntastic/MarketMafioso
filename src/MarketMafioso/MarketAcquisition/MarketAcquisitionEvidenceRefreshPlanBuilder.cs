using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.MarketAcquisition;

public static class MarketAcquisitionEvidenceRefreshPlanBuilder
{
    public static MarketAcquisitionPlan Build(
        MarketAcquisitionClaimView claim,
        string currentWorld,
        DateTimeOffset preparedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(claim);

        var worlds = ResolveLegacyWorlds(claim, currentWorld);
        return Build(claim, worlds, preparedAtUtc);
    }

    public static MarketAcquisitionPlan Build(
        MarketAcquisitionClaimView claim,
        IReadOnlyCollection<string> preparedWorlds,
        DateTimeOffset preparedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(preparedWorlds);

        var worlds = NormalizeWorlds(preparedWorlds);
        var requestLines = MarketAcquisitionPlanPreparationService.GetPlanLines(claim);
        var planLines = requestLines
            .Select(line => new MarketAcquisitionPlanLine
            {
                LineId = line.LineId,
                Ordinal = line.Ordinal,
                ItemId = line.ItemId,
                ItemName = line.ItemName,
                QuantityMode = line.QuantityMode,
                RequestedQuantity = line.TargetQuantity,
                TargetBasis = MarketAcquisitionTargetBases.Normalize(line.TargetBasis),
                MaximumOverage = line.MaximumOverage,
                HqPolicy = line.HqPolicy,
                MaxUnitPrice = line.MaxUnitPrice,
                GilCap = line.GilCap,
                Status = "EvidenceRefresh",
            })
            .ToList();
        var batches = worlds
            .Select(world => new MarketAcquisitionWorldBatch
            {
                WorldName = world,
                DataCenter = MarketAcquisitionWorldCatalog.ResolveDataCenter(world),
                ItemSubtasks = requestLines
                    .Select(line => new MarketAcquisitionWorldItemSubtask
                    {
                        LineId = line.LineId,
                        LineOrdinal = line.Ordinal,
                        Source = "EvidenceRefresh",
                        ItemId = line.ItemId,
                        ItemName = line.ItemName,
                        WorldName = world,
                        DataCenter = MarketAcquisitionWorldCatalog.ResolveDataCenter(world),
                        QuantityMode = line.QuantityMode,
                        RequestedQuantity = line.TargetQuantity,
                        TargetBasis = MarketAcquisitionTargetBases.Normalize(line.TargetBasis),
                        MaximumOverage = line.MaximumOverage,
                        HqPolicy = line.HqPolicy,
                        MaxUnitPrice = line.MaxUnitPrice,
                        GilCap = line.GilCap,
                    })
                    .ToList(),
            })
            .ToList();

        return new MarketAcquisitionPlan
        {
            RequestId = claim.Id,
            Status = "Ready",
            WorldMode = claim.WorldMode,
            ItemId = planLines[0].ItemId,
            RequestedQuantity = (uint)planLines.Sum(line => line.RequestedQuantity),
            PreparedAtUtc = preparedAtUtc,
            Lines = planLines,
            WorldBatches = batches,
        };
    }

    private static IReadOnlyList<string> ResolveLegacyWorlds(MarketAcquisitionClaimView claim, string currentWorld)
    {
        IEnumerable<string> worlds = claim.WorldMode.Equals("Selected", StringComparison.OrdinalIgnoreCase)
            ? claim.SelectedWorlds
            : claim.WorldMode.Equals("CurrentWorldOnly", StringComparison.OrdinalIgnoreCase)
                ? [currentWorld]
                : throw new InvalidOperationException(
                    "Evidence refresh requires a selected-world or current-world request so it cannot expand into an unintended regional sweep.");

        return NormalizeWorlds(worlds);
    }

    private static IReadOnlyList<string> NormalizeWorlds(IEnumerable<string> worlds)
    {
        var normalized = worlds
            .Where(world => !string.IsNullOrWhiteSpace(world))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalized.Count == 0)
            throw new InvalidOperationException("Evidence refresh requires at least one prepared target world.");

        return normalized;
    }
}
