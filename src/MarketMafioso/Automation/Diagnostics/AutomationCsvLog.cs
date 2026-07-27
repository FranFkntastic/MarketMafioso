using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MarketMafioso.Automation.Diagnostics;

public sealed class AutomationCsvLog : IDisposable
{
    private readonly StreamWriter writer;
    private bool disposed;

    private AutomationCsvLog(string filePath, IReadOnlyList<string> headers, bool autoFlush)
    {
        FilePath = filePath;
        writer = new StreamWriter(
            new FileStream(
                filePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan),
            bufferSize: 64 * 1024)
        {
            AutoFlush = autoFlush,
        };

        WriteRow(headers);
    }

    public string FilePath { get; }

    public static AutomationCsvLog Create(
        string directory,
        DateTimeOffset startedAt,
        string filePrefix,
        IReadOnlyList<string> headers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePrefix);
        ArgumentNullException.ThrowIfNull(headers);

        Directory.CreateDirectory(directory);
        return new AutomationCsvLog(
            AutomationDiagnosticsLog.GetAvailablePath(directory, startedAt, filePrefix, ".csv"),
            headers,
            autoFlush: true);
    }

    public static AutomationCsvLog CreateAtPath(
        string filePath,
        IReadOnlyList<string> headers,
        bool autoFlush = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(headers);

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        return new AutomationCsvLog(filePath, headers, autoFlush);
    }

    public void WriteRow(IReadOnlyList<string?> values)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        writer.WriteLine(string.Join(",", values.Select(EscapeCsv)));
    }

    public void Flush()
    {
        if (!disposed)
            writer.Flush();
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        writer.Dispose();
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
