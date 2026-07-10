using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteOperationExecutorTests
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.Parse("2026-07-10T12:00:00Z");

    [Fact]
    public void Begin_CreatesBoundedInspectableOperation()
    {
        var executor = new MarketAcquisitionRouteOperationExecutor();

        var snapshot = executor.Begin(CreateStart());

        Assert.Equal("search-1", snapshot.OperationId);
        Assert.Equal(MarketAcquisitionRouteOperationKind.ItemSearch, snapshot.Kind);
        Assert.Equal(MarketAcquisitionRouteOperationPhase.Started, snapshot.Phase);
        Assert.Equal(MarketAcquisitionRouteOperationDisposition.Pending, snapshot.Disposition);
        Assert.Equal(StartedAt.AddSeconds(15), snapshot.DeadlineUtc);
        Assert.Equal(16_000, snapshot.DeadlineMonotonicMilliseconds);
        Assert.Same(snapshot, executor.ActiveSnapshot);
    }

    [Fact]
    public void Observe_PendingThenSucceeded_CompletesAndClearsActiveOperation()
    {
        var executor = new MarketAcquisitionRouteOperationExecutor();
        executor.Begin(CreateStart());

        var pending = executor.Observe(CreateObservation(MarketAcquisitionRouteOperationDisposition.Pending), StartedAt.AddSeconds(1), 2_000);
        var succeeded = executor.Observe(CreateObservation(MarketAcquisitionRouteOperationDisposition.Succeeded), StartedAt.AddSeconds(2), 3_000);

        Assert.True(pending.Accepted);
        Assert.Equal(MarketAcquisitionRouteOperationPhase.Waiting, pending.Snapshot?.Phase);
        Assert.True(succeeded.Accepted);
        Assert.Equal(MarketAcquisitionRouteOperationDisposition.Succeeded, succeeded.Snapshot?.Disposition);
        Assert.True(succeeded.Snapshot?.IsTerminal);
        Assert.Null(executor.ActiveSnapshot);
    }

    [Fact]
    public void CheckDeadline_FailsOperationAtDeadline()
    {
        var executor = new MarketAcquisitionRouteOperationExecutor();
        executor.Begin(CreateStart());

        var result = executor.CheckDeadline(StartedAt.AddSeconds(15), 16_000);

        Assert.True(result.Accepted);
        Assert.Equal(MarketAcquisitionRouteOperationPhase.TimedOut, result.Snapshot?.Phase);
        Assert.Equal(MarketAcquisitionRouteOperationDisposition.Failed, result.Snapshot?.Disposition);
        Assert.Null(executor.ActiveSnapshot);
    }

    [Fact]
    public void Cancel_IsIdempotentAndFencesLateObservation()
    {
        var executor = new MarketAcquisitionRouteOperationExecutor();
        executor.Begin(CreateStart());

        var cancelled = executor.Cancel(StartedAt.AddSeconds(1), 2_000, "Route stopped.");
        var duplicateCancel = executor.Cancel(StartedAt.AddSeconds(2), 3_000, "Route stopped again.");
        var late = executor.Observe(CreateObservation(MarketAcquisitionRouteOperationDisposition.Succeeded), StartedAt.AddSeconds(3), 4_000);

        Assert.Equal(MarketAcquisitionRouteOperationDisposition.Cancelled, cancelled.Snapshot?.Disposition);
        Assert.False(duplicateCancel.Accepted);
        Assert.False(duplicateCancel.IsLateOrMismatched);
        Assert.False(late.Accepted);
        Assert.True(late.IsLateOrMismatched);
        Assert.Equal(cancelled.Snapshot, late.Snapshot);
    }

    [Fact]
    public void Observe_MismatchedOperationId_DoesNotMutateActiveOperation()
    {
        var executor = new MarketAcquisitionRouteOperationExecutor();
        var active = executor.Begin(CreateStart());

        var result = executor.Observe(
            CreateObservation(MarketAcquisitionRouteOperationDisposition.Succeeded) with { OperationId = "search-other" },
            StartedAt.AddSeconds(1),
            2_000);

        Assert.False(result.Accepted);
        Assert.True(result.IsLateOrMismatched);
        Assert.Equal(active, executor.ActiveSnapshot);
    }

    [Fact]
    public void Observe_UnknownDisposition_FailsClosed()
    {
        var executor = new MarketAcquisitionRouteOperationExecutor();
        executor.Begin(CreateStart());

        var result = executor.Observe(
            CreateObservation((MarketAcquisitionRouteOperationDisposition)999),
            StartedAt.AddSeconds(1),
            2_000);

        Assert.True(result.Accepted);
        Assert.Equal(MarketAcquisitionRouteOperationDisposition.Failed, result.Snapshot?.Disposition);
        Assert.Contains("Unsupported", result.Message, StringComparison.Ordinal);
        Assert.Null(executor.ActiveSnapshot);
    }

    [Fact]
    public void Begin_WhileAnotherOperationIsActive_Throws()
    {
        var executor = new MarketAcquisitionRouteOperationExecutor();
        executor.Begin(CreateStart());

        Assert.Throws<InvalidOperationException>(() => executor.Begin(CreateStart() with { OperationId = "search-2" }));
    }

    [Fact]
    public void Observe_TimeMovesBackward_Throws()
    {
        var executor = new MarketAcquisitionRouteOperationExecutor();
        executor.Begin(CreateStart());
        executor.Observe(CreateObservation(MarketAcquisitionRouteOperationDisposition.Pending), StartedAt.AddSeconds(2), 3_000);

        Assert.Throws<ArgumentOutOfRangeException>(() => executor.Observe(
            CreateObservation(MarketAcquisitionRouteOperationDisposition.Pending),
            StartedAt.AddSeconds(1),
            2_000));
    }

    [Fact]
    public void Begin_CopiesMutableContextIntoSnapshot()
    {
        var context = new Dictionary<string, string?>
        {
            ["itemId"] = "7017",
        };
        var executor = new MarketAcquisitionRouteOperationExecutor();

        var snapshot = executor.Begin(CreateStart() with { Context = context });
        context["itemId"] = "9999";

        Assert.Equal("7017", snapshot.Context["itemId"]);
    }

    [Fact]
    public void Begin_RejectsReusedOperationIdAfterCancellation()
    {
        var executor = new MarketAcquisitionRouteOperationExecutor();
        executor.Begin(CreateStart());
        executor.Cancel(StartedAt.AddSeconds(1), 2_000, "Route stopped.");

        Assert.Throws<InvalidOperationException>(() => executor.Begin(CreateStart()));
    }

    [Fact]
    public void CheckDeadline_UsesConfiguredIndeterminateDisposition()
    {
        var executor = new MarketAcquisitionRouteOperationExecutor();
        executor.Begin(CreateStart() with
        {
            Kind = MarketAcquisitionRouteOperationKind.PurchaseConfirmation,
            TimeoutDisposition = MarketAcquisitionRouteOperationDisposition.Indeterminate,
            TimeoutMessage = "Purchase confirmation is indeterminate.",
        });

        var result = executor.CheckDeadline(StartedAt.AddMinutes(5), 16_000);

        Assert.Equal(MarketAcquisitionRouteOperationDisposition.Indeterminate, result.Snapshot?.Disposition);
        Assert.Equal("Purchase confirmation is indeterminate.", result.Message);
    }

    [Fact]
    public void Deadline_UsesMonotonicTimeWhenWallClockMoves()
    {
        var executor = new MarketAcquisitionRouteOperationExecutor();
        executor.Begin(CreateStart());

        var backwardWallClock = executor.Observe(
            CreateObservation(MarketAcquisitionRouteOperationDisposition.Pending),
            StartedAt.AddHours(-1),
            2_000);
        var forwardWallClockBeforeDeadline = executor.CheckDeadline(StartedAt.AddHours(2), 15_999);
        var atDeadline = executor.CheckDeadline(StartedAt.AddHours(-2), 16_000);

        Assert.True(backwardWallClock.Accepted);
        Assert.Equal(MarketAcquisitionRouteOperationDisposition.Pending, forwardWallClockBeforeDeadline.Snapshot?.Disposition);
        Assert.Equal(MarketAcquisitionRouteOperationPhase.TimedOut, atDeadline.Snapshot?.Phase);
    }

    private static MarketAcquisitionRouteOperationStart CreateStart() => new()
    {
        OperationId = "search-1",
        Kind = MarketAcquisitionRouteOperationKind.ItemSearch,
        StartedAtUtc = StartedAt,
        StartedAtMonotonicMilliseconds = 1_000,
        Timeout = TimeSpan.FromSeconds(15),
        TimeoutDisposition = MarketAcquisitionRouteOperationDisposition.Failed,
        TimeoutMessage = "Item search timed out.",
        Attempt = 1,
        Context = new Dictionary<string, string?>
        {
            ["itemId"] = "7017",
        },
    };

    private static MarketAcquisitionRouteOperationObservation CreateObservation(
        MarketAcquisitionRouteOperationDisposition disposition) => new()
        {
            OperationId = "search-1",
            Disposition = disposition,
            Message = disposition.ToString(),
        };
}
