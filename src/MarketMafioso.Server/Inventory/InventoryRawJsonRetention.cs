using MarketMafioso.Server.Sqlite;
using Microsoft.Data.Sqlite;

namespace MarketMafioso.Server.Inventory;

internal sealed class InventoryRawJsonRetention(
    SqliteConnectionFactory connectionFactory,
    IConfiguration configuration)
{
    public async Task<RawInventoryReportJson?> GetAsync(
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

    public async Task PruneAsync(
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
}
