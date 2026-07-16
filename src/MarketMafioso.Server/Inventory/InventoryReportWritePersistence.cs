using System.Globalization;
using MarketMafioso.Server.Sqlite;
using Microsoft.Data.Sqlite;

namespace MarketMafioso.Server.Inventory;

internal sealed class InventoryReportWritePersistence(
    SqliteConnectionFactory connectionFactory,
    IConfiguration configuration)
{
    public async Task WriteSnapshotAsync(
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
        await UpsertItemMetadataCatalogAsync(
            connection,
            transaction,
            accountId,
            receivedAt,
            report,
            cancellationToken);
        await InsertOwnerAsync(connection, transaction, accountId, id, "player", "Player Inventory", null, null, null, 0, report.PlayerInventory, [], cancellationToken);

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
                i + 1,
                retainer.Bags,
                retainer.MarketListings,
                cancellationToken);
        }
    }

    public async Task PruneSnapshotsAsync(
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

    public async Task<bool> DeleteAsync(long accountId, string id, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM snapshots WHERE account_id = $accountId AND id = $id";
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<int> DeleteAllAsync(long accountId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM snapshots WHERE account_id = $accountId";
        command.Parameters.AddWithValue("$accountId", accountId);
        return await command.ExecuteNonQueryAsync(cancellationToken);
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
        long accountId,
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
            INSERT INTO inventory_bags (owner_id, bag_name, location, sort_order)
            VALUES ($ownerId, $bagName, $location, $sortOrder);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$ownerId", ownerId);
        command.Parameters.AddWithValue("$bagName", bag.BagName);
        command.Parameters.AddWithValue("$location", (object?)bag.Location ?? DBNull.Value);
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
