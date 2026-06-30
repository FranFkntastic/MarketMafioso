using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketAcquisitionRouteDiagnostics : IDisposable
{
    private static readonly MarketAcquisitionRouteDiagnostics DisabledInstance = new();

    private readonly object sync = new();
    private readonly Stopwatch stopwatch;
    private readonly StreamWriter? writer;
    private readonly StreamWriter? observedListingsWriter;
    private readonly StreamWriter? purchaseRecordsWriter;
    private string? lastEventName;
    private string? lastMessage;
    private string? lastSignature;
    private int repeatCount;
    private bool disposed;

    private MarketAcquisitionRouteDiagnostics()
    {
        stopwatch = Stopwatch.StartNew();
    }

    private MarketAcquisitionRouteDiagnostics(
        string filePath,
        DateTimeOffset startedAt,
        bool createCompanionCsvs)
    {
        FilePath = filePath;
        stopwatch = Stopwatch.StartNew();
        if (createCompanionCsvs)
        {
            var directory = Path.GetDirectoryName(filePath) ??
                            throw new InvalidOperationException("Diagnostics file path must include a directory.");
            ObservedListingsCsvPath = GetAvailablePath(directory, startedAt, "observed-listings", ".csv");
            PurchaseRecordsCsvPath = GetAvailablePath(directory, startedAt, "purchase-records", ".csv");
            observedListingsWriter = CreateCsvWriter(ObservedListingsCsvPath);
            purchaseRecordsWriter = CreateCsvWriter(PurchaseRecordsCsvPath);
            WriteObservedListingsHeader();
            WritePurchaseRecordsHeader();
        }

        writer = new StreamWriter(File.Open(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };

        Record(
            "start",
            "Market acquisition route diagnostics started.",
            new Dictionary<string, string?>
            {
                ["startedAt"] = startedAt.ToString("O", CultureInfo.InvariantCulture),
                ["assemblyName"] = PluginAssembly.GetName().Name,
                ["assemblyVersion"] = PluginAssembly.GetName().Version?.ToString(),
                ["informationalVersion"] = PluginAssembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion,
                ["assemblyLocation"] = PluginAssembly.Location,
                ["observedListingsCsvPath"] = ObservedListingsCsvPath,
                ["purchaseRecordsCsvPath"] = PurchaseRecordsCsvPath,
            });
    }

    public static MarketAcquisitionRouteDiagnostics Disabled => DisabledInstance;

    public bool IsEnabled => writer != null;

    public string? FilePath { get; }

    public string? ObservedListingsCsvPath { get; }

    public string? PurchaseRecordsCsvPath { get; }

    private static Assembly PluginAssembly => typeof(Plugin).Assembly;

    public static MarketAcquisitionRouteDiagnostics CreateEnabled(string directory, DateTimeOffset startedAt)
    {
        return CreateEnabled(directory, startedAt, "route");
    }

    public static MarketAcquisitionRouteDiagnostics CreateInputCapture(string directory, DateTimeOffset startedAt)
    {
        return CreateEnabled(directory, startedAt, "input-capture");
    }

    private static MarketAcquisitionRouteDiagnostics CreateEnabled(string directory, DateTimeOffset startedAt, string filePrefix)
    {
        Directory.CreateDirectory(directory);
        return new MarketAcquisitionRouteDiagnostics(
            GetAvailablePath(directory, startedAt, filePrefix, ".log"),
            startedAt,
            filePrefix.Equals("route", StringComparison.OrdinalIgnoreCase));
    }

    public void Record(
        string eventName,
        string message,
        IReadOnlyDictionary<string, string?>? details = null)
    {
        if (writer == null)
            return;

        lock (sync)
        {
            if (disposed)
                return;

            var filteredDetails = FilterDetails(details);
            var signature = BuildSignature(eventName, message, filteredDetails);
            if (signature == lastSignature)
            {
                repeatCount++;
                return;
            }

            FlushRepeatSummary();
            WriteEvent(eventName, message, filteredDetails);
            lastEventName = eventName;
            lastMessage = message;
            lastSignature = signature;
        }
    }

    public void Complete(string message)
    {
        Record("complete", message);
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

        if (observedListingsWriter == null)
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
        string? source)
    {
        if (purchaseRecordsWriter == null)
            return;

        lock (sync)
        {
            if (disposed)
                return;

            WriteCsvRow(
                purchaseRecordsWriter,
                [
                    FormatElapsed(),
                    requestId,
                    worldName,
                    dataCenter,
                    lineId,
                    itemName,
                    source,
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
        var details = exception == null
            ? null
            : new Dictionary<string, string?>
            {
                ["exceptionType"] = exception.GetType().FullName,
                ["exceptionMessage"] = exception.Message,
            };

        Record("failed", message, details);
    }

    public void Dispose()
    {
        if (writer == null)
            return;

        lock (sync)
        {
            if (disposed)
                return;

            FlushRepeatSummary();
            disposed = true;
            observedListingsWriter?.Dispose();
            purchaseRecordsWriter?.Dispose();
            writer.Dispose();
        }
    }

    private void WriteObservedListingsHeader()
    {
        WriteCsvRow(
            observedListingsWriter!,
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
            ]);
    }

    private void WritePurchaseRecordsHeader()
    {
        WriteCsvRow(
            purchaseRecordsWriter!,
            [
                "elapsed",
                "requestId",
                "world",
                "dataCenter",
                "lineId",
                "itemName",
                "source",
                "event",
                "result",
                "listingId",
                "retainerId",
                "quantity",
                "totalGil",
                "unitPrice",
                "message",
                "notes",
            ]);
    }

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
        WriteCsvRow(
            observedListingsWriter!,
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

    private void WriteEvent(
        string eventName,
        string message,
        IReadOnlyList<KeyValuePair<string, string>> details)
    {
        writer!.WriteLine($"[{FormatElapsed()}] {eventName}");
        writer.WriteLine($"  {Escape(message)}");

        foreach (var detail in details)
        {
            writer.WriteLine($"  {detail.Key}: {Escape(detail.Value)}");
        }

        writer.WriteLine();
    }

    private void FlushRepeatSummary()
    {
        if (repeatCount == 0)
            return;

        writer!.WriteLine($"[{FormatElapsed()}] repeat");
        writer.WriteLine($"  Previous event repeated {repeatCount.ToString(CultureInfo.InvariantCulture)} more time(s).");
        writer.WriteLine($"  event: {lastEventName}");
        writer.WriteLine($"  message: {Escape(lastMessage ?? string.Empty)}");
        writer.WriteLine();

        repeatCount = 0;
    }

    private static List<KeyValuePair<string, string>> FilterDetails(IReadOnlyDictionary<string, string?>? details)
    {
        if (details == null)
            return [];

        return details
            .Where(x => x.Value != null)
            .Select(x => new KeyValuePair<string, string>(x.Key, x.Value!))
            .ToList();
    }

    private static string BuildSignature(
        string eventName,
        string message,
        IReadOnlyList<KeyValuePair<string, string>> details)
    {
        var parts = new List<string>
        {
            eventName,
            message,
        };

        parts.AddRange(details.Select(x => $"{x.Key}={x.Value}"));
        return string.Join('\u001f', parts);
    }

    private string FormatElapsed()
    {
        var elapsed = stopwatch.Elapsed;
        return elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture)
            : elapsed.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
    }

    private static string GetAvailablePath(
        string directory,
        DateTimeOffset startedAt,
        string filePrefix,
        string extension)
    {
        var baseName = $"{filePrefix}-{startedAt:yyyyMMdd-HHmmss}";
        var path = Path.Combine(directory, $"{baseName}{extension}");
        if (!File.Exists(path))
            return path;

        for (var suffix = 1; suffix < 1000; suffix++)
        {
            path = Path.Combine(directory, $"{baseName}-{suffix}{extension}");
            if (!File.Exists(path))
                return path;
        }

        throw new IOException($"Unable to create a unique market acquisition route diagnostics log under {directory}.");
    }

    private static string Escape(string value)
    {
        return value
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private static StreamWriter CreateCsvWriter(string filePath) =>
        new(File.Open(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };

    private static void WriteCsvRow(StreamWriter csvWriter, IReadOnlyList<string?> values)
    {
        csvWriter.WriteLine(string.Join(",", values.Select(EscapeCsv)));
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value.IndexOfAny(['"', ',', '\r', '\n']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
