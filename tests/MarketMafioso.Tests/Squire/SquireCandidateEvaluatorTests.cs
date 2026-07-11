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
            instances: [Instance(100)],
            definitions: [Definition(100, 20), Definition(200, 30)],
            jobs: [Job(1, 50, true)],
            gearsets: [Gearset(1, 200)]);
        var candidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked).Candidates);
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
            [Instance(100, crafter: 99)],
            [Definition(100, 20), Definition(200, 30)],
            [Job(1, 50, true)],
            [Gearset(1, 200)]);

        var candidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked).Candidates);

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
        var first = evaluator.Evaluate(Snapshot([Instance(100, slot: 1)], [Definition(100, 20), Definition(200, 30)], [Job(1, 50, true)], [Gearset(1, 200)]), DesynthesisUnlocked);
        var second = evaluator.Evaluate(Snapshot([Instance(100, slot: 2)], [Definition(100, 20), Definition(200, 30)], [Job(1, 50, true)], [Gearset(1, 200)]), DesynthesisUnlocked);
        var review = new SquireReviewState();
        review.Adopt(first);
        Assert.True(review.TrySelect(first, first.Candidates[0].Instance.Fingerprint, SquireDisposition.Desynthesize));
        review.Adopt(second);
        Assert.Empty(review.Selections);
        Assert.False(review.TrySelect(second, first.Candidates[0].Instance.Fingerprint, SquireDisposition.Desynthesize));
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
            [Instance(100)],
            [Definition(100, 20), Definition(200, 30)],
            [Job(1, 50, true)],
            [Gearset(1, 200)]);

        var candidate = Assert.Single(evaluator.Evaluate(snapshot, new SquireDispositionCapabilities(false)).Candidates);

        Assert.Equal(SquireDisposition.VendorSell, candidate.RecommendedDisposition);
        Assert.DoesNotContain(SquireDisposition.Desynthesize, candidate.SupportedDispositions);
        Assert.Contains(candidate.Reasons, reason => reason.Code == "DesynthesisNotUnlocked");
        var displayedReasons = SquireTabPanel.FormatReasons(candidate);
        Assert.Equal(candidate.Reasons.Count, displayedReasons.Split('\n').Length);
        Assert.All(candidate.Reasons, reason => Assert.Contains(reason.Message, displayedReasons));
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
        var reason = Assert.Single(candidate.Reasons, reason => reason.Code == "MissingTrustedBaseline");

        Assert.Contains("cannot prove this item obsolete", reason.Message);
        Assert.Contains("JOB", reason.Message);
    }

    [Fact]
    public void NoUnlockedEligibleJobReason_NamesObservedEligibleJobsAndStates()
    {
        var snapshot = Snapshot(
            [Instance(100)],
            [Definition(100, 20)],
            [new CharacterJobSnapshot(1, "GSM", "Goldsmith", 49, false, 1, "Crafter")],
            []);

        var candidate = Assert.Single(evaluator.Evaluate(snapshot, DesynthesisUnlocked).Candidates);
        var reason = Assert.Single(candidate.Reasons, reason => reason.Code == "NoUnlockedEligibleJob");

        Assert.Contains("GSM", reason.Message);
        Assert.Contains("level 49", reason.Message);
        Assert.Contains("marked locked", reason.Message);
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
        new(itemId, $"Item {itemId}", 1, itemLevel, EquipmentSlot.Body, new HashSet<uint> { 1 }, 1, true, false, true, true, 1, true, false, true, false);

    private static CharacterJobSnapshot Job(uint id, uint level, bool? unlocked) =>
        new(id, "JOB", "Job", level, unlocked, null, "Tank");

    private static GearsetSnapshot Gearset(int id, uint itemId) =>
        new(id, "Set", 1, [new GearsetItemReference(EquipmentSlot.Body, itemId)], true);
}
