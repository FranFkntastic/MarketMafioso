using Franthropy.Dalamud.Characters;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire;

namespace MarketMafioso.Tests.Squire;

public sealed class SquireRunnerTests
{
    private static readonly CharacterScope Scope = new(1, "Runner", 21);

    [Fact]
    public async Task ExactSlotMismatch_StopsWithoutExecutingOrSearching()
    {
        var adapter = new FakeAdapter { Validation = SquireRevalidationResult.Fail("ExactSlotMismatch", "Moved") };
        var result = await new SquireRunner(adapter).RunAsync(Plan(), true, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("ExactSlotMismatch", result.Code);
        Assert.Equal(0, adapter.ExecuteCount);
        Assert.True(adapter.Released);
    }

    [Fact]
    public async Task MissingConfirmation_NeverTouchesGameAdapter()
    {
        var adapter = new FakeAdapter();
        var result = await new SquireRunner(adapter).RunAsync(Plan(SquireDisposition.Discard), false, CancellationToken.None);
        Assert.Equal("ConfirmationRequired", result.Code);
        Assert.Equal(0, adapter.RevalidateCount);
        Assert.Equal(0, adapter.ExecuteCount);
    }

    [Fact]
    public async Task CharacterChange_StopsPlan()
    {
        var adapter = new FakeAdapter { ActiveScope = new CharacterScope(2, "Other", 21) };
        var result = await new SquireRunner(adapter).RunAsync(Plan(), true, CancellationToken.None);
        Assert.Equal("CharacterScopeChanged", result.Code);
        Assert.Equal(0, adapter.ExecuteCount);
    }

    [Fact]
    public async Task Cancellation_ReleasesOwnedState()
    {
        var adapter = new FakeAdapter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await new SquireRunner(adapter).RunAsync(Plan(), true, cancellation.Token);
        Assert.Equal("Cancelled", result.Code);
        Assert.True(adapter.Released);
    }

    [Fact]
    public async Task MixedDispositionPlan_RoutesEachActionByItsOwnDisposition()
    {
        var first = new EquipmentInstanceFingerprint(Scope, "Inventory1", 2, 100, false, 1, 30000, 0, null, [], null, []);
        var second = new EquipmentInstanceFingerprint(Scope, "Inventory1", 3, 200, false, 1, 30000, 0, null, [], null, []);
        var plan = new SquireActionPlan(Guid.NewGuid(), Scope, SquireDisposition.Unsupported, DateTimeOffset.UtcNow,
        [
            new(first, SquireDisposition.ExpertDelivery, ["StrictlyWorseForAllUnlockedJobs"]),
            new(second, SquireDisposition.Desynthesize, ["StrictlyWorseForAllUnlockedJobs"]),
        ]);
        var adapter = new FakeAdapter();

        var result = await new SquireRunner(adapter).RunAsync(plan, true, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal([SquireDisposition.Desynthesize, SquireDisposition.ExpertDelivery], adapter.ExecutedDispositions);
    }

    [Fact]
    public async Task DiagnosticRun_ProbesEveryActionWithoutExecuting()
    {
        var adapter = new FakeAdapter();
        var result = await new SquireRunner(adapter).RunDiagnosticAsync(Plan(SquireDisposition.Desynthesize), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("DiagnosticCompleted", result.Code);
        Assert.Equal(1, adapter.ProbeCount);
        Assert.Equal(0, adapter.ExecuteCount);
        Assert.Contains(result.Events, value => value.Kind == "DiagnosticProbe");
    }

    [Fact]
    public void DispositionBatching_GroupsActionsInExecutionOrderAndPreservesOrderWithinGroup()
    {
        var actions = new[]
        {
            Selection(1, SquireDisposition.ExpertDelivery),
            Selection(2, SquireDisposition.Desynthesize),
            Selection(3, SquireDisposition.ExpertDelivery),
            Selection(4, SquireDisposition.Discard),
            Selection(5, SquireDisposition.VendorSell),
            Selection(6, SquireDisposition.Desynthesize),
        };

        var ordered = SquireDispositionBatching.Order(actions);

        Assert.Equal([2, 6, 1, 3, 5, 4], ordered.Select(value => value.Fingerprint.SlotIndex));
    }

    private static SquireReviewedSelection Selection(int slot, SquireDisposition disposition) =>
        new(new EquipmentInstanceFingerprint(Scope, "Inventory1", slot, (uint)(100 + slot), false, 1, 30000, 0, null, [], null, []), disposition, []);

    private static SquireActionPlan Plan(SquireDisposition disposition = SquireDisposition.VendorSell)
    {
        var fingerprint = new EquipmentInstanceFingerprint(Scope, "Inventory1", 2, 100, false, 1, 30000, 0, null, [], null, []);
        return new SquireActionPlan(Guid.NewGuid(), Scope, disposition, DateTimeOffset.UtcNow,
            [new SquireReviewedSelection(fingerprint, disposition, ["StrictlyWorseForAllUnlockedJobs"])]);
    }

    private sealed class FakeAdapter : ISquireActionGameAdapter
    {
        public CharacterScope? ActiveScope { get; set; } = Scope;
        public SquireRevalidationResult Validation { get; set; } = SquireRevalidationResult.Valid();
        public int RevalidateCount { get; private set; }
        public int ExecuteCount { get; private set; }
        public int ProbeCount { get; private set; }
        public bool Released { get; private set; }
        public List<SquireDisposition> ExecutedDispositions { get; } = [];
        public CharacterScope? GetActiveCharacter() => ActiveScope;
        public bool HasConflictingAutomation(SquireDisposition disposition) => false;
        public SquireRevalidationResult Revalidate(EquipmentInstanceFingerprint fingerprint, SquireDisposition disposition)
        {
            RevalidateCount++;
            return Validation;
        }
        public SquireRevalidationResult RevalidateEvidence(SquireReviewedSelection selection) => Validation;
        public Task<SquireActionResult> ExecuteAsync(EquipmentInstanceFingerprint fingerprint, SquireDisposition disposition, CancellationToken cancellationToken)
        {
            ExecuteCount++;
            ExecutedDispositions.Add(disposition);
            return Task.FromResult(SquireActionResult.Completed());
        }
        public Task<SquireActionResult> ProbeAsync(EquipmentInstanceFingerprint fingerprint, SquireDisposition disposition, CancellationToken cancellationToken)
        {
            ProbeCount++;
            return Task.FromResult(new SquireActionResult(true, "DiagnosticProbePassed", "Passed."));
        }
        public void ReleaseOwnedState() => Released = true;
    }
}
