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

            writer.WriteLine(FormatLine(eventName, message, details));
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

            disposed = true;
            writer.Dispose();
        }
    }

    private string FormatLine(
        string eventName,
        string message,
        IReadOnlyDictionary<string, string?>? details)
    {
        var parts = new List<string>
        {
            $"elapsedMs={stopwatch.Elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)}",
            $"event={eventName}",
            $"message=\"{Escape(message)}\"",
        };

        if (details != null)
        {
            parts.AddRange(details
                .Where(x => x.Value != null)
                .Select(x => $"{x.Key}=\"{Escape(x.Value!)}\""));
        }

        return string.Join(" ", parts);
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
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }
}
