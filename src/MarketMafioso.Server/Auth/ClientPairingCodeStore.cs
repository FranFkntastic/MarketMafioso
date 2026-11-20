using System.Globalization;
using System.Security.Cryptography;
using MarketMafioso.Server.Sqlite;

namespace MarketMafioso.Server.Auth;

public enum ClientPairingRedeemOutcome
{
    Redeemed,
    InvalidCode,
    Expired,
    AlreadyUsed,
}

public sealed record ClientPairingCodeCreated(string Token, DateTimeOffset ExpiresAtUtc);

public sealed record ClientPairingClaim(
    ClientPairingRedeemOutcome Outcome,
    long PairingId = 0,
    long AccountId = 0,
    DateTimeOffset? CreatedAtUtc = null,
    DateTimeOffset? ExpiresAtUtc = null,
    DateTimeOffset? RedeemedAtUtc = null);

/// <summary>
/// Short-lived, single-use codes that let a signed-in dashboard user hand a client credential
/// to a game client. Only the SHA-256 of each token is stored.
/// </summary>
public sealed class ClientPairingCodeStore
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    public const int MaxOutstandingCodesPerAccount = 5;
    private static readonly TimeSpan RetainRowsFor = TimeSpan.FromDays(7);

    private readonly SqliteConnectionFactory connectionFactory;

    public ClientPairingCodeStore(SqliteConnectionFactory connectionFactory)
    {
        this.connectionFactory = connectionFactory;
    }

    /// <summary>Returns null when the account already has the maximum number of outstanding codes.</summary>
    public async Task<ClientPairingCodeCreated?> CreateAsync(long accountId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now + CodeLifetime;
        var token = CreateToken();

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        using var write = connectionFactory.BeginWrite(connection);

        await using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = write.Transaction;
            cleanup.CommandText = "DELETE FROM client_pairing_codes WHERE expires_at_utc < $cutoff";
            cleanup.Parameters.AddWithValue("$cutoff", Format(now - RetainRowsFor));
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var count = connection.CreateCommand())
        {
            count.Transaction = write.Transaction;
            count.CommandText = """
                SELECT COUNT(*)
                FROM client_pairing_codes
                WHERE account_id = $accountId
                  AND redeemed_at_utc IS NULL
                  AND expires_at_utc > $now
                """;
            count.Parameters.AddWithValue("$accountId", accountId);
            count.Parameters.AddWithValue("$now", Format(now));
            if ((long)(await count.ExecuteScalarAsync(cancellationToken))! >= MaxOutstandingCodesPerAccount)
                return null;
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = write.Transaction;
            insert.CommandText = """
                INSERT INTO client_pairing_codes (account_id, token_hash, created_at_utc, expires_at_utc)
                VALUES ($accountId, $tokenHash, $createdAt, $expiresAt)
                """;
            insert.Parameters.AddWithValue("$accountId", accountId);
            insert.Parameters.AddWithValue("$tokenHash", WorkshopHostCredentialStore.HashKey(token));
            insert.Parameters.AddWithValue("$createdAt", Format(now));
            insert.Parameters.AddWithValue("$expiresAt", Format(expiresAt));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await write.Transaction.CommitAsync(cancellationToken);
        return new ClientPairingCodeCreated(token, expiresAt);
    }

    /// <summary>
    /// Claims a code. The claim is one conditional UPDATE, so of any number of concurrent
    /// redeems for the same token exactly one observes a changed row.
    /// </summary>
    public async Task<ClientPairingClaim> ClaimAsync(string token, CancellationToken cancellationToken)
    {
        var tokenHash = WorkshopHostCredentialStore.HashKey(token);
        var now = DateTimeOffset.UtcNow;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using (var claim = connection.CreateCommand())
        {
            claim.CommandText = """
                UPDATE client_pairing_codes
                SET redeemed_at_utc = $now
                WHERE token_hash = $tokenHash
                  AND redeemed_at_utc IS NULL
                  AND expires_at_utc > $now
                RETURNING id, account_id, created_at_utc, expires_at_utc
                """;
            claim.Parameters.AddWithValue("$tokenHash", tokenHash);
            claim.Parameters.AddWithValue("$now", Format(now));
            await using var reader = await claim.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return new ClientPairingClaim(
                    ClientPairingRedeemOutcome.Redeemed,
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    Parse(reader.GetString(2)),
                    Parse(reader.GetString(3)),
                    now);
            }
        }

        await using var diagnose = connection.CreateCommand();
        diagnose.CommandText = """
            SELECT id, account_id, created_at_utc, expires_at_utc, redeemed_at_utc
            FROM client_pairing_codes
            WHERE token_hash = $tokenHash
            """;
        diagnose.Parameters.AddWithValue("$tokenHash", tokenHash);
        await using var row = await diagnose.ExecuteReaderAsync(cancellationToken);
        if (!await row.ReadAsync(cancellationToken))
            return new ClientPairingClaim(ClientPairingRedeemOutcome.InvalidCode);

        var redeemedAt = row.IsDBNull(4) ? (DateTimeOffset?)null : Parse(row.GetString(4));
        return new ClientPairingClaim(
            redeemedAt is null ? ClientPairingRedeemOutcome.Expired : ClientPairingRedeemOutcome.AlreadyUsed,
            row.GetInt64(0),
            row.GetInt64(1),
            Parse(row.GetString(2)),
            Parse(row.GetString(3)),
            redeemedAt);
    }

    public async Task AttachCredentialAsync(long pairingId, long credentialId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE client_pairing_codes SET credential_id = $credentialId WHERE id = $id";
        command.Parameters.AddWithValue("$credentialId", credentialId);
        command.Parameters.AddWithValue("$id", pairingId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string CreateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    // Round-trip UTC timestamps share one fixed-width format, so SQL string comparison orders them correctly.
    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
