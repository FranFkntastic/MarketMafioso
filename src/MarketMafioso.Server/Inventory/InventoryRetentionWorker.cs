using System.Globalization;
using MarketMafioso.Server.Sqlite;
using Microsoft.Data.Sqlite;

namespace MarketMafioso.Server.Inventory;

/// <summary>Restart-safe catch-up, independent of new uploads. Never locks while selecting history.</summary>
internal sealed class InventoryRetentionWorker(
    SqliteConnectionFactory factory, IConfiguration configuration,
    ILogger<InventoryRetentionWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let startup and foreground requests win before starting maintenance.
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            try { await PrunePassAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                log.LogWarning(exception, "InventoryRetentionWorker.PrunePassAsync failed; catch-up will resume next pass");
            }
        }
    }

    internal async Task PrunePassAsync(CancellationToken token)
    {
        var policy = InventoryHistoryRetentionPolicy.Resolve(configuration);
        var accounts = new List<long>();
        await using (var connection = await factory.OpenConnectionAsync(token))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id FROM accounts ORDER BY id";
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) accounts.Add(reader.GetInt64(0));
        }
        foreach (var account in accounts)
        {
            var candidates = new List<InventoryHistoryCandidate>();
            await using (var connection = await factory.OpenConnectionAsync(token))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT s.id, s.character_id, s.received_at_utc,
                        EXISTS(SELECT 1 FROM inventory_owners o WHERE o.snapshot_id = s.id AND o.owner_type = 'retainer')
                    FROM snapshots s WHERE s.account_id = $account AND s.is_current = 0
                    """;
                command.Parameters.AddWithValue("$account", account);
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                    candidates.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt64(1),
                        DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture), reader.GetInt64(3) != 0));
            }
            var deleted = 0;
            foreach (var id in policy.SelectPrunable(candidates, DateTimeOffset.UtcNow).Take(100))
            {
                // One snapshot (including indexed FK cascades) is the atomic unit.
                // Never expose a partially pruned historical report.
                await using (var connection = await factory.OpenConnectionAsync(token))
                {
                    connection.DefaultTimeout = 1;
                    using var write = factory.BeginWrite(connection, "PruneSnapshot");
                    await using var command = connection.CreateCommand();
                    command.Transaction = write.Transaction;
                    command.CommandText = """
                        DELETE FROM snapshots WHERE account_id = $account AND id = $id AND is_current = 0
                        AND (SELECT COUNT(*) FROM snapshots newer
                            WHERE newer.account_id = $account AND newer.is_current = 0
                            AND newer.character_id IS snapshots.character_id
                            AND (newer.received_at_utc > snapshots.received_at_utc OR
                                (newer.received_at_utc = snapshots.received_at_utc AND newer.id > snapshots.id))) >= $minimum
                        """;
                    command.Parameters.AddWithValue("$account", account);
                    command.Parameters.AddWithValue("$id", id);
                    command.Parameters.AddWithValue("$minimum", policy.MinimumRecent);
                    deleted += await command.ExecuteNonQueryAsync(token);
                    await write.Transaction.CommitAsync(token);
                }
                await Task.Delay(250, token);
            }
            await PruneRawAsync(account, token);
            if (deleted > 0) log.LogInformation("Inventory retention pruned {Count} historical snapshots for account {Account}", deleted, account);
        }
        await PruneProjectionRowsAsync(token);
    }

    private async Task PruneRawAsync(long account, CancellationToken token)
    {
        var count = configuration.GetValue("MarketMafioso:RawJsonRetentionCount", 20);
        if (count < 0) throw new InvalidOperationException("RawJsonRetentionCount must be zero or greater.");
        await using var connection = await factory.OpenConnectionAsync(token);
        connection.DefaultTimeout = 1;
        using var write = factory.BeginWrite(connection);
        await using var command = connection.CreateCommand();
        command.Transaction = write.Transaction;
        command.CommandText = """
            UPDATE snapshots SET raw_report_json = NULL, raw_json_retained_at_utc = NULL
            WHERE id IN (SELECT id FROM snapshots WHERE account_id = $account AND raw_report_json IS NOT NULL
                ORDER BY received_at_utc DESC, id DESC LIMIT 10 OFFSET $count)
            """;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$count", count);
        await command.ExecuteNonQueryAsync(token);
        await write.Transaction.CommitAsync(token);
    }

    private async Task PruneProjectionRowsAsync(CancellationToken token)
    {
        // Superseded projections are rebuildable, not evidence history. Keep the current
        // generation and active Building generations; remove only small row batches.
        for (var batch = 0; batch < 20; batch++)
        {
            int deleted;
            await using (var connection = await factory.OpenConnectionAsync(token))
            {
                connection.DefaultTimeout = 1;
                using var write = factory.BeginWrite(connection);
                await using var command = connection.CreateCommand();
                command.Transaction = write.Transaction;
                command.CommandText = """
                    DELETE FROM market_intelligence_market_rows WHERE rowid IN (
                        SELECT r.rowid FROM market_intelligence_market_rows r
                        WHERE r.generation_id = (
                            SELECT g.generation_id FROM market_intelligence_projection_generations g
                            WHERE g.status IN ('Published', 'Failed') AND NOT EXISTS (
                                SELECT 1 FROM market_intelligence_current_projection c WHERE c.generation_id = g.generation_id)
                            AND EXISTS (SELECT 1 FROM market_intelligence_market_rows rows WHERE rows.generation_id = g.generation_id)
                            LIMIT 1)
                        LIMIT 100)
                    """;
                deleted = await command.ExecuteNonQueryAsync(token);
                await write.Transaction.CommitAsync(token);
            }
            if (deleted == 0) break;
            await Task.Delay(250, token);
        }
        await using var cleanup = await factory.OpenConnectionAsync(token);
        cleanup.DefaultTimeout = 1;
        using var cleanupWrite = factory.BeginWrite(cleanup, "PruneEmptyProjectionGenerations");
        await using var empty = cleanup.CreateCommand();
        empty.Transaction = cleanupWrite.Transaction;
        empty.CommandText = """
            DELETE FROM market_intelligence_projection_generations WHERE generation_id IN (
                SELECT g.generation_id FROM market_intelligence_projection_generations g
                WHERE g.status IN ('Published', 'Failed')
                AND NOT EXISTS (SELECT 1 FROM market_intelligence_current_projection c WHERE c.generation_id = g.generation_id)
                AND NOT EXISTS (SELECT 1 FROM market_intelligence_market_rows r WHERE r.generation_id = g.generation_id)
                LIMIT 10)
            """;
        await empty.ExecuteNonQueryAsync(token);
        await cleanupWrite.Transaction.CommitAsync(token);
    }
}
