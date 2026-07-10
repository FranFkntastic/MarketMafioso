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
}
