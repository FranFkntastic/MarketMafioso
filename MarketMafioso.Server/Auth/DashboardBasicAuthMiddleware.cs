using System.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Text;
using System.Globalization;
using MarketMafioso.Server.Sqlite;

namespace MarketMafioso.Server.Auth;

public sealed class DashboardBasicAuthMiddleware
{
    public const string DashboardUserIdItemKey = "MarketMafioso.DashboardUserId";

    private const string Realm = "MarketMafioso Receiver";

    private readonly RequestDelegate next;
    private readonly IConfiguration configuration;
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly DashboardPasswordHasher passwordHasher;
    private readonly ILogger<DashboardBasicAuthMiddleware> log;
    private readonly ConcurrentDictionary<string, CachedDashboardAuth> acceptedCredentials = new(StringComparer.Ordinal);
    private readonly TimeSpan acceptedCredentialTtl;

    public DashboardBasicAuthMiddleware(
        RequestDelegate next,
        IConfiguration configuration,
        SqliteConnectionFactory connectionFactory,
        DashboardPasswordHasher passwordHasher,
        ILogger<DashboardBasicAuthMiddleware> log)
    {
        this.next = next;
        this.configuration = configuration;
        this.connectionFactory = connectionFactory;
        this.passwordHasher = passwordHasher;
        this.log = log;
        acceptedCredentialTtl = TimeSpan.FromSeconds(Math.Max(
            0,
            configuration.GetValue("MarketMafioso:DashboardAuthCacheSeconds", 300)));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!configuration.GetValue<bool>("MarketMafioso:RequireDashboardAuth") ||
            !IsDashboardRoute(context.Request))
        {
            await next(context);
            return;
        }

        var authorizationHeader = context.Request.Headers.Authorization.ToString();
        if (TryGetAcceptedCredential(authorizationHeader, out var cachedUserId))
        {
            context.Items[DashboardUserIdItemKey] = cachedUserId;
            await next(context);
            return;
        }

        var credentials = ParseBasicCredentials(authorizationHeader);
        var userId = credentials == null
            ? null
            : await TryValidateDashboardUserAsync(credentials.Value.Username, credentials.Value.Password, context.RequestAborted);
        if (userId == null)
        {
            await ChallengeAsync(context);
            return;
        }

        context.Items[DashboardUserIdItemKey] = userId.Value;
        CacheAcceptedCredential(authorizationHeader, userId.Value);
        await next(context);
    }

    private static bool IsDashboardRoute(HttpRequest request)
    {
        if (HttpMethods.IsGet(request.Method))
        {
            return request.Path.Equals("/", StringComparison.OrdinalIgnoreCase) ||
                   request.Path == PathString.Empty ||
                   request.Path.Equals("/inventory", StringComparison.OrdinalIgnoreCase) ||
                   request.Path.Equals("/acquisition", StringComparison.OrdinalIgnoreCase) ||
                   request.Path.Equals("/acquisition/requests/recent", StringComparison.OrdinalIgnoreCase) ||
                   request.Path.Equals("/diagnostics", StringComparison.OrdinalIgnoreCase) ||
                   request.Path.StartsWithSegments("/reports", StringComparison.OrdinalIgnoreCase);
        }

        return HttpMethods.IsPost(request.Method) &&
               (request.Path.StartsWithSegments("/reports", StringComparison.OrdinalIgnoreCase) ||
                (request.Path.Equals("/acquisition/requests", StringComparison.OrdinalIgnoreCase) &&
                 request.HasFormContentType));
    }

    private bool TryGetAcceptedCredential(string authorizationHeader, out long userId)
    {
        userId = 0;
        if (acceptedCredentialTtl <= TimeSpan.Zero ||
            string.IsNullOrWhiteSpace(authorizationHeader) ||
            !acceptedCredentials.TryGetValue(authorizationHeader, out var cached))
        {
            return false;
        }

        if (cached.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            acceptedCredentials.TryRemove(authorizationHeader, out _);
            return false;
        }

        userId = cached.UserId;
        return true;
    }

    private void CacheAcceptedCredential(string authorizationHeader, long userId)
    {
        if (acceptedCredentialTtl <= TimeSpan.Zero || string.IsNullOrWhiteSpace(authorizationHeader))
            return;

        acceptedCredentials[authorizationHeader] = new CachedDashboardAuth(
            userId,
            DateTimeOffset.UtcNow.Add(acceptedCredentialTtl));
    }

    private async Task<long?> TryValidateDashboardUserAsync(string username, string password, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, password_hash
            FROM dashboard_users
            WHERE username = $username
              AND disabled_at_utc IS NULL
            """;
        command.Parameters.AddWithValue("$username", username);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var userId = reader.GetInt64(0);
        var passwordHash = reader.GetString(1);
        if (!passwordHasher.VerifyPassword(password, passwordHash))
            return null;

        await UpdateLastLoginAsync(connection, userId, cancellationToken);
        return userId;
    }

    private async Task UpdateLastLoginAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        long userId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE dashboard_users
                SET last_login_at_utc = $lastLoginAt
                WHERE id = $userId
                """;
            command.Parameters.AddWithValue("$lastLoginAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$userId", userId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            log.LogWarning(ex, "Could not update dashboard user last login timestamp.");
        }
    }

    private static (string Username, string Password)? ParseBasicCredentials(string? header)
    {
        if (string.IsNullOrWhiteSpace(header) ||
            !AuthenticationHeaderValue.TryParse(header, out var auth) ||
            !string.Equals(auth.Scheme, "Basic", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(auth.Parameter))
        {
            return null;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter));
        }
        catch (FormatException)
        {
            return null;
        }

        var separator = decoded.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0)
            return null;

        return (decoded[..separator], decoded[(separator + 1)..]);
    }

    private static Task ChallengeAsync(HttpContext context)
    {
        context.Response.Headers.WWWAuthenticate = $"Basic realm=\"{Realm}\", charset=\"UTF-8\"";
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return context.Response.WriteAsJsonAsync(new { error = "invalid_dashboard_credentials" });
    }

    private readonly record struct CachedDashboardAuth(long UserId, DateTimeOffset ExpiresAtUtc);
}
