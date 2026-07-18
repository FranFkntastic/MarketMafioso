using Franthropy.Dalamud.Equipment;
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Squire.Outfitter.Acquisition;
using MarketMafioso.Tests.Squire;
using MarketMafioso.Windows.MarketAcquisitionPanels;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteEngineOutfitterLifecycleTests
{
    [Fact]
    public void ChangedVisibleCoverage_StopsOriginalRunnerAndStartsRecoveredPlanWithoutGenericResume()
    {
        var fixture = Fixture();
        var store = new MemoryStore();
        using var harness = MarketAcquisitionRouteEngineHarness.Create(store);
        harness.Context.CurrentWorld = "Siren";
        Assert.True(harness.Engine.Start(
            fixture.Plan,
            fixture.Claim,
            false,
            false,
            fixture.Contract,
            fixture.Document).Success);
        harness.Runner.RecordCurrentWorld("Siren");
        harness.Runner.RecordProbe("Siren", CandidatePlan());
        var rejection = harness.Engine.EnforceOutfitterCandidateAuthority(
            fixture.Plan.WorldBatches[0].ItemSubtasks[0],
            CandidatePlan(MarketAcquisitionLiveCandidateStatuses.IncompleteListingCoverage));

        var recovery = harness.Engine.CreateSnapshot();
        Assert.NotNull(rejection);
        Assert.Equal(OutfitterRouteAuthorityPhase.RecoveryNeeded, recovery.OutfitterExecution!.Phase);
        Assert.False(recovery.IsPaused);
        Assert.Equal("Stopped", harness.Runner.State);
        Assert.Equal(
            MarketAcquisitionGuidedRoutePrimaryAction.RetryOutfitterRecovery,
            MarketAcquisitionGuidedRouteActionPresenter.Resolve(recovery));

        var remaining = harness.Engine.CreateOutfitterRecoveryClaim(fixture.Claim);
        var recoveredPlan = WithListingId(fixture.Plan, "recovered-2");
        var result = harness.Engine.StartOutfitterRecovery(recoveredPlan, remaining, fixture.Document);

        Assert.True(result.Success);
        var active = harness.Engine.CreateSnapshot();
        Assert.Equal(OutfitterRouteAuthorityPhase.Active, active.OutfitterExecution!.Phase);
        Assert.True(active.IsRunning);
        Assert.NotEqual(
            MarketAcquisitionGuidedRoutePrimaryAction.ResumeManualPause,
            MarketAcquisitionGuidedRouteActionPresenter.Resolve(active));
    }

    [Fact]
    public void ManualPause_OffersExplicitResumeAndDoesNotEnterRecovery()
    {
        var fixture = Fixture();
        using var harness = MarketAcquisitionRouteEngineHarness.Create(new MemoryStore());
        Assert.True(harness.Engine.Start(
            fixture.Plan,
            fixture.Claim,
            false,
            false,
            fixture.Contract,
            fixture.Document).Success);

        Assert.True(harness.Engine.Pause().Success);
        var paused = harness.Engine.CreateSnapshot();
        Assert.Equal(OutfitterRouteAuthorityPhase.Paused, paused.OutfitterExecution!.Phase);
        Assert.True(paused.IsPaused);
        Assert.Equal(
            MarketAcquisitionGuidedRoutePrimaryAction.ResumeManualPause,
            MarketAcquisitionGuidedRouteActionPresenter.Resolve(paused));

        Assert.True(harness.Engine.Resume().Success);
        var resumed = harness.Engine.CreateSnapshot();
        Assert.Equal(OutfitterRouteAuthorityPhase.Active, resumed.OutfitterExecution!.Phase);
        Assert.True(resumed.IsRunning);
    }

    [Fact]
    public void NoPlanAutoResume_PausesOnceAndIsNotEligibleForAnotherAutomaticAttempt()
    {
        var fixture = Fixture();
        var store = new MemoryStore();
        var session = OutfitterRouteAuthoritySession.Consume(
            fixture.Contract,
            fixture.Document,
            fixture.Plan,
            fixture.Claim,
            store);

        var paused = OutfitterRouteRecoveryLifecycle.PauseUnavailable(
            session.State,
            "No viable exact-quality route remains.",
            DateTimeOffset.UnixEpoch);
        store.Save(paused);

        Assert.Equal(OutfitterRouteAuthorityPhase.Paused, store.Restore()!.Phase);
        Assert.False(OutfitterRouteRecoveryLifecycle.CanAutoResume(store.Restore()));
    }

    [Fact]
    public void FailedInitialPreflight_PersistsVisiblePausedState()
    {
        var fixture = Fixture();
        var store = new MemoryStore();
        using var harness = MarketAcquisitionRouteEngineHarness.Create(store);
        var invalidPlan = fixture.Plan with { Status = "Incomplete" };

        var result = harness.Engine.Start(
            invalidPlan,
            fixture.Claim,
            false,
            false,
            fixture.Contract,
            fixture.Document);

        Assert.False(result.Success);
        Assert.Equal(OutfitterRouteAuthorityPhase.Paused, store.Restore()!.Phase);
        Assert.Contains("preflight", store.Restore()!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(OutfitterRouteRecoveryLifecycle.CanAutoResume(store.Restore()));
    }

    [Fact]
    public void DiagnosticContract_RejectsLiveExecutionButAllowsDryRun()
    {
        var fixture = Fixture(dryRunOnly: true);
        using var liveHarness = MarketAcquisitionRouteEngineHarness.Create(new MemoryStore());

        var live = liveHarness.Engine.Start(
            fixture.Plan,
            fixture.Claim,
            false,
            false,
            fixture.Contract,
            fixture.Document,
            MarketAcquisitionExecutionMode.Live);

        Assert.False(live.Success);
        Assert.Contains("dry runs", live.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Idle", liveHarness.Runner.State);

        using var dryRunHarness = MarketAcquisitionRouteEngineHarness.Create(new MemoryStore());
        var dryRun = dryRunHarness.Engine.Start(
            fixture.Plan,
            fixture.Claim,
            false,
            false,
            fixture.Contract,
            fixture.Document,
            MarketAcquisitionExecutionMode.DryRun);

        Assert.True(dryRun.Success);
        Assert.Equal("Running", dryRunHarness.Runner.State);
    }

    [Fact]
    public void FailedRecoveryPreflight_PausesInsteadOfRemainingRecoveryEligible()
    {
        var fixture = Fixture();
        var store = new MemoryStore();
        using var harness = MarketAcquisitionRouteEngineHarness.Create(store);
        Assert.True(harness.Engine.Start(
            fixture.Plan,
            fixture.Claim,
            false,
            false,
            fixture.Contract,
            fixture.Document).Success);
        Assert.NotNull(harness.Engine.EnforceOutfitterCandidateAuthority(
            fixture.Plan.WorldBatches[0].ItemSubtasks[0],
            CandidatePlan(MarketAcquisitionLiveCandidateStatuses.IncompleteListingCoverage)));
        var remaining = harness.Engine.CreateOutfitterRecoveryClaim(fixture.Claim);

        var result = harness.Engine.StartOutfitterRecovery(
            fixture.Plan with { Status = "Incomplete" },
            remaining,
            fixture.Document);

        Assert.False(result.Success);
        Assert.Equal(OutfitterRouteAuthorityPhase.Paused, store.Restore()!.Phase);
        Assert.False(OutfitterRouteRecoveryLifecycle.CanAutoResume(store.Restore()));
    }

    private static FixtureData Fixture(bool dryRunOnly = false)
    {
        var document = MarketAcquisitionRequestDocument.CreateDefault("Fran", "Siren");
        var transfer = OutfitterWorkbenchAuthorityTests.Transfer() with { DryRunOnly = dryRunOnly };
        document = OutfitterWorkbenchAuthorityService.Stage(document, transfer);
        document = OutfitterWorkbenchAuthorityService.Finalize(document);
        var claim = new MarketAcquisitionClaimView
        {
            Id = "request-1",
            ClaimToken = "claim-token",
            Status = "AcceptedInPlugin",
            TargetCharacterName = "Fran",
            TargetWorld = "Siren",
            Region = "North America",
            WorldMode = "Recommended",
            Lines =
            [
                new MarketAcquisitionBatchLineView
                {
                    LineId = "line-1",
                    ItemId = 10,
                    ItemName = "Exact HQ Ring",
                    QuantityMode = "TargetQuantity",
                    TargetQuantity = 2,
                    HqPolicy = "HQOnly",
                    MaxUnitPrice = 100,
                    GilCap = 200,
                },
            ],
        };
        var listing = Listing("listing-1");
        var subtask = new MarketAcquisitionWorldItemSubtask
        {
            LineId = "line-1",
            ItemId = 10,
            ItemName = "Exact HQ Ring",
            WorldName = "Siren",
            DataCenter = "Aether",
            QuantityMode = "TargetQuantity",
            RequestedQuantity = 2,
            HqPolicy = "HQOnly",
            MaxUnitPrice = 100,
            GilCap = 200,
            PlannedQuantity = 2,
            PlannedGil = 200,
            Listings = [listing],
        };
        var plan = new MarketAcquisitionPlan
        {
            RequestId = claim.Id,
            Status = "Ready",
            WorldMode = "Recommended",
            Lines =
            [
                new MarketAcquisitionPlanLine
                {
                    LineId = "line-1",
                    ItemId = 10,
                    ItemName = "Exact HQ Ring",
                    QuantityMode = "TargetQuantity",
                    RequestedQuantity = 2,
                    HqPolicy = "HQOnly",
                    MaxUnitPrice = 100,
                    GilCap = 200,
                    Status = "Ready",
                    PlannedQuantity = 2,
                    PlannedGil = 200,
                },
            ],
            WorldBatches =
            [
                new MarketAcquisitionWorldBatch
                {
                    WorldName = "Siren",
                    DataCenter = "Aether",
                    PlannedQuantity = 2,
                    PlannedGil = 200,
                    ItemSubtasks = [subtask],
                    Listings = [listing],
                },
            ],
        };
        return new(document, document.OutfitterAuthority!.FinalizedContract!, claim, plan);
    }

    private static MarketAcquisitionLiveCandidatePlan CandidatePlan(string status = "Ready") => new()
    {
        Status = status,
        ListingReadState = status == "Ready"
            ? MarketBoardListingReadState.FreshComplete
            : MarketBoardListingReadState.FreshPartial,
        WouldBuyQuantity = 2,
        WouldSpendGil = 200,
    };

    private static MarketAcquisitionPlannedListing Listing(string listingId) => new()
    {
        LineId = "line-1",
        ItemId = 10,
        ItemName = "Exact HQ Ring",
        ListingId = listingId,
        Quantity = 2,
        UnitPrice = 100,
        TotalGil = 200,
        IsHq = true,
    };

    private static MarketAcquisitionPlan WithListingId(MarketAcquisitionPlan plan, string listingId)
    {
        var listing = Listing(listingId);
        var batch = plan.WorldBatches[0];
        var subtask = batch.ItemSubtasks[0] with { Listings = [listing] };
        return plan with
        {
            WorldBatches = [batch with { Listings = [listing], ItemSubtasks = [subtask] }],
        };
    }

    private sealed class MemoryStore : IOutfitterRouteExecutionStateStore
    {
        private OutfitterRouteExecutionState? state;
        public OutfitterRouteExecutionState? Restore() => state;
        public void Save(OutfitterRouteExecutionState value) => state = value;
        public void Clear() => state = null;
    }

    private sealed record FixtureData(
        MarketAcquisitionRequestDocument Document,
        OutfitterExecutionContract Contract,
        MarketAcquisitionClaimView Claim,
        MarketAcquisitionPlan Plan);
}
