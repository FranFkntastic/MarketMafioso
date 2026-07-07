using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using MarketMafioso.Automation.Diagnostics;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketAcquisitionRouteDiagnostics : IDisposable
{
    private static readonly MarketAcquisitionRouteDiagnostics DisabledInstance = new(
        AutomationDiagnosticsLog.Disabled,
        null,
        null,
        null);

    private readonly object sync = new();
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private readonly AutomationDiagnosticsLog log;
    private readonly AutomationCsvLog? observedListingsCsv;
    private readonly AutomationCsvLog? purchaseRecordsCsv;
    private bool disposed;

    private MarketAcquisitionRouteDiagnostics(
        AutomationDiagnosticsLog log,
        AutomationCsvLog? observedListingsCsv,
        AutomationCsvLog? purchaseRecordsCsv,
        string? packageDirectoryPath)
    {
        this.log = log;
        this.observedListingsCsv = observedListingsCsv;
        this.purchaseRecordsCsv = purchaseRecordsCsv;
        ObservedListingsCsvPath = observedListingsCsv?.FilePath;
        PurchaseRecordsCsvPath = purchaseRecordsCsv?.FilePath;
        PackageDirectoryPath = packageDirectoryPath;
    }

    public static MarketAcquisitionRouteDiagnostics Disabled => DisabledInstance;

    public bool IsEnabled => log.IsEnabled;

    public string? FilePath => log.FilePath;

    public string? ObservedListingsCsvPath { get; }

    public string? PurchaseRecordsCsvPath { get; }

    public string? PackageDirectoryPath { get; }

    public static MarketAcquisitionRouteDiagnostics CreateEnabled(string directory, DateTimeOffset startedAt)
    {
        return CreateEnabled(directory, startedAt, "route");
    }

    public static MarketAcquisitionRouteDiagnostics CreateInputCapture(string directory, DateTimeOffset startedAt)
    {
        return CreateEnabled(directory, startedAt, "input-capture");
    }

    private static MarketAcquisitionRouteDiagnostics CreateEnabled(
        string directory,
        DateTimeOffset startedAt,
        string filePrefix)
    {
        var createCompanionCsvs = filePrefix.Equals("route", StringComparison.OrdinalIgnoreCase);
        var packageDirectory = CreatePackageDirectory(directory, startedAt, filePrefix);
        var observedListingsCsv = createCompanionCsvs
            ? AutomationCsvLog.CreateAtPath(Path.Combine(packageDirectory, "observed-listings.csv"), ObservedListingsHeader)
            : null;
        var purchaseRecordsCsv = createCompanionCsvs
            ? AutomationCsvLog.CreateAtPath(Path.Combine(packageDirectory, "purchase-records.csv"), PurchaseRecordsHeader)
            : null;

        var diagnostics = new MarketAcquisitionRouteDiagnostics(
            AutomationDiagnosticsLog.CreateEnabledAtPath(
                Path.Combine(packageDirectory, $"{filePrefix}.log"),
                startedAt,
                "Market acquisition route diagnostics started.",
                new Dictionary<string, string?>
                {
                    ["packageDirectoryPath"] = packageDirectory,
                    ["observedListingsCsvPath"] = observedListingsCsv?.FilePath,
                    ["purchaseRecordsCsvPath"] = purchaseRecordsCsv?.FilePath,
                }),
            observedListingsCsv,
            purchaseRecordsCsv,
            packageDirectory);

        return diagnostics;
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
        log.Record(eventName, message, details);
    }

    public void Complete(string message)
    {
        log.Complete(message);
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

        if (observedListingsCsv == null)
            return;

        lock (sync)
        {
            if (disposed)
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
        if (purchaseRecordsCsv == null)
            return;

        lock (sync)
        {
            if (disposed)
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
        log.Fail(message, exception);
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;

            disposed = true;
            observedListingsCsv?.Dispose();
            purchaseRecordsCsv?.Dispose();
            log.Dispose();
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
}
