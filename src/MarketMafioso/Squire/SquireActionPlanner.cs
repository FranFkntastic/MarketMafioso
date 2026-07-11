using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire;

public sealed record SquireReviewedSelection(
    EquipmentInstanceFingerprint Fingerprint,
    SquireDisposition Disposition,
    IReadOnlyList<string> ReasonCodes);

public sealed record SquireActionPlan(
    Guid SnapshotGenerationId,
    CharacterScope Character,
    SquireDisposition Disposition,
    DateTimeOffset ApprovedAt,
    IReadOnlyList<SquireReviewedSelection> Actions);

public sealed class SquireActionPlanner
{
    public SquireActionPlan Create(
        SquireAnalysis analysis,
        SquireDisposition disposition,
        IReadOnlyCollection<EquipmentInstanceFingerprint> selected,
        DateTimeOffset approvedAt)
    {
        if (!analysis.Snapshot.Diagnostics.IsComplete)
            throw new InvalidOperationException("A partial snapshot cannot produce an action plan.");
        if (disposition is SquireDisposition.Keep or SquireDisposition.Unsupported)
            throw new InvalidOperationException("The requested disposition is not executable.");
        var scope = analysis.Snapshot.Identity.Scope
            ?? throw new InvalidOperationException("Character scope is unavailable.");

        var byFingerprint = analysis.Candidates.ToDictionary(candidate => candidate.Instance.Fingerprint);
        var actions = selected.Select(fingerprint =>
        {
            if (!byFingerprint.TryGetValue(fingerprint, out var candidate) || !candidate.IsExecutable)
                throw new InvalidOperationException("A selected item is not an executable candidate.");
            if (!candidate.SupportedDispositions.Contains(disposition))
                throw new InvalidOperationException("The selected disposition is unsupported for an item.");
            return new SquireReviewedSelection(fingerprint, disposition, candidate.Reasons.Select(reason => reason.Code).ToArray());
        }).ToArray();

        if (actions.Length == 0)
            throw new InvalidOperationException("At least one reviewed item is required.");
        return new SquireActionPlan(analysis.Snapshot.GenerationId, scope, disposition, approvedAt, actions);
    }
}
