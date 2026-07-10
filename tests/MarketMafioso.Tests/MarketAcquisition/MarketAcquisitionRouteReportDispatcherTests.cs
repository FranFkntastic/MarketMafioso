using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRouteReportDispatcherTests
{
    [Fact]
    public async Task Reports_AreSentInSequence()
    {
        var lineStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new List<string>();
        var reporter = new ScriptedReporter
        {
            OnLineProgress = async (_, token) =>
            {
                operations.Add("line");
                lineStarted.SetResult();
                await releaseLine.Task.WaitAsync(token);
            },
            OnRouteProgress = (report, _) =>
            {
                operations.Add("route");
                return Task.FromResult(ProgressOutcome());
            },
        };
        using var dispatcher = CreateDispatcher(reporter, out var claim);
        dispatcher.BeginSession(claim);

        dispatcher.EnqueueLineProgress(LineReport());
        dispatcher.EnqueueRouteProgress(RouteReport());
        await lineStarted.Task;

        Assert.Equal(["line"], operations);
        releaseLine.SetResult();
        await dispatcher.DrainAsync();
        Assert.Equal(["line", "route"], operations);
    }

    [Fact]
    public async Task FailedRouteReport_CanRetrySameStateLater()
    {
        var attempts = 0;
        var shouldFail = true;
        var reporter = new ScriptedReporter
        {
            OnRouteProgress = (_, _) =>
            {
                attempts++;
                return shouldFail
                    ? Task.FromException<MarketAcquisitionRouteProgressReportOutcome>(new IOException("offline"))
                    : Task.FromResult(ProgressOutcome());
            },
        };
        using var dispatcher = CreateDispatcher(reporter, out var claim);
        dispatcher.BeginSession(claim);

        dispatcher.EnqueueRouteProgress(RouteReport());
        await dispatcher.DrainAsync();
        Assert.Equal(3, attempts);

        shouldFail = false;
        dispatcher.EnqueueRouteProgress(RouteReport());
        await dispatcher.DrainAsync();
        Assert.Equal(4, attempts);
    }

    [Fact]
    public async Task ResetSession_CancelsOldReportsAndKeepsQueueUsable()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancelled = false;
        var completed = 0;
        var reporter = new ScriptedReporter
        {
            OnLineProgress = async (_, token) =>
            {
                if (completed > 0)
                    return;

                firstStarted.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    firstCancelled = true;
                    throw;
                }
            },
        };
        using var dispatcher = CreateDispatcher(reporter, out var claim);
        dispatcher.BeginSession(claim);
        dispatcher.EnqueueLineProgress(LineReport());
        await firstStarted.Task;

        dispatcher.ResetSession();
        await dispatcher.DrainAsync();
        Assert.True(firstCancelled);

        completed = 1;
        dispatcher.BeginSession(claim);
        dispatcher.EnqueueLineProgress(LineReport());
        await dispatcher.DrainAsync();
    }

    [Fact]
    public async Task OldSessionFailure_DoesNotOverwriteNewSessionStatus()
    {
        var reportStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReport = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var statuses = new List<string>();
        var reporter = new ScriptedReporter
        {
            OnLineProgress = async (_, _) =>
            {
                reportStarted.TrySetResult();
                await releaseReport.Task;
                throw new IOException("old session failed late");
            },
        };
        using var dispatcher = CreateDispatcher(reporter, out var claim, statuses.Add);
        dispatcher.BeginSession(claim);
        dispatcher.EnqueueLineProgress(LineReport());
        await reportStarted.Task;

        dispatcher.ResetSession();
        dispatcher.BeginSession(claim);
        releaseReport.SetResult();
        await dispatcher.DrainAsync();

        Assert.Empty(statuses);
    }

    private static MarketAcquisitionRouteReportDispatcher CreateDispatcher(
        IMarketAcquisitionRouteReporter reporter,
        out MarketAcquisitionClaimView claim,
        Action<string>? setStatus = null)
    {
        claim = MarketAcquisitionRouteEngineTestData.AcceptedClaim();
        MarketAcquisitionClaimView? currentClaim = claim;
        var lifecycle = new MarketAcquisitionClaimLifecycleController(
            new Configuration(),
            () => currentClaim,
            value => currentClaim = value,
            () => null,
            () => null,
            () => { },
            setStatus ?? (_ => { }),
            () => string.Empty,
            () => { });
        return new MarketAcquisitionRouteReportDispatcher(reporter, lifecycle, new ImmediateRouteCallbackDispatcher());
    }

    private static MarketAcquisitionRouteProgressReport RouteReport() =>
        new("request", "claim", "Running", "attempt", 2, "stop", "Maduin", "Purchasing", "Buying");

    private static MarketAcquisitionLineProgressReport LineReport() =>
        new("request", "claim", "attempt", 1, "line", "Darksteel Ore", "Running", 0, 0, "Buying", null);

    private static MarketAcquisitionRouteProgressReportOutcome ProgressOutcome() =>
        new("progress", new MarketAcquisitionRequestView { Status = "Running" });

    private sealed class ScriptedReporter : IMarketAcquisitionRouteReporter
    {
        public bool CanReport => true;
        public Func<MarketAcquisitionRouteProgressReport, CancellationToken, Task<MarketAcquisitionRouteProgressReportOutcome>> OnRouteProgress { get; init; } =
            (_, _) => Task.FromResult(ProgressOutcome());
        public Func<MarketAcquisitionLineProgressReport, CancellationToken, Task> OnLineProgress { get; init; } =
            (_, _) => Task.CompletedTask;

        public Task<MarketAcquisitionRouteProgressReportOutcome> ReportRouteProgressAsync(
            MarketAcquisitionRouteProgressReport report,
            CancellationToken cancellationToken) => OnRouteProgress(report, cancellationToken);

        public Task ReportPurchaseAuditAsync(
            MarketAcquisitionPurchaseAuditReport report,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReportLineProgressAsync(
            MarketAcquisitionLineProgressReport report,
            CancellationToken cancellationToken) => OnLineProgress(report, cancellationToken);
    }
}
