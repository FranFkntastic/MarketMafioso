using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Microsoft.Data.Sqlite;
using MarketMafioso.Server.Sqlite;

namespace MarketMafioso.Server.Auth;

public sealed class ReceiverBootstrapper
{
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly IConfiguration configuration;
    private readonly DashboardPasswordHasher passwordHasher;
    private readonly ILogger<ReceiverBootstrapper> log;

    public ReceiverBootstrapper(
        SqliteConnectionFactory connectionFactory,
        IConfiguration configuration,
        DashboardPasswordHasher passwordHasher,
        ILogger<ReceiverBootstrapper> log)
    {
        this.connectionFactory = connectionFactory;
        this.configuration = configuration;
        this.passwordHasher = passwordHasher;
        this.log = log;
    }

    public async Task BootstrapAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var accountId = await EnsureDefaultAccountAsync(connection, transaction, cancellationToken);

        if (await CountAsync(connection, transaction, "dashboard_users", cancellationToken) == 0)
        {
            if (configuration.GetValue<bool>("MarketMafioso:RequireDashboardAuth"))
            {
                var username = configuration["MarketMafioso:DashboardBootstrapUsername"];
                var password = configuration["MarketMafioso:DashboardBootstrapPassword"];
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                    throw new InvalidOperationException("Dashboard bootstrap username and password are required when dashboard auth is enabled.");

                var userId = await CreateDashboardUserAsync(connection, transaction, username, password, cancellationToken);
                await LinkDashboardUserAsync(connection, transaction, userId, accountId, cancellationToken);
            }
        }

        var ingestKey = configuration["MarketMafioso:IngestApiKey"];
        if (!string.IsNullOrWhiteSpace(ingestKey) &&
            await CountAsync(connection, transaction, "ingest_keys", cancellationToken) == 0)
        {
            await CreateIngestKeyAsync(connection, transaction, accountId, ingestKey, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        log.LogInformation("Receiver bootstrap completed.");
    }

    private static async Task<long> EnsureDefaultAccountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var existing = await ScalarLongAsync(connection, transaction, "SELECT id FROM accounts ORDER BY id LIMIT 1", cancellationToken);
        if (existing != null)
            return existing.Value;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO accounts (display_name, created_at_utc)
            VALUES ('Default', $createdAt);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private async Task<long> CreateDashboardUserAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO dashboard_users (username, password_hash, is_admin, created_at_utc)
            VALUES ($username, $passwordHash, 1, $createdAt);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$username", username);
        command.Parameters.AddWithValue("$passwordHash", passwordHasher.HashPassword(password));
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task LinkDashboardUserAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long userId,
        long accountId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO dashboard_user_accounts (dashboard_user_id, account_id, is_default)
            VALUES ($userId, $accountId, 1);
            """;
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$accountId", accountId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CreateIngestKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long accountId,
        string ingestKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ingest_keys (account_id, label, key_hash, created_at_utc)
            VALUES ($accountId, 'default', $keyHash, $createdAt);
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$keyHash", HashIngestKey(ingestKey));
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> CountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {tableName}";
        return checked((int)(long)(await command.ExecuteScalarAsync(cancellationToken))!);
    }

    private static async Task<long?> ScalarLongAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long value ? value : null;
    }

    public static string HashIngestKey(string ingestKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(ingestKey));
        return Convert.ToHexString(bytes);
    }
}
