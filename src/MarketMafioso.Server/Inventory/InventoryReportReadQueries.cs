using MarketMafioso.Server.Sqlite;
using Microsoft.Data.Sqlite;

namespace MarketMafioso.Server.Inventory;

internal sealed class InventoryReportReadQueries(SqliteConnectionFactory connectionFactory)
{
    public async Task<bool> ExistsAsync(long accountId, string id, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM snapshots WHERE account_id = $accountId AND id = $id";
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteScalarAsync(cancellationToken) != null;
    }

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
            characters.Add(InventoryReportRowMapper.ReadCharacter(reader));

        return characters;
    }

    public async Task<IReadOnlyList<ReportSummary>> ListSummariesAsync(
        long accountId,
        long? characterId,
        CancellationToken cancellationToken)
    {
        var summaries = new List<ReportSummary>();
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = characterId == null
            ? SummaryQuery + Environment.NewLine + """
              WHERE s.account_id = $accountId
              ORDER BY s.received_at_utc DESC
              """
            : SummaryQuery + Environment.NewLine + """
              WHERE s.account_id = $accountId AND s.character_id = $characterId
              ORDER BY s.received_at_utc DESC
              """;
        command.Parameters.AddWithValue("$accountId", accountId);
        if (characterId != null)
            command.Parameters.AddWithValue("$characterId", characterId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            summaries.Add(InventoryReportRowMapper.ReadSummary(reader));

        return summaries;
    }

    public async Task<InventoryRetentionSummary> GetRetentionSummaryAsync(
        IReadOnlyList<long> accountIds,
        CancellationToken cancellationToken)
    {
        if (accountIds.Count == 0)
            return new InventoryRetentionSummary();

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var accountParameters = accountIds.Select((_, index) => $"$account{index}").ToArray();
        command.CommandText = $"""
            SELECT
                COUNT(*),
                COALESCE(SUM(CASE WHEN raw_report_json IS NOT NULL THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN raw_report_json IS NULL THEN 1 ELSE 0 END), 0),
                MAX(received_at_utc),
                MIN(received_at_utc)
            FROM snapshots
            WHERE account_id IN ({string.Join(", ", accountParameters)})
            """;

        for (var i = 0; i < accountIds.Count; i++)
            command.Parameters.AddWithValue(accountParameters[i], accountIds[i]);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new InventoryRetentionSummary();

        return InventoryReportRowMapper.ReadRetentionSummary(reader);
    }

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
        var owners = new List<InventoryOwnerRow>();

        await using var ownerCommand = connection.CreateCommand();
        ownerCommand.CommandText = """
            SELECT id, owner_type, owner_name, retainer_id, last_updated, gil
            FROM inventory_owners
            WHERE snapshot_id = $snapshotId
            ORDER BY sort_order
            """;
        ownerCommand.Parameters.AddWithValue("$snapshotId", id);
        await using (var ownerReader = await ownerCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await ownerReader.ReadAsync(cancellationToken))
                owners.Add(InventoryReportRowMapper.ReadOwner(ownerReader));
        }

        foreach (var owner in owners)
        {
            var bags = await ReadBagsAsync(connection, owner.Id, cancellationToken);
            if (owner.OwnerType == "player")
            {
                playerBags.AddRange(bags);
                continue;
            }

            retainers.Add(new RetainerReport
            {
                RetainerName = owner.OwnerName,
                RetainerId = owner.RetainerId ?? 0,
                OwnerCharacterName = snapshot.CharacterName,
                OwnerHomeWorld = snapshot.HomeWorld,
                LastUpdated = owner.LastUpdated ?? string.Empty,
                Gil = owner.Gil ?? 0,
                Bags = bags,
                MarketListings = await ReadMarketListingsAsync(connection, owner.Id, cancellationToken),
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
            Summary = InventoryReportRowMapper.CreateSummary(id, snapshot.ReceivedAt, report),
        };
    }

    private static async Task<InventorySnapshotRow?> ReadSnapshotAsync(
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

        return InventoryReportRowMapper.ReadSnapshot(reader);
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
            items.Add(InventoryReportRowMapper.ReadItem(reader));

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
            listings.Add(InventoryReportRowMapper.ReadMarketListing(reader));

        return listings;
    }

    private const string SummaryQuery = """
        SELECT
            s.id,
            s.received_at_utc,
            s.character_name,
            s.home_world,
            s.report_timestamp,
            (
                SELECT COUNT(*)
                FROM inventory_owners o
                JOIN inventory_bags b ON b.owner_id = o.id
                WHERE o.snapshot_id = s.id AND o.owner_type = 'player'
            ) AS player_bag_count,
            (
                SELECT COUNT(*)
                FROM inventory_owners o
                JOIN inventory_bags b ON b.owner_id = o.id
                JOIN inventory_items i ON i.bag_id = b.id
                WHERE o.snapshot_id = s.id AND o.owner_type = 'player'
            ) AS player_item_stacks,
            (
                SELECT COALESCE(SUM(i.quantity), 0)
                FROM inventory_owners o
                JOIN inventory_bags b ON b.owner_id = o.id
                JOIN inventory_items i ON i.bag_id = b.id
                WHERE o.snapshot_id = s.id AND o.owner_type = 'player'
            ) AS player_item_quantity,
            (
                SELECT COUNT(*)
                FROM inventory_owners o
                WHERE o.snapshot_id = s.id AND o.owner_type = 'retainer'
            ) AS retainer_count,
            (
                SELECT COUNT(*)
                FROM inventory_owners o
                JOIN inventory_bags b ON b.owner_id = o.id
                JOIN inventory_items i ON i.bag_id = b.id
                WHERE o.snapshot_id = s.id AND o.owner_type = 'retainer'
            ) AS retainer_item_stacks,
            (
                SELECT COALESCE(SUM(i.quantity), 0)
                FROM inventory_owners o
                JOIN inventory_bags b ON b.owner_id = o.id
                JOIN inventory_items i ON i.bag_id = b.id
                WHERE o.snapshot_id = s.id AND o.owner_type = 'retainer'
            ) AS retainer_item_quantity
        FROM snapshots s
        """;
}
