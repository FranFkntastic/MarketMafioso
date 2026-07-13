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

        var protections = GetProtections(snapshot, instance, definition, gearsetProtection, capabilities, protectionPolicy);
        if (protections.Any(reason => reason.Severity == SquireReasonSeverity.Blocking))
            return Candidate(instance, definition, SquireAssessment.Protected, SquireDisposition.Keep, new HashSet<SquireDisposition>(), protections, null);

        var use = useAnalyzer.Analyze(instance, definition, snapshot.Jobs, snapshot.Gearsets, snapshot.Instances, snapshot.Definitions);
        var isObsoleteUnderPolicy = use.IsStrictlyObsolete ||
            (!protectionPolicy.ProtectFutureLevelingGear &&
             use.Comparisons.Count > 0 &&
             use.Comparisons.All(comparison => comparison.Status is EquipmentUseStatus.Obsolete or EquipmentUseStatus.FutureUse));
        if (use.Status == EquipmentUseStatus.EvaluationFailure)
        {
            return Candidate(
                instance,
                definition,
                SquireAssessment.EvaluationFailure,
                SquireDisposition.Keep,
                new HashSet<SquireDisposition>(),
                [UseReason(use)],
                use);
        }
        if (use.Status == EquipmentUseStatus.LikelyCosmetic)
            return Candidate(instance, definition, SquireAssessment.Protected, SquireDisposition.Keep, new HashSet<SquireDisposition>(),
                [new("StatlessAllClassesEquipment", "All Classes equipment has no wearer-defining stats and is protected as likely cosmetic or appearance gear.", SquireReasonSeverity.Blocking)], use);
        if (use.Status == EquipmentUseStatus.SpecialPurpose)
            return Candidate(instance, definition, SquireAssessment.Protected, SquireDisposition.Keep, new HashSet<SquireDisposition>(),
                [new("SpecialPurposeEquipment", use.Diagnostic ?? "Special-purpose equipment is protected independently of stat dominance.", SquireReasonSeverity.Blocking)], use);

        var noObtainedConsumer = use.Status == EquipmentUseStatus.NoObtainedEligibleJob;
        if (!isObsoleteUnderPolicy && !noObtainedConsumer)
            return Candidate(instance, definition, SquireAssessment.Protected, SquireDisposition.Keep, new HashSet<SquireDisposition>(),
                [UseReason(use)], use);

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

        var recommended = dispositions.Contains(SquireDisposition.ExpertDelivery)
            ? SquireDisposition.ExpertDelivery
            : dispositions.Contains(SquireDisposition.Desynthesize)
            ? SquireDisposition.Desynthesize
            : dispositions.Contains(SquireDisposition.VendorSell)
                ? SquireDisposition.VendorSell
                : SquireDisposition.Discard;
        var reasons = new List<SquireReason>
        {
            noObtainedConsumer
                ? new("NoObtainedEligibleJob", "No job obtained by this character can use this item.", SquireReasonSeverity.Information)
                : use.Comparisons.Any(comparison => comparison.Status == EquipmentUseStatus.FutureUse)
                ? new("FutureLevelingUseNotProtected", "One or more unlocked jobs are below this item's equip level; future leveling gear protection is off.", SquireReasonSeverity.Information)
                : new("RetainedCoverageForAllUnlockedJobs", DescribeTrustedBaselines(use), SquireReasonSeverity.Information),
        };
        if (!protectionPolicy.ProtectBlueAndPurpleGear &&
            definition.NormalizedRarity is EquipmentRarity.Rare or EquipmentRarity.Relic)
            reasons.Add(new("HighRarityProtectionDisabled", "Blue and purple gear protection is disabled; all other safety rules still apply.", SquireReasonSeverity.Warning));
        reasons.AddRange(protections.Where(reason => reason.Severity != SquireReasonSeverity.Blocking));
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

    private static string DescribeTrustedBaselines(EquipmentUseAnalysis use)
    {
        var comparisons = use.Comparisons
            .Where(comparison => comparison.Status == EquipmentUseStatus.Obsolete && comparison.Baseline is not null)
            .Select(comparison =>
            {
                var baseline = comparison.Baseline!;
                var witness = comparison.WitnessRequirement?.ViableWitnesses
                    .FirstOrDefault(value => value.ItemId == baseline.ItemId);
                var location = witness is null
                    ? "saved gearset"
                    : $"{witness.Fingerprint.Container} slot {witness.Fingerprint.SlotIndex}, {(witness.Fingerprint.IsHighQuality ? "HQ" : "NQ")}";
                return $"{comparison.Job.Abbreviation}: {baseline.Name} (iLvl {baseline.ItemLevel}, {location})";
            })
            .ToArray();
        return comparisons.Length == 0
            ? "Every unlocked eligible job has a trusted baseline that covers this item without relevant-stat loss."
            : $"Trusted retained coverage: {string.Join("; ", comparisons)}.";
    }

    private static List<SquireReason> GetProtections(
        CharacterEquipmentSnapshot snapshot,
        EquipmentInstanceSnapshot instance,
        EquipmentItemDefinition definition,
        GearsetProtectionIndex gearsetProtection,
        SquireDispositionCapabilities capabilities,
        SquireProtectionPolicy protectionPolicy)
    {
        var reasons = new List<SquireReason>();
        if (protectionPolicy.CleanupExcludedItemIds?.Contains(definition.ItemId) == true)
            reasons.Add(new("CleanupExcluded", "This item is on this character's cleanup exclusion list.", SquireReasonSeverity.Blocking));
        if (!snapshot.Diagnostics.IsComplete)
            reasons.Add(new("PartialSnapshot", "The equipment snapshot is incomplete.", SquireReasonSeverity.Blocking));
        if (instance.IsEquipped)
            reasons.Add(new("CurrentlyEquipped", "This exact item is currently equipped.", SquireReasonSeverity.Blocking));
        var exactQualityCount = snapshot.Instances.Count(value =>
            value.Fingerprint.ItemId == definition.ItemId &&
            value.Fingerprint.IsHighQuality == instance.Fingerprint.IsHighQuality);
        if (gearsetProtection.IsProtected(definition.ItemId, instance.Fingerprint.IsHighQuality, exactQualityCount))
            reasons.Add(new("ReferencedByGearset", "An existing valid gearset references this item ID.", SquireReasonSeverity.Blocking));
        if (!definition.IsEquipment)
            reasons.Add(new("NotEquipment", "The item is not equipment.", SquireReasonSeverity.Blocking));
        if (definition.IsSoulCrystal || definition.Slot == EquipmentSlot.SoulCrystal)
            reasons.Add(new("SoulCrystal", "Soul crystals are always protected.", SquireReasonSeverity.Blocking));
        if (definition.IsExplicitlyProtectedFamily)
            reasons.Add(new("ProtectedItemFamily", "The item belongs to an explicitly protected family.", SquireReasonSeverity.Blocking));
        if (definition.NormalizedRarity == EquipmentRarity.Unknown)
            reasons.Add(new("UnknownItemRarity", $"Item rarity value {definition.Rarity} is not mapped.", SquireReasonSeverity.Blocking));
        else if (protectionPolicy.ProtectBlueAndPurpleGear && definition.NormalizedRarity is EquipmentRarity.Rare or EquipmentRarity.Relic)
            reasons.Add(new("HighRarityEquipment", $"{definition.NormalizedRarity} equipment is protected by the blue and purple gear setting.", SquireReasonSeverity.Blocking));
        else if (definition.NormalizedRarity == EquipmentRarity.Uncommon &&
                 definition.ExpertDeliveryEligibility == ExpertDeliveryEligibility.Unknown)
            reasons.Add(new("ExpertDeliveryEligibilityUnknown", "Expert Delivery eligibility is unknown for this uncommon item.", SquireReasonSeverity.Blocking));
        if (definition.IsEquipment && instance.Fingerprint.MateriaIds.Count > 0)
        {
            if (capabilities.MateriaRetrievalUnlocked != true)
                reasons.Add(new(
                    capabilities.MateriaRetrievalUnlocked == false ? "MateriaRetrievalNotUnlocked" : "MateriaRetrievalUnlockUnknown",
                    capabilities.MateriaRetrievalUnlocked == false
                        ? "Attached materia cannot be handled until Forging the Spirit is complete."
                        : "The Forging the Spirit completion required for materia retrieval could not be proven.",
                    SquireReasonSeverity.Blocking));
            else if (!protectionPolicy.AllowRiskyMateriaRetrieval)
                reasons.Add(new("MateriaRetrievalRiskNotAuthorized", "Retrieving materia can fail and destroy the materia; explicit risk authorization is required before this item can be cleaned up.", SquireReasonSeverity.Blocking));
            else
                reasons.Add(new("MateriaRetrievalRequired", $"Squire will attempt to retrieve {instance.Fingerprint.MateriaIds.Count} attached materia before cleanup; failed retrieval can destroy materia.", SquireReasonSeverity.Warning));
        }
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

    private static SquireReason UseReason(EquipmentUseAnalysis analysis) => analysis.Status switch
    {
        EquipmentUseStatus.FutureUse => new("FutureUnlockedJobUse", "A lower-level unlocked job could grow into this item.", SquireReasonSeverity.Blocking),
        EquipmentUseStatus.BaselineNotBetter => new("NoRetainedCoverage", "No retained owned or saved baseline safely supersedes this item for every relevant obtained job.", SquireReasonSeverity.Blocking),
        EquipmentUseStatus.NoObtainedEligibleJob => new("NoObtainedEligibleJob", "No job obtained by this character can use this item.", SquireReasonSeverity.Information),
        EquipmentUseStatus.LikelyCosmetic => new("StatlessAllClassesEquipment", "All Classes equipment has no wearer-defining stats and is likely cosmetic or appearance gear.", SquireReasonSeverity.Blocking),
        EquipmentUseStatus.SpecialPurpose => new("SpecialPurposeEquipment", analysis.Diagnostic ?? "Special-purpose equipment is protected.", SquireReasonSeverity.Blocking),
        EquipmentUseStatus.EvaluationFailure => new(analysis.FailureCode ?? "EquipmentEvaluationFailure", analysis.Diagnostic ?? "Equipment use evaluation failed.", SquireReasonSeverity.Blocking),
        _ => new("EquipmentUseUnknown", "Equipment use could not be classified safely.", SquireReasonSeverity.Blocking),
    };

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
