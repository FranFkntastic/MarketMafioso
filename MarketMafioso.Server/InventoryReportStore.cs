using System.Globalization;
using Microsoft.Data.Sqlite;
using MarketMafioso.Server.Sqlite;

namespace MarketMafioso.Server;

public sealed class InventoryReportStore
{
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly IConfiguration configuration;
    private readonly ILogger<InventoryReportStore> log;

    public string ReportDirectory { get; }

    public InventoryReportStore(
        SqliteConnectionFactory connectionFactory,
        IConfiguration configuration,
        ILogger<InventoryReportStore> log)
    {
        this.connectionFactory = connectionFactory;
        this.configuration = configuration;
        this.log = log;

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

    public async Task<bool> ExistsAsync(long accountId, string id, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM snapshots WHERE account_id = $accountId AND id = $id";
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteScalarAsync(cancellationToken) != null;
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

        var metadata = report.Metadata ?? new InventoryReportMetadata();

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var characterId = await UpsertCharacterAsync(connection, transaction, accountId, report, receivedAt, cancellationToken);

        await InsertSnapshotAsync(
            connection,
            transaction,
            accountId,
            characterId,
            id,
            receivedAt,
            apiKeyLabel,
            rawReportJson,
            report,
            metadata,
            cancellationToken);
        await InsertOwnerAsync(connection, transaction, id, "player", "Player Inventory", null, null, null, 0, report.PlayerInventory, [], cancellationToken);

        for (var i = 0; i < report.Retainers.Count; i++)
        {
            var retainer = report.Retainers[i];
            await InsertOwnerAsync(
                connection,
                transaction,
                id,
                "retainer",
                retainer.RetainerName,
                retainer.RetainerId,
                retainer.LastUpdated,
                retainer.Gil,
                i + 1,
                retainer.Bags,
                retainer.MarketListings,
                cancellationToken);
        }

        await PruneRawJsonAsync(connection, transaction, accountId, cancellationToken);
        await PruneSnapshotsAsync(connection, transaction, accountId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return (await GetAsync(accountId, id, cancellationToken))
            ?? throw new InvalidOperationException($"Saved snapshot {id} could not be reloaded.");
    }

    public Task<IReadOnlyList<ReportSummary>> ListSummariesAsync(CancellationToken cancellationToken) =>
        ListSummariesAsync(1, null, cancellationToken);

    public async Task<IReadOnlyList<CharacterSummary>> ListCharactersAsync(
        long accountId,
        CancellationToken cancellationToken)
    {
        var characters = new List<CharacterSummary>();
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, character_name, home_world, last_seen_at_utc
            FROM characters
            WHERE account_id = $accountId
            ORDER BY last_seen_at_utc DESC, character_name COLLATE NOCASE
            """;
        command.Parameters.AddWithValue("$accountId", accountId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            characters.Add(new CharacterSummary(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return characters;
    }

    public async Task<IReadOnlyList<ReportSummary>> ListSummariesAsync(
        long accountId,
        long? characterId,
        CancellationToken cancellationToken)
    {
        var reports = await ListReportsAsync(accountId, characterId, cancellationToken);
        return reports
            .Select(r => r.Summary)
            .OrderByDescending(r => r.ReceivedAt)
            .ToList();
    }

    public Task<StoredInventoryReport?> GetLatestAsync(CancellationToken cancellationToken) =>
        GetLatestAsync(1, null, cancellationToken);

    public async Task<StoredInventoryReport?> GetLatestAsync(
        long accountId,
        long? characterId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = characterId == null
            ? """
              SELECT id FROM snapshots
              WHERE account_id = $accountId
              ORDER BY received_at_utc DESC
              LIMIT 1
              """
            : """
              SELECT id FROM snapshots
              WHERE account_id = $accountId AND character_id = $characterId
              ORDER BY received_at_utc DESC
              LIMIT 1
              """;
        command.Parameters.AddWithValue("$accountId", accountId);
        if (characterId != null)
            command.Parameters.AddWithValue("$characterId", characterId.Value);

        var id = await command.ExecuteScalarAsync(cancellationToken) as string;
        return id == null ? null : await GetAsync(accountId, id, cancellationToken);
    }

    public Task<StoredInventoryReport?> GetAsync(string id, CancellationToken cancellationToken) =>
        GetAsync(1, id, cancellationToken);

    public Task<RawInventoryReportJson?> GetRawJsonAsync(string id, CancellationToken cancellationToken) =>
        GetRawJsonAsync(1, id, cancellationToken);

    public async Task<RawInventoryReportJson?> GetRawJsonAsync(
        long accountId,
        string id,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT raw_report_json
            FROM snapshots
            WHERE account_id = $accountId AND id = $id
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$id", id);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result == null)
            return null;

        return new RawInventoryReportJson(id, result == DBNull.Value ? null : (string)result);
    }

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

    public async Task<StoredInventoryReport?> GetAsync(
        long accountId,
        string id,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var snapshot = await ReadSnapshotAsync(connection, accountId, id, cancellationToken);
        if (snapshot == null)
            return null;

        var playerBags = new List<InventoryBag>();
        var retainers = new List<RetainerReport>();

        await using var ownerCommand = connection.CreateCommand();
        ownerCommand.CommandText = """
            SELECT id, owner_type, owner_name, retainer_id, last_updated, gil
            FROM inventory_owners
            WHERE snapshot_id = $snapshotId
            ORDER BY sort_order
            """;
        ownerCommand.Parameters.AddWithValue("$snapshotId", id);
        await using var ownerReader = await ownerCommand.ExecuteReaderAsync(cancellationToken);
        while (await ownerReader.ReadAsync(cancellationToken))
        {
            var ownerId = ownerReader.GetInt64(0);
            var ownerType = ownerReader.GetString(1);
            var bags = await ReadBagsAsync(connection, ownerId, cancellationToken);
            if (ownerType == "player")
            {
                playerBags.AddRange(bags);
                continue;
            }

            var retainerId = ownerReader.IsDBNull(3) ? 0UL : checked((ulong)ownerReader.GetInt64(3));
            var gil = ownerReader.IsDBNull(5) ? 0UL : checked((ulong)ownerReader.GetInt64(5));
            retainers.Add(new RetainerReport
            {
                RetainerName = ownerReader.GetString(2),
                RetainerId = retainerId,
                LastUpdated = ownerReader.IsDBNull(4) ? string.Empty : ownerReader.GetString(4),
                Gil = gil,
                Bags = bags,
                MarketListings = await ReadMarketListingsAsync(connection, ownerId, cancellationToken),
            });
        }

        var report = new InventoryReport
        {
            Metadata = snapshot.Metadata,
            CharacterName = snapshot.CharacterName,
            HomeWorld = snapshot.HomeWorld,
            Timestamp = snapshot.ReportTimestamp,
            PlayerInventory = playerBags,
            Retainers = retainers,
        };

        return new StoredInventoryReport
        {
            Id = id,
            ReceivedAt = snapshot.ReceivedAt,
            ApiKeyLabel = snapshot.ApiKeyLabel,
            Report = report,
            Summary = CreateSummary(id, snapshot.ReceivedAt, report),
        };
    }

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken) =>
        DeleteAsync(1, id, cancellationToken);

    public async Task<bool> DeleteAsync(long accountId, string id, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM snapshots WHERE account_id = $accountId AND id = $id";
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public Task<int> DeleteAllAsync(CancellationToken cancellationToken) =>
        DeleteAllAsync(1, cancellationToken);

    public async Task<int> DeleteAllAsync(long accountId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM snapshots WHERE account_id = $accountId";
        command.Parameters.AddWithValue("$accountId", accountId);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<StoredInventoryReport>> ListReportsAsync(
        long accountId,
        long? characterId,
        CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = characterId == null
            ? """
              SELECT id FROM snapshots
              WHERE account_id = $accountId
              ORDER BY received_at_utc DESC
              """
            : """
              SELECT id FROM snapshots
              WHERE account_id = $accountId AND character_id = $characterId
              ORDER BY received_at_utc DESC
              """;
        command.Parameters.AddWithValue("$accountId", accountId);
        if (characterId != null)
            command.Parameters.AddWithValue("$characterId", characterId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            ids.Add(reader.GetString(0));

        var reports = new List<StoredInventoryReport>();
        foreach (var id in ids)
        {
            var report = await GetAsync(accountId, id, cancellationToken);
            if (report != null)
                reports.Add(report);
        }

        return reports;
    }

    private static async Task<long?> UpsertCharacterAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long accountId,
        InventoryReport report,
        DateTimeOffset seenAt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(report.CharacterName))
            return null;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO characters (account_id, character_name, home_world, first_seen_at_utc, last_seen_at_utc)
            VALUES ($accountId, $characterName, $homeWorld, $seenAt, $seenAt)
            ON CONFLICT(account_id, character_name, home_world)
            DO UPDATE SET last_seen_at_utc = excluded.last_seen_at_utc
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$characterName", report.CharacterName);
        command.Parameters.AddWithValue("$homeWorld", (object?)report.HomeWorld ?? DBNull.Value);
        command.Parameters.AddWithValue("$seenAt", seenAt.ToString("O", CultureInfo.InvariantCulture));
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task InsertSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long accountId,
        long? characterId,
        string id,
        DateTimeOffset receivedAt,
        string? apiKeyLabel,
        string? rawReportJson,
        InventoryReport report,
        InventoryReportMetadata metadata,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO snapshots (
                id,
                account_id,
                character_id,
                received_at_utc,
                api_key_label,
                character_name,
                home_world,
                report_timestamp,
                schema_version,
                source_plugin,
                plugin_version,
                generated_at_utc,
                raw_report_json,
                raw_json_retained_at_utc)
            VALUES (
                $id,
                $accountId,
                $characterId,
                $receivedAt,
                $apiKeyLabel,
                $characterName,
                $homeWorld,
                $reportTimestamp,
                $schemaVersion,
                $sourcePlugin,
                $pluginVersion,
                $generatedAtUtc,
                $rawReportJson,
                $rawJsonRetainedAt);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$characterId", (object?)characterId ?? DBNull.Value);
        command.Parameters.AddWithValue("$receivedAt", receivedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$apiKeyLabel", string.IsNullOrWhiteSpace(apiKeyLabel) ? DBNull.Value : "provided");
        command.Parameters.AddWithValue("$characterName", (object?)report.CharacterName ?? DBNull.Value);
        command.Parameters.AddWithValue("$homeWorld", (object?)report.HomeWorld ?? DBNull.Value);
        command.Parameters.AddWithValue("$reportTimestamp", report.Timestamp);
        command.Parameters.AddWithValue("$schemaVersion", metadata.SchemaVersion);
        command.Parameters.AddWithValue("$sourcePlugin", metadata.SourcePlugin);
        command.Parameters.AddWithValue("$pluginVersion", metadata.PluginVersion);
        command.Parameters.AddWithValue("$generatedAtUtc", metadata.GeneratedAtUtc);
        command.Parameters.AddWithValue("$rawReportJson", (object?)rawReportJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$rawJsonRetainedAt", rawReportJson == null ? DBNull.Value : receivedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertOwnerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string snapshotId,
        string ownerType,
        string ownerName,
        ulong? retainerId,
        string? lastUpdated,
        ulong? gil,
        int sortOrder,
        IReadOnlyList<InventoryBag> bags,
        IReadOnlyList<RetainerMarketListing> marketListings,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO inventory_owners (snapshot_id, owner_type, owner_name, retainer_id, last_updated, gil, sort_order)
            VALUES ($snapshotId, $ownerType, $ownerName, $retainerId, $lastUpdated, $gil, $sortOrder);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$snapshotId", snapshotId);
        command.Parameters.AddWithValue("$ownerType", ownerType);
        command.Parameters.AddWithValue("$ownerName", ownerName);
        command.Parameters.AddWithValue("$retainerId", retainerId == null ? DBNull.Value : checked((long)retainerId.Value));
        command.Parameters.AddWithValue("$lastUpdated", string.IsNullOrWhiteSpace(lastUpdated) ? DBNull.Value : lastUpdated);
        command.Parameters.AddWithValue("$gil", gil == null ? DBNull.Value : checked((long)gil.Value));
        command.Parameters.AddWithValue("$sortOrder", sortOrder);
        var ownerId = (long)(await command.ExecuteScalarAsync(cancellationToken))!;

        for (var i = 0; i < bags.Count; i++)
            await InsertBagAsync(connection, transaction, ownerId, bags[i], i, cancellationToken);

        for (var i = 0; i < marketListings.Count; i++)
            await InsertMarketListingAsync(connection, transaction, ownerId, marketListings[i], i, cancellationToken);
    }

    private static async Task InsertBagAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long ownerId,
        InventoryBag bag,
        int sortOrder,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO inventory_bags (owner_id, bag_name, sort_order)
            VALUES ($ownerId, $bagName, $sortOrder);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$ownerId", ownerId);
        command.Parameters.AddWithValue("$bagName", bag.BagName);
        command.Parameters.AddWithValue("$sortOrder", sortOrder);
        var bagId = (long)(await command.ExecuteScalarAsync(cancellationToken))!;

        for (var i = 0; i < bag.Items.Count; i++)
            await InsertItemAsync(connection, transaction, bagId, bag.Items[i], i, cancellationToken);
    }

    private static async Task InsertItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long bagId,
        ItemSlot item,
        int sortOrder,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO inventory_items (bag_id, item_id, item_name, item_type, quantity, is_hq, condition, sort_order)
            VALUES ($bagId, $itemId, $itemName, $itemType, $quantity, $isHq, $condition, $sortOrder);
            """;
        command.Parameters.AddWithValue("$bagId", bagId);
        command.Parameters.AddWithValue("$itemId", checked((long)item.ItemId));
        command.Parameters.AddWithValue("$itemName", (object?)item.ItemName ?? DBNull.Value);
        command.Parameters.AddWithValue("$itemType", (object?)item.ItemType ?? DBNull.Value);
        command.Parameters.AddWithValue("$quantity", checked((long)item.Quantity));
        command.Parameters.AddWithValue("$isHq", item.IsHQ ? 1 : 0);
        command.Parameters.AddWithValue("$condition", item.Condition);
        command.Parameters.AddWithValue("$sortOrder", sortOrder);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertMarketListingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long ownerId,
        RetainerMarketListing listing,
        int sortOrder,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO retainer_market_listings (
                owner_id,
                item_id,
                item_name,
                item_type,
                quantity,
                is_hq,
                condition,
                unit_price,
                listed_at,
                sort_order)
            VALUES (
                $ownerId,
                $itemId,
                $itemName,
                $itemType,
                $quantity,
                $isHq,
                $condition,
                $unitPrice,
                $listedAt,
                $sortOrder);
            """;
        command.Parameters.AddWithValue("$ownerId", ownerId);
        command.Parameters.AddWithValue("$itemId", checked((long)listing.ItemId));
        command.Parameters.AddWithValue("$itemName", (object?)listing.ItemName ?? DBNull.Value);
        command.Parameters.AddWithValue("$itemType", (object?)listing.ItemType ?? DBNull.Value);
        command.Parameters.AddWithValue("$quantity", checked((long)listing.Quantity));
        command.Parameters.AddWithValue("$isHq", listing.IsHQ ? 1 : 0);
        command.Parameters.AddWithValue("$condition", listing.Condition);
        command.Parameters.AddWithValue("$unitPrice", listing.UnitPrice == null ? DBNull.Value : checked((long)listing.UnitPrice.Value));
        command.Parameters.AddWithValue("$listedAt", string.IsNullOrWhiteSpace(listing.ListedAt) ? DBNull.Value : listing.ListedAt);
        command.Parameters.AddWithValue("$sortOrder", sortOrder);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task PruneRawJsonAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long accountId,
        CancellationToken cancellationToken)
    {
        var retentionCount = configuration.GetValue("MarketMafioso:RawJsonRetentionCount", 20);
        if (retentionCount < 0)
            throw new InvalidOperationException("MarketMafioso:RawJsonRetentionCount must be zero or greater.");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE snapshots
            SET raw_report_json = NULL,
                raw_json_retained_at_utc = NULL
            WHERE account_id = $accountId
              AND raw_report_json IS NOT NULL
              AND id NOT IN (
                  SELECT id
                  FROM snapshots
                  WHERE account_id = $accountId
                    AND raw_report_json IS NOT NULL
                  ORDER BY received_at_utc DESC
                  LIMIT $retentionCount
              );
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$retentionCount", retentionCount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task PruneSnapshotsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long accountId,
        CancellationToken cancellationToken)
    {
        var retentionCount = configuration.GetValue("MarketMafioso:SnapshotRetentionCount", 500);
        if (retentionCount < 1)
            throw new InvalidOperationException("MarketMafioso:SnapshotRetentionCount must be one or greater.");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM snapshots
            WHERE account_id = $accountId
              AND id NOT IN (
                  SELECT id
                  FROM snapshots
                  WHERE account_id = $accountId
                  ORDER BY received_at_utc DESC
                  LIMIT $retentionCount
              );
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$retentionCount", retentionCount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<SnapshotRow?> ReadSnapshotAsync(
        SqliteConnection connection,
        long accountId,
        string id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                received_at_utc,
                api_key_label,
                character_name,
                home_world,
                report_timestamp,
                schema_version,
                source_plugin,
                plugin_version,
                generated_at_utc
            FROM snapshots
            WHERE account_id = $accountId AND id = $id
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new SnapshotRow(
            DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            new InventoryReportMetadata
            {
                SchemaVersion = reader.GetInt32(5),
                SourcePlugin = reader.GetString(6),
                PluginVersion = reader.GetString(7),
                GeneratedAtUtc = reader.GetString(8),
            });
    }

    private static async Task<List<InventoryBag>> ReadBagsAsync(
        SqliteConnection connection,
        long ownerId,
        CancellationToken cancellationToken)
    {
        var bags = new List<InventoryBag>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, bag_name
            FROM inventory_bags
            WHERE owner_id = $ownerId
            ORDER BY sort_order
            """;
        command.Parameters.AddWithValue("$ownerId", ownerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var bagId = reader.GetInt64(0);
            bags.Add(new InventoryBag
            {
                BagName = reader.GetString(1),
                Items = await ReadItemsAsync(connection, bagId, cancellationToken),
            });
        }

        return bags;
    }

    private static async Task<List<ItemSlot>> ReadItemsAsync(
        SqliteConnection connection,
        long bagId,
        CancellationToken cancellationToken)
    {
        var items = new List<ItemSlot>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT item_id, item_name, item_type, quantity, is_hq, condition
            FROM inventory_items
            WHERE bag_id = $bagId
            ORDER BY sort_order
            """;
        command.Parameters.AddWithValue("$bagId", bagId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ItemSlot
            {
                ItemId = checked((uint)reader.GetInt64(0)),
                ItemName = reader.IsDBNull(1) ? null : reader.GetString(1),
                ItemType = reader.IsDBNull(2) ? null : reader.GetString(2),
                Quantity = checked((uint)reader.GetInt64(3)),
                IsHQ = reader.GetInt32(4) == 1,
                Condition = reader.GetFloat(5),
            });
        }

        return items;
    }

    private static async Task<List<RetainerMarketListing>> ReadMarketListingsAsync(
        SqliteConnection connection,
        long ownerId,
        CancellationToken cancellationToken)
    {
        var listings = new List<RetainerMarketListing>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT item_id, item_name, item_type, quantity, is_hq, condition, unit_price, listed_at
            FROM retainer_market_listings
            WHERE owner_id = $ownerId
            ORDER BY sort_order
            """;
        command.Parameters.AddWithValue("$ownerId", ownerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            listings.Add(new RetainerMarketListing
            {
                ItemId = checked((uint)reader.GetInt64(0)),
                ItemName = reader.IsDBNull(1) ? null : reader.GetString(1),
                ItemType = reader.IsDBNull(2) ? null : reader.GetString(2),
                Quantity = checked((uint)reader.GetInt64(3)),
                IsHQ = reader.GetInt32(4) == 1,
                Condition = reader.GetFloat(5),
                UnitPrice = reader.IsDBNull(6) ? null : checked((uint)reader.GetInt64(6)),
                ListedAt = reader.IsDBNull(7) ? null : reader.GetString(7),
            });
        }

        return listings;
    }

    private static ReportSummary CreateSummary(string id, DateTimeOffset receivedAt, InventoryReport report)
    {
        var playerItems = report.PlayerInventory.SelectMany(b => b.Items).ToList();
        var retainerItems = report.Retainers.SelectMany(r => r.Bags).SelectMany(b => b.Items).ToList();

        return new ReportSummary
        {
            Id = id,
            ReceivedAt = receivedAt,
            CharacterName = report.CharacterName,
            HomeWorld = report.HomeWorld,
            ReportTimestamp = report.Timestamp,
            PlayerBagCount = report.PlayerInventory.Count,
            PlayerItemStacks = playerItems.Count,
            PlayerItemQuantity = checked((int)playerItems.Sum(i => (long)i.Quantity)),
            RetainerCount = report.Retainers.Count,
            RetainerItemStacks = retainerItems.Count,
            RetainerItemQuantity = checked((int)retainerItems.Sum(i => (long)i.Quantity)),
        };
    }

    private sealed record SnapshotRow(
        DateTimeOffset ReceivedAt,
        string? ApiKeyLabel,
        string? CharacterName,
        string? HomeWorld,
        string ReportTimestamp,
        InventoryReportMetadata Metadata);
}

public sealed record RawInventoryReportJson(string Id, string? RawJson);

public sealed record CharacterSummary(long Id, string CharacterName, string? HomeWorld, DateTimeOffset LastSeenAt);
