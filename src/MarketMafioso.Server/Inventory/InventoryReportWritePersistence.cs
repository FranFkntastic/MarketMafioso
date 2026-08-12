using System.Globalization;
using System.Text.Json;
using MarketMafioso.Server.Sqlite;
using Microsoft.Data.Sqlite;

namespace MarketMafioso.Server.Inventory;

internal sealed class InventoryReportWritePersistence(
    SqliteConnectionFactory connectionFactory,
    IConfiguration configuration,
    ILogger log)
{
    private readonly HashSet<string> incompleteIdentityDiagnostics = new(StringComparer.Ordinal);

    public async Task<InventoryWriteResult> WriteSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long accountId,
        string id,
        DateTimeOffset receivedAt,
        InventoryReport report,
        InventoryReportMetadata metadata,
        string? apiKeyLabel,
        string? rawReportJson,
        CancellationToken cancellationToken)
    {
        var characterId = await UpsertCharacterAsync(
            connection,
            transaction,
            accountId,
            report,
            receivedAt,
            cancellationToken);

        var semanticHash = InventorySemanticFingerprint.Compute(report);
        var current = characterId is null
            ? null
            : await ReadCurrentHeadAsync(connection, transaction, accountId, characterId.Value, cancellationToken);
        if (current is not null && IsOlderObservation(report.Timestamp, current.ReportTimestamp))
        {
            await TouchCurrentDeliveryAsync(
                connection,
                transaction,
                current.SnapshotId,
                receivedAt,
                apiKeyLabel,
                cancellationToken);
            log.LogWarning(
                "Ignored an out-of-order inventory report for character {CharacterId}; current observation {CurrentTimestamp} is newer than {IncomingTimestamp}.",
                characterId,
                current.ReportTimestamp,
                report.Timestamp);
            return new InventoryWriteResult(current.SnapshotId, characterId, current.SemanticRevision, false);
        }

        if (current is not null && string.Equals(current.SemanticHash, semanticHash, StringComparison.Ordinal))
        {
            await RefreshCurrentHeadAsync(
                connection,
                transaction,
                current.SnapshotId,
                receivedAt,
                apiKeyLabel,
                report,
                metadata,
                cancellationToken);
            return new InventoryWriteResult(current.SnapshotId, characterId, current.SemanticRevision, false);
        }

        var semanticRevision = current?.SemanticRevision + 1 ?? (characterId is null ? 0 : 1);
        if (current is not null)
            await DemoteCurrentHeadAsync(connection, transaction, current.SnapshotId, cancellationToken);

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
            characterId is not null,
            semanticRevision,
            semanticHash,
            cancellationToken);
        await UpsertItemMetadataCatalogAsync(
            connection,
            transaction,
            accountId,
            receivedAt,
            report,
            cancellationToken);
        await InsertOwnerAsync(connection, transaction, accountId, id, "player", "Player Inventory", null, null, null, null, null, report.PlayerStorage, 0, report.PlayerInventory, [], cancellationToken);

        for (var i = 0; i < report.Retainers.Count; i++)
        {
            var retainer = report.Retainers[i];
            await InsertOwnerAsync(
                connection,
                transaction,
                accountId,
                id,
                "retainer",
                retainer.RetainerName,
                retainer.RetainerId,
                retainer.LastUpdated,
                retainer.Gil,
                retainer.GilObservedAtUtc,
                retainer.ListingsObservedAtUtc,
                retainer.Storage,
                i + 1,
                retainer.Bags,
                retainer.MarketListings,
                cancellationToken);
        }

        if (characterId is not null)
            await AdvanceAccountRevisionAsync(connection, transaction, accountId, receivedAt, cancellationToken);

        return new InventoryWriteResult(id, characterId, semanticRevision, true);
    }

    public async Task PruneSnapshotsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long accountId,
        CancellationToken cancellationToken)
    {
        var retentionCount = ResolveHistoryRetention(configuration);
        if (retentionCount < 1)
            throw new InvalidOperationException("MarketMafioso:InventoryHistoryRetentionPerCharacter must be one or greater.");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH ranked_history AS (
                SELECT
                    id,
                    ROW_NUMBER() OVER (
                        PARTITION BY COALESCE(character_id, -1)
                        ORDER BY received_at_utc DESC, id DESC
                    ) AS history_position
                FROM snapshots
                WHERE account_id = $accountId
                  AND is_current = 0
            )
            DELETE FROM snapshots
            WHERE account_id = $accountId
              AND is_current = 0
              AND id IN (
                  SELECT id
                  FROM ranked_history
                  WHERE history_position > $retentionCount
              );
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$retentionCount", retentionCount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(long accountId, string id, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM snapshots WHERE account_id = $accountId AND id = $id RETURNING character_id, is_current";
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$id", id);
        long? characterId;
        bool wasCurrent;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                return false;
            characterId = reader.IsDBNull(0) ? null : reader.GetInt64(0);
            wasCurrent = reader.GetInt32(1) != 0;
        }

        if (wasCurrent && characterId is { } deletedCharacterId)
        {
            await PromoteNewestHistoryAsync(connection, transaction, accountId, deletedCharacterId, cancellationToken);
            await AdvanceAccountRevisionAsync(connection, transaction, accountId, DateTimeOffset.UtcNow, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<int> DeleteAllAsync(long accountId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM snapshots WHERE account_id = $accountId";
        command.Parameters.AddWithValue("$accountId", accountId);
        var deleted = await command.ExecuteNonQueryAsync(cancellationToken);
        if (deleted > 0)
            await AdvanceAccountRevisionAsync(connection, transaction, accountId, DateTimeOffset.UtcNow, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    private static async Task PromoteNewestHistoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long accountId,
        long characterId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE snapshots
            SET is_current = 1
            WHERE id = (
                SELECT id
                FROM snapshots
                WHERE account_id = $accountId
                  AND character_id = $characterId
                  AND is_current = 0
                ORDER BY semantic_revision DESC, received_at_utc DESC, id DESC
                LIMIT 1
            );
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$characterId", characterId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long?> UpsertCharacterAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long accountId,
        InventoryReport report,
        DateTimeOffset seenAt,
        CancellationToken cancellationToken)
    {
        var characterName = report.CharacterName?.Trim();
        if (string.IsNullOrWhiteSpace(characterName))
            return null;

        var homeWorld = string.IsNullOrWhiteSpace(report.HomeWorld) ? null : report.HomeWorld.Trim();
        var matchingIds = await FindCompleteCharacterIdsAsync(
            connection,
            transaction,
            accountId,
            characterName,
            homeWorld,
            cancellationToken);
        if (matchingIds.Count > 1 ||
            (homeWorld is null &&
             (matchingIds.Count != 1 ||
              report.ServiceAccountNumber is > 0 &&
              matchingIds[0].ServiceAccountNumber is > 0 &&
              report.ServiceAccountNumber != matchingIds[0].ServiceAccountNumber)))
        {
            var diagnosticKey = $"{accountId}:{characterName.ToUpperInvariant()}";
            if (incompleteIdentityDiagnostics.Add(diagnosticKey))
            {
                log.LogWarning(
                    "Stored inventory without a selectable character for {CharacterName}: the home world was unavailable and {CandidateCount} complete candidate(s) existed.",
                    characterName,
                    matchingIds.Count);
            }
            return null;
        }

        if (matchingIds.Count == 1)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE characters
                SET service_account_key = COALESCE($serviceAccountKey, service_account_key),
                    service_account_number = CASE
                        WHEN $serviceAccountNumber IS NULL THEN service_account_number
                        ELSE $serviceAccountNumber
                    END,
                    last_seen_at_utc = $seenAt
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$serviceAccountKey", (object?)report.ServiceAccountKey ?? DBNull.Value);
            update.Parameters.AddWithValue("$serviceAccountNumber", report.ServiceAccountNumber is > 0 ? report.ServiceAccountNumber.Value : DBNull.Value);
            update.Parameters.AddWithValue("$seenAt", seenAt.ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$id", matchingIds[0].Id);
            await update.ExecuteNonQueryAsync(cancellationToken);
            return matchingIds[0].Id;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO characters (
                account_id,
                character_name,
                home_world,
                service_account_key,
                service_account_number,
                first_seen_at_utc,
                last_seen_at_utc)
            VALUES ($accountId, $characterName, $homeWorld, $serviceAccountKey, $serviceAccountNumber, $seenAt, $seenAt)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$characterName", characterName);
        command.Parameters.AddWithValue("$homeWorld", homeWorld!);
        command.Parameters.AddWithValue("$serviceAccountKey", (object?)report.ServiceAccountKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$serviceAccountNumber", report.ServiceAccountNumber is > 0 ? report.ServiceAccountNumber.Value : DBNull.Value);
        command.Parameters.AddWithValue("$seenAt", seenAt.ToString("O", CultureInfo.InvariantCulture));
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<List<CharacterIdentityCandidate>> FindCompleteCharacterIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long accountId,
        string characterName,
        string? homeWorld,
        CancellationToken cancellationToken)
    {
        var candidates = new List<CharacterIdentityCandidate>(2);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = homeWorld is null
            ? """
              SELECT id, service_account_number
              FROM characters
              WHERE account_id = $accountId
                AND lower(trim(character_name)) = lower(trim($characterName))
                AND home_world IS NOT NULL
                AND trim(home_world) <> ''
              ORDER BY id
              LIMIT 2;
              """
            : """
              SELECT id, service_account_number
              FROM characters
              WHERE account_id = $accountId
                AND lower(trim(character_name)) = lower(trim($characterName))
                AND lower(trim(home_world)) = lower(trim($homeWorld))
              ORDER BY id
              LIMIT 2;
              """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$characterName", characterName);
        if (homeWorld is not null)
            command.Parameters.AddWithValue("$homeWorld", homeWorld);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new CharacterIdentityCandidate(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1)));
        }

        return candidates;
    }

    private sealed record CharacterIdentityCandidate(long Id, int? ServiceAccountNumber);

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
        bool isCurrent,
        long semanticRevision,
        string semanticHash,
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
                service_account_key,
                service_account_number,
                player_gil,
                report_timestamp,
                schema_version,
                source_plugin,
                plugin_version,
                generated_at_utc,
                raw_report_json,
                raw_json_retained_at_utc,
                retainer_management_json,
                is_current,
                semantic_revision,
                semantic_hash,
                last_delivery_at_utc)
            VALUES (
                $id,
                $accountId,
                $characterId,
                $receivedAt,
                $apiKeyLabel,
                $characterName,
                $homeWorld,
                $serviceAccountKey,
                $serviceAccountNumber,
                $playerGil,
                $reportTimestamp,
                $schemaVersion,
                $sourcePlugin,
                $pluginVersion,
                $generatedAtUtc,
                $rawReportJson,
                $rawJsonRetainedAt,
                $retainerManagementJson,
                $isCurrent,
                $semanticRevision,
                $semanticHash,
                $lastDeliveryAtUtc);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$characterId", (object?)characterId ?? DBNull.Value);
        command.Parameters.AddWithValue("$receivedAt", receivedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$apiKeyLabel", string.IsNullOrWhiteSpace(apiKeyLabel) ? DBNull.Value : "provided");
        command.Parameters.AddWithValue("$characterName", (object?)report.CharacterName ?? DBNull.Value);
        command.Parameters.AddWithValue("$homeWorld", (object?)report.HomeWorld ?? DBNull.Value);
        command.Parameters.AddWithValue("$serviceAccountKey", (object?)report.ServiceAccountKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$serviceAccountNumber", report.ServiceAccountNumber is > 0 ? report.ServiceAccountNumber.Value : DBNull.Value);
        command.Parameters.AddWithValue("$playerGil", report.PlayerGil is { } playerGil ? checked((long)playerGil) : DBNull.Value);
        command.Parameters.AddWithValue("$reportTimestamp", report.Timestamp);
        command.Parameters.AddWithValue("$schemaVersion", metadata.SchemaVersion);
        command.Parameters.AddWithValue("$sourcePlugin", metadata.SourcePlugin);
        command.Parameters.AddWithValue("$pluginVersion", metadata.PluginVersion);
        command.Parameters.AddWithValue("$generatedAtUtc", metadata.GeneratedAtUtc);
        command.Parameters.AddWithValue("$rawReportJson", (object?)rawReportJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$rawJsonRetainedAt", rawReportJson == null ? DBNull.Value : receivedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$retainerManagementJson",
            report.RetainerManagement is null
                ? DBNull.Value
                : JsonSerializer.Serialize(report.RetainerManagement));
        command.Parameters.AddWithValue("$isCurrent", isCurrent ? 1 : 0);
        command.Parameters.AddWithValue("$semanticRevision", semanticRevision);
        command.Parameters.AddWithValue("$semanticHash", semanticHash);
        command.Parameters.AddWithValue("$lastDeliveryAtUtc", receivedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static int ResolveHistoryRetention(IConfiguration configuration) =>
        configuration.GetValue<int?>("MarketMafioso:InventoryHistoryRetentionPerCharacter")
        ?? configuration.GetValue("MarketMafioso:SnapshotRetentionCount", 100);

    private static async Task<CurrentInventoryHead?> ReadCurrentHeadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long accountId,
        long characterId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, semantic_revision, semantic_hash, report_timestamp
            FROM snapshots
            WHERE account_id = $accountId
              AND character_id = $characterId
              AND is_current = 1
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new CurrentInventoryHead(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3));
    }

    private static bool IsOlderObservation(string incomingTimestamp, string currentTimestamp) =>
        DateTimeOffset.TryParse(incomingTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var incoming) &&
        DateTimeOffset.TryParse(currentTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var current) &&
        incoming < current;

    private static async Task TouchCurrentDeliveryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string snapshotId,
        DateTimeOffset receivedAt,
        string? apiKeyLabel,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE snapshots
            SET last_delivery_at_utc = $receivedAt,
                api_key_label = $apiKeyLabel
            WHERE id = $snapshotId AND is_current = 1;
            """;
        command.Parameters.AddWithValue("$receivedAt", receivedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$apiKeyLabel", string.IsNullOrWhiteSpace(apiKeyLabel) ? DBNull.Value : "provided");
        command.Parameters.AddWithValue("$snapshotId", snapshotId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RefreshCurrentHeadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string snapshotId,
        DateTimeOffset receivedAt,
        string? apiKeyLabel,
        InventoryReport report,
        InventoryReportMetadata metadata,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE snapshots
            SET received_at_utc = $receivedAt,
                last_delivery_at_utc = $receivedAt,
                api_key_label = $apiKeyLabel,
                report_timestamp = $reportTimestamp,
                generated_at_utc = $generatedAtUtc,
                retainer_management_json = $retainerManagementJson
            WHERE id = $snapshotId AND is_current = 1;
            """;
        command.Parameters.AddWithValue("$receivedAt", receivedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$apiKeyLabel", string.IsNullOrWhiteSpace(apiKeyLabel) ? DBNull.Value : "provided");
        command.Parameters.AddWithValue("$reportTimestamp", report.Timestamp);
        command.Parameters.AddWithValue("$generatedAtUtc", metadata.GeneratedAtUtc);
        command.Parameters.AddWithValue(
            "$retainerManagementJson",
            report.RetainerManagement is null
                ? DBNull.Value
                : JsonSerializer.Serialize(report.RetainerManagement));
        command.Parameters.AddWithValue("$snapshotId", snapshotId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DemoteCurrentHeadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string snapshotId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE snapshots SET is_current = 0 WHERE id = $snapshotId AND is_current = 1";
        command.Parameters.AddWithValue("$snapshotId", snapshotId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AdvanceAccountRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long accountId,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO inventory_account_revisions (account_id, revision, updated_at_utc)
            VALUES ($accountId, 1, $updatedAt)
            ON CONFLICT(account_id) DO UPDATE SET
                revision = inventory_account_revisions.revision + 1,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$updatedAt", changedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record CurrentInventoryHead(
        string SnapshotId,
        long SemanticRevision,
        string? SemanticHash,
        string ReportTimestamp);

    private static async Task InsertOwnerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long accountId,
        string snapshotId,
        string ownerType,
        string ownerName,
        ulong? retainerId,
        string? lastUpdated,
        ulong? gil,
        string? gilObservedAtUtc,
        string? listingsObservedAtUtc,
        StorageSourceEvidence storage,
        int sortOrder,
        IReadOnlyList<InventoryBag> bags,
        IReadOnlyList<RetainerMarketListing> marketListings,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO inventory_owners (snapshot_id, owner_type, owner_name, retainer_id, last_updated, gil, gil_observed_at_utc, listings_observed_at_utc, requested_sources_json, observed_sources_json, sort_order)
            VALUES ($snapshotId, $ownerType, $ownerName, $retainerId, $lastUpdated, $gil, $gilObservedAtUtc, $listingsObservedAtUtc, $requestedSources, $observedSources, $sortOrder);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$snapshotId", snapshotId);
        command.Parameters.AddWithValue("$ownerType", ownerType);
        command.Parameters.AddWithValue("$ownerName", ownerName);
        command.Parameters.AddWithValue("$retainerId", retainerId == null ? DBNull.Value : checked((long)retainerId.Value));
        command.Parameters.AddWithValue("$lastUpdated", string.IsNullOrWhiteSpace(lastUpdated) ? DBNull.Value : lastUpdated);
        command.Parameters.AddWithValue("$gil", gil == null ? DBNull.Value : checked((long)gil.Value));
        command.Parameters.AddWithValue("$gilObservedAtUtc", string.IsNullOrWhiteSpace(gilObservedAtUtc) ? DBNull.Value : gilObservedAtUtc);
        command.Parameters.AddWithValue("$listingsObservedAtUtc", string.IsNullOrWhiteSpace(listingsObservedAtUtc) ? DBNull.Value : listingsObservedAtUtc);
        command.Parameters.AddWithValue("$requestedSources", JsonSerializer.Serialize(storage?.RequestedSources ?? []));
        command.Parameters.AddWithValue("$observedSources", JsonSerializer.Serialize(storage?.ObservedSources ?? []));
        command.Parameters.AddWithValue("$sortOrder", sortOrder);
        var ownerId = (long)(await command.ExecuteScalarAsync(cancellationToken))!;

        for (var i = 0; i < bags.Count; i++)
            await InsertBagAsync(connection, transaction, accountId, ownerId, bags[i], i, cancellationToken);

        for (var i = 0; i < marketListings.Count; i++)
            await InsertMarketListingAsync(connection, transaction, accountId, ownerId, marketListings[i], i, cancellationToken);
    }

    private static async Task InsertBagAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long accountId,
        long ownerId,
        InventoryBag bag,
        int sortOrder,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO inventory_bags (owner_id, bag_name, location, observed_at_utc, sort_order)
            VALUES ($ownerId, $bagName, $location, $observedAtUtc, $sortOrder);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$ownerId", ownerId);
        command.Parameters.AddWithValue("$bagName", bag.BagName);
        command.Parameters.AddWithValue("$location", (object?)bag.Location ?? DBNull.Value);
        command.Parameters.AddWithValue("$observedAtUtc", (object?)bag.ObservedAtUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("$sortOrder", sortOrder);
        var bagId = (long)(await command.ExecuteScalarAsync(cancellationToken))!;

        for (var i = 0; i < bag.Items.Count; i++)
            await InsertItemAsync(connection, transaction, accountId, bagId, bag.Items[i], i, cancellationToken);
    }

    private static async Task InsertItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long accountId,
        long bagId,
        ItemSlot item,
        int sortOrder,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO inventory_items (
                bag_id, item_id, item_name, item_type, quantity, is_hq, condition,
                container_key, slot_index, condition_percent, equipped, sort_order)
            VALUES (
                $bagId, $itemId,
                COALESCE(NULLIF($itemName, ''), (SELECT item_name FROM item_metadata_catalog WHERE account_id = $accountId AND item_id = $itemId)),
                COALESCE(NULLIF($itemType, ''), (SELECT item_type FROM item_metadata_catalog WHERE account_id = $accountId AND item_id = $itemId)),
                $quantity, $isHq, $condition,
                $containerKey, $slotIndex, $conditionPercent, $equipped, $sortOrder);
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$bagId", bagId);
        command.Parameters.AddWithValue("$itemId", checked((long)item.ItemId));
        command.Parameters.AddWithValue("$itemName", (object?)item.ItemName ?? DBNull.Value);
        command.Parameters.AddWithValue("$itemType", (object?)item.ItemType ?? DBNull.Value);
        command.Parameters.AddWithValue("$quantity", checked((long)item.Quantity));
        command.Parameters.AddWithValue("$isHq", item.IsHQ ? 1 : 0);
        command.Parameters.AddWithValue("$condition", item.Condition);
        command.Parameters.AddWithValue("$containerKey", (object?)item.ContainerKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$slotIndex", item.SlotIndex is { } slotIndex ? slotIndex : DBNull.Value);
        command.Parameters.AddWithValue("$conditionPercent", item.ConditionPercent is { } conditionPercent ? conditionPercent : DBNull.Value);
        command.Parameters.AddWithValue("$equipped", item.Equipped is { } equipped ? equipped ? 1 : 0 : DBNull.Value);
        command.Parameters.AddWithValue("$sortOrder", sortOrder);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertMarketListingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long accountId,
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
                container_key,
                slot_index,
                condition_percent,
                unit_price,
                listed_at,
                sort_order)
            VALUES (
                $ownerId,
                $itemId,
                COALESCE(NULLIF($itemName, ''), (SELECT item_name FROM item_metadata_catalog WHERE account_id = $accountId AND item_id = $itemId)),
                COALESCE(NULLIF($itemType, ''), (SELECT item_type FROM item_metadata_catalog WHERE account_id = $accountId AND item_id = $itemId)),
                $quantity,
                $isHq,
                $condition,
                $containerKey,
                $slotIndex,
                $conditionPercent,
                $unitPrice,
                $listedAt,
                $sortOrder);
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$ownerId", ownerId);
        command.Parameters.AddWithValue("$itemId", checked((long)listing.ItemId));
        command.Parameters.AddWithValue("$itemName", (object?)listing.ItemName ?? DBNull.Value);
        command.Parameters.AddWithValue("$itemType", (object?)listing.ItemType ?? DBNull.Value);
        command.Parameters.AddWithValue("$quantity", checked((long)listing.Quantity));
        command.Parameters.AddWithValue("$isHq", listing.IsHQ ? 1 : 0);
        command.Parameters.AddWithValue("$condition", listing.Condition);
        command.Parameters.AddWithValue("$containerKey", (object?)listing.ContainerKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$slotIndex", listing.SlotIndex is { } slotIndex ? slotIndex : DBNull.Value);
        command.Parameters.AddWithValue("$conditionPercent", listing.ConditionPercent is { } conditionPercent ? conditionPercent : DBNull.Value);
        command.Parameters.AddWithValue("$unitPrice", listing.UnitPrice == null ? DBNull.Value : checked((long)listing.UnitPrice.Value));
        command.Parameters.AddWithValue("$listedAt", string.IsNullOrWhiteSpace(listing.ListedAt) ? DBNull.Value : listing.ListedAt);
        command.Parameters.AddWithValue("$sortOrder", sortOrder);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertItemMetadataCatalogAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long accountId,
        DateTimeOffset receivedAt,
        InventoryReport report,
        CancellationToken cancellationToken)
    {
        var metadata = report.PlayerInventory
            .SelectMany(bag => bag.Items)
            .Select(item => (item.ItemId, item.ItemName, item.ItemType))
            .Concat(report.Retainers.SelectMany(retainer => retainer.Bags)
                .SelectMany(bag => bag.Items)
                .Select(item => (item.ItemId, item.ItemName, item.ItemType)))
            .Concat(report.Retainers.SelectMany(retainer => retainer.MarketListings)
                .Select(item => (item.ItemId, item.ItemName, item.ItemType)))
            .Where(item => item.ItemId != 0 &&
                           (!string.IsNullOrWhiteSpace(item.ItemName) || !string.IsNullOrWhiteSpace(item.ItemType)))
            .GroupBy(item => item.ItemId)
            .Select(group => (
                ItemId: group.Key,
                ItemName: group.Select(item => item.ItemName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                ItemType: group.Select(item => item.ItemType).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))))
            .ToArray();

        if (metadata.Length == 0)
            return;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO item_metadata_catalog (account_id, item_id, item_name, item_type, last_seen_at_utc)
            VALUES ($accountId, $itemId, NULLIF($itemName, ''), NULLIF($itemType, ''), $lastSeenAt)
            ON CONFLICT(account_id, item_id) DO UPDATE SET
                item_name = COALESCE(NULLIF(excluded.item_name, ''), item_metadata_catalog.item_name),
                item_type = COALESCE(NULLIF(excluded.item_type, ''), item_metadata_catalog.item_type),
                last_seen_at_utc = excluded.last_seen_at_utc;
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        var itemIdParameter = command.Parameters.Add("$itemId", SqliteType.Integer);
        var itemNameParameter = command.Parameters.Add("$itemName", SqliteType.Text);
        var itemTypeParameter = command.Parameters.Add("$itemType", SqliteType.Text);
        command.Parameters.AddWithValue("$lastSeenAt", receivedAt.ToString("O", CultureInfo.InvariantCulture));

        foreach (var item in metadata)
        {
            itemIdParameter.Value = checked((long)item.ItemId);
            itemNameParameter.Value = item.ItemName ?? string.Empty;
            itemTypeParameter.Value = item.ItemType ?? string.Empty;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}

internal sealed record InventoryWriteResult(
    string SnapshotId,
    long? CharacterId,
    long SemanticRevision,
    bool SemanticChanged);
