using MarketMafioso.Server.Sqlite;

namespace MarketMafioso.Server.Auth;

public sealed class IngestKeyAccountResolver
{
    private readonly SqliteConnectionFactory connectionFactory;

    public IngestKeyAccountResolver(SqliteConnectionFactory connectionFactory)
    {
        this.connectionFactory = connectionFactory;
    }

    public async Task<long?> ResolveAccountIdAsync(string? ingestKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ingestKey))
            return null;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT account_id
            FROM ingest_keys
            WHERE key_hash = $keyHash
              AND disabled_at_utc IS NULL
            ORDER BY id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$keyHash", WorkshopHostCredentialStore.HashKey(ingestKey));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long accountId ? accountId : null;
    }
}
