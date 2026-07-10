using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketAcquisitionRouteReportDispatcher : IDisposable
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);

    private readonly object sync = new();
    private readonly IMarketAcquisitionRouteReporter reporter;
    private readonly MarketAcquisitionClaimLifecycleController claimLifecycle;
    private readonly IMarketAcquisitionRouteCallbackDispatcher callbackDispatcher;
    private readonly HashSet<string> pendingRouteKeys = new(StringComparer.Ordinal);
    private CancellationTokenSource sessionCancellation = new();
    private Task queueTail = Task.CompletedTask;
    private MarketAcquisitionClaimView? claimed;
    private long sessionVersion;
    private string? lastSuccessfulRouteKey;

    public MarketAcquisitionRouteReportDispatcher(
        IMarketAcquisitionRouteReporter reporter,
        MarketAcquisitionClaimLifecycleController claimLifecycle,
        IMarketAcquisitionRouteCallbackDispatcher callbackDispatcher)
    {
        this.reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        this.claimLifecycle = claimLifecycle ?? throw new ArgumentNullException(nameof(claimLifecycle));
        this.callbackDispatcher = callbackDispatcher ?? throw new ArgumentNullException(nameof(callbackDispatcher));
    }

    public bool CanReport => reporter.CanReport;

    public void BeginSession(MarketAcquisitionClaimView sessionClaim)
    {
        ArgumentNullException.ThrowIfNull(sessionClaim);
        lock (sync)
        {
            sessionCancellation.Cancel();
            sessionCancellation.Dispose();
            sessionCancellation = new CancellationTokenSource();
            claimed = sessionClaim;
            sessionVersion++;
            lastSuccessfulRouteKey = null;
            pendingRouteKeys.Clear();
        }
    }

    public void ResetSession()
    {
        lock (sync)
        {
            sessionCancellation.Cancel();
            sessionCancellation.Dispose();
            sessionCancellation = new CancellationTokenSource();
            claimed = null;
            sessionVersion++;
            lastSuccessfulRouteKey = null;
            pendingRouteKeys.Clear();
        }
    }

    public void EnqueueRouteProgress(MarketAcquisitionRouteProgressReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var key = $"{report.RequestId}|{report.RouteState}|{report.RouteStopId}|{report.ActiveWorld}|{report.Phase}|{report.Message}";
        string pendingKey;
        MarketAcquisitionClaimView sessionClaim;
        long capturedSessionVersion;
        lock (sync)
        {
            if (claimed == null)
                return;

            sessionClaim = claimed;
            capturedSessionVersion = sessionVersion;
            pendingKey = $"{capturedSessionVersion}|{key}";
            if (key.Equals(lastSuccessfulRouteKey, StringComparison.Ordinal) || pendingRouteKeys.Contains(pendingKey))
                return;

            pendingRouteKeys.Add(pendingKey);
        }

        Enqueue(async token =>
        {
            try
            {
                var outcome = await ExecuteWithRetryAsync(
                    currentToken => reporter.ReportRouteProgressAsync(report, currentToken),
                    token).ConfigureAwait(false);
                await callbackDispatcher.DispatchAsync(() =>
                {
                    claimLifecycle.ApplySuccessfulRouteProgressReport(
                        outcome,
                        sessionClaim,
                        capturedSessionVersion,
                        CurrentSessionVersion,
                        report.Message);
                }).ConfigureAwait(false);
                lock (sync)
                {
                    pendingRouteKeys.Remove(pendingKey);
                    if (capturedSessionVersion == sessionVersion)
                        lastSuccessfulRouteKey = key;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                lock (sync)
                    pendingRouteKeys.Remove(pendingKey);
            }
            catch (Exception ex)
            {
                lock (sync)
                    pendingRouteKeys.Remove(pendingKey);
                await callbackDispatcher.DispatchAsync(() =>
                {
                    if (capturedSessionVersion != CurrentSessionVersion)
                        return;

                    if (!claimLifecycle.TryHandleRouteProgressConflict(
                            ex,
                            sessionClaim,
                            capturedSessionVersion,
                            CurrentSessionVersion))
                    {
                        claimLifecycle.SetStatus($"Route progress report failed after {MaxAttempts} attempts: {ex.Message}");
                    }
                }).ConfigureAwait(false);
            }
        });
    }

    public void EnqueuePurchaseAudit(MarketAcquisitionPurchaseAuditReport report) =>
        EnqueueReport(
            token => reporter.ReportPurchaseAuditAsync(report, token),
            $"Purchase audit report failed after {MaxAttempts} attempts");

    public void EnqueueLineProgress(MarketAcquisitionLineProgressReport report) =>
        EnqueueReport(
            token => reporter.ReportLineProgressAsync(report, token),
            $"Line progress report failed after {MaxAttempts} attempts");

    private long CurrentSessionVersion
    {
        get
        {
            lock (sync)
                return sessionVersion;
        }
    }

    private void EnqueueReport(Func<CancellationToken, Task> operation, string failurePrefix)
    {
        var capturedSessionVersion = CurrentSessionVersion;
        Enqueue(async token =>
        {
            try
            {
                await ExecuteWithRetryAsync(operation, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                await callbackDispatcher.DispatchAsync(() =>
                {
                    if (capturedSessionVersion == CurrentSessionVersion)
                        claimLifecycle.SetStatus($"{failurePrefix}: {ex.Message}");
                }).ConfigureAwait(false);
            }
        });
    }

    private void Enqueue(Func<CancellationToken, Task> operation)
    {
        lock (sync)
        {
            var token = sessionCancellation.Token;
            queueTail = queueTail
                .ContinueWith(
                    _ => token.IsCancellationRequested ? Task.CompletedTask : operation(token),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default)
                .Unwrap();
        }
    }

    internal Task DrainAsync()
    {
        lock (sync)
            return queueTail;
    }

    private static async Task<T> ExecuteWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCancellation.CancelAfter(AttemptTimeout);
            try
            {
                return await operation(attemptCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (
                !cancellationToken.IsCancellationRequested && attemptCancellation.IsCancellationRequested)
            {
                lastException = new TimeoutException($"Report attempt timed out after {AttemptTimeout.TotalSeconds:N0} seconds.", ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
            }

            if (attempt < MaxAttempts)
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
        }

        throw lastException ?? new InvalidOperationException("Report operation failed without an exception.");
    }

    private static async Task ExecuteWithRetryAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        await ExecuteWithRetryAsync(
            async token =>
            {
                await operation(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (sync)
        {
            sessionCancellation.Cancel();
            sessionCancellation.Dispose();
            claimed = null;
            pendingRouteKeys.Clear();
        }
    }
}
