using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using Newtonsoft.Json;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketAcquisitionWorldVisitCatalog : IDisposable
{
    public const int MaxRecords = 2_000;
    private readonly Configuration config;
    private readonly object sync = new();
    private readonly object writerSync = new();
    private long revision;
    private readonly string path;
    private readonly Timer writer;
    private readonly PersistenceWarning warning = new("world-visit store");
    private List<PersistedMarketAcquisitionWorldVisit> visits = [];
    private bool loaded;
    private bool dirty;
    private bool migrationCommitted;
    private bool disposed;
    private int consecutiveParseFailures;

    public MarketAcquisitionWorldVisitCatalog(Configuration config)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        path = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "market-acquisition-world-visits.json");
        visits = config.LegacyMarketAcquisitionWorldVisits.Where(IsValidVisit).ToList();
        Trim(MaxRecords);
        // Make TTL lookups truthful immediately on startup. Only reads occur here;
        // migration commits, serialization and writes run on the serialized worker.
        try { LoadStore(); }
        catch (Exception ex) { warning.Failed("MarketAcquisitionWorldVisitCatalog.LoadStore", ex); }
        writer = new Timer(_ => FlushStore(), null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
    }

    public IReadOnlyList<PersistedMarketAcquisitionWorldVisit> Visits
    {
        get { lock (sync) return visits.ToArray(); }
    }

    // Called on the framework thread: config itself must not be serialized from the worker.
    public void Tick()
    {
        lock (sync)
        {
            if (!migrationCommitted || config.LegacyMarketAcquisitionWorldVisits.Count == 0) return;
            config.LegacyMarketAcquisitionWorldVisits.Clear();
            config.Save();
        }
    }

    private void ScheduleWrite()
    {
        revision++;
        if (dirty) return; // Fixed coalescing window: sustained probes cannot starve persistence.
        dirty = true;
        writer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
    }

    private void FlushStore()
    {
        lock (writerSync)
        {
            if (disposed) return;
            try
            {
                if (!loaded)
                    LoadStore();
                PersistedMarketAcquisitionWorldVisit[] snapshot;
                long savedRevision;
                lock (sync)
                {
                    if (!dirty) return;
                    Trim(MaxRecords);
                    snapshot = visits.ToArray();
                    savedRevision = revision;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + ".tmp";
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    using var text = new StreamWriter(stream, leaveOpen: true);
                    using var json = new JsonTextWriter(text);
                    JsonSerializer.CreateDefault().Serialize(json, snapshot);
                    json.Flush();
                    text.Flush();
                    stream.Flush(flushToDisk: true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null, ignoreMetadataErrors: true);
                else File.Move(temporary, path);
                lock (sync)
                {
                    dirty = revision != savedRevision;
                    migrationCommitted = true;
                    if (dirty && !disposed) writer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
                }
                warning.Recovered("MarketAcquisitionWorldVisitCatalog.FlushStore");
            }
            catch (Exception ex)
            {
                // Keep unreadable stores intact; LoadStore quarantines repeatedly malformed JSON.
                warning.Failed("MarketAcquisitionWorldVisitCatalog.FlushStore", ex);
                if (!disposed) writer.Change(TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void LoadStore()
    {
        List<PersistedMarketAcquisitionWorldVisit> stored;
        var loadPath = path;
        try
        {
            string contents;
            try { contents = File.ReadAllText(loadPath); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // A crash may leave a durable temporary file before its first atomic promotion.
                loadPath = path + ".tmp";
                try { contents = File.ReadAllText(loadPath); }
                catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException)
                {
                    contents = "[]";
                    loadPath = path;
                }
            }
            stored = JsonConvert.DeserializeObject<List<PersistedMarketAcquisitionWorldVisit>>(contents)
                     ?? throw new JsonSerializationException("World-visit store contained null instead of a list.");
            consecutiveParseFailures = 0;
            if (loadPath != path)
                Plugin.Log.Warning($"[MarketMafioso] Recovered world-visit store from {loadPath}.");
        }
        catch (JsonException)
        {
            if (++consecutiveParseFailures < 3) throw;
            var corruptPath = loadPath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ");
            // Never discard the only copy: if quarantine fails, the worker retries without writing.
            File.Move(loadPath, corruptPath);
            consecutiveParseFailures = 0;
            Plugin.Log.Warning($"[MarketMafioso] Quarantined malformed world-visit store as {corruptPath}; continuing with an empty stored history.");
            stored = [];
        }
        catch
        {
            consecutiveParseFailures = 0;
            throw;
        }
        lock (sync)
        {
            // Replay after a crash between store commit and config cleanup is idempotent.
            visits = stored.Concat(visits).Where(IsValidVisit)
                .GroupBy(v => (v.WorldName.ToUpperInvariant(), v.ItemId, v.HqPolicy.ToUpperInvariant(), v.MaxUnitPrice))
                .Select(g => g.OrderByDescending(v => v.CheckedAtUtc).First()).ToList();
            Trim(MaxRecords);
            loaded = true;
            dirty = true;
        }
    }

    private static bool IsValidVisit(PersistedMarketAcquisitionWorldVisit visit) =>
        visit is not null && !string.IsNullOrWhiteSpace(visit.WorldName) &&
        !string.IsNullOrWhiteSpace(visit.HqPolicy);

    public void Dispose()
    {
        // Disable callbacks before draining; keep the timer valid until an in-flight writer exits.
        writer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        // Drain the same worker without doing the final serialization/write on the framework thread.
        // The store can be briefly held by an external scanner at unload, so retry a few times
        // before giving up rather than dropping every visit since the last successful write.
        const int finalFlushAttempts = 5;
        var stillDirty = true;
        for (var attempt = 1; attempt <= finalFlushAttempts; attempt++)
        {
            System.Threading.Tasks.Task.Run(FlushStore).GetAwaiter().GetResult();
            lock (sync)
                stillDirty = dirty;
            if (!stillDirty)
                break;
            if (attempt < finalFlushAttempts)
                Thread.Sleep(200);
        }
        if (stillDirty)
        {
            int inMemory;
            lock (sync)
                inMemory = visits.Count;
            Plugin.Log.Warning(
                $"[MarketMafioso] World-visit store final flush failed after {finalFlushAttempts} attempts at unload; " +
                $"changes since the last successful write are lost ({inMemory} visit(s) held in memory, {path}). " +
                "Legacy config history is retained if migration had not yet committed.");
        }
        Tick();
        lock (writerSync)
        {
            disposed = true;
            writer.Dispose();
        }
    }

    public void RecordProbe(MarketAcquisitionWorldVisitRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.WorldName))
            throw new InvalidOperationException("World name is required before recording a market acquisition visit.");
        if (record.ItemId == 0)
            throw new InvalidOperationException("Item id is required before recording a market acquisition visit.");
        if (string.IsNullOrWhiteSpace(record.HqPolicy))
            throw new InvalidOperationException("HQ policy is required before recording a market acquisition visit.");
        if (record.MaxUnitPrice == 0)
            throw new InvalidOperationException("Max unit price is required before recording a market acquisition visit.");
        if (record.CheckedAtUtc == default)
            throw new InvalidOperationException("Checked timestamp is required before recording a market acquisition visit.");

        lock (sync)
        {
            var existing = visits.FirstOrDefault(visit =>
                SameKey(
                    visit.WorldName,
                    visit.ItemId,
                    visit.HqPolicy,
                    visit.MaxUnitPrice,
                    record.WorldName,
                    record.ItemId,
                    record.HqPolicy,
                    record.MaxUnitPrice));
            if (existing == null)
                visits.Add(ToPersisted(record, canonicalWorldName: null));
            else
                visits[visits.IndexOf(existing)] = ToPersisted(record, existing.WorldName);
            Trim(MaxRecords);
            ScheduleWrite();
        }
    }

    public bool WasRecentlyChecked(
        string worldName,
        uint itemId,
        string hqPolicy,
        uint maxUnitPrice,
        DateTimeOffset nowUtc,
        TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero)
            return false;

        return Visits.Any(visit =>
            SameKey(visit.WorldName, visit.ItemId, visit.HqPolicy, visit.MaxUnitPrice, worldName, itemId, hqPolicy, maxUnitPrice) &&
            MarketAcquisitionLiveCandidateStatuses.IsConclusiveWorldVisitResult(visit.Result) &&
            IsWithinTtl(visit.CheckedAtUtc, nowUtc, ttl));
    }

    public PersistedMarketAcquisitionWorldVisit? FindRecent(
        string worldName,
        uint itemId,
        string hqPolicy,
        uint maxUnitPrice,
        DateTimeOffset nowUtc,
        TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero)
            return null;

        return Visits
            .Where(visit =>
                SameKey(visit.WorldName, visit.ItemId, visit.HqPolicy, visit.MaxUnitPrice, worldName, itemId, hqPolicy, maxUnitPrice) &&
                MarketAcquisitionLiveCandidateStatuses.IsConclusiveWorldVisitResult(visit.Result) &&
                IsWithinTtl(visit.CheckedAtUtc, nowUtc, ttl))
            .OrderByDescending(visit => visit.CheckedAtUtc)
            .FirstOrDefault();
    }

    public IReadOnlyList<PersistedMarketAcquisitionWorldVisit> FindRecentWorlds(
        uint itemId,
        string hqPolicy,
        uint maxUnitPrice,
        DateTimeOffset nowUtc,
        TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero)
            return [];

        return Visits
            .Where(visit =>
                visit.ItemId == itemId &&
                visit.MaxUnitPrice == maxUnitPrice &&
                visit.HqPolicy.Equals(hqPolicy, StringComparison.OrdinalIgnoreCase) &&
                MarketAcquisitionLiveCandidateStatuses.IsConclusiveWorldVisitResult(visit.Result) &&
                IsWithinTtl(visit.CheckedAtUtc, nowUtc, ttl))
            .GroupBy(visit => visit.WorldName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(visit => visit.CheckedAtUtc).First())
            .OrderBy(visit => visit.WorldName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void Prune(int maxRecords)
    {
        if (maxRecords < 1)
            throw new ArgumentOutOfRangeException(nameof(maxRecords), "Max records must be one or greater.");

        lock (sync)
        {
            if (Trim(Math.Min(maxRecords, MaxRecords))) ScheduleWrite();
        }
    }

    private bool Trim(int maxRecords)
    {
        if (visits.Count <= maxRecords)
            return false;

        visits = visits
            .OrderByDescending(visit => visit.CheckedAtUtc)
            .Take(maxRecords)
            .OrderBy(visit => visit.CheckedAtUtc)
            .ToList();
        return true;
    }

    private static PersistedMarketAcquisitionWorldVisit ToPersisted(
        MarketAcquisitionWorldVisitRecord record,
        string? canonicalWorldName) =>
        new()
        {
            WorldName = canonicalWorldName ?? record.WorldName.Trim(),
            DataCenter = record.DataCenter?.Trim() ?? string.Empty,
            ItemId = record.ItemId,
            ItemName = record.ItemName,
            HqPolicy = record.HqPolicy.Trim(),
            MaxUnitPrice = record.MaxUnitPrice,
            CheckedAtUtc = record.CheckedAtUtc.UtcDateTime,
            Result = record.Result.Trim(),
            PurchasedQuantity = record.PurchasedQuantity,
            SpentGil = record.SpentGil,
            ObservedLegalListingCount = record.ObservedLegalListingCount,
            ObservedLegalQuantity = record.ObservedLegalQuantity,
            ObservedLegalGil = record.ObservedLegalGil,
            Source = record.Source.Trim(),
            RequestId = record.RequestId,
            RouteRunId = record.RouteRunId,
            RouteStopId = record.RouteStopId,
        };

    private static bool SameKey(
        string leftWorld,
        uint leftItemId,
        string leftHqPolicy,
        uint leftMaxUnitPrice,
        string rightWorld,
        uint rightItemId,
        string rightHqPolicy,
        uint rightMaxUnitPrice) =>
        leftItemId == rightItemId &&
        leftMaxUnitPrice == rightMaxUnitPrice &&
        leftWorld.Equals(rightWorld, StringComparison.OrdinalIgnoreCase) &&
        leftHqPolicy.Equals(rightHqPolicy, StringComparison.OrdinalIgnoreCase);

    private static bool IsWithinTtl(DateTime checkedAtUtc, DateTimeOffset nowUtc, TimeSpan ttl) =>
        nowUtc - new DateTimeOffset(DateTime.SpecifyKind(checkedAtUtc, DateTimeKind.Utc)) < ttl;
}

public sealed record MarketAcquisitionWorldVisitRecord
{
    public string WorldName { get; init; } = string.Empty;
    public string? DataCenter { get; init; }
    public uint ItemId { get; init; }
    public string? ItemName { get; init; }
    public string HqPolicy { get; init; } = string.Empty;
    public uint MaxUnitPrice { get; init; }
    public DateTimeOffset CheckedAtUtc { get; init; }
    public string Result { get; init; } = string.Empty;
    public uint PurchasedQuantity { get; init; }
    public uint SpentGil { get; init; }
    public int ObservedLegalListingCount { get; init; }
    public uint ObservedLegalQuantity { get; init; }
    public ulong ObservedLegalGil { get; init; }
    public string Source { get; init; } = string.Empty;
    public string? RequestId { get; init; }
    public string? RouteRunId { get; init; }
    public string? RouteStopId { get; init; }
}
