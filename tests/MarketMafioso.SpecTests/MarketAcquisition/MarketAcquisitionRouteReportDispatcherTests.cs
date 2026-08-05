using System.Text.Json;
using MarketMafioso.Automation.MarketBoard;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.SpecTests.MarketAcquisition;

public sealed class MarketAcquisitionRouteReportDispatcherTests
{
    [Fact]
    public async Task FailedReplay_AttemptsOnlyOneHeadPerRequestAndBacksOff()
    {
        var now = new DateTimeOffset(2026, 7, 31, 1, 0, 0, TimeSpan.Zero);
        var outbox = new VolatileMarketAcquisitionReportOutbox();
        AddLineReports(outbox, "request-a", 5);
        AddLineReports(outbox, "request-b", 4);
        var reporter = new RecordingReporter(fail: true);

        using var dispatcher = CreateDispatcher(outbox, reporter, () => now);
        await dispatcher.DrainAsync();

        Assert.Equal(3, reporter.AttemptsFor("request-a"));
        Assert.Equal(3, reporter.AttemptsFor("request-b"));
        Assert.Equal(9, outbox.Snapshot().Count);
        var backlog = dispatcher.GetBacklogSnapshot();
        Assert.Equal(9, backlog.PendingEntryCount);
        Assert.Equal(2, backlog.PendingRequestCount);
        Assert.Equal(now.AddSeconds(30), backlog.NextRetryAtUtc);

        dispatcher.RetryPendingReports();
        await dispatcher.DrainAsync();

        Assert.Equal(3, reporter.AttemptsFor("request-a"));
        Assert.Equal(3, reporter.AttemptsFor("request-b"));
    }

    [Fact]
    public async Task SuccessfulReplay_DrainsEachRequestInFifoOrder()
    {
        var outbox = new VolatileMarketAcquisitionReportOutbox();
        AddLineReports(outbox, "request-a", 3);
        var reporter = new RecordingReporter(fail: false);

        using var dispatcher = CreateDispatcher(outbox, reporter);
        for (var i = 0; i < 4; i++)
            await dispatcher.DrainAsync();

        Assert.Equal([1L, 2L, 3L], reporter.SequencesFor("request-a"));
        Assert.Empty(outbox.Snapshot());
        Assert.Equal(0, dispatcher.GetBacklogSnapshot().PendingEntryCount);
    }

    [Fact]
    public async Task HostingDisabled_RetainsOutboxWithoutAttemptingDelivery()
    {
        var outbox = new VolatileMarketAcquisitionReportOutbox();
        AddLineReports(outbox, "request-a", 3);
        var reporter = new DisabledReporter();

        using var dispatcher = CreateDispatcher(outbox, reporter);
        dispatcher.RetryPendingReports();
        await dispatcher.DrainAsync();

        Assert.Equal(0, reporter.Attempts);
        Assert.Equal(3, outbox.Snapshot().Count);
    }

    [Fact]
    public void MarketObservation_IsCompactedBeforeEnteringOutbox()
    {
        var outbox = new VolatileMarketAcquisitionReportOutbox();
        using var dispatcher = CreateDispatcher(outbox, new DisabledReporter());
        dispatcher.EnqueueMarketObservation(new MarketAcquisitionMarketObservationReport(
            "request-a",
            "claim-token",
            "attempt-1",
            1,
            "line-1",
            5339,
            "Rose Gold Ingot",
            "Aether",
            "Siren",
            DateTimeOffset.UnixEpoch,
            new MarketBoardReadResult
            {
                ReportedListingCount = 1,
                ListingCapacity = 100,
                Listings =
                [
                    new MarketBoardLiveListing
                    {
                        ListingId = "listing-never-persisted",
                        RetainerName = "seller-never-persisted",
                        Quantity = 3,
                        UnitPrice = 50,
                    },
                ],
            }));

        var entry = Assert.Single(outbox.Snapshot());
        var durable = outbox.Deserialize<MarketAcquisitionMarketObservationReport>(entry);
        Assert.Empty(durable.ReadResult.Listings);
        Assert.False(durable.HasIncompleteCoverage);
        Assert.DoesNotContain("listing-never-persisted", entry.PayloadJson);
        Assert.DoesNotContain("seller-never-persisted", entry.PayloadJson);
    }

    [Fact]
    public void LegacyMarketObservation_WithRawRowsIsDiscardedBeforeReplay()
    {
        var outbox = new VolatileMarketAcquisitionReportOutbox();
        outbox.Put(
            "observation|request-a|attempt-1|1",
            "market-observation.v1",
            "request-a",
            CreateObservationWithListing());

        using var dispatcher = CreateDispatcher(outbox, new DisabledReporter());

        Assert.Empty(outbox.Snapshot());
    }

    [Fact]
    public void LegacyFileOutboxObservation_WithRawRowsIsDiscardedOnStartup()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mmf-outbox-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "route-reports.jsonl");
        Directory.CreateDirectory(directory);
        try
        {
            var outbox = new FileMarketAcquisitionReportOutbox(path);
            outbox.Put(
                "observation|request-a|attempt-1|1",
                "market-observation.v1",
                "request-a",
                CreateObservationWithListing());

            using (var dispatcher = CreateDispatcher(outbox, new DisabledReporter()))
            {
            }

            var reloaded = new FileMarketAcquisitionReportOutbox(path);
            Assert.Empty(reloaded.Snapshot());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DeadLetterObservation_WithRawRowsIsDiscardedOnStartup()
    {
        var outbox = new VolatileMarketAcquisitionReportOutbox();
        var deadLetter = new VolatileMarketAcquisitionReportOutbox();
        var observation = CreateObservationWithListing();
        var originalEntry = outbox.Put(
            "observation|request-a|attempt-1|1",
            "market-observation.v1",
            "request-a",
            observation);
        deadLetter.Put(
            originalEntry.Id,
            originalEntry.ReportType,
            originalEntry.RequestId!,
            new
            {
                Entry = originalEntry,
                RemoteStatus = "Archived",
                FailureKind = "HTTP 409",
                QuarantinedAtUtc = DateTimeOffset.UnixEpoch,
            });
        outbox.Remove(originalEntry.Id);

        using var dispatcher = CreateDispatcher(outbox, new DisabledReporter(), deadLetter: deadLetter);

        Assert.Empty(deadLetter.Snapshot());
        Assert.Equal(0, dispatcher.GetBacklogSnapshot().QuarantinedEntryCount);
    }

    private static MarketAcquisitionMarketObservationReport CreateObservationWithListing() =>
        new(
            "request-a",
            "claim-token",
            "attempt-1",
            1,
            "line-1",
            5339,
            "Rose Gold Ingot",
            "Aether",
            "Siren",
            DateTimeOffset.UnixEpoch,
            new MarketBoardReadResult
            {
                ReportedListingCount = 1,
                ListingCapacity = 100,
                Listings =
                [
                    new MarketBoardLiveListing
                    {
                        ListingId = "listing-never-persisted",
                        RetainerName = "seller-never-persisted",
                        Quantity = 3,
                        UnitPrice = 50,
                    },
                ],
            });

    [Fact]
    public async Task LegacyRequestIds_AreDeserializedOnceAndCachedAcrossReplayScans()
    {
        var now = new DateTimeOffset(2026, 7, 31, 1, 0, 0, TimeSpan.Zero);
        var outbox = new CountingLegacyOutbox("request-a", 50);
        var reporter = new RecordingReporter(fail: true);

        using var dispatcher = CreateDispatcher(outbox, reporter, () => now);
        await dispatcher.DrainAsync();

        Assert.Equal(53, outbox.DeserializeCount);

        dispatcher.RetryPendingReports();
        await dispatcher.DrainAsync();

        Assert.Equal(53, outbox.DeserializeCount);
    }

    [Fact]
    public async Task TerminalRequest_QuarantinesWholeRequestWithoutDiscardingEvidence()
    {
        var outbox = new VolatileMarketAcquisitionReportOutbox();
        var deadLetter = new VolatileMarketAcquisitionReportOutbox();
        AddLineReports(outbox, "request-a", 5);
        var reporter = new TerminalReporter(MarketAcquisitionStatuses.Failed);

        using var dispatcher = CreateDispatcher(outbox, reporter, deadLetter: deadLetter);
        await dispatcher.DrainAsync();

        Assert.Empty(outbox.Snapshot());
        Assert.Equal(5, deadLetter.Snapshot().Count);
        var backlog = dispatcher.GetBacklogSnapshot();
        Assert.Equal(0, backlog.PendingEntryCount);
        Assert.Equal(5, backlog.QuarantinedEntryCount);
        Assert.Equal(MarketAcquisitionStatuses.Failed, backlog.LastQuarantineStatus);
    }

    [Fact]
    public async Task AcceptedRouteEvent_IdempotencyConflictAcknowledgesDurableReplay()
    {
        var outbox = new VolatileMarketAcquisitionReportOutbox();
        var reporter = new AcceptedRouteConflictReporter();
        using var dispatcher = CreateDispatcher(outbox, reporter);
        dispatcher.BeginSession(new MarketAcquisitionClaimView
        {
            Id = "request-a",
            ClaimToken = "claim-token",
            Status = MarketAcquisitionStatuses.Running,
        });
        dispatcher.EnqueueRouteProgress(new MarketAcquisitionRouteProgressReport(
            "request-a",
            "claim-token",
            "Running",
            "attempt-1",
            42,
            "Aether:Faerie",
            "Faerie",
            "Running",
            "Progress",
            "plugin-instance",
            "1.3.0-test",
            new DateTimeOffset(2026, 7, 31, 1, 0, 0, TimeSpan.Zero)));

        await dispatcher.DrainAsync();

        Assert.Empty(outbox.Snapshot());
        Assert.Equal(3, reporter.ReportAttempts);
        Assert.Equal(1, reporter.TimelineReads);
    }

    private static void AddLineReports(
        IMarketAcquisitionReportOutbox outbox,
        string requestId,
        int count)
    {
        for (var sequence = 1; sequence <= count; sequence++)
        {
            outbox.Put(
                $"line|{requestId}|attempt-1|{sequence}",
                "line-progress.v1",
                requestId,
                CreateLineReport(requestId, sequence));
        }
    }

    private static MarketAcquisitionLineProgressReport CreateLineReport(
        string requestId,
        long sequence) =>
        new(
            requestId,
            "claim-token",
            "attempt-1",
            sequence,
            "line-1",
            "Rose Gold Ingot",
            "Running",
            0,
            0,
            "Progress",
            null);

    private static MarketAcquisitionRouteReportDispatcher CreateDispatcher(
        IMarketAcquisitionReportOutbox outbox,
        IMarketAcquisitionRouteReporter reporter,
        Func<DateTimeOffset>? utcNow = null,
        IMarketAcquisitionReportOutbox? deadLetter = null)
    {
        var lifecycle = new MarketAcquisitionClaimLifecycleController(
            new Configuration(),
            () => null,
            _ => { },
            () => null,
            () => null,
            () => { },
            _ => { },
            () => string.Empty,
            () => { });
        return new MarketAcquisitionRouteReportDispatcher(
            reporter,
            lifecycle,
            new ImmediateCallbackDispatcher(),
            outbox,
            utcNow,
            deadLetter);
    }

    private sealed class ImmediateCallbackDispatcher : IMarketAcquisitionRouteCallbackDispatcher
    {
        public Task DispatchAsync(Action callback)
        {
            callback();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingReporter(bool fail) : IMarketAcquisitionRouteReporter
    {
        private readonly List<MarketAcquisitionLineProgressReport> attempts = [];

        public bool CanReport => true;

        public int AttemptsFor(string requestId) =>
            attempts.Count(report => report.RequestId.Equals(requestId, StringComparison.Ordinal));

        public IReadOnlyList<long> SequencesFor(string requestId) =>
            attempts
                .Where(report => report.RequestId.Equals(requestId, StringComparison.Ordinal))
                .Select(report => report.Sequence)
                .ToArray();

        public Task<MarketAcquisitionRouteProgressReportOutcome> ReportRouteProgressAsync(
            MarketAcquisitionRouteProgressReport report,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReportPurchaseAuditAsync(
            MarketAcquisitionPurchaseAuditReport report,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReportLineProgressAsync(
            MarketAcquisitionLineProgressReport report,
            CancellationToken cancellationToken)
        {
            attempts.Add(report);
            return fail
                ? Task.FromException(new HttpRequestException("Receiver unavailable."))
                : Task.CompletedTask;
        }

        public Task ReportMarketObservationAsync(
            MarketAcquisitionMarketObservationReport report,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class DisabledReporter : IMarketAcquisitionRouteReporter
    {
        public bool CanReport => false;
        public int Attempts { get; private set; }

        public Task<MarketAcquisitionRouteProgressReportOutcome> ReportRouteProgressAsync(
            MarketAcquisitionRouteProgressReport report,
            CancellationToken cancellationToken)
        {
            Attempts++;
            throw new NotSupportedException();
        }

        public Task ReportPurchaseAuditAsync(
            MarketAcquisitionPurchaseAuditReport report,
            CancellationToken cancellationToken)
        {
            Attempts++;
            throw new NotSupportedException();
        }

        public Task ReportLineProgressAsync(
            MarketAcquisitionLineProgressReport report,
            CancellationToken cancellationToken)
        {
            Attempts++;
            throw new NotSupportedException();
        }

        public Task ReportMarketObservationAsync(
            MarketAcquisitionMarketObservationReport report,
            CancellationToken cancellationToken)
        {
            Attempts++;
            throw new NotSupportedException();
        }
    }

    private sealed class TerminalReporter(string remoteStatus) : IMarketAcquisitionRouteReporter
    {
        public bool CanReport => true;

        public Task<MarketAcquisitionRequestView> GetRequestAsync(
            string requestId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new MarketAcquisitionRequestView
            {
                Id = requestId,
                Status = remoteStatus,
            });

        public Task<MarketAcquisitionRouteProgressReportOutcome> ReportRouteProgressAsync(
            MarketAcquisitionRouteProgressReport report,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReportPurchaseAuditAsync(
            MarketAcquisitionPurchaseAuditReport report,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReportLineProgressAsync(
            MarketAcquisitionLineProgressReport report,
            CancellationToken cancellationToken) =>
            Task.FromException(new MarketAcquisitionLifecycleHttpException(
                System.Net.HttpStatusCode.Conflict,
                "line progress",
                $"Cannot move acquisition request from {remoteStatus} to Running.",
                null));

        public Task ReportMarketObservationAsync(
            MarketAcquisitionMarketObservationReport report,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class AcceptedRouteConflictReporter : IMarketAcquisitionRouteReporter
    {
        public bool CanReport => true;
        public int ReportAttempts { get; private set; }
        public int TimelineReads { get; private set; }

        public Task<MarketAcquisitionRequestView> GetRequestAsync(
            string requestId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new MarketAcquisitionRequestView
            {
                Id = requestId,
                Status = MarketAcquisitionStatuses.Running,
            });

        public Task<MarketAcquisitionRequestTimelineView> GetRequestTimelineAsync(
            string requestId,
            CancellationToken cancellationToken)
        {
            TimelineReads++;
            return Task.FromResult(new MarketAcquisitionRequestTimelineView
            {
                Request = new MarketAcquisitionRequestView
                {
                    Id = requestId,
                    Status = MarketAcquisitionStatuses.Running,
                },
                AttemptEvents =
                [
                    new MarketAcquisitionAttemptEventView
                    {
                        AttemptId = "attempt-1",
                        Sequence = 42,
                        EventType = "progress",
                    },
                ],
            });
        }

        public Task<MarketAcquisitionRouteProgressReportOutcome> ReportRouteProgressAsync(
            MarketAcquisitionRouteProgressReport report,
            CancellationToken cancellationToken)
        {
            ReportAttempts++;
            return Task.FromException<MarketAcquisitionRouteProgressReportOutcome>(
                new MarketAcquisitionLifecycleHttpException(
                    System.Net.HttpStatusCode.Conflict,
                    "progress",
                    "Idempotency key was already used with a different request body.",
                    null));
        }

        public Task ReportPurchaseAuditAsync(
            MarketAcquisitionPurchaseAuditReport report,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReportLineProgressAsync(
            MarketAcquisitionLineProgressReport report,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReportMarketObservationAsync(
            MarketAcquisitionMarketObservationReport report,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CountingLegacyOutbox : IMarketAcquisitionReportOutbox
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly List<MarketAcquisitionReportOutboxEntry> entries;

        public CountingLegacyOutbox(string requestId, int count)
        {
            entries = Enumerable.Range(1, count)
                .Select(sequence => new MarketAcquisitionReportOutboxEntry
                {
                    Id = $"line|{requestId}|attempt-1|{sequence}",
                    ReportType = "line-progress.v1",
                    RequestId = null,
                    PayloadJson = JsonSerializer.Serialize(
                        CreateLineReport(requestId, sequence),
                        JsonOptions),
                    EnqueuedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(sequence),
                })
                .ToList();
        }

        public int DeserializeCount { get; private set; }

        public MarketAcquisitionReportOutboxEntry Put<T>(
            string id,
            string reportType,
            string requestId,
            T payload) =>
            throw new NotSupportedException();

        public IReadOnlyList<MarketAcquisitionReportOutboxEntry> Snapshot() => entries.ToArray();

        public void Remove(string id) => entries.RemoveAll(entry => entry.Id.Equals(id, StringComparison.Ordinal));

        public void RemoveMany(IReadOnlyCollection<string> ids)
        {
            var idSet = ids.ToHashSet(StringComparer.Ordinal);
            entries.RemoveAll(entry => idSet.Contains(entry.Id));
        }

        public T Deserialize<T>(MarketAcquisitionReportOutboxEntry entry)
        {
            DeserializeCount++;
            return JsonSerializer.Deserialize<T>(entry.PayloadJson, JsonOptions)
                   ?? throw new InvalidDataException($"Could not deserialize {entry.Id}.");
        }
    }
}
