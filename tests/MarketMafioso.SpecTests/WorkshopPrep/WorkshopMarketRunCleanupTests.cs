using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.SpecTests.WorkshopPrep;

// Market-run jobs are removed automatically. Removing a share that still owes its assembler materials
// erases the only record of that debt, and nothing in game would say it happened. This pins the
// fail-closed cases: only an accepted, fully handed-over, fully reported share counts as finished.
public sealed class WorkshopMarketRunCleanupTests
{
    private static readonly WorkshopMaterialRequirement[] Requirements =
    [
        new(1, "Cobalt Ingot", 0, 100),
        new(2, "Mythrite Rivets", 0, 40),
    ];

    [Fact]
    public void AcceptedShareWithEverythingHandedOverAndReportedIsFinished()
    {
        Assert.NotNull(WorkshopMarketRunCleanup.FinishedReason(Facts(FinishedShare())));
    }

    [Fact]
    public void ShareThatStillOwesMaterialsIsKept()
    {
        var job = FinishedShare();
        job.HandedOver[1].Quantity = 39;
        Assert.Null(WorkshopMarketRunCleanup.FinishedReason(Facts(job)));

        // Counted holdings may cover the rest, but only from the frozen baseline.
        job.HandoverBaseline = [new() { ItemId = 2, Quantity = 1 }];
        Assert.NotNull(WorkshopMarketRunCleanup.FinishedReason(Facts(job, countTheirs: true)));
        Assert.Null(WorkshopMarketRunCleanup.FinishedReason(Facts(job, countTheirs: false)));
    }

    [Fact]
    public void ShareWithNothingHandedOverIsKeptEvenWhenNothingIsDue()
    {
        var job = FinishedShare();
        job.HandedOver.Clear();
        job.HandoverBaseline = [new() { ItemId = 1, Quantity = 100 }, new() { ItemId = 2, Quantity = 40 }];
        Assert.Null(WorkshopMarketRunCleanup.FinishedReason(Facts(job, countTheirs: true)));
    }

    [Fact]
    public void UnsettledShareStatesAreKept()
    {
        foreach (var status in new[]
                 {
                     WorkshopSavedJobHandoffStatus.Offered,
                     WorkshopSavedJobHandoffStatus.Delivering,
                     WorkshopSavedJobHandoffStatus.NotDelivered,
                     WorkshopSavedJobHandoffStatus.Declined,
                     WorkshopSavedJobHandoffStatus.Withdrawn,
                 })
            Assert.Null(WorkshopMarketRunCleanup.FinishedReason(Facts(FinishedShare()) with { Status = status }));

        var pending = FinishedShare();
        pending.HandoverReportPending = true;
        Assert.Null(WorkshopMarketRunCleanup.FinishedReason(Facts(pending)));

        var refused = FinishedShare();
        refused.HandoverReportRefusal = "Handoff not found.";
        Assert.Null(WorkshopMarketRunCleanup.FinishedReason(Facts(refused)));

        var trading = FinishedShare();
        trading.HandoverInFlight = new WorkshopHandoverInFlight { Items = [new() { ItemId = 1, Quantity = 5 }] };
        Assert.Null(WorkshopMarketRunCleanup.FinishedReason(Facts(trading)));

        var bound = FinishedShare();
        Assert.Null(WorkshopMarketRunCleanup.FinishedReason(Facts(bound) with { TradeQueueBoundShareId = bound.Id }));
        Assert.Null(WorkshopMarketRunCleanup.FinishedReason(Facts(FinishedShare()) with { HandoffBusy = true }));
        Assert.Null(WorkshopMarketRunCleanup.FinishedReason(Facts(FinishedShare()) with { IsSender = false }));
        Assert.Null(WorkshopMarketRunCleanup.FinishedReason(Facts(FinishedShare()) with { Requirements = [] }));
    }

    [Fact]
    public void JobsWithoutProofOfCompletionAreKept()
    {
        var manual = FinishedShare();
        manual.Origin = WorkshopSavedJobOrigin.Manual;
        Assert.Null(WorkshopMarketRunCleanup.FinishedReason(Facts(manual)));

        // A job this character assembles itself has no completion record.
        var own = FinishedShare();
        own.HandoffTargetName = null;
        own.HandoffTargetWorld = null;
        Assert.Null(WorkshopMarketRunCleanup.FinishedReason(Facts(own)));

        var loaded = FinishedShare();
        Assert.Null(WorkshopMarketRunCleanup.FinishedReason(Facts(loaded) with { ActiveJobId = loaded.Id, PrepQueueCount = 1 }));
    }

    private static WorkshopFrozenQueue FinishedShare() => new()
    {
        Name = "Shark-class Pressure Hull — Alys",
        Origin = WorkshopSavedJobOrigin.MarketRun,
        Items = [new() { WorkshopItemId = 10, Quantity = 1 }],
        HandoffTargetName = "Alys Vane",
        HandoffTargetWorld = "Lamia",
        HandoffSenderName = "Fran Tastic",
        HandoffSenderWorld = "Lamia",
        HandoffId = Guid.NewGuid(),
        HandoffState = WorkshopHandoffState.Accepted,
        HandedOver = [new() { ItemId = 1, Quantity = 100 }, new() { ItemId = 2, Quantity = 40 }],
        HandoverBaseline = [],
    };

    private static WorkshopMarketRunJobFacts Facts(WorkshopFrozenQueue job, bool countTheirs = false) => new(
        job,
        WorkshopSavedJobHandoffStatus.Accepted,
        IsSender: true,
        Requirements,
        countTheirs,
        IncludeTheirRetainers: false,
        ActiveJobId: null,
        PrepQueueCount: 0,
        TradeQueueBoundShareId: null,
        HandoffBusy: false);
}
