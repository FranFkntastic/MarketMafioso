namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketBoardPaginationProbeTests
{
    [Fact]
    public void Evaluate_ReturnsNotTruncatedForCompleteVisibleCache()
    {
        var state = new MarketMafioso.MarketAcquisition.MarketBoardPaginationState(
            ItemId: 5064,
            WorldName: "Siren",
            ReportedListingCount: 40,
            ReadableListingCount: 40,
            ListingCapacity: 100,
            CurrentRequestId: 1,
            NextRequestId: 2);

        var result = MarketMafioso.MarketAcquisition.MarketBoardPaginationProbe.Evaluate(state);

        Assert.Equal("NotTruncated", result.Status);
        Assert.False(result.CanAttemptLiveProbe);
    }

    [Fact]
    public void Evaluate_ReturnsRequestIdsNotCoherentForUnadvancedRequestIds()
    {
        var state = new MarketMafioso.MarketAcquisition.MarketBoardPaginationState(
            ItemId: 5064,
            WorldName: "Siren",
            ReportedListingCount: 180,
            ReadableListingCount: 100,
            ListingCapacity: 100,
            CurrentRequestId: 1,
            NextRequestId: 1);

        var result = MarketMafioso.MarketAcquisition.MarketBoardPaginationProbe.Evaluate(state);

        Assert.Equal("RequestIdsNotCoherent", result.Status);
        Assert.False(result.CanAttemptLiveProbe);
    }

    [Fact]
    public void Evaluate_ReturnsReadyForLiveProbeWhenTruncatedAndRequestIdsAreCoherent()
    {
        var state = new MarketMafioso.MarketAcquisition.MarketBoardPaginationState(
            ItemId: 5064,
            WorldName: "Siren",
            ReportedListingCount: 180,
            ReadableListingCount: 100,
            ListingCapacity: 100,
            CurrentRequestId: 1,
            NextRequestId: 2);

        var result = MarketMafioso.MarketAcquisition.MarketBoardPaginationProbe.Evaluate(state);

        Assert.Equal("ReadyForLiveProbe", result.Status);
        Assert.True(result.CanAttemptLiveProbe);
    }

    [Fact]
    public void EvaluateContinuation_ReturnsAdvancedWhenContinuationIsSameItemAndRequestIdChanges()
    {
        var before = new MarketMafioso.MarketAcquisition.MarketBoardPaginationState(
            ItemId: 5064,
            WorldName: "Siren",
            ReportedListingCount: 180,
            ReadableListingCount: 100,
            ListingCapacity: 100,
            CurrentRequestId: 1,
            NextRequestId: 2);
        var after = new MarketMafioso.MarketAcquisition.MarketBoardPaginationState(
            ItemId: 5064,
            WorldName: "Siren",
            ReportedListingCount: 80,
            ReadableListingCount: 80,
            ListingCapacity: 100,
            CurrentRequestId: 2,
            NextRequestId: 3);

        var result = MarketMafioso.MarketAcquisition.MarketBoardPaginationProbe.EvaluateContinuation(before, after);

        Assert.Equal("Advanced", result.Status);
    }

    [Fact]
    public void EvaluateContinuation_ReturnsWrongContinuationWhenItemChanges()
    {
        var before = new MarketMafioso.MarketAcquisition.MarketBoardPaginationState(
            ItemId: 5064,
            WorldName: "Siren",
            ReportedListingCount: 180,
            ReadableListingCount: 100,
            ListingCapacity: 100,
            CurrentRequestId: 1,
            NextRequestId: 2);
        var after = new MarketMafioso.MarketAcquisition.MarketBoardPaginationState(
            ItemId: 2,
            WorldName: "Siren",
            ReportedListingCount: 80,
            ReadableListingCount: 80,
            ListingCapacity: 100,
            CurrentRequestId: 2,
            NextRequestId: 3);

        var result = MarketMafioso.MarketAcquisition.MarketBoardPaginationProbe.EvaluateContinuation(before, after);

        Assert.Equal("WrongContinuation", result.Status);
    }

    [Fact]
    public void EvaluateContinuation_ReturnsUnchangedWhenRequestIdDoesNotMove()
    {
        var before = new MarketMafioso.MarketAcquisition.MarketBoardPaginationState(
            ItemId: 5064,
            WorldName: "Siren",
            ReportedListingCount: 180,
            ReadableListingCount: 100,
            ListingCapacity: 100,
            CurrentRequestId: 1,
            NextRequestId: 2);
        var after = before with
        {
            ReportedListingCount = 180,
            ReadableListingCount = 100,
        };

        var result = MarketMafioso.MarketAcquisition.MarketBoardPaginationProbe.EvaluateContinuation(before, after);

        Assert.Equal("Unchanged", result.Status);
    }
}
