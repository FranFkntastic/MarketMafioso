using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketBoardAutomationControllerTests
{
    [Fact]
    public void RecordPurchaseSelection_StartsConfirmationSession()
    {
        var controller = new MarketBoardAutomationController();
        var now = DateTimeOffset.Parse("2026-06-29T12:00:00Z");

        controller.RecordPurchaseSelection(
            new MarketBoardPurchaseResult
            {
                Status = "PurchaseSelectionSent",
                Message = "Selection sent.",
                Candidate = CreateCandidate(),
            },
            now,
            TimeSpan.FromSeconds(15));

        Assert.True(controller.IsBusy);
        Assert.Equal("WaitingForConfirmation", controller.Status);
        Assert.NotNull(controller.PurchaseSession);
    }

    [Fact]
    public void RecordPurchaseSelection_DoesNotStartSessionForRecoverableWait()
    {
        var controller = new MarketBoardAutomationController();

        controller.RecordPurchaseSelection(
            new MarketBoardPurchaseResult
            {
                Status = "ListingListNotReady",
                Message = "Waiting for clickable rows.",
                Candidate = CreateCandidate(),
            },
            DateTimeOffset.Parse("2026-06-29T12:00:00Z"),
            TimeSpan.FromSeconds(15));

        Assert.False(controller.IsBusy);
        Assert.Equal("ListingListNotReady", controller.Status);
        Assert.Null(controller.PurchaseSession);
    }

    [Fact]
    public void RecordConfirmationAttempt_MovesToListingRemovalPhase()
    {
        var controller = new MarketBoardAutomationController();
        var now = DateTimeOffset.Parse("2026-06-29T12:00:00Z");
        var candidate = CreateCandidate();
        controller.RecordPurchaseSelection(
            new MarketBoardPurchaseResult
            {
                Status = "PurchaseSelectionSent",
                Message = "Selection sent.",
                Candidate = candidate,
            },
            now,
            TimeSpan.FromSeconds(15));

        controller.RecordConfirmationAttempt(
            new MarketBoardPurchaseResult
            {
                Status = "ConfirmationSubmitted",
                Message = "Submitted.",
                Candidate = candidate,
            },
            now.AddSeconds(2),
            TimeSpan.FromSeconds(15));

        Assert.True(controller.IsBusy);
        Assert.Equal("WaitingForListingRemoval", controller.Status);
        Assert.Equal(MarketBoardPurchaseSessionPhase.WaitingForListingRemoval, controller.PurchaseSession?.Phase);
    }

    [Fact]
    public void Abort_ClearsBusyStateAndReportsAborted()
    {
        var controller = new MarketBoardAutomationController();
        controller.RecordPurchaseSelection(
            new MarketBoardPurchaseResult
            {
                Status = "PurchaseSelectionSent",
                Message = "Selection sent.",
                Candidate = CreateCandidate(),
            },
            DateTimeOffset.Parse("2026-06-29T12:00:00Z"),
            TimeSpan.FromSeconds(15));

        controller.Abort("Stopped by route reset.");

        Assert.False(controller.IsBusy);
        Assert.Equal("Aborted", controller.Status);
        Assert.Equal("Stopped by route reset.", controller.Message);
    }

    [Fact]
    public void RecordMonitorFailure_PreservesFailedSessionStatus()
    {
        var controller = new MarketBoardAutomationController();
        controller.RecordPurchaseSelection(
            new MarketBoardPurchaseResult
            {
                Status = "PurchaseSelectionSent",
                Message = "Selection sent.",
                Candidate = CreateCandidate(),
            },
            DateTimeOffset.Parse("2026-06-29T12:00:00Z"),
            TimeSpan.FromSeconds(15));

        controller.RecordMonitorFailure("PurchaseMonitorFailed", "Unable to read the confirmation window.");

        Assert.False(controller.IsBusy);
        Assert.Equal("PurchaseMonitorFailed", controller.Status);
        Assert.Equal("Unable to read the confirmation window.", controller.Message);
        Assert.NotNull(controller.PurchaseSession);
    }

    [Fact]
    public void Clear_ClearsSessionAndResult()
    {
        var controller = new MarketBoardAutomationController();
        controller.RecordPurchaseSelection(
            new MarketBoardPurchaseResult
            {
                Status = "PurchaseSelectionSent",
                Message = "Selection sent.",
                Candidate = CreateCandidate(),
            },
            DateTimeOffset.Parse("2026-06-29T12:00:00Z"),
            TimeSpan.FromSeconds(15));

        controller.Clear();

        Assert.False(controller.IsBusy);
        Assert.Null(controller.PurchaseSession);
        Assert.Null(controller.LastPurchaseResult);
        Assert.Equal("Idle", controller.Status);
    }

    [Fact]
    public void IsMonitorDue_UsesScheduledMonitorTime()
    {
        var controller = new MarketBoardAutomationController();
        var now = DateTimeOffset.Parse("2026-06-29T12:00:00Z");
        controller.RecordPurchaseSelection(
            new MarketBoardPurchaseResult
            {
                Status = "PurchaseSelectionSent",
                Message = "Selection sent.",
                Candidate = CreateCandidate(),
            },
            now,
            TimeSpan.FromSeconds(15));

        controller.ScheduleNextMonitor(now, TimeSpan.FromMilliseconds(250));

        Assert.False(controller.IsMonitorDue(now.AddMilliseconds(249)));
        Assert.True(controller.IsMonitorDue(now.AddMilliseconds(250)));
    }

    [Fact]
    public void Clear_ResetsMonitorSchedule()
    {
        var controller = new MarketBoardAutomationController();
        var now = DateTimeOffset.Parse("2026-06-29T12:00:00Z");
        controller.RecordPurchaseSelection(
            new MarketBoardPurchaseResult
            {
                Status = "PurchaseSelectionSent",
                Message = "Selection sent.",
                Candidate = CreateCandidate(),
            },
            now,
            TimeSpan.FromSeconds(15));
        controller.ScheduleNextMonitor(now, TimeSpan.FromMilliseconds(250));

        controller.Clear();

        Assert.False(controller.IsMonitorDue(now.AddSeconds(1)));
    }

    [Fact]
    public void MonitorPurchase_ConfirmationPhaseCallsConfirmAndMovesToListingRemoval()
    {
        var controller = new MarketBoardAutomationController();
        var now = DateTimeOffset.Parse("2026-06-29T12:00:00Z");
        controller.RecordPurchaseSelection(
            new MarketBoardPurchaseResult
            {
                Status = "PurchaseSelectionSent",
                Message = "Selection sent.",
                Candidate = CreateCandidate(),
            },
            now,
            TimeSpan.FromSeconds(15));
        controller.ScheduleNextMonitor(now, TimeSpan.Zero);

        var tick = controller.MonitorPurchase(
            now,
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(15),
            candidate => new MarketBoardPurchaseResult
            {
                Status = "ConfirmationSubmitted",
                Message = $"Submitted {candidate.ListingId}.",
                Candidate = candidate,
            },
            () => new MarketBoardReadResult
            {
                Status = "Ready",
                Message = "Listing is still present.",
                Listings =
                [
                    new MarketBoardLiveListing
                    {
                        ItemId = 5066,
                        WorldName = "Siren",
                        ListingId = "listing-1",
                        RetainerId = "retainer-1",
                        RetainerName = "Darkwinds",
                        UnitPrice = 800,
                        Quantity = 4,
                        IsHq = false,
                    },
                ],
            });

        Assert.True(tick.DidWork);
        Assert.NotNull(tick.ConfirmationResult);
        Assert.NotNull(tick.FreshRead);
        Assert.Equal("WaitingForListingRemoval", tick.Session!.Status);
        Assert.Equal(MarketBoardPurchaseSessionPhase.WaitingForListingRemoval, controller.PurchaseSession?.Phase);
    }

    [Fact]
    public void MonitorPurchase_ListingRemovalPhaseReadsFreshListingsAndCompletes()
    {
        var controller = new MarketBoardAutomationController();
        var now = DateTimeOffset.Parse("2026-06-29T12:00:00Z");
        var candidate = CreateCandidate();
        controller.RecordPurchaseSelection(
            new MarketBoardPurchaseResult
            {
                Status = "PurchaseSelectionSent",
                Message = "Selection sent.",
                Candidate = candidate,
            },
            now,
            TimeSpan.FromSeconds(15));
        controller.RecordConfirmationAttempt(
            new MarketBoardPurchaseResult
            {
                Status = "ConfirmationSubmitted",
                Message = "Submitted.",
                Candidate = candidate,
            },
            now,
            TimeSpan.FromSeconds(15));
        controller.ScheduleNextMonitor(now, TimeSpan.Zero);

        var tick = controller.MonitorPurchase(
            now.AddSeconds(1),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(15),
            _ => throw new InvalidOperationException("Should not confirm once listing removal is being watched."),
            () => new MarketBoardReadResult
            {
                Status = "NoListings",
                Message = "No remaining listings.",
            });

        Assert.True(tick.DidWork);
        Assert.NotNull(tick.FreshRead);
        Assert.NotNull(tick.FreshReadSession);
        Assert.Equal("Completed", tick.Session!.Status);
        Assert.Equal("Completed", controller.PurchaseSession?.Status);
    }

    [Fact]
    public void MonitorPurchase_DoesNotPollBeforeScheduledTime()
    {
        var controller = new MarketBoardAutomationController();
        var now = DateTimeOffset.Parse("2026-06-29T12:00:00Z");
        controller.RecordPurchaseSelection(
            new MarketBoardPurchaseResult
            {
                Status = "PurchaseSelectionSent",
                Message = "Selection sent.",
                Candidate = CreateCandidate(),
            },
            now,
            TimeSpan.FromSeconds(15));
        controller.ScheduleNextMonitor(now, TimeSpan.FromMilliseconds(250));

        var tick = controller.MonitorPurchase(
            now.AddMilliseconds(249),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(15),
            _ => throw new InvalidOperationException("Should not poll before scheduled time."),
            () => throw new InvalidOperationException("Should not poll before scheduled time."));

        Assert.False(tick.DidWork);
        Assert.Equal("WaitingForConfirmation", tick.Session!.Status);
    }

    private static MarketBoardPurchaseCandidate CreateCandidate() =>
        new()
        {
            ItemId = 5066,
            WorldName = "Siren",
            ListingId = "listing-1",
            RetainerId = "retainer-1",
            RetainerName = "Darkwinds",
            UnitPrice = 800,
            Quantity = 4,
            IsHq = false,
        };
}
