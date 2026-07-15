using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MarketMafioso.Server.Sqlite;

namespace MarketMafioso.Server.Auth;

public enum WorkshopHostCredentialScope
{
    CapabilitiesRead,
    InventoryWrite,
    InventoryRead,
    CraftQuote,
    AcquisitionQueue,
    DiagnosticsRead,
    AutomationRun,
}

public static class WorkshopHostCredentialPurposes
{
    public const string CraftArchitect = "CraftArchitect";
    public const string MarketMafiosoClient = "MarketMafiosoClient";
    public const string LegacyClient = "LegacyClient";

    public static bool IsSupported(string purpose) =>
        purpose is CraftArchitect or MarketMafiosoClient;
}

public sealed class WorkshopHostCredentialStore
{
    private static readonly TimeSpan LastUsedWriteInterval = TimeSpan.FromMinutes(1);

    private readonly SqliteConnectionFactory connectionFactory;
    private readonly ConfiguredWorkshopHostKeys configuredKeys;
    private readonly ConcurrentDictionary<long, DateTimeOffset> lastUsedWrites = new();

    public WorkshopHostCredentialStore(
        SqliteConnectionFactory connectionFactory,
        IConfiguration configuration)
    {
        this.connectionFactory = connectionFactory;
        configuredKeys = ConfiguredWorkshopHostKeys.FromConfiguration(configuration);
    }

    public async Task<bool> IsAuthorizedAsync(
        string? suppliedKey,
        WorkshopHostCredentialScope scope,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(suppliedKey))
            return false;

        if (configuredKeys.HasKeyForScope(suppliedKey, scope))
            return true;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, purpose
            FROM ingest_keys
            WHERE key_hash = $keyHash
              AND disabled_at_utc IS NULL
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$keyHash", HashKey(suppliedKey));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return false;

        var id = reader.GetInt64(0);
        var purpose = reader.GetString(1);
        if (!AllowsScope(purpose, scope))
            return false;

        await reader.DisposeAsync();
        await RecordUseAsync(connection, id, cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<ClientCredentialView>> ListAsync(
        IReadOnlyList<long> accountIds,
        CancellationToken cancellationToken)
    {
        if (accountIds.Count == 0)
            return [];

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, label, purpose, key_prefix, created_at_utc, last_used_at_utc, disabled_at_utc
            FROM ingest_keys
            WHERE account_id IN ({string.Join(", ", accountIds.Select((_, index) => $"$account{index}"))})
              AND key_prefix IS NOT NULL
            ORDER BY disabled_at_utc IS NOT NULL, created_at_utc DESC
            """;
        for (var index = 0; index < accountIds.Count; index++)
            command.Parameters.AddWithValue($"$account{index}", accountIds[index]);

        var credentials = new List<ClientCredentialView>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            credentials.Add(new ClientCredentialView
            {
                Id = reader.GetInt64(0),
                Label = reader.GetString(1),
                Purpose = reader.GetString(2),
                KeyPrefix = reader.GetString(3),
                CreatedAtUtc = ParseTimestamp(reader.GetString(4)),
                LastUsedAtUtc = reader.IsDBNull(5) ? null : ParseTimestamp(reader.GetString(5)),
                RevokedAtUtc = reader.IsDBNull(6) ? null : ParseTimestamp(reader.GetString(6)),
            });
        }

        return credentials;
    }

    public async Task<ClientCredentialCreatedView> CreateAsync(
        long accountId,
        string label,
        string purpose,
        CancellationToken cancellationToken)
    {
        if (!WorkshopHostCredentialPurposes.IsSupported(purpose))
            throw new ArgumentException("Unsupported client credential purpose.", nameof(purpose));

        var normalizedLabel = label.Trim();
        if (normalizedLabel.Length is < 1 or > 80)
            throw new ArgumentException("Client credential labels must be between 1 and 80 characters.", nameof(label));

        var secret = CreateSecret(purpose);
        var prefix = secret[..Math.Min(secret.Length, 16)];
        var createdAt = DateTimeOffset.UtcNow;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ingest_keys (
                account_id,
                label,
                key_hash,
                purpose,
                key_prefix,
                created_at_utc
            )
            VALUES (
                $accountId,
                $label,
                $keyHash,
                $purpose,
                $keyPrefix,
                $createdAt
            );
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$label", normalizedLabel);
        command.Parameters.AddWithValue("$keyHash", HashKey(secret));
        command.Parameters.AddWithValue("$purpose", purpose);
        command.Parameters.AddWithValue("$keyPrefix", prefix);
        command.Parameters.AddWithValue("$createdAt", createdAt.ToString("O", CultureInfo.InvariantCulture));
        var id = (long)(await command.ExecuteScalarAsync(cancellationToken))!;

        return new ClientCredentialCreatedView
        {
            Id = id,
            Label = normalizedLabel,
            Purpose = purpose,
            KeyPrefix = prefix,
            CreatedAtUtc = createdAt,
            Secret = secret,
        };
    }

    public async Task<bool> RevokeAsync(
        long id,
        IReadOnlyList<long> accountIds,
        CancellationToken cancellationToken)
    {
        if (accountIds.Count == 0)
            return false;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE ingest_keys
            SET disabled_at_utc = $revokedAt
            WHERE id = $id
              AND account_id IN ({string.Join(", ", accountIds.Select((_, index) => $"$account{index}"))})
              AND key_prefix IS NOT NULL
              AND disabled_at_utc IS NULL
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$revokedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        for (var index = 0; index < accountIds.Count; index++)
            command.Parameters.AddWithValue($"$account{index}", accountIds[index]);

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public static string HashKey(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes);
    }

    private async Task RecordUseAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        long id,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (lastUsedWrites.TryGetValue(id, out var lastWrite) && now - lastWrite < LastUsedWriteInterval)
            return;

        lastUsedWrites[id] = now;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ingest_keys
            SET last_used_at_utc = $lastUsedAt
            WHERE id = $id
              AND disabled_at_utc IS NULL
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$lastUsedAt", now.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool AllowsScope(string purpose, WorkshopHostCredentialScope scope) =>
        purpose switch
        {
            WorkshopHostCredentialPurposes.CraftArchitect =>
                scope is WorkshopHostCredentialScope.CapabilitiesRead or WorkshopHostCredentialScope.AcquisitionQueue,
            WorkshopHostCredentialPurposes.MarketMafiosoClient => true,
            WorkshopHostCredentialPurposes.LegacyClient => true,
            _ => false,
        };

    private static string CreateSecret(string purpose)
    {
        var prefix = purpose == WorkshopHostCredentialPurposes.CraftArchitect ? "mmf_ca_" : "mmf_client_";
        return prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed record ConfiguredWorkshopHostKeys(
        string? Client,
        string? PreviousClient,
        string? InventoryWrite,
        string? InventoryRead,
        string? CraftQuote,
        string? AcquisitionQueue,
        string? DiagnosticsRead,
        string? AutomationRun)
    {
        public static ConfiguredWorkshopHostKeys FromConfiguration(IConfiguration configuration)
        {
            var client = FirstNonBlank(
                configuration["MarketMafioso:ClientApiKey"],
                configuration["MarketMafioso:ApiKey"],
                configuration["MarketMafioso:IngestApiKey"],
                configuration["MarketMafioso:CommandPickupApiKey"]);
            var previousClient = FirstNonBlank(
                configuration["MarketMafioso:PreviousClientApiKey"],
                configuration["MarketMafioso:PreviousIngestApiKey"],
                configuration["MarketMafioso:PreviousReadApiKey"]);
            return new ConfiguredWorkshopHostKeys(
                client,
                previousClient,
                FirstNonBlank(
                    configuration["MarketMafioso:InventoryWriteApiKey"],
                    configuration["MarketMafioso:IngestWriteApiKey"]),
                FirstNonBlank(
                    configuration["MarketMafioso:InventoryReadApiKey"],
                    configuration["MarketMafioso:ReportReadApiKey"]),
                configuration["MarketMafioso:CraftQuoteApiKey"],
                FirstNonBlank(
                    configuration["MarketMafioso:AcquisitionQueueApiKey"],
                    configuration["MarketMafioso:CommandPickupScopedApiKey"]),
                configuration["MarketMafioso:DiagnosticsReadApiKey"],
                configuration["MarketMafioso:AutomationRunApiKey"]);
        }

        public bool HasKeyForScope(string supplied, WorkshopHostCredentialScope scope)
        {
            if (Matches(supplied, Client) || Matches(supplied, PreviousClient))
                return true;

            return scope switch
            {
                WorkshopHostCredentialScope.CapabilitiesRead =>
                    Matches(supplied, InventoryWrite) ||
                    Matches(supplied, InventoryRead) ||
                    Matches(supplied, CraftQuote) ||
                    Matches(supplied, AcquisitionQueue) ||
                    Matches(supplied, DiagnosticsRead) ||
                    Matches(supplied, AutomationRun),
                WorkshopHostCredentialScope.InventoryWrite => Matches(supplied, InventoryWrite),
                WorkshopHostCredentialScope.InventoryRead => Matches(supplied, InventoryRead),
                WorkshopHostCredentialScope.CraftQuote => Matches(supplied, CraftQuote),
                WorkshopHostCredentialScope.AcquisitionQueue => Matches(supplied, AcquisitionQueue),
                WorkshopHostCredentialScope.DiagnosticsRead => Matches(supplied, DiagnosticsRead),
                WorkshopHostCredentialScope.AutomationRun => Matches(supplied, AutomationRun),
                _ => false,
            };
        }

        private static string? FirstNonBlank(params string?[] values) =>
            values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        private static bool Matches(string supplied, string? configured)
        {
            if (string.IsNullOrWhiteSpace(configured))
                return false;

            var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
            var configuredBytes = Encoding.UTF8.GetBytes(configured);
            return suppliedBytes.Length == configuredBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(suppliedBytes, configuredBytes);
        }
    }
}
