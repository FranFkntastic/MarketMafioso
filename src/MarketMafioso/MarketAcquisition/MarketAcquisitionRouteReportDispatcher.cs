using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketAcquisitionRouteReportDispatcher : IDisposable
{
    private const int MaxAttempts = 3;
    private const string RouteProgressType = "route-progress.v1";
    private const string PurchaseAuditType = "purchase-audit.v1";
    private const string LineProgressType = "line-progress.v1";
    private const string MarketObservationType = "market-observation.v1";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReplayInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumReplayBackoff = TimeSpan.FromMinutes(15);

    private readonly object sync = new();
    private readonly IMarketAcquisitionRouteReporter reporter;
    private readonly MarketAcquisitionClaimLifecycleController claimLifecycle;
    private readonly IMarketAcquisitionRouteCallbackDispatcher callbackDispatcher;
    private readonly IMarketAcquisitionReportOutbox outbox;
    private readonly VolatileMarketAcquisitionReportOutbox volatileFallback = new();
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly Func<DateTimeOffset> utcNow;
    private readonly Dictionary<string, Queue<MarketAcquisitionReportOutboxEntry>> pendingEntriesByRequest = new(StringComparer.Ordinal);
    private readonly HashSet<string> pendingEntryIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> inFlightRequestIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> legacyRequestIdsByEntryId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReportRetryState> retryStatesByRequest = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> entrySessionVersions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> pendingRouteEntryIdsByKey = new(StringComparer.Ordinal);
    private readonly Task replayLoop;
    private Task queueTail = Task.CompletedTask;
    private MarketAcquisitionClaimView? claimed;
    private long sessionVersion;
    private string? lastSuccessfulRouteKey;
    private string? lastFailureKind;
    private DateTimeOffset? lastFailureAtUtc;

    public MarketAcquisitionRouteReportDispatcher(
        IMarketAcquisitionRouteReporter reporter,
        MarketAcquisitionClaimLifecycleController claimLifecycle,
        IMarketAcquisitionRouteCallbackDispatcher callbackDispatcher,
        IMarketAcquisitionReportOutbox? outbox = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.reporter = reporter ?? throw new ArgumentNullException(nameof(reporter));
        this.claimLifecycle = claimLifecycle ?? throw new ArgumentNullException(nameof(claimLifecycle));
        this.callbackDispatcher = callbackDispatcher ?? throw new ArgumentNullException(nameof(callbackDispatcher));
        this.outbox = outbox ?? new VolatileMarketAcquisitionReportOutbox();
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        CompactDuplicateRouteProgress();
        LoadPendingOutboxEntries();
        QueuePendingRequestHeads();
        replayLoop = Task.Run(() => ReplayLoopAsync(lifetimeCancellation.Token));
    }

    public bool CanReport => reporter.CanReport;

    public void BeginSession(MarketAcquisitionClaimView sessionClaim)
    {
        ArgumentNullException.ThrowIfNull(sessionClaim);
        lock (sync)
        {
            claimed = sessionClaim;
            sessionVersion++;
            lastSuccessfulRouteKey = null;
        }

        QueuePendingRequestHeads();
    }

    public void ResetSession()
    {
        lock (sync)
        {
            claimed = null;
            sessionVersion++;
            lastSuccessfulRouteKey = null;
        }
    }

    public void EnqueueRouteProgress(MarketAcquisitionRouteProgressReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var routeKey = $"{report.RequestId}|{report.RouteState}|{report.RouteStopId}|{report.ActiveWorld}|{report.Phase}|{report.Message}";
        MarketAcquisitionClaimView sessionClaim;
        lock (sync)
        {
            if (claimed == null ||
                routeKey.Equals(lastSuccessfulRouteKey, StringComparison.Ordinal) ||
                pendingRouteEntryIdsByKey.ContainsKey(routeKey))
                return;
            sessionClaim = claimed;
            pendingRouteEntryIdsByKey[routeKey] = string.Empty;
        }

        try
        {
            var entry = Persist(
                $"route|{report.RequestId}|{report.AttemptId}|{report.Sequence}",
                RouteProgressType,
                report.RequestId,
                new DurableRouteProgress(report, sessionClaim, routeKey));
            lock (sync)
                pendingRouteEntryIdsByKey[routeKey] = entry.Id;
            TrackPendingEntry(entry);
            QueueRequestHead(report.RequestId);
        }
        catch
        {
            lock (sync)
                pendingRouteEntryIdsByKey.Remove(routeKey);
            throw;
        }
    }

    public void EnqueuePurchaseAudit(MarketAcquisitionPurchaseAuditReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        TrackAndQueue(Persist(
            $"purchase|{report.RequestId}|{report.AttemptId}|{report.Sequence}",
            PurchaseAuditType,
            report.RequestId,
            report));
    }

    public void EnqueueLineProgress(MarketAcquisitionLineProgressReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        TrackAndQueue(Persist(
            $"line|{report.RequestId}|{report.AttemptId}|{report.Sequence}",
            LineProgressType,
            report.RequestId,
            report));
    }

    public void EnqueueMarketObservation(MarketAcquisitionMarketObservationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        TrackAndQueue(Persist(
            $"observation|{report.RequestId}|{report.AttemptId}|{report.Sequence}",
            MarketObservationType,
            report.RequestId,
            report));
    }

    private MarketAcquisitionReportOutboxEntry Persist<T>(
        string id,
        string reportType,
        string requestId,
        T payload)
    {
        try
        {
            var entry = outbox.Put(id, reportType, requestId, payload);
            lock (sync)
                entrySessionVersions[id] = sessionVersion;
            return entry;
        }
        catch (Exception ex)
        {
            claimLifecycle.SetStatus($"Could not persist the report outbox; sending this report without crash recovery: {ex.Message}");
            var entry = volatileFallback.Put(id, reportType, requestId, payload);
            lock (sync)
                entrySessionVersions[id] = sessionVersion;
            return entry;
        }
    }

    private void TrackAndQueue(MarketAcquisitionReportOutboxEntry entry)
    {
        var requestId = TrackPendingEntry(entry);
        QueueRequestHead(requestId);
    }

    private void LoadPendingOutboxEntries()
    {
        foreach (var entry in outbox.Snapshot())
            TrackPendingEntry(entry);
    }

    private string TrackPendingEntry(MarketAcquisitionReportOutboxEntry entry)
    {
        var requestId = GetRequestId(entry);
        lock (sync)
        {
            if (!pendingEntryIds.Add(entry.Id))
                return requestId;
            if (!pendingEntriesByRequest.TryGetValue(requestId, out var pending))
            {
                pending = new Queue<MarketAcquisitionReportOutboxEntry>();
                pendingEntriesByRequest.Add(requestId, pending);
            }
            pending.Enqueue(entry);
        }
        return requestId;
    }

    internal void RetryPendingReports() => QueuePendingRequestHeads();

    private void CompactDuplicateRouteProgress()
    {
        var duplicateIds = new List<string>();
        foreach (var entry in outbox.Snapshot())
        {
            if (!entry.ReportType.Equals(RouteProgressType, StringComparison.Ordinal))
                continue;

            DurableRouteProgress durable;
            try
            {
                durable = outbox.Deserialize<DurableRouteProgress>(entry);
            }
            catch
            {
                continue;
            }

            if (pendingRouteEntryIdsByKey.TryAdd(durable.RouteKey, entry.Id))
                continue;
            duplicateIds.Add(entry.Id);
        }

        if (duplicateIds.Count > 0)
            outbox.RemoveMany(duplicateIds);
    }

    private void ForgetPendingRouteEntry(MarketAcquisitionReportOutboxEntry entry)
    {
        if (!entry.ReportType.Equals(RouteProgressType, StringComparison.Ordinal))
            return;

        DurableRouteProgress durable;
        try
        {
            durable = outbox.Deserialize<DurableRouteProgress>(entry);
        }
        catch
        {
            return;
        }

        if (pendingRouteEntryIdsByKey.TryGetValue(durable.RouteKey, out var pendingId) &&
            pendingId.Equals(entry.Id, StringComparison.Ordinal))
        {
            pendingRouteEntryIdsByKey.Remove(durable.RouteKey);
        }
    }

    private void QueuePendingRequestHeads()
    {
        string[] requestIds;
        lock (sync)
            requestIds = [.. pendingEntriesByRequest.Keys];
        foreach (var requestId in requestIds)
            QueueRequestHead(requestId);
    }

    private void QueueRequestHead(string requestId)
    {
        lock (sync)
        {
            if (inFlightRequestIds.Contains(requestId) ||
                !pendingEntriesByRequest.TryGetValue(requestId, out var pending) ||
                pending.Count == 0 ||
                retryStatesByRequest.TryGetValue(requestId, out var retry) && retry.NextAttemptAtUtc > utcNow())
            {
                return;
            }

            var entry = pending.Peek();
            inFlightRequestIds.Add(requestId);
            var token = lifetimeCancellation.Token;
            queueTail = queueTail
                .ContinueWith(
                    _ => token.IsCancellationRequested
                        ? Task.CompletedTask
                        : SendAndAcknowledgeAsync(entry, requestId, token),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default)
                .Unwrap();
        }
    }

    private async Task SendAndAcknowledgeAsync(
        MarketAcquisitionReportOutboxEntry entry,
        string requestId,
        CancellationToken cancellationToken)
    {
        var acknowledged = false;
        try
        {
            await SendAsync(entry, cancellationToken).ConfigureAwait(false);
            outbox.Remove(entry.Id);
            volatileFallback.Remove(entry.Id);
            lock (sync)
            {
                entrySessionVersions.Remove(entry.Id);
                ForgetPendingRouteEntry(entry);
                AcknowledgePendingEntry(entry, requestId);
                retryStatesByRequest.Remove(requestId);
            }
            acknowledged = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RegisterFailure(requestId, ex);
            if (IsForCurrentClaim(entry))
            {
                await callbackDispatcher.DispatchAsync(() =>
                    claimLifecycle.SetStatus($"Report retained for automatic retry after {MaxAttempts} attempts: {ex.Message}"))
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            lock (sync)
                inFlightRequestIds.Remove(requestId);
        }

        if (acknowledged)
            QueueRequestHead(requestId);
    }

    private void AcknowledgePendingEntry(MarketAcquisitionReportOutboxEntry entry, string requestId)
    {
        if (pendingEntriesByRequest.TryGetValue(requestId, out var pending) &&
            pending.Count > 0 &&
            pending.Peek().Id.Equals(entry.Id, StringComparison.Ordinal))
        {
            pending.Dequeue();
            if (pending.Count == 0)
                pendingEntriesByRequest.Remove(requestId);
        }
        pendingEntryIds.Remove(entry.Id);
        legacyRequestIdsByEntryId.Remove(entry.Id);
    }

    private bool IsForCurrentClaim(MarketAcquisitionReportOutboxEntry entry)
    {
        string? currentRequestId;
        lock (sync)
        {
            if (entrySessionVersions.TryGetValue(entry.Id, out var reportSessionVersion) &&
                reportSessionVersion != sessionVersion)
            {
                return false;
            }
            currentRequestId = claimed?.Id;
        }
        if (string.IsNullOrWhiteSpace(currentRequestId))
            return false;

        var entryRequestId = GetRequestId(entry);
        return currentRequestId.Equals(entryRequestId, StringComparison.Ordinal);
    }

    private string GetRequestId(MarketAcquisitionReportOutboxEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.RequestId))
            return entry.RequestId;

        lock (sync)
        {
            if (legacyRequestIdsByEntryId.TryGetValue(entry.Id, out var cached))
                return cached;
        }

        var requestId = entry.ReportType switch
        {
            RouteProgressType => outbox.Deserialize<DurableRouteProgress>(entry).Report.RequestId,
            PurchaseAuditType => outbox.Deserialize<MarketAcquisitionPurchaseAuditReport>(entry).RequestId,
            LineProgressType => outbox.Deserialize<MarketAcquisitionLineProgressReport>(entry).RequestId,
            MarketObservationType => outbox.Deserialize<MarketAcquisitionMarketObservationReport>(entry).RequestId,
            _ => string.Empty,
        };
        if (string.IsNullOrWhiteSpace(requestId))
            throw new InvalidOperationException($"Outbox entry '{entry.Id}' does not identify an acquisition request.");

        lock (sync)
            legacyRequestIdsByEntryId[entry.Id] = requestId;
        return requestId;
    }

    private void RegisterFailure(string requestId, Exception exception)
    {
        var now = utcNow();
        lock (sync)
        {
            var failureCount = retryStatesByRequest.TryGetValue(requestId, out var current)
                ? current.FailureCount + 1
                : 1;
            var exponent = Math.Min(failureCount - 1, 5);
            var backoff = TimeSpan.FromTicks(ReplayInterval.Ticks * (1L << exponent));
            if (backoff > MaximumReplayBackoff)
                backoff = MaximumReplayBackoff;
            var failureKind = exception is HttpRequestException { StatusCode: { } statusCode }
                ? $"HTTP {(int)statusCode}"
                : exception.GetType().Name;
            retryStatesByRequest[requestId] = new ReportRetryState(
                failureCount,
                now + backoff,
                failureKind);
            lastFailureKind = failureKind;
            lastFailureAtUtc = now;
        }
    }

    public MarketAcquisitionReportBacklogSnapshot GetBacklogSnapshot()
    {
        lock (sync)
        {
            DateTimeOffset? oldest = null;
            foreach (var pending in pendingEntriesByRequest.Values)
            {
                if (pending.Count == 0)
                    continue;
                var enqueuedAt = pending.Peek().EnqueuedAtUtc;
                if (oldest == null || enqueuedAt < oldest)
                    oldest = enqueuedAt;
            }

            DateTimeOffset? nextRetry = null;
            foreach (var retry in retryStatesByRequest.Values)
            {
                if (nextRetry == null || retry.NextAttemptAtUtc < nextRetry)
                    nextRetry = retry.NextAttemptAtUtc;
            }

            return new MarketAcquisitionReportBacklogSnapshot(
                pendingEntryIds.Count,
                pendingEntriesByRequest.Count,
                inFlightRequestIds.Count,
                oldest,
                nextRetry,
                lastFailureKind,
                lastFailureAtUtc);
        }
    }

    private Task SendAsync(MarketAcquisitionReportOutboxEntry entry, CancellationToken cancellationToken) =>
        entry.ReportType switch
        {
            RouteProgressType => SendRouteProgressAsync(
                outbox.Deserialize<DurableRouteProgress>(entry),
                cancellationToken),
            PurchaseAuditType => ExecuteWithRetryAsync(
                token => reporter.ReportPurchaseAuditAsync(
                    outbox.Deserialize<MarketAcquisitionPurchaseAuditReport>(entry),
                    token),
                cancellationToken),
            LineProgressType => ExecuteWithRetryAsync(
                token => reporter.ReportLineProgressAsync(
                    outbox.Deserialize<MarketAcquisitionLineProgressReport>(entry),
                    token),
                cancellationToken),
            MarketObservationType => ExecuteWithRetryAsync(
                token => reporter.ReportMarketObservationAsync(
                    outbox.Deserialize<MarketAcquisitionMarketObservationReport>(entry),
                    token),
                cancellationToken),
            _ => throw new InvalidOperationException($"Unknown market acquisition outbox report type '{entry.ReportType}'."),
        };

    private async Task SendRouteProgressAsync(
        DurableRouteProgress durable,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await ExecuteWithRetryAsync(
                token => reporter.ReportRouteProgressAsync(durable.Report, token),
                cancellationToken).ConfigureAwait(false);
            var currentVersion = CurrentSessionVersion;
            await callbackDispatcher.DispatchAsync(() =>
            {
                claimLifecycle.ApplySuccessfulRouteProgressReport(
                    outcome,
                    durable.Claim,
                    currentVersion,
                    CurrentSessionVersion,
                    durable.Report.Message);
                lock (sync)
                    lastSuccessfulRouteKey = durable.RouteKey;
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var handled = false;
            var currentVersion = CurrentSessionVersion;
            await callbackDispatcher.DispatchAsync(() =>
            {
                handled = claimLifecycle.TryHandleRouteProgressConflict(
                    ex,
                    durable.Claim,
                    currentVersion,
                    CurrentSessionVersion);
            }).ConfigureAwait(false);
            if (!handled)
                throw;
        }
    }

    private long CurrentSessionVersion
    {
        get
        {
            lock (sync)
                return sessionVersion;
        }
    }

    private async Task ReplayLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(ReplayInterval, cancellationToken).ConfigureAwait(false);
                QueuePendingRequestHeads();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
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
        lifetimeCancellation.Cancel();
        lifetimeCancellation.Dispose();
        lock (sync)
        {
            claimed = null;
            pendingEntriesByRequest.Clear();
            pendingEntryIds.Clear();
            inFlightRequestIds.Clear();
            legacyRequestIdsByEntryId.Clear();
            retryStatesByRequest.Clear();
            entrySessionVersions.Clear();
            pendingRouteEntryIdsByKey.Clear();
        }
        _ = replayLoop;
    }

    private sealed record DurableRouteProgress(
        MarketAcquisitionRouteProgressReport Report,
        MarketAcquisitionClaimView Claim,
        string RouteKey);

    private sealed record ReportRetryState(
        int FailureCount,
        DateTimeOffset NextAttemptAtUtc,
        string FailureKind);
}

public sealed record MarketAcquisitionReportBacklogSnapshot(
    int PendingEntryCount,
    int PendingRequestCount,
    int InFlightRequestCount,
    DateTimeOffset? OldestEnqueuedAtUtc,
    DateTimeOffset? NextRetryAtUtc,
    string? LastFailureKind,
    DateTimeOffset? LastFailureAtUtc);
