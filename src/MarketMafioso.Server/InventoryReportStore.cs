using MarketMafioso.Server.Inventory;
using MarketMafioso.Server.Sqlite;
using Microsoft.Data.Sqlite;

namespace MarketMafioso.Server;

public sealed class InventoryReportStore
{
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly IConfiguration configuration;
    private readonly ILogger<InventoryReportStore> log;
    private readonly InventoryReportWritePersistence writePersistence;
    private readonly InventoryReportReadQueries readQueries;
    private readonly InventoryRawJsonRetention rawJsonRetention;

    public string ReportDirectory { get; }

    public InventoryReportStore(
        SqliteConnectionFactory connectionFactory,
        IConfiguration configuration,
        ILogger<InventoryReportStore> log)
    {
        this.connectionFactory = connectionFactory;
        this.configuration = configuration;
        this.log = log;
        writePersistence = new InventoryReportWritePersistence(connectionFactory, configuration, log);
        readQueries = new InventoryReportReadQueries(connectionFactory);
        rawJsonRetention = new InventoryRawJsonRetention(connectionFactory, configuration);

        ReportDirectory = Path.GetDirectoryName(connectionFactory.DatabasePath) ?? connectionFactory.DatabasePath;
    }

    public Task<StoredInventoryReport> SaveAsync(
        InventoryReport report,
        string? apiKey,
        CancellationToken cancellationToken) =>
        SaveAsync(1, report, apiKey, null, cancellationToken);

    public async Task<StoredInventoryReport> SaveAsync(
        long accountId,
        InventoryReport report,
        string? apiKeyLabel,
        string? rawReportJson,
        CancellationToken cancellationToken)
    {
        var receivedAt = DateTimeOffset.UtcNow;
        var id = $"{receivedAt:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..26];
        return await SaveCoreAsync(accountId, id, receivedAt, report, apiKeyLabel, rawReportJson, cancellationToken);
    }

    public Task<StoredInventoryReport> SaveImportedAsync(
        long accountId,
        StoredInventoryReport stored,
        string rawReportJson,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stored);
        return SaveCoreAsync(
            accountId,
            stored.Id,
            stored.ReceivedAt,
            stored.Report,
            stored.ApiKeyLabel,
            rawReportJson,
            cancellationToken);
    }

    public Task<bool> ExistsAsync(long accountId, string id, CancellationToken cancellationToken) =>
        readQueries.ExistsAsync(accountId, id, cancellationToken);

    public Task<bool> CanApplyDeltaBaseAsync(long accountId, string id, CancellationToken cancellationToken) =>
        readQueries.CanApplyDeltaBaseAsync(accountId, id, cancellationToken);

    public async Task<StoredInventoryReport> SaveDeltaAsync(long accountId, InventoryReportDelta delta,
        string? apiKeyLabel, CancellationToken token)
    {
        await writeGate.WaitAsync(token);
        try
        {
            var before = await GetAsync(accountId, delta.BaseSnapshotId, token)
                ?? throw new InventoryDeltaConflictException("The inventory delta base is missing.");
            var report = InventoryReportDeltaApplier.Apply(before.Id, before.Report, delta);
            if (!InventoryReportEvidence.HasSnapshotEvidence(report))
                throw new InventoryDeltaConflictException("The inventory delta removes all observation evidence.");
            var hash = InventorySemanticFingerprint.Compute(report);
            var receivedAt = DateTimeOffset.UtcNow;
            var id = $"{receivedAt:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..26];
            // The response needs only a summary, not another full database reconstruction.
            var summary = InventoryReportRowMapper.CreateSummary(id, receivedAt, report);
            await using var connection = await connectionFactory.OpenConnectionAsync(token);
            using (var write = connectionFactory.BeginWrite(connection))
            {
                var result = await writePersistence.WriteDeltaAsync(connection, write.Transaction, accountId,
                    before, report, delta, id, receivedAt, apiKeyLabel, hash, token);
                if (result.SnapshotId != id)
                    throw new InventoryDeltaConflictException("The inventory head changed during delta persistence.");
                await write.Transaction.CommitAsync(token);
            }
            return new StoredInventoryReport
            {
                Id = id,
                ReceivedAt = receivedAt,
                ApiKeyLabel = string.IsNullOrWhiteSpace(apiKeyLabel) ? null : "provided",
                Report = report,
                Summary = summary,
            };
        }
        finally { writeGate.Release(); }
    }

    private async Task<StoredInventoryReport> SaveCoreAsync(
        long accountId,
        string id,
        DateTimeOffset receivedAt,
        InventoryReport report,
        string? apiKeyLabel,
        string? rawReportJson,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        await writeGate.WaitAsync(cancellationToken);
        try
        {
            var metadata = report.Metadata ?? new InventoryReportMetadata();

            await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
            InventoryWriteResult writeResult;
            using (var write = connectionFactory.BeginWrite(connection))
            {
                var transaction = write.Transaction;
                writeResult = await writePersistence.WriteSnapshotAsync(
                    connection,
                    transaction,
                    accountId,
                    id,
                    receivedAt,
                    report,
                    metadata,
                    apiKeyLabel,
                    rawReportJson,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            return (await GetAsync(accountId, writeResult.SnapshotId, cancellationToken))
                ?? throw new InvalidOperationException($"Saved inventory head {writeResult.SnapshotId} could not be reloaded.");
        }
        finally
        {
            writeGate.Release();
        }
    }

    public Task<IReadOnlyList<ReportSummary>> ListSummariesAsync(CancellationToken cancellationToken) =>
        ListSummariesAsync(1, null, cancellationToken);

    public Task<IReadOnlyList<CharacterSummary>> ListCharactersAsync(
        long accountId,
        CancellationToken cancellationToken) =>
        readQueries.ListCharactersAsync(accountId, cancellationToken);

    public Task<IReadOnlyList<ReportSummary>> ListSummariesAsync(
        long accountId,
        long? characterId,
        CancellationToken cancellationToken) =>
        readQueries.ListSummariesAsync(accountId, characterId, cancellationToken);

    public Task<InventoryRetentionSummary> GetRetentionSummaryAsync(
        IReadOnlyList<long> accountIds,
        CancellationToken cancellationToken) =>
        readQueries.GetRetentionSummaryAsync(accountIds, cancellationToken);

    public Task<InventoryRevisionState> GetRevisionAsync(
        IReadOnlyList<long> accountIds,
        CancellationToken cancellationToken) =>
        readQueries.GetRevisionAsync(accountIds, cancellationToken);

    public Task<StoredInventoryReport?> GetLatestAsync(CancellationToken cancellationToken) =>
        GetLatestAsync(1, null, cancellationToken);

    public Task<StoredInventoryReport?> GetLatestAsync(
        long accountId,
        long? characterId,
        CancellationToken cancellationToken) =>
        readQueries.GetLatestAsync(accountId, characterId, cancellationToken);

    public async Task<StoredInventoryReport?> GetLatestAsync(
        IReadOnlyList<long> accountIds,
        long? characterId,
        CancellationToken cancellationToken)
    {
        StoredInventoryReport? newest = null;
        foreach (var accountId in accountIds)
        {
            var candidate = await GetLatestAsync(accountId, characterId, cancellationToken);
            if (candidate != null && (newest == null || candidate.ReceivedAt > newest.ReceivedAt))
                newest = candidate;
        }

        return newest;
    }

    public async Task<IReadOnlyList<StoredInventoryReport>> GetLatestByCharacterAsync(
        IReadOnlyList<long> accountIds,
        CancellationToken cancellationToken)
    {
        var references = await readQueries.ListLatestSnapshotIdsByCharacterAsync(accountIds, cancellationToken);
        var reports = new List<StoredInventoryReport>(references.Count);
        foreach (var reference in references)
        {
            var report = await GetAsync(reference.AccountId, reference.SnapshotId, cancellationToken);
            if (report != null)
                reports.Add(report);
        }

        return reports;
    }

    public Task<StoredInventoryReport?> GetAsync(string id, CancellationToken cancellationToken) =>
        GetAsync(1, id, cancellationToken);

    public Task<RawInventoryReportJson?> GetRawJsonAsync(string id, CancellationToken cancellationToken) =>
        GetRawJsonAsync(1, id, cancellationToken);

    public Task<RawInventoryReportJson?> GetRawJsonAsync(
        long accountId,
        string id,
        CancellationToken cancellationToken) =>
        rawJsonRetention.GetAsync(accountId, id, cancellationToken);

    public Task<RawInventoryReportJson?> GetLatestRawJsonAsync(CancellationToken cancellationToken) =>
        GetLatestRawJsonAsync(1, null, cancellationToken);

    public async Task<RawInventoryReportJson?> GetLatestRawJsonAsync(
        long accountId,
        long? characterId,
        CancellationToken cancellationToken)
    {
        var latest = await GetLatestAsync(accountId, characterId, cancellationToken);
        return latest == null
            ? null
            : await GetRawJsonAsync(accountId, latest.Id, cancellationToken);
    }

    public Task<StoredInventoryReport?> GetAsync(
        long accountId,
        string id,
        CancellationToken cancellationToken) =>
        readQueries.GetAsync(accountId, id, cancellationToken);

    public async Task<StoredInventoryReport?> GetAsync(
        IReadOnlyList<long> accountIds,
        string id,
        CancellationToken cancellationToken)
    {
        foreach (var accountId in accountIds)
        {
            var report = await GetAsync(accountId, id, cancellationToken);
            if (report != null)
                return report;
        }

        return null;
    }

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken) =>
        DeleteAsync(1, id, cancellationToken);

    public async Task<bool> DeleteAsync(long accountId, string id, CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            return await writePersistence.DeleteAsync(accountId, id, cancellationToken);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public Task<int> DeleteAllAsync(CancellationToken cancellationToken) =>
        DeleteAllAsync(1, cancellationToken);

    public async Task<int> DeleteAllAsync(long accountId, CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            return await writePersistence.DeleteAllAsync(accountId, cancellationToken);
        }
        finally
        {
            writeGate.Release();
        }
    }
}

public sealed record RawInventoryReportJson(string Id, string? RawJson);

public sealed record CharacterSummary(
    long Id,
    string CharacterName,
    string? HomeWorld,
    DateTimeOffset LastSeenAt,
    int? ServiceAccountNumber = null,
    string? ServiceAccountKey = null);

public sealed record InventoryRetentionSummary
{
    public int SnapshotCount { get; init; }
    public int RawJsonRetainedCount { get; init; }
    public int RawJsonPrunedCount { get; init; }
    public int CurrentHeadCount { get; init; }
    public int HistoryCount { get; init; }
    public DateTimeOffset? NewestSnapshotReceivedAtUtc { get; init; }
    public DateTimeOffset? OldestSnapshotReceivedAtUtc { get; init; }
}

public sealed record InventoryRevisionState(string Token, DateTimeOffset? UpdatedAtUtc);
