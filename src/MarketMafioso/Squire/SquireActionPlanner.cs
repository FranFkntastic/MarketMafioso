using System;
using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire;

public sealed record SquireReviewedSelection(
    EquipmentInstanceFingerprint Fingerprint,
    SquireDisposition Disposition,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<SquireWitnessProof>? Witnesses = null);

public sealed record SquireWitnessProof(
    uint ClassJobId,
    string JobAbbreviation,
    EquipmentSlot Slot,
    IReadOnlyList<EquipmentInstanceFingerprint> Fingerprints);

public sealed record SquireActionPlan(
    Guid SnapshotGenerationId,
    CharacterScope Character,
    SquireDisposition Disposition,
    DateTimeOffset ApprovedAt,
    IReadOnlyList<SquireReviewedSelection> Actions,
    string EvaluatorVersion = "owned-equipment-v1",
    SquireProtectionPolicy? Policy = null);

public sealed class SquireActionPlanner
{
    public SquireActionPlan Create(
        SquireAnalysis analysis,
        SquireDisposition disposition,
        IReadOnlyCollection<EquipmentInstanceFingerprint> selected,
        DateTimeOffset approvedAt,
        SquireProtectionPolicy? policy = null)
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
            var witnessProofs = new List<SquireWitnessProof>();
            foreach (var comparison in candidate.UseAnalysis?.Comparisons ?? [])
            {
                if (comparison.WitnessRequirement is not { } requirement)
                    continue;
                var retained = requirement.ViableWitnesses
                    .Where(witness => !selected.Contains(witness.Fingerprint))
                    .ToArray();
                EquipmentDominanceWitness[] chosen;
                if (requirement.RequiredCount == 2)
                {
                    chosen = FindRingPair(retained, analysis.Snapshot.Definitions)
                        ?? throw new InvalidOperationException($"Selecting {candidate.Definition.Name} would remove the retained ring evidence required for {comparison.Job.Abbreviation}.");
                }
                else
                {
                    chosen = retained.Take(1).ToArray();
                    if (chosen.Length == 0)
                        throw new InvalidOperationException($"Selecting {candidate.Definition.Name} would remove its final retained witness for {comparison.Job.Abbreviation}.");
                }
                witnessProofs.Add(new(comparison.Job.ClassJobId, comparison.Job.Abbreviation, candidate.Definition.Slot,
                    chosen.Select(witness => witness.Fingerprint).ToArray()));
            }
            return new SquireReviewedSelection(fingerprint, disposition, candidate.Reasons.Select(reason => reason.Code).ToArray(), witnessProofs);
        }).ToArray();

        if (actions.Length == 0)
            throw new InvalidOperationException("At least one reviewed item is required.");
        return new SquireActionPlan(analysis.Snapshot.GenerationId, scope, disposition, approvedAt, actions,
            Policy: policy);
    }

    private static EquipmentDominanceWitness[]? FindRingPair(
        IReadOnlyList<EquipmentDominanceWitness> retained,
        IReadOnlyDictionary<uint, EquipmentItemDefinition> definitions)
    {
        for (var left = 0; left < retained.Count; left++)
            for (var right = left + 1; right < retained.Count; right++)
                if (retained[left].ItemId != retained[right].ItemId || !definitions[retained[left].ItemId].IsUnique)
                    return [retained[left], retained[right]];
        return null;
    }
}
