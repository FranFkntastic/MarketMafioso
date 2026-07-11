using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire;

public sealed class SquireCandidateEvaluator
{
    private readonly EquipmentUseAnalyzer useAnalyzer = new();
    private readonly SquireDispositionEligibilityEvaluator dispositionEligibility = new();

    public SquireAnalysis Evaluate(
        CharacterEquipmentSnapshot snapshot,
        SquireDispositionCapabilities? capabilities = null,
        SquireProtectionPolicy? protectionPolicy = null)
    {
        capabilities ??= new SquireDispositionCapabilities(null);
        protectionPolicy ??= new SquireProtectionPolicy();
        var gearsetProtection = GearsetProtectionIndex.Create(snapshot.Gearsets);
        var candidates = snapshot.Instances
            .Select(instance => EvaluateInstance(snapshot, instance, gearsetProtection, capabilities, protectionPolicy))
            .ToArray();
        return new SquireAnalysis(snapshot, candidates);
    }

    private SquireCandidate EvaluateInstance(
        CharacterEquipmentSnapshot snapshot,
        EquipmentInstanceSnapshot instance,
        GearsetProtectionIndex gearsetProtection,
        SquireDispositionCapabilities capabilities,
        SquireProtectionPolicy protectionPolicy)
    {
        if (!snapshot.Definitions.TryGetValue(instance.Fingerprint.ItemId, out var definition))
            return Unsupported(instance, UnknownDefinition(instance.Fingerprint.ItemId));

        var protections = GetProtections(snapshot, instance, definition, gearsetProtection, protectionPolicy);
        if (protections.Count > 0)
            return Candidate(instance, definition, SquireAssessment.Protected, SquireDisposition.Keep, new HashSet<SquireDisposition>(), protections, null);

        var use = useAnalyzer.Analyze(definition, snapshot.Jobs, snapshot.Gearsets, snapshot.Definitions);
        var isObsoleteUnderPolicy = use.IsStrictlyObsolete ||
            (!protectionPolicy.ProtectFutureLevelingGear &&
             use.Comparisons.Count > 0 &&
             use.Comparisons.All(comparison => comparison.Status is EquipmentUseStatus.Obsolete or EquipmentUseStatus.FutureUse));
        if (!isObsoleteUnderPolicy)
        {
            var assessment = use.Status == EquipmentUseStatus.NoUnlockedEligibleJob
                ? SquireAssessment.NeedsReview
                : SquireAssessment.Protected;
            return Candidate(
                instance,
                definition,
                assessment,
                SquireDisposition.Keep,
                new HashSet<SquireDisposition>(),
                [UseReason(use, definition, snapshot.Jobs)],
                use);
        }

        var eligibility = dispositionEligibility.Evaluate(definition, capabilities);
        var dispositions = eligibility.SupportedDispositions;
        if (dispositions.Count == 0)
        {
            return Candidate(
                instance,
                definition,
                SquireAssessment.Unsupported,
                SquireDisposition.Unsupported,
                dispositions,
                [new("NoSupportedDisposition", "No safe V1 disposition can be proven for this item.", SquireReasonSeverity.Blocking)],
                use);
        }

        var recommended = dispositions.Contains(SquireDisposition.Desynthesize)
            ? SquireDisposition.Desynthesize
            : dispositions.Contains(SquireDisposition.VendorSell)
                ? SquireDisposition.VendorSell
                : SquireDisposition.Discard;
        var reasons = new List<SquireReason>
        {
            use.Comparisons.Any(comparison => comparison.Status == EquipmentUseStatus.FutureUse)
                ? new("FutureLevelingUseNotProtected", "One or more unlocked jobs are below this item's equip level; future leveling gear protection is off.", SquireReasonSeverity.Information)
                : new("StrictlyWorseForAllUnlockedJobs", "Every unlocked eligible job has a strictly better trusted baseline.", SquireReasonSeverity.Information),
        };
        reasons.AddRange(eligibility.Reasons);
        return Candidate(
            instance,
            definition,
            SquireAssessment.Candidate,
            recommended,
            dispositions,
            reasons,
            use);
    }

    private static List<SquireReason> GetProtections(
        CharacterEquipmentSnapshot snapshot,
        EquipmentInstanceSnapshot instance,
        EquipmentItemDefinition definition,
        GearsetProtectionIndex gearsetProtection,
        SquireProtectionPolicy protectionPolicy)
    {
        var reasons = new List<SquireReason>();
        if (!snapshot.Diagnostics.IsComplete)
            reasons.Add(new("PartialSnapshot", "The equipment snapshot is incomplete.", SquireReasonSeverity.Blocking));
        if (instance.IsEquipped)
            reasons.Add(new("CurrentlyEquipped", "This exact item is currently equipped.", SquireReasonSeverity.Blocking));
        if (gearsetProtection.IsProtected(definition.ItemId))
            reasons.Add(new("ReferencedByGearset", "An existing valid gearset references this item ID.", SquireReasonSeverity.Blocking));
        if (!definition.IsEquipment)
            reasons.Add(new("NotEquipment", "The item is not equipment.", SquireReasonSeverity.Blocking));
        if (definition.IsSoulCrystal || definition.Slot == EquipmentSlot.SoulCrystal)
            reasons.Add(new("SoulCrystal", "Soul crystals are always protected.", SquireReasonSeverity.Blocking));
        if (definition.IsExplicitlyProtectedFamily)
            reasons.Add(new("ProtectedItemFamily", "The item belongs to an explicitly protected family.", SquireReasonSeverity.Blocking));
        if (instance.Fingerprint.MateriaIds.Count > 0)
            reasons.Add(new("MateriaAttached", "Materia-bearing gear is protected by default.", SquireReasonSeverity.Blocking));
        if (protectionPolicy.ProtectSignedGear && instance.Fingerprint.CrafterContentId is > 0)
            reasons.Add(new("PlayerSignature", "Player-signed gear is protected by default.", SquireReasonSeverity.Blocking));
        if (definition.IsArmoireEligible is null)
            reasons.Add(new("ArmoireEligibilityUnknown", "Armoire eligibility is unknown.", SquireReasonSeverity.Blocking));
        else if (definition.IsArmoireEligible == true)
            reasons.Add(new("ArmoireEligible", "Armoire-eligible gear is protected by default.", SquireReasonSeverity.Blocking));
        if (definition.IsRecoverable is null)
            reasons.Add(new("RecoverabilityUnknown", "Recoverability is unknown.", SquireReasonSeverity.Blocking));
        return reasons;
    }

    private static SquireReason UseReason(
        EquipmentUseAnalysis analysis,
        EquipmentItemDefinition definition,
        IReadOnlyList<CharacterJobSnapshot> jobs) => analysis.Status switch
    {
        EquipmentUseStatus.FutureUse => new("FutureUnlockedJobUse", "A lower-level unlocked job could grow into this item.", SquireReasonSeverity.Blocking),
        EquipmentUseStatus.MissingBaseline => new(
            "MissingTrustedBaseline",
            $"Squire cannot prove this item obsolete because it found no saved gearset containing usable gear for this slot for: {FormatAffectedJobs(analysis, EquipmentUseStatus.MissingBaseline)}.",
            SquireReasonSeverity.Blocking),
        EquipmentUseStatus.BaselineNotBetter => new("BaselineNotStrictlyBetter", "An unlocked eligible job's best baseline is tied with or worse than this item.", SquireReasonSeverity.Blocking),
        EquipmentUseStatus.NoUnlockedEligibleJob => new(
            "NoUnlockedEligibleJob",
            $"Squire found no unlocked job that can equip this item. Eligible job observations: {FormatEligibleJobObservations(definition, jobs)}.",
            SquireReasonSeverity.Warning),
        EquipmentUseStatus.UnknownJobUnlockState => new("JobUnlockStateUnknown", "An eligible job's unlock state is unknown.", SquireReasonSeverity.Blocking),
        _ => new("EquipmentUseUnknown", "Equipment use could not be classified safely.", SquireReasonSeverity.Blocking),
    };

    private static string FormatEligibleJobObservations(
        EquipmentItemDefinition definition,
        IReadOnlyList<CharacterJobSnapshot> jobs)
    {
        var observations = jobs
            .Where(job => definition.EligibleClassJobIds.Contains(job.ClassJobId))
            .Select(job => $"{job.Abbreviation} (level {job.Level}, {FormatUnlockState(job.IsUnlocked)})")
            .ToArray();
        return observations.Length == 0 ? "none were present in the job snapshot" : string.Join(", ", observations);
    }

    private static string FormatUnlockState(bool? unlocked) => unlocked switch
    {
        true => "unlocked",
        false => "marked locked",
        null => "unlock state unknown",
    };

    private static string FormatAffectedJobs(EquipmentUseAnalysis analysis, EquipmentUseStatus status)
    {
        var jobs = analysis.Comparisons
            .Where(comparison => comparison.Status == status)
            .Select(comparison => comparison.Job.Abbreviation)
            .Where(abbreviation => !string.IsNullOrWhiteSpace(abbreviation))
            .Distinct()
            .ToArray();
        return jobs.Length == 0 ? "an unlocked job that can equip the item" : string.Join(", ", jobs);
    }

    private static SquireCandidate Unsupported(EquipmentInstanceSnapshot instance, SquireReason reason) =>
        Candidate(
            instance,
            new EquipmentItemDefinition(instance.Fingerprint.ItemId, $"Item {instance.Fingerprint.ItemId}", 0, 0, EquipmentSlot.Unknown, new HashSet<uint>(), 0, false, false, null, null, null, null, null, null, false),
            SquireAssessment.Unsupported,
            SquireDisposition.Unsupported,
            new HashSet<SquireDisposition>(),
            [reason],
            null);

    private static SquireReason UnknownDefinition(uint itemId) =>
        new("ItemDefinitionMissing", $"Item definition {itemId} is unavailable.", SquireReasonSeverity.Blocking);

    private static SquireCandidate Candidate(
        EquipmentInstanceSnapshot instance,
        EquipmentItemDefinition definition,
        SquireAssessment assessment,
        SquireDisposition recommendation,
        IReadOnlySet<SquireDisposition> supported,
        IReadOnlyList<SquireReason> reasons,
        EquipmentUseAnalysis? use) =>
        new(instance, definition, assessment, recommendation, supported, reasons, use);
}
