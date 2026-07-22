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
    public void InactiveRoute_ClearsPausedStateWithoutFinalizedContract()
    {
        var fixture = Fixture(dryRunOnly: true, splitListings: true);
        var store = new MemoryStore();
        store.Save(SeededState(fixture));

        Assert.True(OutfitterRouteRecoveryLifecycle.ClearOrphanedState(
            isRouteActive: false,
            store.Restore(),
            finalizedContract: null,
            store));

        Assert.Null(store.Restore());
        Assert.Equal(1, store.ClearCount);
    }

    [Theory]
    [InlineData(OutfitterRouteAuthorityPhase.Paused)]
    [InlineData(OutfitterRouteAuthorityPhase.RecoveryNeeded)]
    public void InactiveRoute_PreservesStateWithMatchingFinalizedContract(OutfitterRouteAuthorityPhase phase)
    {
        var fixture = Fixture(dryRunOnly: true, splitListings: true);
        var store = new MemoryStore();
        var persisted = SeededState(fixture) with { Phase = phase };
        store.Save(persisted);

        Assert.False(OutfitterRouteRecoveryLifecycle.ClearOrphanedState(
            isRouteActive: false,
            persisted,
            fixture.Contract,
            store));

        Assert.Same(persisted, store.Restore());
        Assert.Equal(0, store.ClearCount);
    }

    [Fact]
    public void ActiveRoute_PreservesPersistedStateWithoutFinalizedContract()
    {
        var fixture = Fixture(dryRunOnly: true, splitListings: true);
        var store = new MemoryStore();
        var persisted = SeededState(fixture);
        store.Save(persisted);

        Assert.False(OutfitterRouteRecoveryLifecycle.ClearOrphanedState(
            isRouteActive: true,
            persisted,
            finalizedContract: null,
            store));

        Assert.Same(persisted, store.Restore());
        Assert.Equal(0, store.ClearCount);
    }

    [Fact]
    public void OrdinaryRouteWithoutOutfitterState_PerformsNoRepair()
    {
        var store = new MemoryStore();

        Assert.False(OutfitterRouteRecoveryLifecycle.ClearOrphanedState(
            isRouteActive: false,
            persisted: null,
            finalizedContract: null,
            store));

        Assert.Equal(0, store.ClearCount);
    }

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
    public void DiagnosticChangedRowScenario_EntersRecoveryOnceAndCanResumeRecoveredPlan()
    {
        var fixture = Fixture(dryRunOnly: true);
        using var harness = MarketAcquisitionRouteEngineHarness.Create(new MemoryStore());
        Assert.True(harness.Engine.ArmOutfitterDryRunScenario(OutfitterDryRunScenario.ChangedListingRecovery));
        Assert.True(harness.Engine.Start(
            fixture.Plan,
            fixture.Claim,
            false,
            false,
            fixture.Contract,
            fixture.Document,
            MarketAcquisitionExecutionMode.DryRun).Success);

        var injected = harness.Engine.EnforceOutfitterCandidateAuthority(
            fixture.Plan.WorldBatches[0].ItemSubtasks[0],
            CandidatePlan());

        Assert.NotNull(injected);
        Assert.Equal(OutfitterRouteAuthorityPhase.RecoveryNeeded, harness.Engine.CreateSnapshot().OutfitterExecution!.Phase);
        var remaining = harness.Engine.CreateOutfitterRecoveryClaim(fixture.Claim);
        Assert.True(harness.Engine.StartOutfitterRecovery(fixture.Plan, remaining, fixture.Document).Success);
        Assert.Null(harness.Engine.EnforceOutfitterCandidateAuthority(
            fixture.Plan.WorldBatches[0].ItemSubtasks[0],
            CandidatePlan()));
    }

    [Fact]
    public void DiagnosticNoViableScenario_IsConsumedOnceOnlyAfterRecoveryIsNeeded()
    {
        var fixture = Fixture(dryRunOnly: true);
        using var harness = MarketAcquisitionRouteEngineHarness.Create(new MemoryStore());
        Assert.True(harness.Engine.ArmOutfitterDryRunScenario(OutfitterDryRunScenario.NoViableRecovery));
        Assert.True(harness.Engine.Start(
            fixture.Plan,
            fixture.Claim,
            false,
            false,
            fixture.Contract,
            fixture.Document,
            MarketAcquisitionExecutionMode.DryRun).Success);
        Assert.False(harness.Engine.ConsumeNoViableOutfitterDryRunScenario());

        Assert.NotNull(harness.Engine.EnforceOutfitterCandidateAuthority(
            fixture.Plan.WorldBatches[0].ItemSubtasks[0],
            CandidatePlan()));

        Assert.True(harness.Engine.ConsumeNoViableOutfitterDryRunScenario());
        Assert.False(harness.Engine.ConsumeNoViableOutfitterDryRunScenario());
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

    [Fact]
    public void RestartDryRun_RestoresSunkReceiptOnceWithoutMutatingStoreOrAttemptingPurchase()
    {
        var fixture = Fixture(dryRunOnly: true, splitListings: true);
        var config = new Configuration();
        var saveCount = 0;
        var store = new ConfigurationOutfitterRouteExecutionStateStore(config, () => saveCount++);
        var seeded = SeededState(fixture);
        store.Save(seeded);
        var persistedJson = config.OutfitterRouteExecutionStateJson;
        var initialSaveCount = saveCount;

        using (var first = MarketAcquisitionRouteEngineHarness.Create(store))
        {
            first.Context.CurrentWorld = "Siren";
            Assert.True(first.Engine.Start(
                fixture.Plan,
                fixture.Claim,
                false,
                false,
                fixture.Contract,
                fixture.Document,
                MarketAcquisitionExecutionMode.DryRun).Success);
            var snapshot = first.Engine.CreateSnapshot();
            Assert.Equal(1u, snapshot.ActivePlan!.Lines[0].RequestedQuantity);
            Assert.Equal(100u, snapshot.ActivePlan.Lines[0].GilCap);
            Assert.Equal(1u, snapshot.ActivePlan.Lines[0].PlannedQuantity);
            Assert.Equal(100u, snapshot.ActivePlan.Lines[0].PlannedGil);
            Assert.Equal(1u, snapshot.OutfitterExecution!.Lines[0].PurchasedQuantity);
            Assert.Equal(100ul, snapshot.OutfitterExecution.TotalSpentGil);
            Assert.Same(snapshot.ActivePlan, OutfitterDryRunExecutionStateRestorer.RestoreRemainingPlan(
                fixture.Contract,
                fixture.Document,
                fixture.Claim,
                snapshot.ActivePlan,
                store.Restore()));

            first.Runner.RecordCurrentWorld("Siren");
            first.Runner.RecordProbe("Siren", CandidatePlan(quantity: 1, gil: 100));
            first.MarketBoard.Reads.Enqueue(ReadWithListings(
                LiveListing("listing-1", "retainer-1", quantity: 1),
                LiveListing("listing-2", "retainer-2", quantity: 1)));
            first.Engine.BeginNextWorldPurchase();

            var liveCandidatePlan = Assert.IsType<MarketAcquisitionLiveCandidatePlan>(
                first.Engine.CreateSnapshot().LiveCandidatePlan);
            Assert.Equal(1, liveCandidatePlan.ReadableListingCount);
            Assert.Equal(1, liveCandidatePlan.ReportedListingCount);
            var selectedRow = Assert.Single(liveCandidatePlan.Rows, row => row.Decision == "WouldBuy");
            Assert.Equal("listing-2", selectedRow.LiveListing.ListingId);
            Assert.DoesNotContain(
                liveCandidatePlan.Rows,
                row => row.LiveListing.ListingId == "listing-1");
            Assert.Equal(0, first.Purchase.ExecuteCallCount);
            Assert.Equal(0, first.Purchase.ConfirmCallCount);
            Assert.Equal(1u, first.Runner.LastRunSummary?.PurchasedQuantity);
            Assert.Equal(initialSaveCount, saveCount);
            Assert.Equal(persistedJson, config.OutfitterRouteExecutionStateJson);
            Assert.Equal(1u, store.Restore()!.Lines[0].PurchasedQuantity);
            Assert.Equal(100ul, store.Restore()!.TotalSpentGil);
        }

        using var restarted = MarketAcquisitionRouteEngineHarness.Create(store);
        Assert.True(restarted.Engine.Start(
            fixture.Plan,
            fixture.Claim,
            false,
            false,
            fixture.Contract,
            fixture.Document,
            MarketAcquisitionExecutionMode.DryRun).Success);
        Assert.Equal(1u, restarted.Engine.CreateSnapshot().ActivePlan!.Lines[0].RequestedQuantity);
        Assert.Equal(100u, restarted.Engine.CreateSnapshot().ActivePlan!.Lines[0].GilCap);
        Assert.Equal(0, restarted.Purchase.ExecuteCallCount);
        Assert.Equal(0, restarted.Purchase.ConfirmCallCount);
        Assert.Equal(initialSaveCount, saveCount);
        Assert.Equal(persistedJson, config.OutfitterRouteExecutionStateJson);
        Assert.Equal(seeded.SunkPurchases[0].ReceiptId, store.Restore()!.SunkPurchases[0].ReceiptId);
    }

    [Fact]
    public void RestartDryRun_AuthorityRejectsSunkCandidatePassedAfterPlanning()
    {
        var fixture = Fixture(dryRunOnly: true, splitListings: true);
        var store = new MemoryStore();
        store.Save(SeededState(fixture));
        using var harness = MarketAcquisitionRouteEngineHarness.Create(store);
        Assert.True(harness.Engine.Start(
            fixture.Plan,
            fixture.Claim,
            false,
            false,
            fixture.Contract,
            fixture.Document,
            MarketAcquisitionExecutionMode.DryRun).Success);
        var sunkListing = LiveListing("listing-1", "retainer-1", quantity: 1);
        var candidate = CandidatePlan(quantity: 1, gil: 100) with
        {
            Rows =
            [
                new MarketAcquisitionLiveCandidateRow
                {
                    Decision = "WouldBuy",
                    LiveListing = sunkListing,
                    RunningQuantityAfter = 1,
                    RunningGilAfter = 100,
                },
            ],
        };

        var rejection = harness.Engine.EnforceOutfitterCandidateAuthority(
            harness.Engine.CreateSnapshot().ActivePlan!.WorldBatches[0].ItemSubtasks[0],
            candidate);

        Assert.NotNull(rejection);
        Assert.Equal(
            OutfitterRouteAuthorityPhase.RecoveryNeeded,
            harness.Engine.CreateSnapshot().OutfitterExecution!.Phase);
    }

    [Theory]
    [InlineData("contract")]
    [InlineData("stale")]
    [InlineData("over-cap")]
    [InlineData("anonymous")]
    public void RestartDryRun_InvalidSunkReceiptFailsClosedBeforePurchase(string invalidity)
    {
        var fixture = Fixture(dryRunOnly: true, splitListings: true);
        var store = new MemoryStore();
        var seeded = SeededState(fixture, receipt => invalidity switch
        {
            "contract" => receipt with { ContractId = "other-contract" },
            "stale" => receipt with { PlanPreparedAtUtc = receipt.PlanPreparedAtUtc.AddSeconds(-1) },
            "over-cap" => receipt with { Quantity = 3, TotalGil = 300 },
            _ => receipt,
        });
        if (invalidity == "anonymous")
            seeded = seeded with { SunkPurchases = [] };
        store.Save(seeded);
        var saveCount = store.SaveCount;
        using var harness = MarketAcquisitionRouteEngineHarness.Create(store);

        var result = harness.Engine.Start(
            fixture.Plan,
            fixture.Claim,
            false,
            false,
            fixture.Contract,
            fixture.Document,
            MarketAcquisitionExecutionMode.DryRun);

        Assert.False(result.Success);
        Assert.Contains("preflight stopped", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.Purchase.ExecuteCallCount);
        Assert.Equal(0, harness.Purchase.ConfirmCallCount);
        Assert.Equal(saveCount, store.SaveCount);
        Assert.Equal(seeded, store.State);
    }

    [Fact]
    public void LivePurchase_ListingDisappearanceCannotApplyWithoutServerPacket()
    {
        var fixture = Fixture(splitListings: true);
        var store = new MemoryStore();
        using var harness = MarketAcquisitionRouteEngineHarness.Create(store);
        StartLivePurchase(harness, fixture);
        harness.MarketBoard.Reads.Enqueue(new MarketBoardReadResult
        {
            Status = "NoListings",
            ReadState = MarketBoardListingReadState.FreshComplete,
            WorldName = "Siren",
        });

        SubmitPurchaseConfirmation(harness);
        harness.Clock.UtcNow = harness.Clock.UtcNow.AddSeconds(1);
        harness.Engine.MonitorMarketBoardPurchase();

        Assert.IsType<PendingMarketPurchase>(harness.Purchase.PurchaseEvidenceState);
        Assert.Equal(0u, store.Restore()!.Lines[0].PurchasedQuantity);
        Assert.Empty(store.Restore()!.SunkPurchases);
        Assert.Single(harness.MarketBoard.Reads);
        Assert.Equal(0u, harness.Engine.CreateSnapshot().ActiveLinePurchasedQuantity);
    }

    [Fact]
    public void LivePurchase_EvidenceBlockPausesAuthorityWithoutApplyingPurchase()
    {
        var fixture = Fixture(splitListings: true);
        var store = new MemoryStore();
        using var harness = MarketAcquisitionRouteEngineHarness.Create(store);
        StartLivePurchase(harness, fixture);
        harness.Purchase.HasServerPurchaseEvidence = false;
        harness.Purchase.ConfirmationResults.Enqueue(new MarketBoardPurchaseResult
        {
            Status = "PurchaseEvidenceBlocked",
            Message = "Server purchase evidence is unavailable; confirmation was not submitted.",
            Candidate = Candidate("listing-1", "retainer-1", 1),
        });

        harness.Clock.UtcNow = harness.Clock.UtcNow.AddSeconds(1);
        harness.Engine.MonitorMarketBoardPurchase();

        Assert.Equal("Failed", harness.Runner.State);
        Assert.Equal(OutfitterRouteAuthorityPhase.Paused, store.Restore()!.Phase);
        Assert.Empty(store.Restore()!.SunkPurchases);
        Assert.Null(harness.Purchase.PurchaseEvidenceState);
    }

    [Fact]
    public void LivePurchase_ExactServerPacketAppliesOneSunkReceiptAndDuplicateReconciliationIsIdempotent()
    {
        var fixture = Fixture(splitListings: true);
        var store = new MemoryStore();
        using var harness = MarketAcquisitionRouteEngineHarness.Create(store);
        StartLivePurchase(harness, fixture);
        SubmitPurchaseConfirmation(harness);
        var pending = Assert.IsType<PendingMarketPurchase>(harness.Purchase.PurchaseEvidenceState);
        var confirmed = new ConfirmedMarketPurchase(pending.Intent, new MarketPurchasePacketObservation
        {
            Position = new MarketPurchasePacketPosition { Epoch = pending.Intent.PacketFloor.Epoch, Sequence = 1 },
            ObservedAtUtc = harness.Clock.UtcNow,
            RawCatalogId = 1_000_010,
            ItemId = 10,
            IsHighQuality = true,
            Quantity = 1,
        });
        harness.Purchase.EvidenceAdvanceResults.Enqueue(new(
            MarketPurchaseEvidenceAdvanceStatus.Applied,
            1,
            confirmed,
            "Exact packet confirmed."));
        harness.MarketBoard.Reads.Enqueue(ReadWithListings(LiveListing("listing-2", "retainer-2", 1)));
        harness.Purchase.PurchaseResults.Enqueue(new MarketBoardPurchaseResult
        {
            Status = "PurchaseSelectionSent",
            Candidate = Candidate("listing-2", "retainer-2", 1),
        });

        harness.Clock.UtcNow = harness.Clock.UtcNow.AddSeconds(1);
        harness.Engine.MonitorMarketBoardPurchase();

        var applied = store.Restore()!;
        Assert.Equal(1u, applied.Lines[0].PurchasedQuantity);
        Assert.Equal(100ul, applied.TotalSpentGil);
        Assert.Single(applied.SunkPurchases);
        Assert.Equal(1, harness.Purchase.ResolveEvidenceCallCount);
        Assert.Equal(1u, harness.Engine.CreateSnapshot().ActiveLinePurchasedQuantity);

        harness.Purchase.PurchaseEvidenceState = confirmed;
        var rejected = harness.Engine.ReconcileTerminalPurchaseEvidence(false, "Incorrectly treated confirmed evidence as no purchase.");
        Assert.Equal(MarketPurchaseTerminalResolutionStatus.InvalidDisposition, rejected.Status);
        Assert.IsType<ConfirmedMarketPurchase>(harness.Purchase.PurchaseEvidenceState);

        var reconciled = harness.Engine.ReconcileTerminalPurchaseEvidence(true, "Retried confirmed terminal evidence after restart.");

        Assert.True(reconciled.IsResolved);
        Assert.Single(store.Restore()!.SunkPurchases);
        Assert.Equal(1u, store.Restore()!.Lines[0].PurchasedQuantity);
        Assert.Equal(2, harness.Purchase.ResolveEvidenceCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LivePurchase_IndeterminateEvidencePausesWithoutApplyingPurchase(bool conflicting)
    {
        var fixture = Fixture(splitListings: true);
        var store = new MemoryStore();
        using var harness = MarketAcquisitionRouteEngineHarness.Create(store);
        StartLivePurchase(harness, fixture);
        SubmitPurchaseConfirmation(harness);
        var pending = Assert.IsType<PendingMarketPurchase>(harness.Purchase.PurchaseEvidenceState);
        MarketPurchaseEvidenceState terminal = conflicting
            ? new ConflictingMarketPurchasePacket(pending.Intent, new MarketPurchasePacketObservation
            {
                Position = new MarketPurchasePacketPosition { Epoch = pending.Intent.PacketFloor.Epoch, Sequence = 1 },
                ObservedAtUtc = harness.Clock.UtcNow,
                RawCatalogId = 11,
                ItemId = 11,
                IsHighQuality = true,
                Quantity = 1,
            })
            : new TimedOutIndeterminateMarketPurchase(pending.Intent, harness.Clock.UtcNow);
        harness.Purchase.EvidenceAdvanceResults.Enqueue(new(
            MarketPurchaseEvidenceAdvanceStatus.Applied,
            conflicting ? 1 : 0,
            terminal,
            "Terminal evidence."));

        harness.Clock.UtcNow = harness.Clock.UtcNow.AddSeconds(1);
        harness.Engine.MonitorMarketBoardPurchase();

        Assert.Equal("Failed", harness.Runner.State);
        Assert.Equal(OutfitterRouteAuthorityPhase.Paused, store.Restore()!.Phase);
        Assert.Empty(store.Restore()!.SunkPurchases);
        Assert.Equal(0u, store.Restore()!.Lines[0].PurchasedQuantity);
        Assert.Equal(0, harness.Purchase.ResolveEvidenceCallCount);
    }

    private static void StartLivePurchase(MarketAcquisitionRouteEngineHarness harness, FixtureData fixture)
    {
        harness.Context.CurrentWorld = "Siren";
        harness.Purchase.HasServerPurchaseEvidence = true;
        Assert.True(harness.Engine.Start(
            fixture.Plan,
            fixture.Claim,
            false,
            false,
            fixture.Contract,
            fixture.Document,
            MarketAcquisitionExecutionMode.Live).Success);
        harness.Runner.RecordCurrentWorld("Siren");
        harness.Runner.RecordProbe("Siren", CandidatePlan());
        harness.MarketBoard.Reads.Enqueue(ReadWithListings(
            LiveListing("listing-1", "retainer-1", 1),
            LiveListing("listing-2", "retainer-2", 1)));
        harness.Purchase.PurchaseResults.Enqueue(new MarketBoardPurchaseResult
        {
            Status = "PurchaseSelectionSent",
            Candidate = Candidate("listing-1", "retainer-1", 1),
        });
        harness.Engine.BeginNextWorldPurchase();
    }

    private static void SubmitPurchaseConfirmation(MarketAcquisitionRouteEngineHarness harness)
    {
        harness.Purchase.ConfirmationResults.Enqueue(new MarketBoardPurchaseResult
        {
            Status = "ConfirmationSubmitted",
            Candidate = Candidate("listing-1", "retainer-1", 1),
        });
        harness.Clock.UtcNow = harness.Clock.UtcNow.AddSeconds(1);
        harness.Engine.MonitorMarketBoardPurchase();
        Assert.NotNull(harness.Purchase.LastIntentContext);
    }

    private static FixtureData Fixture(bool dryRunOnly = false, bool splitListings = false)
    {
        var document = MarketAcquisitionRequestDocument.CreateDefault("Fran", "Siren");
        var sourceTransfer = OutfitterWorkbenchAuthorityTests.Transfer();
        var sourceLot = sourceTransfer.MarketLots.Single();
        var transfer = sourceTransfer with
        {
            DryRunOnly = dryRunOnly,
            MarketLots = splitListings
                ?
                [
                    sourceLot with { RequiredQuantity = 1, ObservedAvailableQuantity = 1, ObservedTotalPriceGil = 100 },
                    sourceLot with
                    {
                        RequiredQuantity = 1,
                        ObservedAvailableQuantity = 1,
                        ObservedTotalPriceGil = 100,
                        DiscoveryObservationId = "listing-2",
                        RetainerName = "Retainer 2",
                        RetainerId = "retainer-2",
                    },
                ]
                : sourceTransfer.MarketLots,
        };
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
        var listings = splitListings
            ? new[] { Listing("listing-1", "retainer-1", 1, sourceLot.ReviewedAtUtc), Listing("listing-2", "retainer-2", 1, sourceLot.ReviewedAtUtc) }
            : new[] { Listing("listing-1", reviewedAt: sourceLot.ReviewedAtUtc) };
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
            Listings = listings,
        };
        var plan = new MarketAcquisitionPlan
        {
            RequestId = claim.Id,
            Status = "Ready",
            WorldMode = "Recommended",
            PreparedAtUtc = DateTimeOffset.Parse("2026-07-20T12:00:00Z"),
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
                    Listings = listings,
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

    private static MarketAcquisitionLiveCandidatePlan CandidatePlan(uint quantity, uint gil) => new()
    {
        Status = "Ready",
        ListingReadState = MarketBoardListingReadState.FreshComplete,
        WouldBuyQuantity = quantity,
        WouldSpendGil = gil,
    };

    private static MarketAcquisitionPlannedListing Listing(
        string listingId,
        string retainerId = "retainer-1",
        uint quantity = 2,
        DateTimeOffset reviewedAt = default) => new()
    {
        LineId = "line-1",
        ItemId = 10,
        ItemName = "Exact HQ Ring",
        ListingId = listingId,
        RetainerId = retainerId,
        Quantity = quantity,
        UnitPrice = 100,
        TotalGil = checked(quantity * 100),
        IsHq = true,
        LastReviewTimeUtc = reviewedAt,
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

    private static MarketBoardReadResult ReadWithListings(params MarketBoardLiveListing[] listings) => new()
    {
        Status = "Ready",
        ReadState = MarketBoardListingReadState.FreshComplete,
        ItemId = 10,
        WorldName = "Siren",
        ReportedListingCount = listings.Length,
        Listings = listings,
    };

    private static MarketBoardLiveListing LiveListing(string listingId, string retainerId, uint quantity) => new()
    {
        ItemId = 10,
        WorldName = "Siren",
        ListingId = listingId,
        RetainerId = retainerId,
        Quantity = quantity,
        UnitPrice = 100,
        IsHq = true,
    };

    private static MarketBoardPurchaseCandidate Candidate(string listingId, string retainerId, uint quantity) => new()
    {
        ItemId = 10,
        WorldName = "Siren",
        ListingId = listingId,
        RetainerId = retainerId,
        Quantity = quantity,
        UnitPrice = 100,
        IsHq = true,
    };

    private static OutfitterRouteExecutionState SeededState(
        FixtureData fixture,
        Func<OutfitterRouteSunkPurchase, OutfitterRouteSunkPurchase>? mutateReceipt = null)
    {
        var line = fixture.Contract.Lines[0];
        var draft = new OutfitterRouteSunkPurchase
        {
            SchemaVersion = OutfitterRouteSunkPurchase.CurrentSchemaVersion,
            ReceiptId = string.Empty,
            ContractId = fixture.Contract.ContractId,
            CanonicalIntentHash = fixture.Contract.CanonicalIntentHash,
            WorkbenchDocumentId = fixture.Document.LocalRequestId,
            WorkbenchRevision = fixture.Document.LocalRevision,
            PlanRequestId = fixture.Plan.RequestId,
            PlanPreparedAtUtc = fixture.Plan.PreparedAtUtc,
            WorldName = "Siren",
            LineId = "line-1",
            ItemId = line.ItemId,
            Quality = line.Quality,
            ListingId = "listing-1",
            RetainerId = "retainer-1",
            Quantity = 1,
            UnitPriceGil = 100,
            TotalGil = 100,
        };
        var receipt = mutateReceipt?.Invoke(draft) ?? draft;
        receipt = receipt with { ReceiptId = OutfitterDryRunExecutionStateRestorer.ComputeReceiptId(receipt) };
        return new OutfitterRouteExecutionState(
            OutfitterRouteExecutionState.CurrentSchemaVersion,
            fixture.Contract.ContractId,
            fixture.Contract.CanonicalIntentHash,
            OutfitterRouteAuthorityPhase.Paused,
            [new OutfitterRouteLineProgress("line-1", line.ItemId, line.ItemName, line.Quality, 2, 1, 100, 100, 200)],
            100,
            0,
            null,
            0,
            "Seeded fixture.",
            DateTimeOffset.Parse("2026-07-20T12:01:00Z"))
        {
            SunkPurchases = [receipt],
        };
    }

    private sealed class MemoryStore : IOutfitterRouteExecutionStateStore
    {
        private OutfitterRouteExecutionState? state;
        public OutfitterRouteExecutionState? State => state;
        public int SaveCount { get; private set; }
        public int ClearCount { get; private set; }
        public OutfitterRouteExecutionState? Restore() => state;
        public void Save(OutfitterRouteExecutionState value)
        {
            SaveCount++;
            state = value;
        }
        public void Clear()
        {
            ClearCount++;
            state = null;
        }
    }

    private sealed record FixtureData(
        MarketAcquisitionRequestDocument Document,
        OutfitterExecutionContract Contract,
        MarketAcquisitionClaimView Claim,
        MarketAcquisitionPlan Plan);
}
