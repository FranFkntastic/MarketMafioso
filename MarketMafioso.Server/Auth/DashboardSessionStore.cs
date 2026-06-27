using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MarketMafioso.Server.Sqlite;

namespace MarketMafioso.Server.Auth;

public sealed class DashboardSessionStore
{
    public const string CookieName = "mmf_dashboard_session";
    public const string DashboardUserIdItemKey = "MarketMafioso.DashboardUserId";
    public const string DashboardSessionIdItemKey = "MarketMafioso.DashboardSessionId";

    private readonly SqliteConnectionFactory connectionFactory;
    private readonly DashboardPasswordHasher passwordHasher;
    private readonly IConfiguration configuration;

    public DashboardSessionStore(
        SqliteConnectionFactory connectionFactory,
        DashboardPasswordHasher passwordHasher,
        IConfiguration configuration)
    {
        this.connectionFactory = connectionFactory;
        this.passwordHasher = passwordHasher;
        this.configuration = configuration;
    }

    public async Task<DashboardSessionCreateResult?> CreateAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return null;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT id, username, password_hash
            FROM dashboard_users
            WHERE username = $username
              AND disabled_at_utc IS NULL
            """;
        query.Parameters.AddWithValue("$username", username.Trim());

        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var userId = reader.GetInt64(0);
        var normalizedUsername = reader.GetString(1);
        var passwordHash = reader.GetString(2);
        if (!passwordHasher.VerifyPassword(password, passwordHash))
            return null;

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(SessionLifetime);
        var sessionId = Guid.NewGuid().ToString("N");
        var token = CreateToken();
        var tokenHash = HashToken(token);

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO dashboard_sessions (
                    id,
                    dashboard_user_id,
                    token_hash,
                    created_at_utc,
                    expires_at_utc,
                    last_seen_at_utc
                )
                VALUES (
                    $id,
                    $dashboardUserId,
                    $tokenHash,
                    $createdAt,
                    $expiresAt,
                    $lastSeenAt
                )
                """;
            insert.Parameters.AddWithValue("$id", sessionId);
            insert.Parameters.AddWithValue("$dashboardUserId", userId);
            insert.Parameters.AddWithValue("$tokenHash", tokenHash);
            insert.Parameters.AddWithValue("$createdAt", now.ToString("O", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$expiresAt", expiresAt.ToString("O", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$lastSeenAt", now.ToString("O", CultureInfo.InvariantCulture));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var updateLogin = connection.CreateCommand())
        {
            updateLogin.CommandText = """
                UPDATE dashboard_users
                SET last_login_at_utc = $lastLoginAt
                WHERE id = $dashboardUserId
                """;
            updateLogin.Parameters.AddWithValue("$lastLoginAt", now.ToString("O", CultureInfo.InvariantCulture));
            updateLogin.Parameters.AddWithValue("$dashboardUserId", userId);
            await updateLogin.ExecuteNonQueryAsync(cancellationToken);
        }

        return new DashboardSessionCreateResult(
            token,
            new DashboardSessionView(sessionId, userId, normalizedUsername, expiresAt));
    }

    public async Task<DashboardSessionView?> GetAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var tokenHash = HashToken(token);
        var now = DateTimeOffset.UtcNow;
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT sessions.id, users.id, users.username, sessions.expires_at_utc
            FROM dashboard_sessions AS sessions
            JOIN dashboard_users AS users ON users.id = sessions.dashboard_user_id
            WHERE sessions.token_hash = $tokenHash
              AND sessions.revoked_at_utc IS NULL
              AND sessions.expires_at_utc > $now
              AND users.disabled_at_utc IS NULL
            """;
        query.Parameters.AddWithValue("$tokenHash", tokenHash);
        query.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));

        await using var reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var session = new DashboardSessionView(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture));

        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE dashboard_sessions
            SET last_seen_at_utc = $lastSeenAt
            WHERE id = $sessionId
            """;
        update.Parameters.AddWithValue("$lastSeenAt", now.ToString("O", CultureInfo.InvariantCulture));
        update.Parameters.AddWithValue("$sessionId", session.SessionId);
        await update.ExecuteNonQueryAsync(cancellationToken);

        return session;
    }

    public async Task RevokeAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
            return;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dashboard_sessions
            SET revoked_at_utc = $revokedAt
            WHERE token_hash = $tokenHash
              AND revoked_at_utc IS NULL
            """;
        command.Parameters.AddWithValue("$revokedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$tokenHash", HashToken(token));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private TimeSpan SessionLifetime =>
        TimeSpan.FromMinutes(Math.Max(5, configuration.GetValue("MarketMafioso:DashboardSessionMinutes", 720)));

    private static string CreateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    private static string HashToken(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash);
    }
}

public sealed record DashboardSessionCreateResult(string Token, DashboardSessionView Session);

public sealed record DashboardSessionView(
    string SessionId,
    long UserId,
    string Username,
    DateTimeOffset ExpiresAtUtc);
