using System.Collections.Generic;
using System.Linq;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Squire;

public sealed class SquireCandidateEvaluator
{
    private readonly EquipmentUseAnalyzer useAnalyzer = new();

    public SquireAnalysis Evaluate(CharacterEquipmentSnapshot snapshot)
    {
        var gearsetProtection = GearsetProtectionIndex.Create(snapshot.Gearsets);
        var candidates = snapshot.Instances
            .Select(instance => EvaluateInstance(snapshot, instance, gearsetProtection))
            .ToArray();
        return new SquireAnalysis(snapshot, candidates);
    }

    private SquireCandidate EvaluateInstance(
        CharacterEquipmentSnapshot snapshot,
        EquipmentInstanceSnapshot instance,
        GearsetProtectionIndex gearsetProtection)
    {
        if (!snapshot.Definitions.TryGetValue(instance.Fingerprint.ItemId, out var definition))
            return Unsupported(instance, UnknownDefinition(instance.Fingerprint.ItemId));

        var protections = GetProtections(snapshot, instance, definition, gearsetProtection);
        if (protections.Count > 0)
            return Candidate(instance, definition, SquireAssessment.Protected, SquireDisposition.Keep, new HashSet<SquireDisposition>(), protections, null);

        var use = useAnalyzer.Analyze(definition, snapshot.Jobs, snapshot.Gearsets, snapshot.Definitions);
        if (!use.IsStrictlyObsolete)
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
                [UseReason(use.Status)],
                use);
        }

        var dispositions = GetSupportedDispositions(definition);
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
        return Candidate(
            instance,
            definition,
            SquireAssessment.Candidate,
            recommended,
            dispositions,
            [new("StrictlyWorseForAllUnlockedJobs", "Every unlocked eligible job has a strictly better trusted baseline.", SquireReasonSeverity.Information)],
            use);
    }

    private static List<SquireReason> GetProtections(
        CharacterEquipmentSnapshot snapshot,
        EquipmentInstanceSnapshot instance,
        EquipmentItemDefinition definition,
        GearsetProtectionIndex gearsetProtection)
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
        if (instance.Fingerprint.CrafterContentId is > 0)
            reasons.Add(new("PlayerSignature", "Player-signed gear is protected by default.", SquireReasonSeverity.Blocking));
        if (definition.IsArmoireEligible is null)
            reasons.Add(new("ArmoireEligibilityUnknown", "Armoire eligibility is unknown.", SquireReasonSeverity.Blocking));
        else if (definition.IsArmoireEligible == true)
            reasons.Add(new("ArmoireEligible", "Armoire-eligible gear is protected by default.", SquireReasonSeverity.Blocking));
        if (definition.IsRecoverable is null)
            reasons.Add(new("RecoverabilityUnknown", "Recoverability is unknown.", SquireReasonSeverity.Blocking));
        return reasons;
    }

    private static IReadOnlySet<SquireDisposition> GetSupportedDispositions(EquipmentItemDefinition definition)
    {
        var values = new HashSet<SquireDisposition>();
        if (definition.IsDesynthesizable == true)
            values.Add(SquireDisposition.Desynthesize);
        if (definition.IsVendorSellable == true && definition.VendorSellPrice is > 0)
            values.Add(SquireDisposition.VendorSell);
        if (definition.IsDiscardable == true)
            values.Add(SquireDisposition.Discard);
        return values;
    }

    private static SquireReason UseReason(EquipmentUseStatus status) => status switch
    {
        EquipmentUseStatus.FutureUse => new("FutureUnlockedJobUse", "A lower-level unlocked job could grow into this item.", SquireReasonSeverity.Blocking),
        EquipmentUseStatus.MissingBaseline => new("MissingTrustedBaseline", "An unlocked eligible job has no trusted same-slot gearset baseline.", SquireReasonSeverity.Blocking),
        EquipmentUseStatus.BaselineNotBetter => new("BaselineNotStrictlyBetter", "An unlocked eligible job's best baseline is tied with or worse than this item.", SquireReasonSeverity.Blocking),
        EquipmentUseStatus.NoUnlockedEligibleJob => new("NoUnlockedEligibleJob", "No unlocked eligible job can be used to prove obsolescence.", SquireReasonSeverity.Warning),
        EquipmentUseStatus.UnknownJobUnlockState => new("JobUnlockStateUnknown", "An eligible job's unlock state is unknown.", SquireReasonSeverity.Blocking),
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
