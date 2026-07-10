using MarketMafioso.Server.Inventory;
using MarketMafioso.Server.Sqlite;
using Microsoft.Data.Sqlite;

namespace MarketMafioso.Server;

public sealed class InventoryReportStore
{
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
        writePersistence = new InventoryReportWritePersistence(connectionFactory, configuration);
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

        var metadata = report.Metadata ?? new InventoryReportMetadata();

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await writePersistence.WriteSnapshotAsync(
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
        await rawJsonRetention.PruneAsync(connection, transaction, accountId, cancellationToken);
        await writePersistence.PruneSnapshotsAsync(connection, transaction, accountId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return (await GetAsync(accountId, id, cancellationToken))
            ?? throw new InvalidOperationException($"Saved snapshot {id} could not be reloaded.");
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

    public Task<StoredInventoryReport?> GetLatestAsync(CancellationToken cancellationToken) =>
        GetLatestAsync(1, null, cancellationToken);

    public Task<StoredInventoryReport?> GetLatestAsync(
        long accountId,
        long? characterId,
        CancellationToken cancellationToken) =>
        readQueries.GetLatestAsync(accountId, characterId, cancellationToken);

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

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken) =>
        DeleteAsync(1, id, cancellationToken);

    public Task<bool> DeleteAsync(long accountId, string id, CancellationToken cancellationToken) =>
        writePersistence.DeleteAsync(accountId, id, cancellationToken);

    public Task<int> DeleteAllAsync(CancellationToken cancellationToken) =>
        DeleteAllAsync(1, cancellationToken);

    public Task<int> DeleteAllAsync(long accountId, CancellationToken cancellationToken) =>
        writePersistence.DeleteAllAsync(accountId, cancellationToken);
}

public sealed record RawInventoryReportJson(string Id, string? RawJson);

public sealed record CharacterSummary(long Id, string CharacterName, string? HomeWorld, DateTimeOffset LastSeenAt);

public sealed record InventoryRetentionSummary
{
    public int SnapshotCount { get; init; }
    public int RawJsonRetainedCount { get; init; }
    public int RawJsonPrunedCount { get; init; }
    public DateTimeOffset? NewestSnapshotReceivedAtUtc { get; init; }
    public DateTimeOffset? OldestSnapshotReceivedAtUtc { get; init; }
}
