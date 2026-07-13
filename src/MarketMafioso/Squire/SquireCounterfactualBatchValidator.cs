using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire;

public sealed record SquireBatchValidationResult(
    bool Success,
    string Code,
    string Message,
    IReadOnlyDictionary<EquipmentInstanceFingerprint, EquipmentUseAnalysis> UseAnalyses)
{
    public static SquireBatchValidationResult Fail(string code, string message) => new(false, code, message,
        new Dictionary<EquipmentInstanceFingerprint, EquipmentUseAnalysis>(EquipmentInstanceFingerprintComparer.Instance));
}

public sealed class SquireCounterfactualBatchValidator
{
    private readonly SquireCandidateEvaluator evaluator = new();
    private readonly EquipmentUseAnalyzer useAnalyzer = new();

    public SquireBatchValidationResult Validate(
        CharacterEquipmentSnapshot snapshot,
        IReadOnlyDictionary<EquipmentInstanceFingerprint, SquireDisposition> removals,
        SquireDispositionCapabilities capabilities,
        SquireProtectionPolicy policy)
    {
        if (!snapshot.Diagnostics.IsComplete)
            return SquireBatchValidationResult.Fail("PartialSnapshot", "A complete equipment snapshot is required for counterfactual validation.");
        if (removals.Count == 0)
            return SquireBatchValidationResult.Fail("EmptyBatch", "At least one removal is required.");

        var current = evaluator.Evaluate(snapshot, capabilities, policy).Candidates
            .ToDictionary(candidate => candidate.Instance.Fingerprint, EquipmentInstanceFingerprintComparer.Instance);
        foreach (var removal in removals)
        {
            if (!current.TryGetValue(removal.Key, out var candidate) || !candidate.IsExecutable)
                return SquireBatchValidationResult.Fail("CandidateNoLongerExecutable", $"{candidate?.Definition.Name ?? "A selected item"} is no longer an executable cleanup candidate.");
            if (!candidate.SupportedDispositions.Contains(removal.Value))
                return SquireBatchValidationResult.Fail("DispositionUnavailable", $"{candidate.Definition.Name} no longer supports {removal.Value}.");
        }

        var removed = removals.Keys.ToHashSet(EquipmentInstanceFingerprintComparer.Instance);
        var retained = snapshot.Instances.Where(instance => !removed.Contains(instance.Fingerprint)).ToArray();
        if (!GearsetProtectionIndex.Create(snapshot.Gearsets).DoesNotReduceRequiredMultiplicity(snapshot.Instances, retained))
            return SquireBatchValidationResult.Fail("GearsetMultiplicityLost", "The selected batch would remove an item instance required by a valid saved gearset.");
        var analyses = new Dictionary<EquipmentInstanceFingerprint, EquipmentUseAnalysis>(EquipmentInstanceFingerprintComparer.Instance);
        foreach (var removal in removals)
        {
            var candidate = current[removal.Key];
            var use = useAnalyzer.Analyze(candidate.Instance, candidate.Definition, snapshot.Jobs, snapshot.Gearsets, retained, snapshot.Definitions);
            if (use.IsEvaluationFailure)
                return SquireBatchValidationResult.Fail(use.FailureCode ?? "CounterfactualEvaluationFailure", use.Diagnostic ?? $"Could not validate removal of {candidate.Definition.Name}.");
            var safe = use.Status == EquipmentUseStatus.NoObtainedEligibleJob || use.IsStrictlyObsolete ||
                (!policy.ProtectFutureLevelingGear && use.Comparisons.Count > 0 &&
                 use.Comparisons.All(comparison => comparison.Status is EquipmentUseStatus.Obsolete or EquipmentUseStatus.FutureUse));
            if (!safe)
                return SquireBatchValidationResult.Fail("RetainedLoadoutInsufficient", $"Removing the selected batch leaves no retained loadout that covers {candidate.Definition.Name} without relevant-stat loss.");
            analyses[removal.Key] = use;
        }

        return new(true, "Valid", "The complete selected batch is covered by retained counterfactual loadouts.", analyses);
    }
}
