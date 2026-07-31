using System.Text.Json;
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
        Func<DateTimeOffset>? utcNow = null)
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
            utcNow);
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
