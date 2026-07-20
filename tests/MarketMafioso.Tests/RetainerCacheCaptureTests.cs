using FFXIVClientStructs.FFXIV.Client.Game;
using MarketMafioso.RetainerRestock;

namespace MarketMafioso.Tests;

public sealed class RetainerCacheCaptureTests
{
    private static readonly InventoryType[] OrdinaryPages =
    [
        InventoryType.RetainerPage1,
        InventoryType.RetainerPage2,
        InventoryType.RetainerPage3,
        InventoryType.RetainerPage4,
        InventoryType.RetainerPage5,
        InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    [Fact]
    public void EvaluateCapture_AllOrdinaryPagesLoaded_AllowsReplacementWithoutCrystals()
    {
        var capture = Capture(OrdinaryPages);

        var decision = RetainerCacheManager.EvaluateCapture(capture);

        Assert.True(decision.CanReplace);
        Assert.Empty(decision.MissingRequiredContainers);
    }

    [Fact]
    public void EvaluateCapture_MissingOrdinaryPage_PreservesPriorEvidence()
    {
        var capture = Capture(OrdinaryPages.Where(page => page != InventoryType.RetainerPage4));

        var decision = RetainerCacheManager.EvaluateCapture(capture);

        Assert.False(decision.CanReplace);
        Assert.Equal([InventoryType.RetainerPage4], decision.MissingRequiredContainers);
    }

    [Fact]
    public void EvaluateReceipt_RequiresNewerMatchingPersistedReceipt()
    {
        var checkpoint = new RetainerCaptureCheckpoint(4);
        var older = Receipt(4, 100, RetainerCaptureOutcome.Persisted);
        var persisted = Receipt(5, 100, RetainerCaptureOutcome.Persisted);
        var otherRetainer = Receipt(5, 101, RetainerCaptureOutcome.Persisted);

        Assert.Equal(RetainerCaptureReceiptMatch.Pending, RetainerCacheManager.EvaluateReceipt(older, 100, checkpoint));
        Assert.Equal(RetainerCaptureReceiptMatch.Persisted, RetainerCacheManager.EvaluateReceipt(persisted, 100, checkpoint));
        Assert.Equal(RetainerCaptureReceiptMatch.IdentityMismatch, RetainerCacheManager.EvaluateReceipt(otherRetainer, 100, checkpoint));
    }

    [Fact]
    public void EvaluateReceipt_AtomicCaptureWaitSnapshot_DoesNotFenceOutTheCloseReceipt()
    {
        var session = new RetainerCaptureSession(100, "Eris-morne", new RetainerOwnerScope("Owner", "World"));
        var beforeClose = new RetainerCaptureWaitSnapshot(session, new RetainerCaptureCheckpoint(4));
        var closeReceipt = Receipt(5, 100, RetainerCaptureOutcome.Persisted);

        // Reading the session before close but the checkpoint after close would miss this
        // receipt. The single snapshot records both values before the close command.
        var separatelyReadAfterClose = beforeClose with { Checkpoint = new RetainerCaptureCheckpoint(5) };

        Assert.Equal(RetainerCaptureReceiptMatch.Persisted, RetainerCacheManager.EvaluateReceipt(closeReceipt, beforeClose));
        Assert.Equal(RetainerCaptureReceiptMatch.Pending, RetainerCacheManager.EvaluateReceipt(closeReceipt, separatelyReadAfterClose));
    }

    [Fact]
    public void BuildCachedRetainer_UnobservedOptionalContainers_PreservesPriorEvidence()
    {
        var previous = new CachedRetainer
        {
            RetainerId = 100,
            Gil = 70_000,
            MarketListings =
            [
                new CachedMarketListing { ItemId = 5114, Quantity = 12, UnitPrice = 99 },
            ],
        };
        var capture = Capture(OrdinaryPages);

        var cached = RetainerCacheManager.BuildCachedRetainer(Session(), capture, previous);

        Assert.False(capture.HasObservedGil);
        Assert.False(capture.HasObservedMarketListings);
        Assert.Equal((ulong)70_000, cached.Gil);
        var listing = Assert.Single(cached.MarketListings);
        Assert.Equal((uint)5114, listing.ItemId);
        Assert.Equal((uint)99, listing.UnitPrice);
        Assert.NotSame(previous.MarketListings[0], listing);
    }

    [Fact]
    public void BuildCachedRetainer_ObservedEmptyOptionalContainers_ReplacePriorEvidence()
    {
        var previous = new CachedRetainer
        {
            RetainerId = 100,
            Gil = 70_000,
            MarketListings = [new CachedMarketListing { ItemId = 5114, Quantity = 12 }],
        };
        var capture = new RetainerInventoryCaptureResult(
            [],
            OrdinaryPages.Append(InventoryType.RetainerGil).Append(InventoryType.RetainerMarket).ToHashSet(),
            OrdinaryPages.Append(InventoryType.RetainerGil).Append(InventoryType.RetainerMarket).ToHashSet(),
            ObservedGil: 0,
            ObservedMarketListings: []);

        var cached = RetainerCacheManager.BuildCachedRetainer(Session(), capture, previous);

        Assert.True(capture.HasObservedGil);
        Assert.True(capture.HasObservedMarketListings);
        Assert.Equal((ulong)0, cached.Gil);
        Assert.Empty(cached.MarketListings);
    }

    [Fact]
    public void BuildCachedRetainer_NewRetainerWithUnobservedOptionalContainers_UsesSafeDefaults()
    {
        var capture = Capture(OrdinaryPages);

        var cached = RetainerCacheManager.BuildCachedRetainer(Session(), capture, previous: null);

        Assert.False(capture.HasObservedGil);
        Assert.False(capture.HasObservedMarketListings);
        Assert.Equal((ulong)0, cached.Gil);
        Assert.Empty(cached.MarketListings);
    }

    [Fact]
    public void PublishSubscribersSafely_ContinuesAfterThrowingSubscriberAndContainsDiagnosticFailure()
    {
        var received = 0;
        var diagnosticAttempts = 0;
        Action<RetainerCaptureReceipt> throwing = _ => throw new InvalidOperationException("subscriber failure");
        Action<RetainerCaptureReceipt> succeeding = _ => received++;

        var exception = Record.Exception(() => RetainerCacheManager.PublishSubscribersSafely(
            throwing + succeeding,
            Receipt(5, 100, RetainerCaptureOutcome.Persisted),
            _ =>
            {
                diagnosticAttempts++;
                throw new InvalidOperationException("diagnostic failure");
            }));

        Assert.Null(exception);
        Assert.Equal(1, diagnosticAttempts);
        Assert.Equal(1, received);
    }

    [Fact]
    public void AutoRetainerGate_ReportsIncompleteAndPersistenceFailuresBeforeRelease()
    {
        var checkpoint = new RetainerCaptureCheckpoint(9);
        var incomplete = Receipt(10, 100, RetainerCaptureOutcome.Incomplete, "Missing required pages: RetainerPage7.");
        var persistenceFailure = Receipt(10, 100, RetainerCaptureOutcome.PersistenceFailed, "Cache file write failed.");

        var incompleteFailure = AutoRetainerRefreshService.GetCaptureReceiptGateFailure(incomplete, 100, checkpoint);
        var persistenceFailureMessage = AutoRetainerRefreshService.GetCaptureReceiptGateFailure(persistenceFailure, 100, checkpoint);

        Assert.Contains("incomplete", incompleteFailure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not be persisted", persistenceFailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static RetainerInventoryCaptureResult Capture(IEnumerable<InventoryType> loaded) =>
        new([], OrdinaryPages.ToHashSet(), loaded.ToHashSet());

    private static RetainerCaptureSession Session() =>
        new(100, "Eris-morne", new RetainerOwnerScope("Owner", "World"));

    private static RetainerCaptureReceipt Receipt(
        long checkpoint,
        ulong retainerId,
        RetainerCaptureOutcome outcome,
        string message = "Test receipt.") =>
        new(
            new RetainerCaptureCheckpoint(checkpoint),
            retainerId,
            new RetainerOwnerScope("Owner", "World"),
            outcome,
            message,
            DateTime.UtcNow);
}
