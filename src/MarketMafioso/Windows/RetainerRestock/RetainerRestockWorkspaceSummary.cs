using System;
using System.Collections.Generic;
using System.Linq;
using MarketMafioso.RetainerRestock;

namespace MarketMafioso.Windows.RetainerRestock;

public sealed record RetainerRestockWorkspaceSummary(
    string Owner,
    int PlanLineCount,
    int ReadyLineCount,
    int UnitsToRetrieve,
    int MissingUnits,
    int CachedRetainerCount,
    DateTime? NewestCacheUtc)
{
    public static RetainerRestockWorkspaceSummary Build(
        RetainerRestockPlan plan,
        RetainerOwnerScope ownerScope,
        IReadOnlyCollection<CachedRetainer> retainers)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(ownerScope);
        ArgumentNullException.ThrowIfNull(retainers);

        var scopedRetainers = ownerScope.IsAvailable
            ? retainers.Where(retainer => ownerScope.Matches(retainer.OwnerCharacterName, retainer.OwnerHomeWorld)).ToList()
            : [];
        var actionable = plan.Lines.Where(line => line.NeededQuantity > 0).ToList();
        return new RetainerRestockWorkspaceSummary(
            ownerScope.IsAvailable ? $"{ownerScope.CharacterName} @ {ownerScope.HomeWorld}" : "Character unavailable",
            plan.Lines.Count,
            actionable.Count(line => line.Candidates.Count > 0),
            actionable.Sum(line => Math.Min(line.NeededQuantity, line.CachedRetainerQuantity)),
            actionable.Sum(line => line.MissingQuantity),
            scopedRetainers.Count,
            scopedRetainers.Count == 0 ? null : scopedRetainers.Max(retainer => retainer.LastUpdated));
    }
}
