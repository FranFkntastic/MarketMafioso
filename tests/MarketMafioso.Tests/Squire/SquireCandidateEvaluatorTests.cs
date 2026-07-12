using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire;
using MarketMafioso.Windows.Squire;

namespace MarketMafioso.Tests.Squire;

public sealed class SquireCandidateEvaluatorTests
{
    private static readonly CharacterScope Scope = new(7, "Squire", 21);
    private static readonly SquireDispositionCapabilities DesynthesisUnlocked = new(true);
    private readonly SquireCandidateEvaluator evaluator = new();

    [Fact]
    public void CompleteStrictlyWorseItem_BecomesExecutableCandidate()
    {
        var snapshot = Snapshot(
            instances: [Instance(100), Instance(200, equipped: true, slot: 99)],
            definitions: [Definition(100, 20), Definition(200, 30)],
            jobs: [Job(1, 50, true)],
            gearsets: [Gearset(1, 200)]);
        var candidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked).Candidates, value => value.Definition.ItemId == 100);
        Assert.Equal(SquireAssessment.Candidate, candidate.Assessment);
        Assert.Equal(SquireDisposition.Desynthesize, candidate.RecommendedDisposition);
        Assert.Contains(SquireDisposition.VendorSell, candidate.SupportedDispositions);
    }

    [Fact]
    public void PartialSnapshot_ProducesNoExecutableCandidate()
    {
        var snapshot = Snapshot(
            [Instance(100)], [Definition(100, 20), Definition(200, 30)], [Job(1, 50, true)], [Gearset(1, 200)], complete: false);
        var candidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked).Candidates);
        Assert.Equal(SquireAssessment.Protected, candidate.Assessment);
        Assert.Contains(candidate.Reasons, reason => reason.Code == "PartialSnapshot");
    }

    [Fact]
    public void EquippedGearsetAndMateriaItems_AreProtected()
    {
        var instance = Instance(100, equipped: true, materia: [500], crafter: 99);
        var snapshot = Snapshot([instance], [Definition(100, 20)], [Job(1, 50, true)], [Gearset(1, 100)]);
        var candidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked).Candidates);
        Assert.Equal(SquireAssessment.Protected, candidate.Assessment);
        Assert.Contains(candidate.Reasons, reason => reason.Code == "CurrentlyEquipped");
        Assert.Contains(candidate.Reasons, reason => reason.Code == "ReferencedByGearset");
        Assert.Contains(candidate.Reasons, reason => reason.Code == "MateriaAttached");
        Assert.DoesNotContain(candidate.Reasons, reason => reason.Code == "PlayerSignature");
    }

    [Fact]
    public void SignedGear_IsNotProtectedByDefault()
    {
        var snapshot = Snapshot(
            [Instance(100, crafter: 99), Instance(200, equipped: true, slot: 99)],
            [Definition(100, 20), Definition(200, 30)],
            [Job(1, 50, true)],
            [Gearset(1, 200)]);

        var candidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked).Candidates, value => value.Definition.ItemId == 100);

        Assert.Equal(SquireAssessment.Candidate, candidate.Assessment);
        Assert.DoesNotContain(candidate.Reasons, reason => reason.Code == "PlayerSignature");
    }

    [Fact]
    public void SignedGear_CanBeProtectedByOptInPolicy()
    {
        var snapshot = Snapshot(
            [Instance(100, crafter: 99)],
            [Definition(100, 20), Definition(200, 30)],
            [Job(1, 50, true)],
            [Gearset(1, 200)]);

        var candidate = Assert.Single(evaluator.Evaluate(
            snapshot,
            DesynthesisUnlocked,
            new SquireProtectionPolicy(ProtectSignedGear: true)).Candidates);

        Assert.Equal(SquireAssessment.Protected, candidate.Assessment);
        Assert.Contains(candidate.Reasons, reason => reason.Code == "PlayerSignature");
    }

    [Fact]
    public void ArmoireEligibleItem_IsProtectedByDefault()
    {
        var armoireItem = Definition(100, 20) with { IsArmoireEligible = true };
        var snapshot = Snapshot([Instance(100)], [armoireItem, Definition(200, 30)], [Job(1, 50, true)], [Gearset(1, 200)]);
        var candidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked).Candidates);
        Assert.Equal(SquireAssessment.Protected, candidate.Assessment);
        Assert.Contains(candidate.Reasons, reason => reason.Code == "ArmoireEligible");
    }

    [Fact]
    public void UnsupportedDispositionCannotEnterPlan()
    {
        var snapshot = Snapshot([Instance(100)], [Definition(100, 20), Definition(200, 30)], [Job(1, 50, true)], [Gearset(1, 200)]);
        var analysis = evaluator.Evaluate(snapshot, DesynthesisUnlocked);
        Assert.Throws<InvalidOperationException>(() => new SquireActionPlanner().Create(
            analysis, SquireDisposition.Unsupported, [analysis.Candidates[0].Instance.Fingerprint], DateTimeOffset.UtcNow));
    }

    [Fact]
    public void RefreshInvalidatesPriorSelectionEvenWhenItemIdMatches()
    {
        var first = evaluator.Evaluate(Snapshot([Instance(100, slot: 1), Instance(200, equipped: true, slot: 99)], [Definition(100, 20), Definition(200, 30)], [Job(1, 50, true)], [Gearset(1, 200)]), DesynthesisUnlocked);
        var second = evaluator.Evaluate(Snapshot([Instance(100, slot: 2), Instance(200, equipped: true, slot: 99)], [Definition(100, 20), Definition(200, 30)], [Job(1, 50, true)], [Gearset(1, 200)]), DesynthesisUnlocked);
        var review = new SquireReviewState();
        review.Adopt(first);
        var firstCandidate = first.Candidates.Single(value => value.Definition.ItemId == 100);
        Assert.True(review.TrySelect(first, firstCandidate.Instance.Fingerprint, SquireDisposition.Desynthesize));
        review.Adopt(second);
        Assert.Empty(review.Selections);
        Assert.False(review.TrySelect(second, firstCandidate.Instance.Fingerprint, SquireDisposition.Desynthesize));
    }

    [Fact]
    public void PartialSnapshotCannotProduceActionPlan()
    {
        var analysis = evaluator.Evaluate(Snapshot(
            [Instance(100)], [Definition(100, 20), Definition(200, 30)], [Job(1, 50, true)], [Gearset(1, 200)], complete: false), DesynthesisUnlocked);
        Assert.Throws<InvalidOperationException>(() => new SquireActionPlanner().Create(
            analysis, SquireDisposition.Desynthesize, [analysis.Candidates[0].Instance.Fingerprint], DateTimeOffset.UtcNow));
    }

    [Fact]
    public void GrandCompanyDeliveryIsNotAPlanningDisposition()
    {
        Assert.DoesNotContain(Enum.GetNames<SquireDisposition>(), name => name.Contains("Grand", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Enum.GetNames<SquireDisposition>(), name => name.Contains("Company", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GearsetDuplicateItemId_ProtectsEveryObservedCopy()
    {
        var snapshot = Snapshot(
            [Instance(100, slot: 1), Instance(100, slot: 2)],
            [Definition(100, 20)],
            [Job(1, 50, true)],
            [Gearset(1, 100)]);
        var analysis = evaluator.Evaluate(snapshot, DesynthesisUnlocked);
        Assert.All(analysis.Candidates, candidate => Assert.Equal(SquireAssessment.Protected, candidate.Assessment));
    }

    [Fact]
    public void DesynthesisLocked_FallsBackWithoutOfferingDesynthesis()
    {
        var snapshot = Snapshot(
            [Instance(100), Instance(200, equipped: true, slot: 99)],
            [Definition(100, 20), Definition(200, 30)],
            [Job(1, 50, true)],
            [Gearset(1, 200)]);

        var candidate = Assert.Single(evaluator.Evaluate(snapshot, new SquireDispositionCapabilities(false)).Candidates, value => value.Definition.ItemId == 100);

        Assert.Equal(SquireDisposition.VendorSell, candidate.RecommendedDisposition);
        Assert.DoesNotContain(SquireDisposition.Desynthesize, candidate.SupportedDispositions);
        Assert.Contains(candidate.Reasons, reason => reason.Code == "DesynthesisNotUnlocked");
        var displayedReasons = SquireTabPanel.FormatReasons(candidate);
        Assert.Equal(candidate.Reasons.Count, displayedReasons.Split('\n').Length);
        Assert.All(candidate.Reasons, reason => Assert.Contains(reason.Message, displayedReasons));
        var summary = SquireTabPanel.FormatReasonSummary(candidate);
        Assert.StartsWith(candidate.Reasons[0].Message, summary);
        Assert.Contains($"(+{candidate.Reasons.Count - 1} more)", summary);
    }

    [Fact]
    public void MissingBaselineReason_ExplainsTheDecisionAndNamesTheJob()
    {
        var snapshot = Snapshot(
            [Instance(100)],
            [Definition(100, 20)],
            [Job(1, 50, true)],
            []);

        var candidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked).Candidates);
        var reason = Assert.Single(candidate.Reasons, reason => reason.Code == "JobComparisonFailed");

        Assert.Equal(SquireAssessment.EvaluationFailure, candidate.Assessment);
        Assert.Contains("No saved or owned usable Body witness", reason.Message);
        Assert.Contains("JOB", reason.Message);
    }

    [Fact]
    public void UnobtainedEligibleJob_DoesNotProtectItem()
    {
        var snapshot = Snapshot(
            [Instance(100)],
            [Definition(100, 20)],
            [new CharacterJobSnapshot(1, "GSM", "Goldsmith", 49, false, 1, "Crafter")],
            []);

        var candidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked).Candidates);
        var reason = Assert.Single(candidate.Reasons, reason => reason.Code == "NoObtainedEligibleJob");

        Assert.Equal(SquireAssessment.Candidate, candidate.Assessment);
        Assert.DoesNotContain("locked", reason.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FutureLevelingGear_IsACandidateByDefault()
    {
        var futureItem = Definition(100, 20) with { EquipLevel = 40 };
        var snapshot = Snapshot([Instance(100)], [futureItem], [Job(1, 30, true)], []);

        var candidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked).Candidates);

        Assert.Equal(SquireAssessment.Candidate, candidate.Assessment);
        Assert.Contains(candidate.Reasons, reason => reason.Code == "FutureLevelingUseNotProtected");
    }

    [Theory]
    [InlineData(EquipmentRarity.Rare)]
    [InlineData(EquipmentRarity.Relic)]
    public void HighRarityEquipment_IsProtectedUntilExplicitlyOverridden(EquipmentRarity rarity)
    {
        var item = Definition(100, 20) with
        {
            NormalizedRarity = rarity,
            Rarity = rarity == EquipmentRarity.Rare ? (byte)3 : (byte)4,
            ExpertDeliveryEligibility = ExpertDeliveryEligibility.Eligible,
        };
        var baseline = Definition(200, 30);
        var snapshot = Snapshot([Instance(100), Instance(200, equipped: true, slot: 99)], [item, baseline], [Job(1, 50, true)], [Gearset(1, 200)]);

        var protectedCandidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked).Candidates, value => value.Definition.ItemId == 100);
        Assert.Equal(SquireAssessment.Protected, protectedCandidate.Assessment);
        Assert.Contains(protectedCandidate.Reasons, reason => reason.Code == "HighRarityEquipment");

        var overridden = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked,
            new SquireProtectionPolicy(HighRarityCleanupOverrides: new HashSet<uint> { 100 })).Candidates, value => value.Definition.ItemId == 100);
        Assert.Equal(SquireAssessment.Candidate, overridden.Assessment);
        Assert.Equal(SquireDisposition.ExpertDelivery, overridden.RecommendedDisposition);
        Assert.Contains(overridden.Reasons, reason => reason.Code == "HighRarityCleanupOverride");
    }

    [Fact]
    public void UncommonEquipment_EligibleForExpertDelivery_BecomesDeliveryCandidate()
    {
        var item = Definition(100, 20) with
        {
            NormalizedRarity = EquipmentRarity.Uncommon,
            Rarity = 2,
            ExpertDeliveryEligibility = ExpertDeliveryEligibility.Eligible,
            ExpertDeliveryProvenance = "rarity rule",
        };
        var snapshot = Snapshot([Instance(100)], [item], [Job(1, 50, false)], []);
        var candidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked).Candidates, value => value.Definition.ItemId == 100);
        Assert.Equal(SquireAssessment.Candidate, candidate.Assessment);
        Assert.Equal(SquireDisposition.ExpertDelivery, candidate.RecommendedDisposition);
        Assert.Contains(SquireDisposition.ExpertDelivery, candidate.SupportedDispositions);
    }

    [Fact]
    public void FutureLevelingGear_CanBeProtectedByOptInPolicy()
    {
        var futureItem = Definition(100, 20) with { EquipLevel = 40 };
        var snapshot = Snapshot([Instance(100)], [futureItem], [Job(1, 30, true)], []);

        var candidate = Assert.Single(evaluator.Evaluate(
            snapshot,
            DesynthesisUnlocked,
            new SquireProtectionPolicy(ProtectFutureLevelingGear: true)).Candidates);

        Assert.Equal(SquireAssessment.Protected, candidate.Assessment);
        Assert.Contains(candidate.Reasons, reason => reason.Code == "FutureUnlockedJobUse");
    }

    [Fact]
    public void ActionPlanRetainsExactLooseWitness()
    {
        var snapshot = Snapshot(
            [Instance(100, slot: 1), Instance(200, slot: 2)],
            [Definition(100, 20), Definition(200, 30)],
            [Job(1, 50, true)],
            []);
        var analysis = evaluator.Evaluate(snapshot, DesynthesisUnlocked);
        var target = analysis.Candidates.Single(value => value.Definition.ItemId == 100);

        var plan = new SquireActionPlanner().Create(
            analysis, target.RecommendedDisposition, [target.Instance.Fingerprint], DateTimeOffset.UtcNow);

        var proof = Assert.Single(Assert.Single(plan.Actions).Witnesses!);
        Assert.Equal(200u, Assert.Single(proof.Fingerprints).ItemId);
    }

    [Fact]
    public void ObsoleteReason_NamesTrustedBaselineAndExactOwnedLocation()
    {
        var snapshot = Snapshot(
            [Instance(100, slot: 1), Instance(200, slot: 7)],
            [Definition(100, 20), Definition(200, 30)],
            [Job(1, 50, true)],
            []);

        var candidate = evaluator.Evaluate(snapshot, DesynthesisUnlocked).Candidates.Single(value => value.Definition.ItemId == 100);
        var reason = Assert.Single(candidate.Reasons, value => value.Code == "StrictlyWorseForAllUnlockedJobs");

        Assert.Contains("JOB: Item 200", reason.Message);
        Assert.Contains("iLvl 30", reason.Message);
        Assert.Contains("Inventory1 slot 7, NQ", reason.Message);
    }

    [Fact]
    public void ActionPlanRejectsRemovingFinalLooseWitness()
    {
        var snapshot = Snapshot(
            [Instance(100, slot: 1), Instance(200, slot: 2)],
            [Definition(100, 20), Definition(200, 30)],
            [Job(1, 50, true)],
            []);
        var analysis = evaluator.Evaluate(snapshot, DesynthesisUnlocked);
        var target = analysis.Candidates.Single(value => value.Definition.ItemId == 100);
        var witness = analysis.Candidates.Single(value => value.Definition.ItemId == 200);

        Assert.Throws<InvalidOperationException>(() => new SquireActionPlanner().Create(
            analysis, target.RecommendedDisposition, [target.Instance.Fingerprint, witness.Instance.Fingerprint], DateTimeOffset.UtcNow));
    }

    private static CharacterEquipmentSnapshot Snapshot(
        IReadOnlyList<EquipmentInstanceSnapshot> instances,
        IReadOnlyList<EquipmentItemDefinition> definitions,
        IReadOnlyList<CharacterJobSnapshot> jobs,
        IReadOnlyList<GearsetSnapshot> gearsets,
        bool complete = true) =>
        new(
            Guid.NewGuid(),
            new CharacterIdentitySnapshot(Scope, 21, 1, DateTimeOffset.UtcNow, true, SnapshotComponentStatus.Complete),
            jobs,
            gearsets,
            instances,
            definitions.ToDictionary(definition => definition.ItemId),
            new CharacterEquipmentSnapshotDiagnostics(
            [
                new("identity", SnapshotComponentStatus.Complete),
                new("jobs", complete ? SnapshotComponentStatus.Complete : SnapshotComponentStatus.Partial),
                new("gearsets", SnapshotComponentStatus.Complete),
                new("equipped", SnapshotComponentStatus.Complete),
                new("armoury", SnapshotComponentStatus.Complete),
                new("inventory", SnapshotComponentStatus.Complete),
                new("definitions", SnapshotComponentStatus.Complete),
            ]));

    private static EquipmentInstanceSnapshot Instance(
        uint itemId,
        bool equipped = false,
        IReadOnlyList<uint>? materia = null,
        ulong? crafter = null,
        int slot = 1) =>
        new(new EquipmentInstanceFingerprint(Scope, "Inventory1", slot, itemId, false, 1, 30000, 0, crafter, materia ?? [], null, []), DateTimeOffset.UtcNow, equipped);

    private static EquipmentItemDefinition Definition(uint itemId, uint itemLevel) =>
        new(itemId, $"Item {itemId}", 1, itemLevel, EquipmentSlot.Body, new HashSet<uint> { 1 }, 1, true, false, true, true, 1, true, false, true, false,
            new EquipmentStatProfile([new(1, EquipmentStatSemantic.Strength, checked((int)itemLevel), false)], 0, 0, checked((int)itemLevel), checked((int)itemLevel), true),
            EquipmentRarity.Normal);

    private static CharacterJobSnapshot Job(uint id, uint level, bool? unlocked) =>
        new(id, "JOB", "Job", level, unlocked, null, "Tank", EquipmentStatSemantic.Strength, EquipmentDiscipline.Combat);

    private static GearsetSnapshot Gearset(int id, uint itemId) =>
        new(id, "Set", 1, [new GearsetItemReference(EquipmentSlot.Body, itemId)], true);
}
