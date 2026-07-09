namespace MarketMafioso.Server.Auth;

public sealed class DashboardSessionAuthMiddleware
{
    private readonly RequestDelegate next;
    private readonly IConfiguration configuration;
    private readonly DashboardSessionStore sessions;

    public DashboardSessionAuthMiddleware(
        RequestDelegate next,
        IConfiguration configuration,
        DashboardSessionStore sessions)
    {
        this.next = next;
        this.configuration = configuration;
        this.sessions = sessions;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!configuration.GetValue<bool>("MarketMafioso:RequireDashboardAuth") ||
            !RequiresDashboardSession(context.Request))
        {
            await next(context);
            return;
        }

        var session = await sessions.GetAsync(
            context.Request.Cookies[DashboardSessionStore.CookieName],
            context.RequestAborted);
        if (session == null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "dashboard_session_required" }, context.RequestAborted);
            return;
        }

        context.Items[DashboardSessionStore.DashboardUserIdItemKey] = session.UserId;
        context.Items[DashboardSessionStore.DashboardSessionIdItemKey] = session.SessionId;
        await next(context);
    }

    private static bool RequiresDashboardSession(HttpRequest request)
    {
        if (request.Path.Equals("/api/settings/features", StringComparison.OrdinalIgnoreCase))
            return false;

        if (IsPluginInventoryIngestRoute(request))
            return false;

        if (IsPluginAcquisitionApiRoute(request))
            return false;

        if (request.Path.StartsWithSegments("/api/acquisition", StringComparison.OrdinalIgnoreCase) ||
            request.Path.StartsWithSegments("/api/inventory", StringComparison.OrdinalIgnoreCase) ||
            request.Path.StartsWithSegments("/api/settings", StringComparison.OrdinalIgnoreCase) ||
            request.Path.StartsWithSegments("/api/diagnostics", StringComparison.OrdinalIgnoreCase) ||
            request.Path.StartsWithSegments("/api/events", StringComparison.OrdinalIgnoreCase) ||
            request.Path.StartsWithSegments("/api/xivdata", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (HttpMethods.IsDelete(request.Method) &&
            request.Path.StartsWithSegments("/api/reports", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsPluginInventoryIngestRoute(HttpRequest request) =>
        HttpMethods.IsPost(request.Method) &&
        request.Headers.ContainsKey("X-Api-Key") &&
        (request.Path.Equals("/inventory", StringComparison.OrdinalIgnoreCase) ||
         request.Path.Equals("/api/inventory", StringComparison.OrdinalIgnoreCase));

    private static bool IsPluginAcquisitionApiRoute(HttpRequest request)
    {
        if (!request.Path.StartsWithSegments("/api/acquisition/requests", StringComparison.OrdinalIgnoreCase) &&
            !request.Path.StartsWithSegments("/api/acquisition/batches", StringComparison.OrdinalIgnoreCase))
            return false;

        if (HttpMethods.IsPost(request.Method) &&
            (request.Path.Equals("/api/acquisition/requests", StringComparison.OrdinalIgnoreCase) ||
             request.Path.Equals("/api/acquisition/batches", StringComparison.OrdinalIgnoreCase)))
        {
            return request.Headers.ContainsKey("X-Api-Key");
        }

        if (HttpMethods.IsGet(request.Method) &&
            (request.Path.Equals("/api/acquisition/requests/pending", StringComparison.OrdinalIgnoreCase) ||
             request.Path.Equals("/api/acquisition/batches/pending", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (HttpMethods.IsGet(request.Method) &&
            request.Headers.ContainsKey("X-Api-Key") &&
            IsApiBatchDetailPath(request.Path))
        {
            return true;
        }

        if (HttpMethods.IsPut(request.Method) &&
            request.Headers.ContainsKey("X-Api-Key") &&
            request.Path.StartsWithSegments("/api/acquisition/batches", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!HttpMethods.IsPost(request.Method))
            return false;

        var path = request.Path.Value ?? string.Empty;
        if (path.StartsWith("/api/acquisition/batches/", StringComparison.OrdinalIgnoreCase))
        {
            return request.Headers.ContainsKey("X-Api-Key") &&
                   (path.EndsWith("/purchases", StringComparison.OrdinalIgnoreCase) ||
                    (path.Contains("/lines/", StringComparison.OrdinalIgnoreCase) &&
                     path.EndsWith("/progress", StringComparison.OrdinalIgnoreCase)));
        }

        return path.Contains("/claim", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/accept", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/reject", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/progress", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/complete", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/fail", StringComparison.OrdinalIgnoreCase) ||
               (request.Headers.ContainsKey("X-Api-Key") &&
                (path.Contains("/cancel", StringComparison.OrdinalIgnoreCase) ||
                 path.Contains("/resend", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsApiBatchDetailPath(PathString path)
    {
        var value = path.Value ?? string.Empty;
        if (!value.StartsWith("/api/acquisition/batches/", StringComparison.OrdinalIgnoreCase))
            return false;

        return value.Split('/', StringSplitOptions.RemoveEmptyEntries).Length == 4;
    }
}
