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
        Assert.Equal("Arrived", runner.ActiveStop?.Status);
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
    public void PostPurchaseRefreshPending_DoesNotConfirmPurchaseFromClosedResultWindow()
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
                Status = "PostPurchaseRefreshPending",
                Message = "The old result window was closed; a new browse is starting.",
                ReadState = MarketBoardListingReadState.Loading,
                ItemId = 5067,
                WorldName = "Siren",
            },
            now.AddSeconds(1));

        Assert.True(session.IsActive);
        Assert.True(session.ConfirmationWasSubmitted);
        Assert.Equal(MarketBoardPurchaseSessionPhase.WaitingForListingRemoval, session.Phase);
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
