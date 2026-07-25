using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Franthropy.Dalamud.Persistence;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketAcquisitionReportOutboxEntry
{
    public string Id { get; init; } = string.Empty;
    public string ReportType { get; init; } = string.Empty;
    public string PayloadJson { get; init; } = string.Empty;
    public DateTimeOffset EnqueuedAtUtc { get; init; }
}

public interface IMarketAcquisitionReportOutbox
{
    MarketAcquisitionReportOutboxEntry Put<T>(string id, string reportType, T payload);
    IReadOnlyList<MarketAcquisitionReportOutboxEntry> Snapshot();
    void Remove(string id);
    void RemoveMany(IReadOnlyCollection<string> ids);
    T Deserialize<T>(MarketAcquisitionReportOutboxEntry entry);
}

public sealed class FileMarketAcquisitionReportOutbox : IMarketAcquisitionReportOutbox
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private const long StartupCompactionSizeThresholdBytes = 8 * 1024 * 1024;
    private const int StartupCompactionMinimumRecordCount = 1024;

    private readonly object sync = new();
    private readonly string path;
    private readonly string backupPath;
    private readonly Action<long>? observePhysicalWrite;
    private List<MarketAcquisitionReportOutboxEntry> entries;

    public FileMarketAcquisitionReportOutbox(string path)
        : this(path, observePhysicalWrite: null)
    {
    }

    internal FileMarketAcquisitionReportOutbox(string path, Action<long>? observePhysicalWrite)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("An outbox path is required.", nameof(path));

        this.path = Path.GetFullPath(path);
        backupPath = this.path + ".bak";
        this.observePhysicalWrite = observePhysicalWrite;
        if (TryLoadJournal(
                this.path,
                out entries,
                out var journalRecordCount,
                out var requiresTailRepair))
        {
            if (requiresTailRepair)
                RewriteJournal();
            else
                CompactJournalAtStartupWhenNeeded(journalRecordCount);
            return;
        }

        entries = LoadLegacySnapshot(this.path) ?? LoadLegacySnapshot(backupPath) ?? [];
        RewriteJournal();
    }

    public MarketAcquisitionReportOutboxEntry Put<T>(string id, string reportType, T payload)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("An outbox entry id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(reportType))
            throw new ArgumentException("A report type is required.", nameof(reportType));

        lock (sync)
        {
            var existing = entries.FirstOrDefault(candidate => candidate.Id.Equals(id, StringComparison.Ordinal));
            if (existing != null)
                return existing;

            var entry = new MarketAcquisitionReportOutboxEntry
            {
                Id = id,
                ReportType = reportType,
                PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
                EnqueuedAtUtc = DateTimeOffset.UtcNow,
            };
            AppendJournalRecord(new OutboxJournalRecord
            {
                Operation = OutboxJournalOperation.Put,
                Entry = entry,
            });
            entries.Add(entry);
            return entry;
        }
    }

    public IReadOnlyList<MarketAcquisitionReportOutboxEntry> Snapshot()
    {
        lock (sync)
            return entries.OrderBy(entry => entry.EnqueuedAtUtc).ToArray();
    }

    public void Remove(string id) => RemoveMany([id]);

    public void RemoveMany(IReadOnlyCollection<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
            return;

        var idSet = ids.ToHashSet(StringComparer.Ordinal);
        lock (sync)
        {
            var removed = entries.Where(entry => idSet.Contains(entry.Id)).ToArray();
            if (removed.Length == 0)
                return;

            AppendJournalRecord(new OutboxJournalRecord
            {
                Operation = OutboxJournalOperation.Remove,
                Ids = removed.Select(entry => entry.Id).ToArray(),
            });
            entries.RemoveAll(entry => idSet.Contains(entry.Id));
        }
    }

    public T Deserialize<T>(MarketAcquisitionReportOutboxEntry entry) =>
        JsonSerializer.Deserialize<T>(entry.PayloadJson, JsonOptions)
        ?? throw new InvalidDataException($"Outbox entry '{entry.Id}' has an empty {entry.ReportType} payload.");

    private static List<MarketAcquisitionReportOutboxEntry>? LoadLegacySnapshot(string candidatePath)
    {
        if (!File.Exists(candidatePath))
            return null;

        try
        {
            return AtomicJsonFile.Read<List<MarketAcquisitionReportOutboxEntry>>(candidatePath, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryLoadJournal(
        string candidatePath,
        out List<MarketAcquisitionReportOutboxEntry> loadedEntries,
        out int recordCount,
        out bool requiresTailRepair)
    {
        loadedEntries = [];
        recordCount = 0;
        requiresTailRepair = false;
        if (!File.Exists(candidatePath))
            return false;

        using var reader = new StreamReader(candidatePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var entriesById = new Dictionary<string, MarketAcquisitionReportOutboxEntry>(StringComparer.Ordinal);
        var recognizedJournal = false;
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (!recognizedJournal && line.TrimStart().StartsWith("[", StringComparison.Ordinal))
                return false;

            recognizedJournal = true;
            OutboxJournalRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<OutboxJournalRecord>(line, JsonOptions);
            }
            catch (JsonException) when (reader.EndOfStream)
            {
                // A process termination can leave only the final append incomplete.
                requiresTailRepair = true;
                break;
            }

            if (record == null)
                throw new InvalidDataException($"Outbox journal '{candidatePath}' contains an empty record.");

            ApplyJournalRecord(entriesById, record, candidatePath);
            recordCount++;
        }

        loadedEntries = entriesById.Values.ToList();
        return recognizedJournal || new FileInfo(candidatePath).Length == 0;
    }

    private static void ApplyJournalRecord(
        Dictionary<string, MarketAcquisitionReportOutboxEntry> entriesById,
        OutboxJournalRecord record,
        string candidatePath)
    {
        switch (record.Operation)
        {
            case OutboxJournalOperation.Put when record.Entry != null:
                entriesById.TryAdd(record.Entry.Id, record.Entry);
                break;
            case OutboxJournalOperation.Remove when record.Ids != null:
                foreach (var id in record.Ids)
                    entriesById.Remove(id);
                break;
            default:
                throw new InvalidDataException(
                    $"Outbox journal '{candidatePath}' contains an invalid '{record.Operation}' record.");
        }
    }

    private void AppendJournalRecord(OutboxJournalRecord record)
    {
        EnsureParentDirectory();
        var json = JsonSerializer.Serialize(record, JsonOptions);
        var bytes = Utf8NoBom.GetBytes(json + Environment.NewLine);
        using var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        observePhysicalWrite?.Invoke(bytes.LongLength);
    }

    private void CompactJournalAtStartupWhenNeeded(int recordCount)
    {
        var recordThreshold = Math.Max(StartupCompactionMinimumRecordCount, entries.Count * 4);
        if (new FileInfo(path).Length < StartupCompactionSizeThresholdBytes
            && recordCount < recordThreshold)
        {
            return;
        }

        RewriteJournal();
    }

    private void RewriteJournal()
    {
        EnsureParentDirectory();
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            long bytesWritten = 0;
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                foreach (var entry in entries.OrderBy(entry => entry.EnqueuedAtUtc))
                {
                    var record = new OutboxJournalRecord
                    {
                        Operation = OutboxJournalOperation.Put,
                        Entry = entry,
                    };
                    var bytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine);
                    stream.Write(bytes);
                    bytesWritten += bytes.LongLength;
                }

                stream.Flush(flushToDisk: true);
            }
            observePhysicalWrite?.Invoke(bytesWritten);

            if (File.Exists(path))
                File.Replace(temporaryPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private void EnsureParentDirectory()
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }

    private static class OutboxJournalOperation
    {
        public const string Put = "put";
        public const string Remove = "remove";
    }

    private sealed record OutboxJournalRecord
    {
        public string Operation { get; init; } = string.Empty;
        public MarketAcquisitionReportOutboxEntry? Entry { get; init; }
        public IReadOnlyList<string>? Ids { get; init; }
    }
}

internal sealed class VolatileMarketAcquisitionReportOutbox : IMarketAcquisitionReportOutbox
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object sync = new();
    private readonly List<MarketAcquisitionReportOutboxEntry> entries = [];

    public MarketAcquisitionReportOutboxEntry Put<T>(string id, string reportType, T payload)
    {
        lock (sync)
        {
            var existing = entries.FirstOrDefault(candidate => candidate.Id.Equals(id, StringComparison.Ordinal));
            if (existing != null)
                return existing;
            var entry = new MarketAcquisitionReportOutboxEntry
            {
                Id = id,
                ReportType = reportType,
                PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
                EnqueuedAtUtc = DateTimeOffset.UtcNow,
            };
            entries.Add(entry);
            return entry;
        }
    }

    public IReadOnlyList<MarketAcquisitionReportOutboxEntry> Snapshot()
    {
        lock (sync)
            return entries.ToArray();
    }

    public void Remove(string id) => RemoveMany([id]);

    public void RemoveMany(IReadOnlyCollection<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
            return;

        var idSet = ids.ToHashSet(StringComparer.Ordinal);
        lock (sync)
            entries.RemoveAll(entry => idSet.Contains(entry.Id));
    }

    public T Deserialize<T>(MarketAcquisitionReportOutboxEntry entry) =>
        JsonSerializer.Deserialize<T>(entry.PayloadJson, JsonOptions)
        ?? throw new InvalidDataException($"Outbox entry '{entry.Id}' could not be deserialized.");
}
