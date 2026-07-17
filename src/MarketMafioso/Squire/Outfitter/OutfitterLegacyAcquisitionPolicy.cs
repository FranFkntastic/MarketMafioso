using System;
using System.Linq;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire.Outfitter;

public sealed record OutfitterAcquisitionReadiness(
    bool CanStage,
    string Code,
    string Message);

/// <summary>
/// Containment policy for the legacy item-level Outfitter. Its offers do not carry
/// exact quality identity, so it cannot create a truthful Market Acquisition request.
/// The replacement planner will use a separate policy over quality-resolved offers.
/// </summary>
public static class OutfitterLegacyAcquisitionPolicy
{
    public static OutfitterAcquisitionReadiness Evaluate(EquipmentLoadoutPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var marketCount = plan.Entries.Count(entry =>
            entry.Recommended?.SourceKind == EquipmentAcquisitionSourceKind.MarketBoard);
        return marketCount == 0
            ? new(false, "NoMarketItems", "The selected legacy plan has no market-board items to stage.")
            : new(
                false,
                "ExactQualityUnavailable",
                "Market staging is blocked because the legacy Outfitter does not preserve exact NQ/HQ offer identity.");
    }
}
