using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class MarketPurchaseEvidenceRoutePreflightTests
{
    [Fact]
    public void NoEvidenceAllowsRouteExecution()
    {
        var result = MarketPurchaseEvidenceRoutePreflight.Evaluate(null, false, Time(1));

        Assert.True(result.CanExecute);
        Assert.Null(result.BlockReason);
    }

    [Fact]
    public void ActivePurchaseSessionMayContinueToProcessItsEvidence()
    {
        var result = MarketPurchaseEvidenceRoutePreflight.Evaluate(
            new PendingMarketPurchase(Intent()),
            purchaseSessionActive: true,
            nowUtc: Time(1));

        Assert.True(result.CanExecute);
    }

    [Fact]
    public void OrphanedPendingIntentBlocksRouteBeforeItsDeadline()
    {
        var result = MarketPurchaseEvidenceRoutePreflight.Evaluate(
            new PendingMarketPurchase(Intent()),
            purchaseSessionActive: false,
            nowUtc: Time(2));

        Assert.False(result.CanExecute);
        Assert.Contains("still awaiting server evidence", result.BlockReason);
        Assert.Contains("listing-1", result.BlockReason);
    }

    [Fact]
    public void ExpiredPendingIntentIsShownAsReconciliationRequired()
    {
        var result = MarketPurchaseEvidenceRoutePreflight.Evaluate(
            new PendingMarketPurchase(Intent()),
            purchaseSessionActive: false,
            nowUtc: Time(6));

        Assert.False(result.CanExecute);
        Assert.Contains("expired without server evidence", result.BlockReason);
    }

    [Theory]
    [InlineData(typeof(ConfirmedMarketPurchase))]
    [InlineData(typeof(TimedOutIndeterminateMarketPurchase))]
    [InlineData(typeof(ConflictingMarketPurchasePacket))]
    public void TerminalEvidenceBlocksOrphanedRoute(Type terminalType)
    {
        MarketPurchaseEvidenceState terminal = terminalType == typeof(ConfirmedMarketPurchase)
            ? new ConfirmedMarketPurchase(Intent(), Observation())
            : terminalType == typeof(TimedOutIndeterminateMarketPurchase)
                ? new TimedOutIndeterminateMarketPurchase(Intent(), Time(6))
                : new ConflictingMarketPurchasePacket(Intent(), Observation());

        var result = MarketPurchaseEvidenceRoutePreflight.Evaluate(terminal, false, Time(7));

        Assert.False(result.CanExecute);
        Assert.Contains("route execution can continue", result.BlockReason);
    }

    private static MarketPurchaseIntent Intent() => new()
    {
        IntentId = "intent-1",
        RouteId = "request-1",
        RouteRunId = "run-1",
        AttemptId = "attempt-1",
        LineId = "line-1",
        ItemId = 42,
        IsHighQuality = false,
        Quantity = 3,
        ListingId = "listing-1",
        RetainerId = "retainer-1",
        UnitPrice = 99,
        TotalGil = 297,
        WorldId = 57,
        WorldName = "Siren",
        ArmedAtUtc = Time(1),
        DeadlineUtc = Time(5),
        PacketFloor = new MarketPurchasePacketPosition { Epoch = "epoch-a/1", Sequence = 0 },
    };

    private static MarketPurchasePacketObservation Observation() => new()
    {
        Position = new MarketPurchasePacketPosition { Epoch = "epoch-a/1", Sequence = 1 },
        ObservedAtUtc = Time(2),
        RawCatalogId = 42,
        ItemId = 42,
        IsHighQuality = false,
        Quantity = 3,
    };

    private static DateTimeOffset Time(int minute) => DateTimeOffset.UnixEpoch.AddMinutes(minute);
}
