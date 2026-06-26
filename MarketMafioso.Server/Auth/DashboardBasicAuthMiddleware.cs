using System.Net.Http.Headers;
using System.Text;
using System.Globalization;
using MarketMafioso.Server.Sqlite;

namespace MarketMafioso.Server.Auth;

public sealed class DashboardBasicAuthMiddleware
{
    private const string Realm = "MarketMafioso Receiver";

    private readonly RequestDelegate next;
    private readonly IConfiguration configuration;
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly DashboardPasswordHasher passwordHasher;
    private readonly ILogger<DashboardBasicAuthMiddleware> log;

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
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!configuration.GetValue<bool>("MarketMafioso:RequireDashboardAuth") ||
            !IsDashboardRoute(context.Request))
        {
            await next(context);
            return;
        }

        var credentials = ParseBasicCredentials(context.Request.Headers.Authorization);
        if (credentials == null ||
            !await IsValidDashboardUserAsync(credentials.Value.Username, credentials.Value.Password, context.RequestAborted))
        {
            await ChallengeAsync(context);
            return;
        }

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

    private async Task<bool> IsValidDashboardUserAsync(string username, string password, CancellationToken cancellationToken)
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
            return false;

        var userId = reader.GetInt64(0);
        var passwordHash = reader.GetString(1);
        if (!passwordHasher.VerifyPassword(password, passwordHash))
            return false;

        await UpdateLastLoginAsync(connection, userId, cancellationToken);
        return true;
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
}
