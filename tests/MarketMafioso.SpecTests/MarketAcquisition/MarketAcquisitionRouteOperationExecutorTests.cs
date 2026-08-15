using System;
using MarketMafioso.MarketAcquisition;
using Xunit;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class MarketAcquisitionRouteOperationExecutorTests
{
    [Fact]
    public void OperationWithoutTimeout_RemainsActiveUntilItsOwnerCompletesIt()
    {
        var startedAt = DateTimeOffset.Parse("2026-08-15T04:00:00Z");
        var executor = new MarketAcquisitionRouteOperationExecutor();
        var started = executor.Begin(new MarketAcquisitionRouteOperationStart
        {
            OperationId = "route:item-search:1",
            Kind = MarketAcquisitionRouteOperationKind.ItemSearch,
            StartedAtUtc = startedAt,
            StartedAtMonotonicMilliseconds = 1_000,
            Timeout = null,
            TimeoutDisposition = MarketAcquisitionRouteOperationDisposition.Failed,
            TimeoutMessage = "The progress-aware browse gate owns item-search liveness.",
        });

        Assert.Null(started.DeadlineUtc);
        Assert.Null(started.DeadlineMonotonicMilliseconds);

        var afterOneDay = executor.CheckDeadline(startedAt.AddDays(1), 86_401_000);

        Assert.True(afterOneDay.Accepted);
        Assert.NotNull(afterOneDay.Snapshot);
        Assert.Equal(MarketAcquisitionRouteOperationDisposition.Pending, afterOneDay.Snapshot!.Disposition);
        Assert.Same(afterOneDay.Snapshot, executor.ActiveSnapshot);
    }
}
