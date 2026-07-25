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
        dispatcher.RetryPendingReports();
        await dispatcher.DrainAsync();
        Assert.Equal(4, attempts);
    }

    [Fact]
    public async Task DuplicateRouteState_QueuesOneDurableEntryWhileFirstReportIsBlocked()
    {
        var reportStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReport = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outbox = new VolatileMarketAcquisitionReportOutbox();
        var reporter = new ScriptedReporter
        {
            OnRouteProgress = async (_, token) =>
            {
                reportStarted.TrySetResult();
                await releaseReport.Task.WaitAsync(token);
                return ProgressOutcome();
            },
        };
        using var dispatcher = CreateDispatcher(reporter, out var claim, outbox: outbox);
        dispatcher.BeginSession(claim);

        dispatcher.EnqueueRouteProgress(RouteReport(sequence: 2));
        await reportStarted.Task;
        dispatcher.EnqueueRouteProgress(RouteReport(sequence: 3));

        Assert.Single(outbox.Snapshot());
        releaseReport.SetResult();
        await dispatcher.DrainAsync();
        Assert.Empty(outbox.Snapshot());
    }

    [Fact]
    public async Task DuplicateRouteState_DoesNotRewriteFileOutbox()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mmf-outbox-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "outbox.json");
        var reportStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReport = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var reporter = new ScriptedReporter
            {
                OnRouteProgress = async (_, token) =>
                {
                    reportStarted.TrySetResult();
                    await releaseReport.Task.WaitAsync(token);
                    return ProgressOutcome();
                },
            };
            var outbox = new FileMarketAcquisitionReportOutbox(path);
            using var dispatcher = CreateDispatcher(reporter, out var claim, outbox: outbox);
            dispatcher.BeginSession(claim);

            dispatcher.EnqueueRouteProgress(RouteReport(sequence: 2));
            await reportStarted.Task;
            var writtenAtUtc = File.GetLastWriteTimeUtc(path);
            var length = new FileInfo(path).Length;

            for (var sequence = 3; sequence <= 100; sequence++)
                dispatcher.EnqueueRouteProgress(RouteReport(sequence));

            Assert.Single(outbox.Snapshot());
            Assert.Equal(length, new FileInfo(path).Length);
            Assert.Equal(writtenAtUtc, File.GetLastWriteTimeUtc(path));
            Assert.False(File.Exists(path + ".bak"));

            releaseReport.SetResult();
            await dispatcher.DrainAsync();
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Constructor_CompactsPreviouslyPersistedDuplicateRouteStates()
    {
        var releaseReport = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outbox = new VolatileMarketAcquisitionReportOutbox();
        var claim = MarketAcquisitionRouteEngineTestData.AcceptedClaim();
        var first = RouteReport(sequence: 2);
        var second = RouteReport(sequence: 3);
        var routeKey = $"{first.RequestId}|{first.RouteState}|{first.RouteStopId}|{first.ActiveWorld}|{first.Phase}|{first.Message}";
        outbox.Put("route|request|attempt|2", "route-progress.v1", new { Report = first, Claim = claim, RouteKey = routeKey });
        outbox.Put("route|request|attempt|3", "route-progress.v1", new { Report = second, Claim = claim, RouteKey = routeKey });
        var reporter = new ScriptedReporter
        {
            OnRouteProgress = async (_, token) =>
            {
                await releaseReport.Task.WaitAsync(token);
                return ProgressOutcome();
            },
        };

        using var dispatcher = CreateDispatcher(reporter, out _, outbox: outbox);

        Assert.Single(outbox.Snapshot());
        releaseReport.SetResult();
        await dispatcher.DrainAsync();
    }

    [Fact]
    public async Task ResetSession_DoesNotDiscardAnInFlightDurableReport()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reporter = new ScriptedReporter
        {
            OnLineProgress = async (_, token) =>
            {
                firstStarted.SetResult();
                await releaseLine.Task.WaitAsync(token);
            },
        };
        using var dispatcher = CreateDispatcher(reporter, out var claim);
        dispatcher.BeginSession(claim);
        dispatcher.EnqueueLineProgress(LineReport());
        await firstStarted.Task;

        dispatcher.ResetSession();
        releaseLine.SetResult();
        await dispatcher.DrainAsync();

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

    [Fact]
    public async Task FailedReport_IsReplayedFromFileOutboxAfterDispatcherRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mmf-outbox-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "outbox.json");
        try
        {
            var failingReporter = new ScriptedReporter
            {
                OnLineProgress = (_, _) => Task.FromException(new IOException("offline")),
            };
            var firstOutbox = new FileMarketAcquisitionReportOutbox(path);
            using (var dispatcher = CreateDispatcher(failingReporter, out var claim, outbox: firstOutbox))
            {
                dispatcher.BeginSession(claim);
                dispatcher.EnqueueLineProgress(LineReport());
                await dispatcher.DrainAsync();
            }

            Assert.Single(new FileMarketAcquisitionReportOutbox(path).Snapshot());

            var replayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var recoveredReporter = new ScriptedReporter
            {
                OnLineProgress = (_, _) =>
                {
                    replayed.TrySetResult();
                    return Task.CompletedTask;
                },
            };
            var recoveredOutbox = new FileMarketAcquisitionReportOutbox(path);
            using (var dispatcher = CreateDispatcher(recoveredReporter, out var claim, outbox: recoveredOutbox))
            {
                dispatcher.BeginSession(claim);
                await replayed.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await dispatcher.DrainAsync();
            }

            Assert.Empty(new FileMarketAcquisitionReportOutbox(path).Snapshot());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static MarketAcquisitionRouteReportDispatcher CreateDispatcher(
        IMarketAcquisitionRouteReporter reporter,
        out MarketAcquisitionClaimView claim,
        Action<string>? setStatus = null,
        IMarketAcquisitionReportOutbox? outbox = null)
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
        return new MarketAcquisitionRouteReportDispatcher(reporter, lifecycle, new ImmediateRouteCallbackDispatcher(), outbox);
    }

    private static MarketAcquisitionRouteProgressReport RouteReport(long sequence = 2) =>
        new("request", "claim", "Running", "attempt", sequence, "stop", "Maduin", "Purchasing", "Buying");

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

        public Task ReportMarketObservationAsync(
            MarketAcquisitionMarketObservationReport report,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
