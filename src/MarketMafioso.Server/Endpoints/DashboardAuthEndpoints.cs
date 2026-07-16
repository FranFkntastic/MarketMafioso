using MarketMafioso.Server.Auth;

namespace MarketMafioso.Server.Endpoints;

internal static class DashboardAuthEndpoints
{
    public static void MapDashboardAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/auth/login", Login);
        app.MapPost("/auth/logout", Logout);
        app.MapGet("/auth/session", GetSession);
    }

    private static async Task<IResult> Login(
        HttpRequest request,
        HttpResponse response,
        DashboardSessionStore sessions,
        DashboardLoginRequest login,
        CancellationToken token)
    {
        var created = await sessions.CreateAsync(login.Username, login.Password, token);
        if (created == null)
            return Results.Unauthorized();

        response.Cookies.Append(
            DashboardSessionStore.CookieName,
            created.Token,
            CreateCookieOptions(request, created.Session.ExpiresAtUtc));

        return Results.Ok(new
        {
            user = new
            {
                created.Session.UserId,
                created.Session.Username,
            },
            created.Session.ExpiresAtUtc,
        });
    }

    private static async Task<IResult> Logout(
        HttpRequest request,
        HttpResponse response,
        DashboardSessionStore sessions,
        CancellationToken token)
    {
        await sessions.RevokeAsync(request.Cookies[DashboardSessionStore.CookieName], token);
        response.Cookies.Delete(
            DashboardSessionStore.CookieName,
            CreateCookieOptions(request, DateTimeOffset.UtcNow.AddDays(-1)));
        return Results.Ok(new { ok = true });
    }

    private static async Task<IResult> GetSession(
        HttpRequest request,
        DashboardSessionStore sessions,
        IConfiguration configuration,
        CancellationToken token)
    {
        if (!configuration.GetValue<bool>("MarketMafioso:RequireDashboardAuth"))
        {
            return Results.Ok(new
            {
                user = new { userId = 0L, username = "Local dashboard" },
                expiresAtUtc = DateTimeOffset.UtcNow.AddYears(100),
            });
        }

        var session = await sessions.GetAsync(request.Cookies[DashboardSessionStore.CookieName], token);
        if (session == null)
            return Results.Unauthorized();

        return Results.Ok(new
        {
            user = new
            {
                session.UserId,
                session.Username,
            },
            session.ExpiresAtUtc,
        });
    }

    private static CookieOptions CreateCookieOptions(HttpRequest request, DateTimeOffset expiresAt) => new()
    {
        HttpOnly = true,
        IsEssential = true,
        SameSite = SameSiteMode.Lax,
        Secure = request.IsHttps,
        Expires = expiresAt,
        Path = string.IsNullOrWhiteSpace(request.PathBase) ? "/" : request.PathBase.ToString(),
    };
}
