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
    private const int SummaryDetailMaximumLength = 512;

    private static readonly HashSet<string> StatefulSummaryEvents =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "current-world",
            "world-unavailable",
            "travel-preflight-blocked",
            "market-board-travel-wait",
            "automation-snapshot",
            "item-search",
        };

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
    private readonly HashSet<string> observedWorlds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<uint> observedItemIds = [];
    private readonly List<string> maintenanceWarnings = [];
    private string diagnosticsRootDirectory = string.Empty;
    private MarketAcquisitionRouteDiagnosticRetentionPolicy retentionPolicy = new();
    private IMarketAcquisitionDiagnosticCompressor compressor = new MarketAcquisitionGzipDiagnosticCompressor();
    private string? routeEventsStoredFileName;
    private IReadOnlyList<MarketAcquisitionRouteDiagnosticTraceSegment> finalizedTraceSegments = [];
    private IReadOnlyList<MarketAcquisitionRouteDiagnosticStoredArtifact> storedArtifacts = [];
    private DateTimeOffset? finalizedAtUtc;
    private string? terminalEventName;
    private string storageState = "Active";
    private string? retentionReason;
    private string captureStatus = "Active";
    private long nextSummaryEventSequence;
    private long nextFullTraceEventSequence;
    private string? lastSummaryStateEvent;
    private string? lastSummaryStateSignature;
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
        MarketAcquisitionRouteDiagnosticsLevel level = MarketAcquisitionRouteDiagnosticsLevel.FullTrace,
        MarketAcquisitionRouteDiagnosticRetentionPolicy? retentionPolicy = null,
        IMarketAcquisitionDiagnosticCompressor? compressor = null)
    {
        if (!packageKind.Equals("route", StringComparison.OrdinalIgnoreCase) &&
            !packageKind.Equals("dry-run", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Package kind must be route or dry-run.", nameof(packageKind));
        if (level == MarketAcquisitionRouteDiagnosticsLevel.Off)
            return Disabled;
        return CreatePackage(directory, startedAt, packageKind, level, retentionPolicy, compressor);
    }

    public static MarketAcquisitionRouteDiagnostics CreateInputCapture(string directory, DateTimeOffset startedAt)
    {
        return CreatePackage(directory, startedAt, "input-capture", MarketAcquisitionRouteDiagnosticsLevel.FullTrace);
    }

    private static MarketAcquisitionRouteDiagnostics CreatePackage(
        string directory,
        DateTimeOffset startedAt,
        string filePrefix,
        MarketAcquisitionRouteDiagnosticsLevel level,
        MarketAcquisitionRouteDiagnosticRetentionPolicy? retentionPolicy = null,
        IMarketAcquisitionDiagnosticCompressor? compressor = null)
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
            diagnostics.diagnosticsRootDirectory = directory;
            diagnostics.retentionPolicy = (retentionPolicy ?? new MarketAcquisitionRouteDiagnosticRetentionPolicy()).Normalize();
            diagnostics.compressor = compressor ?? new MarketAcquisitionGzipDiagnosticCompressor();
            diagnostics.routeEventsStoredFileName = Path.GetFileName(routeEventsJsonlPath);

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
        var dateDirectory = Path.Combine(
            directory,
            startedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dateDirectory);
        var baseName = $"{filePrefix}-{startedAt:yyyyMMdd-HHmmss}";
        var packageDirectory = Path.Combine(dateDirectory, baseName);
        if (!Directory.Exists(packageDirectory))
        {
            Directory.CreateDirectory(packageDirectory);
            return packageDirectory;
        }

        for (var suffix = 1; suffix < 1000; suffix++)
        {
            packageDirectory = Path.Combine(dateDirectory, $"{baseName}-{suffix}");
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
                terminalEventName = eventName;
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

            observedWorlds.Add(currentWorld);
            if (activeSubtask?.ItemId > 0)
                observedItemIds.Add(activeSubtask.ItemId);

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
                finalizedAtUtc = DateTimeOffset.UtcNow;
                storageState = terminalCapture ? "Hot" : "Incomplete";
                retentionReason = terminalCapture
                    ? "Finalized machine streams are compressed; human log and analytical CSVs remain on the hot shelf."
                    : "Capture did not reach a terminal event and remains raw.";
                finalizedTraceSegments = fullTraceWriter?.Segments.ToArray() ?? [];
                if (terminalCapture)
                    FinalizeMachineArtifacts();
                WriteManifest();
                disposed = true;
                MaintainRetentionCatalog();
            }
        }
    }

    private void FinalizeMachineArtifacts()
    {
        TryCompressArtifact("routeEventsJsonl", RouteEventsJsonlPath);
        foreach (var segment in finalizedTraceSegments.ToArray())
        {
            if (PackageDirectoryPath == null)
                break;

            var sourcePath = Path.Combine(PackageDirectoryPath, segment.FileName);
            try
            {
                var compressed = compressor.Compress(sourcePath);
                finalizedTraceSegments = finalizedTraceSegments
                    .Select(candidate => candidate.FileName.Equals(segment.FileName, StringComparison.Ordinal)
                        ? candidate with
                        {
                            FileName = compressed.StoredFileName,
                            ContentEncoding = compressed.ContentEncoding,
                            StoredByteLength = compressed.StoredByteLength,
                            StoredSha256 = compressed.StoredSha256,
                        }
                        : candidate)
                    .ToArray();
                StoreArtifact($"fullTrace:{segment.FirstSequence.ToString(CultureInfo.InvariantCulture)}", compressed);
                WriteManifest();
                File.Delete(sourcePath);
            }
            catch (Exception exception)
            {
                maintenanceWarnings.Add($"Unable to compress {segment.FileName}: {exception.Message}");
            }
        }
    }

    private void TryCompressArtifact(string role, string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return;

        try
        {
            var compressed = compressor.Compress(sourcePath);
            if (role.Equals("routeEventsJsonl", StringComparison.Ordinal))
                routeEventsStoredFileName = compressed.StoredFileName;
            StoreArtifact(role, compressed);
            WriteManifest();
            File.Delete(sourcePath);
        }
        catch (Exception exception)
        {
            maintenanceWarnings.Add($"Unable to compress {Path.GetFileName(sourcePath)}: {exception.Message}");
        }
    }

    private void StoreArtifact(string role, MarketAcquisitionDiagnosticCompressedFile compressed)
    {
        storedArtifacts = storedArtifacts
            .Where(artifact => !artifact.Role.Equals(role, StringComparison.Ordinal))
            .Append(new MarketAcquisitionRouteDiagnosticStoredArtifact
            {
                Role = role,
                FileName = compressed.StoredFileName,
                ContentEncoding = compressed.ContentEncoding,
                RawByteLength = compressed.RawByteLength,
                RawSha256 = compressed.RawSha256,
                StoredByteLength = compressed.StoredByteLength,
                StoredSha256 = compressed.StoredSha256,
            })
            .OrderBy(artifact => artifact.Role, StringComparer.Ordinal)
            .ToArray();
    }

    private void MaintainRetentionCatalog()
    {
        if (string.IsNullOrWhiteSpace(diagnosticsRootDirectory))
            return;

        try
        {
            var result = new MarketAcquisitionRouteDiagnosticRetention(compressor).Maintain(
                diagnosticsRootDirectory,
                retentionPolicy,
                DateTimeOffset.UtcNow);
            if (result.Warnings.Count > 0)
            {
                maintenanceWarnings.AddRange(result.Warnings);
                WriteManifest();
            }
        }
        catch (Exception exception)
        {
            maintenanceWarnings.Add($"Unable to maintain diagnostic retention catalog: {exception.Message}");
            WriteManifest();
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
        "retainerNameSource",
        "sellerOwnerContentId",
        "artisanContentId",
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
            listing?.RetainerNameSource,
            listing?.SellerOwnerContentId?.ToString(CultureInfo.InvariantCulture),
            listing?.ArtisanContentId?.ToString(CultureInfo.InvariantCulture),
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

        var summaryDetails = BuildSummaryDetails(filteredDetails);
        var summaryMessage = CompactSummaryValue(message);

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

        if (!ShouldIncludeInSummary(eventName, message, summaryDetails))
            return;

        var summaryEvent = new MarketAcquisitionRouteDiagnosticEvent
        {
            SchemaVersion = MarketAcquisitionRouteDiagnosticEvent.CurrentSchemaVersion,
            Sequence = ++nextSummaryEventSequence,
            ElapsedMilliseconds = elapsedMilliseconds,
            RecordedAtUtc = recordedAtUtc,
            EventName = eventName,
            Message = summaryMessage,
            Details = summaryDetails,
        };
        routeEventsWriter.WriteLine(JsonSerializer.Serialize(summaryEvent, JsonOptions));
        if (writeLog)
            log.Record(
                eventName,
                summaryMessage,
                summaryDetails.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal));
    }

    private void WriteManifest()
    {
        if (ManifestPath == null || RouteEventsJsonlPath == null)
            return;

        var assembly = typeof(Plugin).Assembly;
        var manifest = new MarketAcquisitionRouteDiagnosticManifest
        {
            SchemaVersion = MarketAcquisitionRouteDiagnosticManifest.CurrentSchemaVersion,
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
            FullTraceSegments = finalizedTraceSegments.Count > 0
                ? finalizedTraceSegments
                : fullTraceWriter?.Segments ?? [],
            FinalizedAtUtc = finalizedAtUtc,
            TerminalEventName = terminalEventName,
            StorageState = storageState,
            RetentionReason = retentionReason,
            MaintenanceWarnings = maintenanceWarnings.ToArray(),
            Worlds = observedWorlds.OrderBy(world => world, StringComparer.OrdinalIgnoreCase).ToArray(),
            ItemIds = observedItemIds.OrderBy(itemId => itemId).ToArray(),
            StoredArtifacts = storedArtifacts,
        };

        AtomicJsonFile.Write(ManifestPath, manifest, JsonOptions);
    }

    private IReadOnlyList<string> BuildCaptureCapabilities()
    {
        var capabilities = new List<string>
        {
            "route-events-jsonl-v1",
            "route-log",
            "summary-projection-v1",
            "summary-omission-markers-v1",
        };

        if (observedListingsCsv != null)
            capabilities.Add("observed-listings-csv");
        if (purchaseRecordsCsv != null)
            capabilities.Add("purchase-records-csv");
        if (fullTraceWriter != null)
        {
            capabilities.Add("segmented-full-trace-jsonl-v1");
            capabilities.Add("full-trace-authoritative-v1");
        }
        if (storedArtifacts.Any(artifact => artifact.ContentEncoding.Equals("gzip", StringComparison.OrdinalIgnoreCase)))
            capabilities.Add("gzip-finalized-artifacts-v1");
        capabilities.Add("hot-cold-retention-v1");
        capabilities.Add("diagnostic-catalog-v1");

        return capabilities;
    }

    private IReadOnlyDictionary<string, string> BuildArtifacts()
    {
        var artifacts = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["manifest"] = Path.GetFileName(ManifestPath) ?? throw new InvalidOperationException("Manifest path is invalid."),
            ["routeEventsJsonl"] = routeEventsStoredFileName ??
                Path.GetFileName(RouteEventsJsonlPath) ?? throw new InvalidOperationException("Route event path is invalid."),
        };

        if (FilePath != null)
            artifacts["routeLog"] = Path.GetFileName(FilePath);
        if (ObservedListingsCsvPath != null)
            artifacts["observedListingsCsv"] = Path.GetFileName(ObservedListingsCsvPath);
        if (PurchaseRecordsCsvPath != null)
            artifacts["purchaseRecordsCsv"] = Path.GetFileName(PurchaseRecordsCsvPath);
        if (fullTraceWriter != null)
            artifacts["fullTraceSegments"] = finalizedTraceSegments.Any(segment => segment.ContentEncoding.Equals("gzip", StringComparison.OrdinalIgnoreCase))
                ? "trace-*.jsonl.gz"
                : "trace-*.jsonl";

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
            ResetSummaryState();
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
            var include = details.TryGetValue("operationId", out var operationId) &&
                          summarizedPendingOperations.Add(operationId);
            if (include)
                ResetSummaryState();
            return include;
        }

        if (StatefulSummaryEvents.Contains(eventName))
        {
            var signature = BuildSummaryStateSignature(eventName, message, details);
            if (string.Equals(lastSummaryStateEvent, eventName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(lastSummaryStateSignature, signature, StringComparison.Ordinal))
            {
                return false;
            }

            lastSummaryStateEvent = eventName;
            lastSummaryStateSignature = signature;
            return true;
        }

        ResetSummaryState();
        return true;
    }

    private static SortedDictionary<string, string> BuildSummaryDetails(
        IReadOnlyDictionary<string, string> details)
    {
        var projected = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var omittedKeys = new List<string>();

        foreach (var detail in details)
        {
            if (IsSummaryOmittedDetail(detail.Key))
            {
                omittedKeys.Add(detail.Key);
                continue;
            }

            projected[detail.Key] = CompactSummaryValue(detail.Value);
        }

        if (omittedKeys.Count > 0)
        {
            projected["summaryOmittedDetailCount"] = omittedKeys.Count.ToString(CultureInfo.InvariantCulture);
            projected["summaryOmittedDetailKeys"] = CompactSummaryValue(string.Join(", ", omittedKeys));
        }

        return projected;
    }

    private static bool IsSummaryOmittedDetail(string key) =>
        key.Contains("preview", StringComparison.OrdinalIgnoreCase);

    private static string CompactSummaryValue(string value)
    {
        if (value.Length <= SummaryDetailMaximumLength)
            return value;

        return $"{value[..SummaryDetailMaximumLength]}... [truncated {value.Length.ToString(CultureInfo.InvariantCulture)} chars]";
    }

    private static string BuildSummaryStateSignature(
        string eventName,
        string message,
        IReadOnlyDictionary<string, string> details)
    {
        var stateDetails = details
            .Where(detail => !IsVolatileSummaryDetail(detail.Key))
            .Select(detail => $"{detail.Key}={detail.Value}");
        return string.Join('\u001f', new[] { eventName, message }.Concat(stateDetails));
    }

    private static bool IsVolatileSummaryDetail(string key) =>
        key.Contains("elapsed", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("timestamp", StringComparison.OrdinalIgnoreCase) ||
        key.EndsWith("Utc", StringComparison.OrdinalIgnoreCase);

    private void ResetSummaryState()
    {
        lastSummaryStateEvent = null;
        lastSummaryStateSignature = null;
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
