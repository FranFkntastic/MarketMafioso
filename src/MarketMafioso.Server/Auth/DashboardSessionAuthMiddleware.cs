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

        if (MarketAcquisitionEndpointClassifier.RequiresPluginCredential(request))
            return false;

        if (request.Path.StartsWithSegments("/api/acquisition", StringComparison.OrdinalIgnoreCase) ||
            request.Path.StartsWithSegments("/api/inventory", StringComparison.OrdinalIgnoreCase) ||
            request.Path.StartsWithSegments("/api/reports", StringComparison.OrdinalIgnoreCase) ||
            request.Path.StartsWithSegments("/api/settings", StringComparison.OrdinalIgnoreCase) ||
            request.Path.StartsWithSegments("/api/diagnostics", StringComparison.OrdinalIgnoreCase) ||
            request.Path.StartsWithSegments("/api/events", StringComparison.OrdinalIgnoreCase) ||
            request.Path.StartsWithSegments("/api/xivdata", StringComparison.OrdinalIgnoreCase) ||
            request.Path.StartsWithSegments("/reports", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsPluginInventoryIngestRoute(HttpRequest request) =>
        HttpMethods.IsPost(request.Method) &&
        (request.Path.Equals("/inventory", StringComparison.OrdinalIgnoreCase) ||
         request.Path.Equals("/api/inventory", StringComparison.OrdinalIgnoreCase));

}
