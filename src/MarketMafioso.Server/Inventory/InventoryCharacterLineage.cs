using Microsoft.Data.Sqlite;

namespace MarketMafioso.Server.Inventory;

// Retainer IDs are owner-scoped game identities, unlike character/retainer display names.
// Keep the link after history pruning so an old-world replay cannot create another head.
internal static class InventoryCharacterLineage
{
    public static async Task RepairAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var owners = new List<(long Account, long Character, ulong Retainer)>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT DISTINCT s.account_id, s.character_id, o.retainer_id
                FROM snapshots s JOIN inventory_owners o ON o.snapshot_id = s.id
                WHERE s.is_current = 1 AND s.character_id IS NOT NULL
                  AND o.owner_type = 'retainer' AND o.retainer_id > 0;
                """;
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                owners.Add((reader.GetInt64(0), reader.GetInt64(1), checked((ulong)reader.GetInt64(2))));
        }
        foreach (var group in owners.GroupBy(row => (row.Account, row.Character)))
            await LinkAsync(connection, transaction, group.Key.Account, group.Key.Character,
                group.Select(row => row.Retainer), cancellationToken);
    }

    public static async Task LinkAsync(SqliteConnection connection, SqliteTransaction transaction,
        long accountId, long characterId, IEnumerable<ulong> retainerIds, CancellationToken cancellationToken)
    {
        var ids = retainerIds.Where(id => id != 0).Distinct().ToArray();
        if (ids.Length == 0)
            return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$character", characterId);
        var parameters = ids.Select((id, index) =>
        {
            var name = $"$retainer{index}";
            command.Parameters.AddWithValue(name, checked((long)id));
            return name;
        });
        command.CommandText = $"""
            SELECT DISTINCT COALESCE(c.inventory_identity_id, c.id)
            FROM characters c
            WHERE c.account_id = $account AND (c.id = $character OR c.id IN (
                SELECT s.character_id FROM snapshots s
                JOIN inventory_owners o ON o.snapshot_id = s.id
                WHERE s.account_id = $account AND o.owner_type = 'retainer'
                  AND o.retainer_id IN ({string.Join(",", parameters)})));
            """;
        var groups = new List<long>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                groups.Add(reader.GetInt64(0));
        if (groups.Count == 1)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$account", accountId);
            command.Parameters.AddWithValue("$identity", groups[0]);
            command.CommandText = "SELECT count(*) FROM characters WHERE account_id = $account AND inventory_identity_id = $identity";
            if ((long)(await command.ExecuteScalarAsync(cancellationToken))! < 2)
                return;
        }

        command.Parameters.Clear();
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$identity", groups.Min());
        var groupParameters = groups.Select((id, index) =>
        {
            var name = $"$group{index}";
            command.Parameters.AddWithValue(name, id);
            return name;
        });
        command.CommandText = $"""
            UPDATE characters SET inventory_identity_id = $identity
            WHERE account_id = $account
              AND COALESCE(inventory_identity_id, id) IN ({string.Join(",", groupParameters)});
            UPDATE snapshots SET is_current = 0
            WHERE account_id = $account AND is_current = 1 AND character_id IN (
                SELECT id FROM characters WHERE account_id = $account AND inventory_identity_id = $identity)
              AND id <> (
                SELECT s.id FROM snapshots s JOIN characters c ON c.id = s.character_id
                WHERE s.account_id = $account AND c.inventory_identity_id = $identity AND s.is_current = 1
                ORDER BY julianday(s.report_timestamp) DESC, s.report_timestamp DESC, s.received_at_utc DESC, s.id DESC
                LIMIT 1);
            UPDATE inventory_account_revisions SET revision = revision + 1,
                updated_at_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE account_id = $account AND changes() > 0;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
