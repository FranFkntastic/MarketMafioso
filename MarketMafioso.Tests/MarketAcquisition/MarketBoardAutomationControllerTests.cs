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
