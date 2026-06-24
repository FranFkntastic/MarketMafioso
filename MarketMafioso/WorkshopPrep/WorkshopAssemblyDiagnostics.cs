using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MarketMafioso.WorkshopPrep;

public sealed class WorkshopAssemblyDiagnostics : IDisposable
{
    private static readonly WorkshopAssemblyDiagnostics DisabledInstance = new();

    private readonly object sync = new();
    private readonly Stopwatch stopwatch;
    private readonly StreamWriter? writer;
    private string? lastEventName;
    private string? lastMessage;
    private string? lastSignature;
    private int repeatCount;
    private bool disposed;

    private WorkshopAssemblyDiagnostics()
    {
        stopwatch = Stopwatch.StartNew();
    }

    private WorkshopAssemblyDiagnostics(string filePath, DateTimeOffset startedAt)
    {
        FilePath = filePath;
        stopwatch = Stopwatch.StartNew();
        writer = new StreamWriter(File.Open(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };

        Record(
            "start",
            "Workshop assembly diagnostics started.",
            new Dictionary<string, string?>
            {
                ["startedAt"] = startedAt.ToString("O", CultureInfo.InvariantCulture),
            });
    }

    public static WorkshopAssemblyDiagnostics Disabled => DisabledInstance;

    public bool IsEnabled => writer != null;

    public string? FilePath { get; }

    public static WorkshopAssemblyDiagnostics CreateEnabled(string directory, DateTimeOffset startedAt)
    {
        Directory.CreateDirectory(directory);
        return new WorkshopAssemblyDiagnostics(GetAvailableLogPath(directory, startedAt), startedAt);
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
            writer.Dispose();
        }
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

    private static string GetAvailableLogPath(string directory, DateTimeOffset startedAt)
    {
        var baseName = $"assembly-{startedAt:yyyyMMdd-HHmmss}";
        var path = Path.Combine(directory, $"{baseName}.log");
        if (!File.Exists(path))
            return path;

        for (var suffix = 1; suffix < 1000; suffix++)
        {
            path = Path.Combine(directory, $"{baseName}-{suffix}.log");
            if (!File.Exists(path))
                return path;
        }

        throw new IOException($"Unable to create a unique workshop assembly diagnostics log under {directory}.");
    }

    private static string Escape(string value)
    {
        return value
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }
}
