using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MarketMafioso.Server.Inventory;

internal sealed partial class InventoryReportWritePersistence
{
    public async Task<InventoryWriteResult> WriteDeltaAsync(
        SqliteConnection connection, SqliteTransaction transaction, long accountId,
        StoredInventoryReport before, InventoryReport report, InventoryReportDelta delta,
        string id, DateTimeOffset receivedAt, string? apiKeyLabel, string semanticHash,
        CancellationToken token)
    {
        async Task<object?> Execute(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return await command.ExecuteScalarAsync(token);
        }

        // Validate under the writer lock, not just in the HTTP preflight. Rotating the
        // acknowledgement ID fences concurrent writers and retries without copying items.
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = """
            SELECT character_id, semantic_revision, semantic_hash,
                   COALESCE(head_started_at_utc, received_at_utc), report_timestamp
            FROM snapshots WHERE account_id = $account AND id = $id AND is_current = 1;
            """;
        read.Parameters.AddWithValue("$account", accountId);
        read.Parameters.AddWithValue("$id", before.Id);
        long characterId, revision;
        string? oldHash;
        DateTimeOffset started;
        string timestamp;
        await using (var reader = await read.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token))
                throw new InventoryDeltaConflictException("The inventory delta base is no longer current.");
            characterId = reader.GetInt64(0);
            revision = reader.GetInt64(1);
            oldHash = reader.IsDBNull(2) ? null : reader.GetString(2);
            started = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture);
            timestamp = reader.GetString(4);
        }
        if (IsOlderObservation(report.Timestamp, timestamp))
            throw new InventoryDeltaConflictException("The inventory delta observation is older than its base.");

        var changed = !string.Equals(oldHash, semanticHash, StringComparison.Ordinal);
        var interval = TimeSpan.FromMinutes(Math.Max(1,
            configuration.GetValue("MarketMafioso:InventoryDeltaHistoryIntervalMinutes", 15)));
        if (changed && receivedAt - started >= interval)
            return await WriteSnapshotAsync(connection, transaction, accountId, id, receivedAt,
                report, report.Metadata, apiKeyLabel, null, token);

        var changedReport = report with
        {
            PlayerInventory = delta.UpsertedPlayerBags,
            Retainers = report.Retainers.Where(r => delta.RetainerChanges.Any(c => c.RetainerId == r.RetainerId))
                .Select(r => r with
                {
                    Bags = delta.RetainerChanges.Single(c => c.RetainerId == r.RetainerId) is { Replacement: null } c
                        ? c.UpsertedBags : r.Bags,
                    MarketListings = delta.RetainerChanges.Single(c => c.RetainerId == r.RetainerId) is { Replacement: null, ReplaceMarketListings: false }
                        ? [] : r.MarketListings,
                }).ToList(),
        };
        await UpsertItemMetadataCatalogAsync(connection, transaction, accountId, receivedAt, changedReport, token);

        async Task PatchBags(long owner, IReadOnlyList<InventoryBag> oldBags, IReadOnlyList<InventoryBag> bags,
            IReadOnlyList<InventoryBag> upserts, IReadOnlyList<InventoryBagKey> removals)
        {
            foreach (var key in removals.Concat(upserts.Select(InventoryBagKey.From)).Distinct())
                await Execute("DELETE FROM inventory_bags WHERE owner_id = $owner AND bag_name = $name AND location IS $location",
                    ("$owner", owner), ("$name", key.BagName), ("$location", key.Location));
            foreach (var bag in upserts)
                await InsertBagAsync(connection, transaction, accountId, owner, bag,
                    bags.ToList().FindIndex(b => InventoryBagKey.From(b) == InventoryBagKey.From(bag)), token);
            // Existing upserts preserve their ordinal; only structural changes can
            // shift untouched bags, and even then avoid updating unchanged rows.
            if (removals.Count == 0 && upserts.All(b => oldBags.Any(old => InventoryBagKey.From(old) == InventoryBagKey.From(b))))
                return;
            for (var i = 0; i < bags.Count; i++)
                await Execute("UPDATE inventory_bags SET sort_order = $sort WHERE owner_id = $owner AND bag_name = $name AND location IS $location AND sort_order <> $sort",
                    ("$sort", i), ("$owner", owner), ("$name", bags[i].BagName), ("$location", bags[i].Location));
        }
        var player = (long)(await Execute("SELECT id FROM inventory_owners WHERE snapshot_id = $id AND owner_type = 'player'", ("$id", before.Id)))!;
        await PatchBags(player, before.Report.PlayerInventory, report.PlayerInventory, delta.UpsertedPlayerBags, delta.RemovedPlayerBags);
        if (delta.ReplacePlayerStorage)
            await Execute("UPDATE inventory_owners SET requested_sources_json = $requested, observed_sources_json = $observed WHERE id = $owner",
            ("$requested", JsonSerializer.Serialize(report.PlayerStorage.RequestedSources)),
            ("$observed", JsonSerializer.Serialize(report.PlayerStorage.ObservedSources)), ("$owner", player));
        foreach (var removed in delta.RemovedRetainerIds)
            await Execute("DELETE FROM inventory_owners WHERE snapshot_id = $id AND retainer_id = $retainer",
                ("$id", before.Id), ("$retainer", checked((long)removed)));
        foreach (var change in delta.RetainerChanges)
        {
            var retainer = report.Retainers.Single(r => r.RetainerId == change.RetainerId);
            var sort = report.Retainers.IndexOf(retainer) + 1;
            if (change.Replacement is not null)
            {
                await InsertOwnerAsync(connection, transaction, accountId, before.Id, "retainer", retainer.RetainerName,
                    retainer.RetainerId, retainer.LastUpdated, retainer.Gil, retainer.GilObservedAtUtc,
                    retainer.ListingsObservedAtUtc, retainer.Storage, sort, retainer.Bags, retainer.MarketListings, token);
                continue;
            }
            var owner = (long)(await Execute("SELECT id FROM inventory_owners WHERE snapshot_id = $id AND retainer_id = $retainer",
                ("$id", before.Id), ("$retainer", checked((long)retainer.RetainerId))))!;
            await Execute("""
                UPDATE inventory_owners SET owner_name = $name, last_updated = $updated, gil = $gil,
                    gil_observed_at_utc = $gilAt, listings_observed_at_utc = $listAt,
                    requested_sources_json = $requested, observed_sources_json = $observed WHERE id = $owner;
                """, ("$name", retainer.RetainerName), ("$updated", retainer.LastUpdated), ("$gil", checked((long)retainer.Gil)),
                ("$gilAt", retainer.GilObservedAtUtc), ("$listAt", retainer.ListingsObservedAtUtc),
                ("$requested", JsonSerializer.Serialize(retainer.Storage.RequestedSources)),
                ("$observed", JsonSerializer.Serialize(retainer.Storage.ObservedSources)), ("$owner", owner));
            await PatchBags(owner, before.Report.Retainers.Single(r => r.RetainerId == retainer.RetainerId).Bags,
                retainer.Bags, change.UpsertedBags, change.RemovedBags);
            if (change.ReplaceMarketListings)
            {
                await Execute("DELETE FROM retainer_market_listings WHERE owner_id = $owner", ("$owner", owner));
                for (var i = 0; i < retainer.MarketListings.Count; i++)
                    await InsertMarketListingAsync(connection, transaction, accountId, owner, retainer.MarketListings[i], i, token);
            }
        }
        if (delta.RemovedRetainerIds.Count > 0 || delta.RetainerChanges.Any(c => c.Replacement is not null))
            for (var i = 0; i < report.Retainers.Count; i++)
                await Execute("UPDATE inventory_owners SET sort_order = $sort WHERE snapshot_id = $id AND retainer_id = $retainer AND sort_order <> $sort",
                    ("$sort", i + 1), ("$id", before.Id), ("$retainer", checked((long)report.Retainers[i].RetainerId)));

        await RefreshCurrentHeadAsync(connection, transaction, before.Id, receivedAt, apiKeyLabel, report, report.Metadata, token);
        // Only inventory_owners references snapshots; defer its FK until both IDs have moved.
        await Execute("PRAGMA defer_foreign_keys = ON;");
        await Execute("""
            UPDATE snapshots SET id = $new, semantic_hash = $hash, semantic_revision = $revision,
                head_started_at_utc = $started, player_gil = $gil, source_plugin = $source, plugin_version = $version,
                raw_report_json = NULL, raw_json_retained_at_utc = NULL WHERE id = $old;
            UPDATE inventory_owners SET snapshot_id = $new WHERE snapshot_id = $old;
            UPDATE characters SET last_seen_at_utc = $seen WHERE id = $character;
            """, ("$new", id), ("$old", before.Id), ("$hash", semanticHash), ("$revision", revision + (changed ? 1 : 0)),
            ("$started", started.ToString("O")), ("$gil", report.PlayerGil is { } gil ? checked((long)gil) : null),
            ("$source", report.Metadata.SourcePlugin), ("$version", report.Metadata.PluginVersion),
            ("$seen", receivedAt.ToString("O")), ("$character", characterId));
        // The acknowledgement ID changes even for observation-only patches, so
        // invalidate dashboard references too; semantic_revision remains semantic.
        await AdvanceAccountRevisionAsync(connection, transaction, accountId, receivedAt, token);
        if (delta.RetainerChanges.Any(c => c.Replacement is not null))
            await InventoryCharacterLineage.LinkAsync(connection, transaction, accountId, characterId,
                report.Retainers.Select(r => r.RetainerId), token);
        return new InventoryWriteResult(id, characterId, revision + (changed ? 1 : 0), changed);
    }
}
