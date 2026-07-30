using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Franthropy.Dalamud.Persistence;
using MarketMafioso.Automation.Diagnostics;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketAcquisitionRouteDiagnostics : IDisposable
{
    private static readonly MarketAcquisitionRouteDiagnostics DisabledInstance = new(
        AutomationDiagnosticsLog.Disabled,
        null,
        null,
        null,
        null,
        null,
        null,
        DateTimeOffset.MinValue,
        string.Empty,
        string.Empty,
        MarketAcquisitionRouteDiagnosticsLevel.Off);

    private readonly object sync = new();
    private readonly HashSet<string> summarizedPendingOperations = new(StringComparer.Ordinal);
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private readonly AutomationDiagnosticsLog log;
    private readonly AutomationCsvLog? observedListingsCsv;
    private readonly AutomationCsvLog? purchaseRecordsCsv;
    private readonly StreamWriter? routeEventsWriter;
    private readonly MarketAcquisitionSegmentedJsonlWriter? fullTraceWriter;
    private readonly DateTimeOffset startedAt;
    private readonly string packageKind;
    private readonly string runId;
    private readonly MarketAcquisitionRouteDiagnosticsLevel level;
    private string captureStatus = "Active";
    private long nextSummaryEventSequence;
    private long nextFullTraceEventSequence;
    private bool disposed;

    private MarketAcquisitionRouteDiagnostics(
        AutomationDiagnosticsLog log,
        AutomationCsvLog? observedListingsCsv,
        AutomationCsvLog? purchaseRecordsCsv,
        StreamWriter? routeEventsWriter,
        MarketAcquisitionSegmentedJsonlWriter? fullTraceWriter,
        string? manifestPath,
        string? packageDirectoryPath,
        DateTimeOffset startedAt,
        string packageKind,
        string runId,
        MarketAcquisitionRouteDiagnosticsLevel level)
    {
        this.log = log;
        this.observedListingsCsv = observedListingsCsv;
        this.purchaseRecordsCsv = purchaseRecordsCsv;
        this.routeEventsWriter = routeEventsWriter;
        this.fullTraceWriter = fullTraceWriter;
        this.startedAt = startedAt;
        this.packageKind = packageKind;
        this.runId = runId;
        this.level = level;
        ObservedListingsCsvPath = observedListingsCsv?.FilePath;
        PurchaseRecordsCsvPath = purchaseRecordsCsv?.FilePath;
        RouteEventsJsonlPath = routeEventsWriter == null
            ? null
            : Path.Combine(packageDirectoryPath!, "route-events.jsonl");
        ManifestPath = manifestPath;
        PackageDirectoryPath = packageDirectoryPath;
    }

    public static MarketAcquisitionRouteDiagnostics Disabled => DisabledInstance;

    public bool IsEnabled => log.IsEnabled;

    public string? FilePath => log.FilePath;

    public string? ObservedListingsCsvPath { get; }

    public string? PurchaseRecordsCsvPath { get; }

    public string? RouteEventsJsonlPath { get; }

    public string? ManifestPath { get; }

    public string? PackageDirectoryPath { get; }

    public static MarketAcquisitionRouteDiagnostics CreateEnabled(string directory, DateTimeOffset startedAt)
    {
        return CreatePackage(directory, startedAt, "route", MarketAcquisitionRouteDiagnosticsLevel.FullTrace);
    }

    public static MarketAcquisitionRouteDiagnostics CreateEnabled(
        string directory,
        DateTimeOffset startedAt,
        string packageKind,
        MarketAcquisitionRouteDiagnosticsLevel level = MarketAcquisitionRouteDiagnosticsLevel.FullTrace)
    {
        if (!packageKind.Equals("route", StringComparison.OrdinalIgnoreCase) &&
            !packageKind.Equals("dry-run", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Package kind must be route or dry-run.", nameof(packageKind));
        if (level == MarketAcquisitionRouteDiagnosticsLevel.Off)
            return Disabled;
        return CreatePackage(directory, startedAt, packageKind, level);
    }

    public static MarketAcquisitionRouteDiagnostics CreateInputCapture(string directory, DateTimeOffset startedAt)
    {
        return CreatePackage(directory, startedAt, "input-capture", MarketAcquisitionRouteDiagnosticsLevel.FullTrace);
    }

    private static MarketAcquisitionRouteDiagnostics CreatePackage(
        string directory,
        DateTimeOffset startedAt,
        string filePrefix,
        MarketAcquisitionRouteDiagnosticsLevel level)
    {
        var createCompanionCsvs =
            level == MarketAcquisitionRouteDiagnosticsLevel.FullTrace &&
            (filePrefix.Equals("route", StringComparison.OrdinalIgnoreCase) ||
             filePrefix.Equals("dry-run", StringComparison.OrdinalIgnoreCase));
        var packageDirectory = CreatePackageDirectory(directory, startedAt, filePrefix);
        AutomationCsvLog? observedListingsCsv = null;
        AutomationCsvLog? purchaseRecordsCsv = null;
        StreamWriter? routeEventsWriter = null;
        MarketAcquisitionSegmentedJsonlWriter? fullTraceWriter = null;
        AutomationDiagnosticsLog? log = null;

        try
        {
            observedListingsCsv = createCompanionCsvs
                ? AutomationCsvLog.CreateAtPath(Path.Combine(packageDirectory, "observed-listings.csv"), ObservedListingsHeader, autoFlush: false)
                : null;
            purchaseRecordsCsv = createCompanionCsvs
                ? AutomationCsvLog.CreateAtPath(Path.Combine(packageDirectory, "purchase-records.csv"), PurchaseRecordsHeader, autoFlush: false)
                : null;
            var routeEventsJsonlPath = Path.Combine(packageDirectory, "route-events.jsonl");
            routeEventsWriter = new StreamWriter(
                new FileStream(
                    routeEventsJsonlPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    FileOptions.SequentialScan),
                bufferSize: 64 * 1024)
            {
                AutoFlush = false,
            };
            fullTraceWriter = level == MarketAcquisitionRouteDiagnosticsLevel.FullTrace
                ? new MarketAcquisitionSegmentedJsonlWriter(packageDirectory)
                : null;
            var manifestPath = Path.Combine(packageDirectory, "manifest.json");
            log = AutomationDiagnosticsLog.CreateEnabledAtPath(
                Path.Combine(packageDirectory, $"{filePrefix}.log"),
                startedAt,
                "Market acquisition route diagnostics started.",
                new Dictionary<string, string?>
                {
                    ["packageDirectoryPath"] = packageDirectory,
                    ["observedListingsCsvPath"] = observedListingsCsv?.FilePath,
                    ["purchaseRecordsCsvPath"] = purchaseRecordsCsv?.FilePath,
                    ["diagnosticsLevel"] = level.ToString(),
                },
                autoFlush: false);

            var diagnostics = new MarketAcquisitionRouteDiagnostics(
                log,
                observedListingsCsv,
                purchaseRecordsCsv,
                routeEventsWriter,
                fullTraceWriter,
                manifestPath,
                packageDirectory,
                startedAt,
                filePrefix,
                Path.GetFileName(packageDirectory),
                level);

            diagnostics.WriteManifest();
            diagnostics.RecordRouteEvent(
                "start",
                "Market acquisition route diagnostics started.",
                new Dictionary<string, string?>
                {
                    ["runId"] = diagnostics.runId,
                    ["packageKind"] = filePrefix,
                    ["diagnosticsLevel"] = level.ToString(),
                    ["routeLog"] = Path.GetFileName(diagnostics.FilePath),
                    ["observedListingsCsv"] = Path.GetFileName(diagnostics.ObservedListingsCsvPath),
                    ["purchaseRecordsCsv"] = Path.GetFileName(diagnostics.PurchaseRecordsCsvPath),
                    ["routeEventsJsonl"] = Path.GetFileName(diagnostics.RouteEventsJsonlPath),
                    ["manifest"] = Path.GetFileName(diagnostics.ManifestPath),
                });

            return diagnostics;
        }
        catch
        {
            log?.Dispose();
            fullTraceWriter?.Dispose();
            routeEventsWriter?.Dispose();
            purchaseRecordsCsv?.Dispose();
            observedListingsCsv?.Dispose();
            throw;
        }
    }

    private static string CreatePackageDirectory(
        string directory,
        DateTimeOffset startedAt,
        string filePrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePrefix);

        Directory.CreateDirectory(directory);
        var baseName = $"{filePrefix}-{startedAt:yyyyMMdd-HHmmss}";
        var packageDirectory = Path.Combine(directory, baseName);
        if (!Directory.Exists(packageDirectory))
        {
            Directory.CreateDirectory(packageDirectory);
            return packageDirectory;
        }

        for (var suffix = 1; suffix < 1000; suffix++)
        {
            packageDirectory = Path.Combine(directory, $"{baseName}-{suffix}");
            if (Directory.Exists(packageDirectory))
                continue;

            Directory.CreateDirectory(packageDirectory);
            return packageDirectory;
        }

        throw new IOException($"Unable to create a unique market acquisition diagnostics package under {directory}.");
    }

    public void Record(
        string eventName,
        string message,
        IReadOnlyDictionary<string, string?>? details = null)
    {
        lock (sync)
        {
            if (disposed)
                return;

            RecordUnsafe(eventName, message, details);
            if (IsTerminalEvent(eventName))
            {
                captureStatus = "Finalizing";
                WriteManifest();
            }
        }
    }

    public void Complete(string message)
    {
        Record("complete", message);
        Dispose();
    }

    public void RecordAutomationSnapshot(MarketBoardAutomationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Record(
            "automation-snapshot",
            $"{snapshot.Step}/{snapshot.Phase}: observed {snapshot.Observed}; outcome {snapshot.Outcome}; next {snapshot.NextAction}.",
            snapshot.ToDetails());
    }

    public void RecordObservedListings(
        string requestId,
        string currentWorld,
        string? dataCenter,
        MarketAcquisitionWorldItemSubtask? activeSubtask,
        MarketAcquisitionLiveCandidatePlan candidatePlan)
    {
        ArgumentNullException.ThrowIfNull(candidatePlan);

        var eventDetails = new Dictionary<string, string?>
        {
            ["requestId"] = requestId,
            ["currentWorld"] = currentWorld,
            ["dataCenter"] = dataCenter,
            ["lineId"] = activeSubtask?.LineId,
            ["lineOrdinal"] = activeSubtask?.LineOrdinal.ToString(CultureInfo.InvariantCulture),
            ["itemId"] = activeSubtask?.ItemId.ToString(CultureInfo.InvariantCulture),
            ["planStatus"] = candidatePlan.Status,
            ["listingReadState"] = candidatePlan.ListingReadState.ToString(),
            ["listingReadFresh"] = candidatePlan.IsListingReadFresh.ToString(),
            ["readableListings"] = candidatePlan.ReadableListingCount.ToString(CultureInfo.InvariantCulture),
            ["reportedListings"] = candidatePlan.ReportedListingCount.ToString(CultureInfo.InvariantCulture),
            ["visibleListingCacheTruncated"] = candidatePlan.IsVisibleListingCacheTruncated.ToString(),
            ["coverageStatus"] = FormatCoverageStatus(candidatePlan),
        };

        lock (sync)
        {
            if (disposed)
                return;

            RecordUnsafe(
                "observed-listings",
                "Recorded observed market-board listing evidence.",
                eventDetails);
            if (observedListingsCsv == null)
                return;

            if (candidatePlan.Rows.Count == 0)
            {
                WriteObservedListingRow(
                    requestId,
                    currentWorld,
                    dataCenter,
                    activeSubtask,
                    candidatePlan,
                    rowOrdinal: 0,
                    row: null);
                return;
            }

            for (var i = 0; i < candidatePlan.Rows.Count; i++)
            {
                WriteObservedListingRow(
                    requestId,
                    currentWorld,
                    dataCenter,
                    activeSubtask,
                    candidatePlan,
                    i + 1,
                    candidatePlan.Rows[i]);
            }
        }
    }

    public void RecordPurchaseAudit(
        string requestId,
        string? dataCenter,
        string lineId,
        string? itemName,
        string worldName,
        string listingId,
        string retainerId,
        uint quantity,
        uint totalGil,
        string result,
        string? source,
        uint? itemId = null,
        string? sourceCandidateStatus = null)
    {
        var eventDetails = new Dictionary<string, string?>
        {
            ["requestId"] = requestId,
            ["dataCenter"] = dataCenter,
            ["lineId"] = lineId,
            ["itemId"] = itemId?.ToString(CultureInfo.InvariantCulture),
            ["itemName"] = itemName,
            ["worldName"] = worldName,
            ["listingId"] = listingId,
            ["retainerId"] = retainerId,
            ["quantity"] = quantity.ToString(CultureInfo.InvariantCulture),
            ["totalGil"] = totalGil.ToString(CultureInfo.InvariantCulture),
            ["result"] = result,
            ["source"] = source,
            ["sourceCandidateStatus"] = sourceCandidateStatus,
        };

        lock (sync)
        {
            if (disposed)
                return;

            RecordUnsafe(
                "purchase-audit",
                "Recorded market-board purchase audit evidence.",
                eventDetails);
            if (purchaseRecordsCsv == null)
                return;

            purchaseRecordsCsv.WriteRow(
            [
                FormatElapsed(),
                requestId,
                worldName,
                dataCenter,
                lineId,
                itemId?.ToString(CultureInfo.InvariantCulture),
                itemName,
                source,
                sourceCandidateStatus,
                "purchase-audit",
                result,
                listingId,
                retainerId,
                quantity.ToString(CultureInfo.InvariantCulture),
                totalGil.ToString(CultureInfo.InvariantCulture),
                quantity == 0
                    ? null
                    : (totalGil / quantity).ToString(CultureInfo.InvariantCulture),
                null,
                null,
            ]);
        }
    }

    public void Fail(string message, Exception? exception = null)
    {
        Record(
            "failed",
            message,
            exception == null
                ? null
                : new Dictionary<string, string?>
                {
                    ["exceptionType"] = exception.GetType().FullName,
                    ["exceptionMessage"] = exception.Message,
                });
        Dispose();
    }

    public void Flush()
    {
        lock (sync)
        {
            if (disposed)
                return;

            observedListingsCsv?.Flush();
            purchaseRecordsCsv?.Flush();
            routeEventsWriter?.Flush();
            fullTraceWriter?.Flush();
            log.Flush();
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;

            var terminalCapture = string.Equals(captureStatus, "Finalizing", StringComparison.Ordinal);
            try
            {
                observedListingsCsv?.Dispose();
                purchaseRecordsCsv?.Dispose();
                routeEventsWriter?.Dispose();
                fullTraceWriter?.Dispose();
                log.Dispose();
            }
            finally
            {
                captureStatus = terminalCapture ? "Complete" : "Incomplete";
                WriteManifest();
                disposed = true;
            }
        }
    }

    private static IReadOnlyList<string> ObservedListingsHeader =>
    [
        "elapsed",
        "requestId",
        "currentWorld",
        "dataCenter",
        "lineId",
        "lineOrdinal",
        "source",
        "itemId",
        "itemName",
        "planStatus",
        "planMessage",
        "readableListings",
        "reportedListings",
        "listingCapacity",
        "visibleListingCacheTruncated",
        "listingReadState",
        "listingReadFresh",
        "coverageStatus",
        "unreadListings",
        "rawItemIdMismatchCounts",
        "requestedQuantity",
        "wouldBuyQuantity",
        "wouldSpendGil",
        "rowOrdinal",
        "decision",
        "reason",
        "message",
        "listingItemId",
        "rawItemId",
        "listingWorld",
        "listingId",
        "retainerId",
        "retainerName",
        "unitPrice",
        "quantity",
        "totalGil",
        "isHq",
        "runningQuantityAfter",
        "runningGilAfter",
    ];

    private static IReadOnlyList<string> PurchaseRecordsHeader =>
    [
        "elapsed",
        "requestId",
        "world",
        "dataCenter",
        "lineId",
        "itemId",
        "itemName",
        "source",
        "sourceCandidateStatus",
        "event",
        "result",
        "listingId",
        "retainerId",
        "quantity",
        "totalGil",
        "unitPrice",
        "message",
        "notes",
    ];

    private void WriteObservedListingRow(
        string requestId,
        string currentWorld,
        string? dataCenter,
        MarketAcquisitionWorldItemSubtask? activeSubtask,
        MarketAcquisitionLiveCandidatePlan candidatePlan,
        int rowOrdinal,
        MarketAcquisitionLiveCandidateRow? row)
    {
        var listing = row?.LiveListing;
        observedListingsCsv!.WriteRow(
        [
            FormatElapsed(),
            requestId,
            currentWorld,
            dataCenter,
            activeSubtask?.LineId,
            activeSubtask?.LineOrdinal.ToString(CultureInfo.InvariantCulture),
            activeSubtask?.Source,
            activeSubtask?.ItemId.ToString(CultureInfo.InvariantCulture),
            activeSubtask?.ItemName,
            candidatePlan.Status,
            candidatePlan.Message,
            candidatePlan.ReadableListingCount.ToString(CultureInfo.InvariantCulture),
            candidatePlan.ReportedListingCount.ToString(CultureInfo.InvariantCulture),
            candidatePlan.ListingCapacity.ToString(CultureInfo.InvariantCulture),
            candidatePlan.IsVisibleListingCacheTruncated.ToString(),
            candidatePlan.ListingReadState.ToString(),
            candidatePlan.IsListingReadFresh.ToString(),
            FormatCoverageStatus(candidatePlan),
            FormatUnreadListings(candidatePlan),
            FormatRawItemIdMismatchCounts(candidatePlan.RawItemIdMismatchCounts),
            candidatePlan.RequestedQuantity.ToString(CultureInfo.InvariantCulture),
            candidatePlan.WouldBuyQuantity.ToString(CultureInfo.InvariantCulture),
            candidatePlan.WouldSpendGil.ToString(CultureInfo.InvariantCulture),
            rowOrdinal.ToString(CultureInfo.InvariantCulture),
            row?.Decision,
            row?.Reason,
            row?.Message,
            listing?.ItemId.ToString(CultureInfo.InvariantCulture),
            listing?.RawItemId?.ToString(CultureInfo.InvariantCulture),
            listing?.WorldName,
            listing?.ListingId,
            listing?.RetainerId,
            listing?.RetainerName,
            listing?.UnitPrice.ToString(CultureInfo.InvariantCulture),
            listing?.Quantity.ToString(CultureInfo.InvariantCulture),
            listing == null
                ? null
                : ((ulong)listing.UnitPrice * listing.Quantity).ToString(CultureInfo.InvariantCulture),
            listing?.IsHq.ToString(),
            row?.RunningQuantityAfter.ToString(CultureInfo.InvariantCulture),
            row?.RunningGilAfter.ToString(CultureInfo.InvariantCulture),
        ]);
    }

    private void RecordRouteEvent(
        string eventName,
        string message,
        IReadOnlyDictionary<string, string?>? details)
    {
        if (routeEventsWriter == null)
            return;

        lock (sync)
        {
            if (disposed)
                return;

            RecordUnsafe(eventName, message, details, writeLog: false);
        }
    }

    private void RecordUnsafe(
        string eventName,
        string message,
        IReadOnlyDictionary<string, string?>? details,
        bool writeLog = true)
    {
        if (routeEventsWriter == null)
            return;

        var filteredDetails = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (details != null)
        {
            foreach (var detail in details)
            {
                if (detail.Value != null)
                    filteredDetails[detail.Key] = detail.Value;
            }
        }

        var elapsedMilliseconds = (long)stopwatch.Elapsed.TotalMilliseconds;
        var recordedAtUtc = DateTimeOffset.UtcNow;
        if (fullTraceWriter != null)
        {
            var fullTraceEvent = new MarketAcquisitionRouteDiagnosticEvent
            {
                SchemaVersion = MarketAcquisitionRouteDiagnosticEvent.CurrentSchemaVersion,
                Sequence = ++nextFullTraceEventSequence,
                ElapsedMilliseconds = elapsedMilliseconds,
                RecordedAtUtc = recordedAtUtc,
                EventName = eventName,
                Message = message,
                Details = filteredDetails,
            };
            fullTraceWriter.Write(fullTraceEvent.Sequence, JsonSerializer.Serialize(fullTraceEvent, JsonOptions));
        }

        if (!ShouldIncludeInSummary(eventName, message, filteredDetails))
            return;

        var summaryEvent = new MarketAcquisitionRouteDiagnosticEvent
        {
            SchemaVersion = MarketAcquisitionRouteDiagnosticEvent.CurrentSchemaVersion,
            Sequence = ++nextSummaryEventSequence,
            ElapsedMilliseconds = elapsedMilliseconds,
            RecordedAtUtc = recordedAtUtc,
            EventName = eventName,
            Message = message,
            Details = filteredDetails,
        };
        routeEventsWriter.WriteLine(JsonSerializer.Serialize(summaryEvent, JsonOptions));
        if (writeLog)
            log.Record(eventName, message, details);
    }

    private void WriteManifest()
    {
        if (ManifestPath == null || RouteEventsJsonlPath == null)
            return;

        var assembly = typeof(Plugin).Assembly;
        var manifest = new MarketAcquisitionRouteDiagnosticManifest
        {
            SchemaVersion = MarketAcquisitionRouteDiagnosticEvent.CurrentSchemaVersion,
            RunId = runId,
            PackageKind = packageKind,
            DiagnosticsLevel = level.ToString(),
            CaptureStatus = captureStatus,
            StartedAtUtc = startedAt,
            AssemblyName = assembly.GetName().Name,
            AssemblyVersion = assembly.GetName().Version?.ToString(),
            InformationalVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion,
            Artifacts = BuildArtifacts(),
            CaptureCapabilities = BuildCaptureCapabilities(),
            FullTraceSegments = fullTraceWriter?.Segments ?? [],
        };

        AtomicJsonFile.Write(ManifestPath, manifest, JsonOptions);
    }

    private IReadOnlyList<string> BuildCaptureCapabilities()
    {
        var capabilities = new List<string>
        {
            "route-events-jsonl-v1",
            "route-log",
        };

        if (observedListingsCsv != null)
            capabilities.Add("observed-listings-csv");
        if (purchaseRecordsCsv != null)
            capabilities.Add("purchase-records-csv");
        if (fullTraceWriter != null)
            capabilities.Add("segmented-full-trace-jsonl-v1");

        return capabilities;
    }

    private IReadOnlyDictionary<string, string> BuildArtifacts()
    {
        var artifacts = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["manifest"] = Path.GetFileName(ManifestPath) ?? throw new InvalidOperationException("Manifest path is invalid."),
            ["routeEventsJsonl"] = Path.GetFileName(RouteEventsJsonlPath) ?? throw new InvalidOperationException("Route event path is invalid."),
        };

        if (FilePath != null)
            artifacts["routeLog"] = Path.GetFileName(FilePath);
        if (ObservedListingsCsvPath != null)
            artifacts["observedListingsCsv"] = Path.GetFileName(ObservedListingsCsvPath);
        if (PurchaseRecordsCsvPath != null)
            artifacts["purchaseRecordsCsv"] = Path.GetFileName(PurchaseRecordsCsvPath);
        if (fullTraceWriter != null)
            artifacts["fullTraceSegments"] = "trace-*.jsonl";

        return artifacts;
    }

    private static bool IsTerminalEvent(string eventName) =>
        eventName is "complete" or "failed" or "stopped" or "input-capture-finalized";

    private bool ShouldIncludeInSummary(
        string eventName,
        string message,
        IReadOnlyDictionary<string, string> details)
    {
        if (IsTerminalEvent(eventName) ||
            eventName.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
            eventName.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            eventName.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            eventName.Contains("warning", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (eventName is "listing-read-pending" or "market-board-approach" or "probe-start")
            return false;

        if (eventName.Equals("automation-snapshot", StringComparison.OrdinalIgnoreCase) &&
            !details.ContainsKey("candidateListingId") &&
            details.TryGetValue("outcome", out var outcome) &&
            (outcome.Equals("InProgress", StringComparison.OrdinalIgnoreCase) ||
             outcome.Equals("Pending", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (eventName.Equals("route-operation", StringComparison.OrdinalIgnoreCase) &&
            details.TryGetValue("disposition", out var disposition) &&
            (disposition.Equals("Pending", StringComparison.OrdinalIgnoreCase) ||
             disposition.Equals("Running", StringComparison.OrdinalIgnoreCase)))
        {
            return details.TryGetValue("operationId", out var operationId) &&
                   summarizedPendingOperations.Add(operationId);
        }

        return true;
    }

    private static string FormatCoverageStatus(MarketAcquisitionLiveCandidatePlan candidatePlan) =>
        candidatePlan.ReportedListingCount > candidatePlan.ReadableListingCount
            ? "Incomplete"
            : "Complete";

    private static string FormatUnreadListings(MarketAcquisitionLiveCandidatePlan candidatePlan) =>
        Math.Max(0, candidatePlan.ReportedListingCount - candidatePlan.ReadableListingCount)
            .ToString(CultureInfo.InvariantCulture);

    private string FormatElapsed()
    {
        var elapsed = stopwatch.Elapsed;
        return elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture)
            : elapsed.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
    }

    private static string? FormatRawItemIdMismatchCounts(IReadOnlyDictionary<uint, int> counts)
    {
        if (counts.Count == 0)
            return null;

        return string.Join(
            ";",
            counts
                .OrderBy(count => count.Key)
                .Select(count => $"{count.Key}={count.Value}"));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}
