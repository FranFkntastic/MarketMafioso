using MarketMafioso.MarketAcquisition;
using MarketMafioso.Windows.MarketAcquisitionPanels;
using MarketMafioso.Automation.MarketBoard;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class MarketAcquisitionRouteRecoveryTests
{
    [Fact]
    public void Recover_PreservesRetainedStopAndResumesAtCurrentWorld()
    {
        using var runner = new MarketAcquisitionRouteRunner(Path.GetTempPath());
        var plan = CreatePlan("Siren");

        Assert.True(runner.Start(plan).Success);
        Assert.True(runner.Stop().Success);

        var result = runner.Recover("Siren");

        Assert.True(result.Success);
        Assert.True(runner.IsRunning);
        Assert.Equal("Siren", runner.ActiveStop?.WorldName);
        Assert.Equal("Pending", runner.ActiveStop?.Status);
    }

    [Fact]
    public void MarketSessionExpiration_PausesWithExactReasonAndRetainsStop()
    {
        using var runner = new MarketAcquisitionRouteRunner(Path.GetTempPath());
        Assert.True(runner.Start(CreatePlan("Siren")).Success);
        Assert.True(runner.RecordCurrentWorld("Siren").Success);
        const string message = "MMF market access expired after three server rate limits. Relog before resuming.";

        var paused = runner.Pause(message);

        Assert.True(paused.Success);
        Assert.True(runner.IsPaused);
        Assert.Equal(message, runner.StatusMessage);
        Assert.Equal("Siren", runner.ActiveStop?.WorldName);
        Assert.Equal("Arrived", runner.ActiveStop?.Status);
        Assert.True(runner.Resume().Success);
        Assert.Equal("Siren", runner.ActiveStop?.WorldName);
    }

    [Fact]
    public void ExhaustedRoute_CompletesWithBelowTargetOutcome()
    {
        using var runner = new MarketAcquisitionRouteRunner(Path.GetTempPath());
        var plan = CreatePlan("Siren") with
        {
            Lines =
            [
                CreatePlan("Siren").Lines[0] with
                {
                    QuantityMode = "TargetQuantity",
                    RequestedQuantity = 10,
                },
            ],
        };

        Assert.True(runner.Start(plan).Success);
        Assert.True(runner.RecordCurrentWorld("Siren").Success);
        Assert.True(runner.RecordProbe(
            "Siren",
            new MarketAcquisitionLiveCandidatePlan
            {
                Status = "UnderProcured",
                Message = "Three safe items found.",
                WouldBuyQuantity = 3,
                WouldSpendGil = 150,
            }).Success);
        var result = runner.RecordWorldPurchaseBatchComplete("Siren", 3, 150);

        Assert.True(result.Success);
        Assert.Equal("Completed", runner.State);
        Assert.Equal(
            new MarketAcquisitionRouteCompletionOutcome(
                MarketAcquisitionRouteCompletionKinds.ScopeExhaustedBelowTarget,
                10,
                3,
                7),
            runner.CompletionOutcome);
        Assert.Equal(
            MarketAcquisitionRouteProgressReporter.CompleteAction,
            MarketAcquisitionRouteProgressReporter.ResolveAction(runner.State));
    }

    [Fact]
    public void ExhaustedEvidenceRefresh_CompletesWithoutJudgingTargetFulfillment()
    {
        using var runner = new MarketAcquisitionRouteRunner(Path.GetTempPath());
        var plan = CreatePlan("Siren") with
        {
            Lines =
            [
                CreatePlan("Siren").Lines[0] with
                {
                    QuantityMode = "TargetQuantity",
                    RequestedQuantity = 10,
                },
            ],
        };

        Assert.True(runner.Start(plan, evaluateTargetFulfillment: false).Success);
        Assert.True(runner.RecordCurrentWorld("Siren").Success);
        Assert.True(runner.RecordProbe(
            "Siren",
            new MarketAcquisitionLiveCandidatePlan
            {
                Status = "UnderProcured",
                Message = "Three safe items found.",
                WouldBuyQuantity = 3,
                WouldSpendGil = 150,
            }).Success);
        var result = runner.RecordWorldPurchaseBatchComplete("Siren", 3, 150);

        Assert.True(result.Success);
        Assert.Equal("Completed", runner.State);
        Assert.Equal(
            MarketAcquisitionRouteCompletionKinds.EvidenceRefreshCompleted,
            runner.CompletionOutcome?.Kind);
    }

    [Fact]
    public void ExhaustedRoute_CompletesWithTargetSatisfiedOutcome()
    {
        using var runner = new MarketAcquisitionRouteRunner(Path.GetTempPath());
        var plan = CreatePlan("Siren") with
        {
            Lines =
            [
                CreatePlan("Siren").Lines[0] with
                {
                    QuantityMode = "TargetQuantity",
                    RequestedQuantity = 3,
                },
            ],
        };

        Assert.True(runner.Start(plan).Success);
        Assert.True(runner.RecordCurrentWorld("Siren").Success);
        Assert.True(runner.RecordProbe(
            "Siren",
            new MarketAcquisitionLiveCandidatePlan
            {
                Status = "Ready",
                Message = "Three safe items found.",
                WouldBuyQuantity = 3,
                WouldSpendGil = 150,
            }).Success);
        Assert.True(runner.RecordWorldPurchaseBatchComplete("Siren", 3, 150).Success);

        Assert.Equal("Completed", runner.State);
        Assert.Equal(
            new MarketAcquisitionRouteCompletionOutcome(
                MarketAcquisitionRouteCompletionKinds.TargetSatisfied,
                3,
                3,
                0),
            runner.CompletionOutcome);
    }

    [Fact]
    public void ExhaustedRoute_CountsInitialInventoryTowardTarget()
    {
        using var runner = new MarketAcquisitionRouteRunner(Path.GetTempPath());
        var plan = CreatePlan("Siren") with
        {
            Lines =
            [
                CreatePlan("Siren").Lines[0] with
                {
                    QuantityMode = "TargetQuantity",
                    RequestedQuantity = 10,
                    InitialOnHandQuantity = 7,
                },
            ],
        };

        Assert.True(runner.Start(plan).Success);
        Assert.True(runner.RecordCurrentWorld("Siren").Success);
        Assert.True(runner.RecordProbe("Siren", new MarketAcquisitionLiveCandidatePlan
        {
            Status = "Ready",
            Message = "Three safe items found.",
            WouldBuyQuantity = 3,
            WouldSpendGil = 150,
        }).Success);
        Assert.True(runner.RecordWorldPurchaseBatchComplete("Siren", 3, 150).Success);

        Assert.Equal(MarketAcquisitionRouteCompletionKinds.TargetSatisfied, runner.CompletionOutcome?.Kind);
        Assert.Equal(10u, runner.CompletionOutcome?.TargetPurchasedQuantity);
    }

    [Fact]
    public void ExhaustedRoute_ReportsOverageLimitedCompletionKind()
    {
        using var runner = new MarketAcquisitionRouteRunner(Path.GetTempPath());
        var plan = CreatePlan("Siren") with
        {
            Lines = [CreatePlan("Siren").Lines[0] with { QuantityMode = "TargetQuantity", RequestedQuantity = 10 }],
        };

        Assert.True(runner.Start(plan).Success);
        Assert.True(runner.RecordCurrentWorld("Siren").Success);
        Assert.True(runner.RecordProbe("Siren", new MarketAcquisitionLiveCandidatePlan
        {
            Status = MarketAcquisitionLiveCandidateStatuses.OverageLimit,
            Message = "No whole listing fits.",
        }).Success);

        Assert.Equal(MarketAcquisitionRouteCompletionKinds.IncompleteOverageLimit, runner.CompletionOutcome?.Kind);
    }

    [Fact]
    public void PendingStopOnCurrentWorld_StillDelegatesCompleteTripToLifestream()
    {
        using var runner = new MarketAcquisitionRouteRunner(Path.GetTempPath());
        Assert.True(runner.Start(CreatePlan("Siren")).Success);
        string? submittedCommand = null;

        var result = runner.PreparePendingStopForCurrentWorld(
            currentWorldIsValid: true,
            currentWorld: "Siren",
            command =>
            {
                submittedCommand = command;
                return true;
            });

        Assert.True(result.Success);
        Assert.Equal("/li Siren mb", submittedCommand);
        Assert.Equal("TravelCommandSent", runner.ActiveStop?.Status);
    }

    [Fact]
    public void Recover_WithDiagnosticsEnabled_StartsAValidRoutePackage()
    {
        var diagnosticsDirectory = Path.Combine(
            Path.GetTempPath(),
            "MarketMafioso.SpecTests",
            Guid.NewGuid().ToString("N"));
        using var runner = new MarketAcquisitionRouteRunner(diagnosticsDirectory);

        Assert.True(runner.Start(CreatePlan("Siren"), enableDiagnostics: true).Success);
        Assert.False(runner.FailRoute("Simulated mid-route failure.").Success);

        var result = runner.Recover("Siren");

        Assert.True(result.Success);
        Assert.True(runner.IsRunning);
        Assert.Equal("route.log", Path.GetFileName(runner.LastDiagnosticFilePath));
        Assert.StartsWith(
            "route-",
            Path.GetFileName(Path.GetDirectoryName(runner.LastDiagnosticFilePath)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void PrepareForRecovery_RetainsCompletedStops()
    {
        var session = MarketAcquisitionGuidedRouteSession.Start(CreatePlan("Siren", "Jenova"));
        Assert.True(session.RecordCurrentWorld("Siren").Success);
        Assert.True(session.RecordProbe(
            "Siren",
            new MarketAcquisitionLiveCandidatePlan
            {
                Status = "NoSafeCandidates",
                Message = "No safe candidates.",
            }).Success);

        var result = session.PrepareForRecovery("Siren");

        Assert.True(result.Success);
        Assert.Equal("Jenova", session.ActiveStop?.WorldName);
        Assert.Equal("Pending", session.ActiveStop?.Status);
        Assert.Equal("Complete", session.Stops[0].Status);
    }

    [Fact]
    public void Presenter_ChoosesRecoveryInsteadOfRestart()
    {
        var action = MarketAcquisitionGuidedRouteActionPresenter.Resolve(
            new MarketAcquisitionRouteEngineSnapshot
            {
                RouteState = "Stopped",
                CanRecover = true,
                CanRestart = true,
            });

        Assert.Equal(MarketAcquisitionGuidedRoutePrimaryAction.RecoverStoppedRoute, action);
    }

    [Fact]
    public void OutcomeRefreshPending_DoesNotConfirmPurchaseFromClosedResultWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var session = MarketBoardPurchaseSession.Start(
            new MarketBoardPurchaseCandidate
            {
                ItemId = 5067,
                WorldName = "Siren",
                ListingId = "listing-1",
                RetainerId = "retainer-1",
                UnitPrice = 49,
                Quantity = 17,
            },
            now,
            TimeSpan.FromSeconds(15));
        session = session.RecordConfirmationAttempt(
            new MarketBoardPurchaseResult
            {
                Status = "ConfirmationSubmitted",
                Message = "Submitted.",
                Candidate = session.Candidate,
            },
            now,
            TimeSpan.FromSeconds(30));

        session = session.RecordFreshRead(
            new MarketBoardReadResult
            {
                Status = "FallbackOutcomeRefreshPending",
                Message = "The old result window was closed; a new browse is starting.",
                ReadState = MarketBoardListingReadState.Loading,
                ItemId = 5067,
                WorldName = "Siren",
            },
            now.AddSeconds(1));

        Assert.True(session.IsActive);
        Assert.True(session.ConfirmationWasSubmitted);
        Assert.Equal(MarketBoardPurchaseSessionPhase.WaitingForOutcome, session.Phase);
    }

    [Fact]
    public void ClosedResultWindow_IsNotPurchaseSuccessEvidence()
    {
        var now = DateTimeOffset.UtcNow;
        var session = ConfirmedSession(now);

        session = session.RecordFreshRead(
            new MarketBoardReadResult
            {
                Status = "MarketBoardNotOpen",
                Message = "Result window closed.",
                ReadState = MarketBoardListingReadState.Unavailable,
                ItemId = 5067,
                WorldName = "Siren",
            },
            now.AddSeconds(1));

        Assert.True(session.IsActive);
        Assert.Equal(MarketBoardPurchaseSessionPhase.WaitingForOutcome, session.Phase);
    }

    [Fact]
    public void EvidenceBackedPurchase_DoesNotRefreshListingsWhileAwaitingOutcome()
    {
        var now = DateTimeOffset.UtcNow;
        using var controller = new MarketBoardAutomationController();
        var candidate = new MarketBoardPurchaseCandidate
        {
            ItemId = 5067,
            WorldName = "Siren",
            ListingId = "listing-1",
            RetainerId = "retainer-1",
            UnitPrice = 49,
            Quantity = 17,
        };
        controller.RecordPurchaseSelection(
            new MarketBoardPurchaseResult
            {
                Status = "PurchaseSelectionSent",
                Message = "Selected.",
                Candidate = candidate,
            },
            now,
            TimeSpan.FromSeconds(15));
        controller.ScheduleNextMonitor(now, TimeSpan.Zero);
        var listingReadCount = 0;

        var tick = controller.MonitorPurchase(
            now,
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(30),
            selected => new MarketBoardPurchaseResult
            {
                Status = "ConfirmationSubmitted",
                Message = "Submitted.",
                Candidate = selected,
            },
            () =>
            {
                listingReadCount++;
                return Snapshot(Listing("listing-1", 49, 17));
            },
            verifyOutcomeFromListings: false);

        Assert.True(tick.DidWork);
        Assert.Equal(0, listingReadCount);
        Assert.Equal(MarketBoardPurchaseSessionPhase.WaitingForOutcome, tick.Session?.Phase);
    }

    [Fact]
    public void ConfirmedPurchase_ProjectsRemovalWithoutChangingBrowseEpoch()
    {
        var snapshot = Snapshot(
            Listing("listing-1", 49, 17),
            Listing("listing-2", 52, 3));
        var candidate = MarketBoardPurchaseCandidate.FromLiveListing(snapshot.Listings[0]);

        var projected = MarketBoardPurchaseSnapshotProjector.ApplyConfirmedPurchase(snapshot, candidate);

        Assert.Equal("Ready", projected.Status);
        Assert.Equal("browse-1", projected.BrowseOperationId);
        Assert.Equal(1, projected.ReportedListingCount);
        Assert.Single(projected.Listings);
        Assert.Equal("listing-2", projected.Listings[0].ListingId);
    }

    [Fact]
    public void ConfirmedLastPurchase_ProjectsAuthoritativeEmptySnapshot()
    {
        var snapshot = Snapshot(Listing("listing-1", 49, 17));

        var projected = MarketBoardPurchaseSnapshotProjector.ApplyConfirmedPurchase(
            snapshot,
            MarketBoardPurchaseCandidate.FromLiveListing(snapshot.Listings[0]));

        Assert.Equal("NoListings", projected.Status);
        Assert.True(projected.IsFresh);
        Assert.Empty(projected.Listings);
        Assert.Equal(0, projected.ReportedListingCount);
    }

    [Fact]
    public void PrepareForRecovery_AfterFailedTravel_RequeuesRetainedStop()
    {
        var session = MarketAcquisitionGuidedRouteSession.Start(CreatePlan("Jenova"));
        Assert.True(session.ExecuteActiveStop(_ => true).Success);

        var result = session.PrepareForRecovery("Siren");

        Assert.True(result.Success);
        Assert.Equal("Jenova", session.ActiveStop?.WorldName);
        Assert.Equal("Pending", session.ActiveStop?.Status);
    }

    [Fact]
    public void ReconcileInterruptedTravel_AfterPause_RequeuesRetainedStopBeforeResume()
    {
        using var runner = new MarketAcquisitionRouteRunner(Path.GetTempPath());

        Assert.True(runner.Start(CreatePlan("Jenova")).Success);
        Assert.True(runner.ExecutePendingTravelCommand(_ => true).Success);
        Assert.True(runner.Pause().Success);

        var reconcile = runner.ReconcileInterruptedTravel("Siren");

        Assert.True(reconcile.Success);
        Assert.Equal("Pending", runner.RetainedActiveStop?.Status);
        Assert.Contains("travel will resume", runner.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(runner.Resume().Success);
        Assert.True(runner.IsRunning);
        Assert.Equal("Pending", runner.ActiveStop?.Status);
    }

    [Fact]
    public void ExecutePendingTravelCommand_SendsOneCommandAndMarksStop()
    {
        using var runner = new MarketAcquisitionRouteRunner(Path.GetTempPath());
        var commands = new List<string>();

        Assert.True(runner.Start(CreatePlan("Jenova")).Success);
        Assert.True(runner.ExecutePendingTravelCommand(command =>
        {
            commands.Add(command);
            return true;
        }).Success);
        Assert.True(runner.ExecutePendingTravelCommand(command =>
        {
            commands.Add(command);
            return true;
        }).Success);

        Assert.Equal(["/li Jenova mb"], commands);
        Assert.Equal("TravelCommandSent", runner.ActiveStop?.Status);
    }

    [Fact]
    public void RecordTravelRecoveryBlocked_LeavesRecoveryActionVisible()
    {
        using var runner = new MarketAcquisitionRouteRunner(Path.GetTempPath());

        Assert.True(runner.Start(CreatePlan("Jenova")).Success);
        Assert.True(runner.Stop().Success);

        var result = runner.RecordTravelRecoveryBlocked("Waiting for Lifestream travel state.");

        Assert.True(result.Success);
        Assert.True(runner.CanRecover);
        Assert.Equal("Waiting for Lifestream travel state.", runner.StatusMessage);
    }

    private static MarketBoardPurchaseSession ConfirmedSession(DateTimeOffset now)
    {
        var session = MarketBoardPurchaseSession.Start(
            new MarketBoardPurchaseCandidate
            {
                ItemId = 5067,
                WorldName = "Siren",
                ListingId = "listing-1",
                RetainerId = "retainer-1",
                UnitPrice = 49,
                Quantity = 17,
            },
            now,
            TimeSpan.FromSeconds(15));
        return session.RecordConfirmationAttempt(
            new MarketBoardPurchaseResult
            {
                Status = "ConfirmationSubmitted",
                Message = "Submitted.",
                Candidate = session.Candidate,
            },
            now,
            TimeSpan.FromSeconds(30));
    }

    private static MarketBoardReadResult Snapshot(params MarketBoardLiveListing[] listings) =>
        new()
        {
            Status = "Ready",
            Message = "Snapshot ready.",
            ReadState = MarketBoardListingReadState.FreshComplete,
            ItemId = 5067,
            WorldName = "Siren",
            ReportedListingCount = listings.Length,
            ListingCapacity = 100,
            BrowseOperationId = "browse-1",
            BrowseHeaderStatus = 0,
            BrowseExpectedPageCount = 1,
            BrowseObservedPageCount = 1,
            BrowseHistoryItemId = 5067,
            Listings = listings,
        };

    private static MarketBoardLiveListing Listing(string listingId, uint unitPrice, uint quantity) =>
        new()
        {
            ItemId = 5067,
            WorldName = "Siren",
            ListingId = listingId,
            RetainerId = $"retainer-{listingId}",
            UnitPrice = unitPrice,
            Quantity = quantity,
        };

    private static MarketAcquisitionPlan CreatePlan(params string[] worlds) =>
        new()
        {
            RequestId = "request-1",
            Status = "Ready",
            WorldMode = "Recommended",
            Lines =
            [
                new MarketAcquisitionPlanLine
                {
                    LineId = "line-1",
                    ItemId = 5067,
                    ItemName = "Rose Gold Ingot",
                    Status = "Ready",
                },
            ],
            WorldBatches = worlds
                .Select(world => new MarketAcquisitionWorldBatch
                {
                    WorldName = world,
                    DataCenter = "Aether",
                    ItemSubtasks =
                    [
                        new MarketAcquisitionWorldItemSubtask
                        {
                            LineId = "line-1",
                            ItemId = 5067,
                            ItemName = "Rose Gold Ingot",
                            WorldName = world,
                            DataCenter = "Aether",
                            Source = "Planned",
                        },
                    ],
                })
                .ToArray(),
        };
}
