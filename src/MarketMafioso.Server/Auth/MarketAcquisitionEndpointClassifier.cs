namespace MarketMafioso.Server.Auth;

internal static class MarketAcquisitionEndpointClassifier
{
    public static bool IsBrowserCreate(HttpRequest request) =>
        IsCreate(request) &&
        !HasApiKey(request) &&
        (request.HasFormContentType || request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase));

    public static bool IsBrowserControl(HttpRequest request) =>
        !HasApiKey(request) &&
        ((HttpMethods.IsPost(request.Method) && IsCancelOrResend(request.Path)) ||
         (HttpMethods.IsPut(request.Method) && IsBatchResource(request.Path)));

    public static bool IsBrowserListRead(HttpRequest request) =>
        HttpMethods.IsGet(request.Method) &&
        request.Path.Equals("/api/acquisition/requests", StringComparison.OrdinalIgnoreCase);

    public static bool RequiresPluginCredential(HttpRequest request)
    {
        if (IsCreate(request))
            return HasApiKey(request) ||
                   (!request.HasFormContentType &&
                    !request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase));

        if (HttpMethods.IsGet(request.Method))
        {
            if (IsPending(request.Path))
                return true;
            if (IsTimeline(request.Path) || IsBatchDetail(request.Path))
                return HasApiKey(request);
            return false;
        }

        if (HttpMethods.IsPut(request.Method) && IsBatchResource(request.Path))
            return HasApiKey(request);

        if (!HttpMethods.IsPost(request.Method))
            return false;

        if (IsCancelOrResend(request.Path))
            return HasApiKey(request);

        var path = request.Path.Value ?? string.Empty;
        return ((path.StartsWith("/acquisition/requests/", StringComparison.OrdinalIgnoreCase) ||
                 path.StartsWith("/api/acquisition/requests/", StringComparison.OrdinalIgnoreCase)) &&
                (path.EndsWith("/claim", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith("/accept", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith("/reject", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith("/progress", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith("/complete", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith("/fail", StringComparison.OrdinalIgnoreCase))) ||
               ((path.StartsWith("/acquisition/batches/", StringComparison.OrdinalIgnoreCase) ||
                 path.StartsWith("/api/acquisition/batches/", StringComparison.OrdinalIgnoreCase)) &&
                (path.EndsWith("/purchases", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith("/observations", StringComparison.OrdinalIgnoreCase) ||
                 (path.Contains("/lines/", StringComparison.OrdinalIgnoreCase) &&
                  path.EndsWith("/progress", StringComparison.OrdinalIgnoreCase))));
    }

    private static bool IsCreate(HttpRequest request) =>
        HttpMethods.IsPost(request.Method) &&
        (request.Path.Equals("/acquisition/requests", StringComparison.OrdinalIgnoreCase) ||
         request.Path.Equals("/api/acquisition/requests", StringComparison.OrdinalIgnoreCase) ||
         request.Path.Equals("/acquisition/batches", StringComparison.OrdinalIgnoreCase) ||
         request.Path.Equals("/api/acquisition/batches", StringComparison.OrdinalIgnoreCase));

    private static bool IsPending(PathString path) =>
        path.Equals("/acquisition/requests/pending", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/acquisition/requests/pending", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/acquisition/batches/pending", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/api/acquisition/batches/pending", StringComparison.OrdinalIgnoreCase);

    private static bool IsTimeline(PathString requestPath)
    {
        var path = requestPath.Value ?? string.Empty;
        return path.StartsWith("/api/acquisition/requests/", StringComparison.OrdinalIgnoreCase) &&
               path.EndsWith("/timeline", StringComparison.OrdinalIgnoreCase) &&
               path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length == 5;
    }

    private static bool IsBatchDetail(PathString requestPath)
    {
        var path = requestPath.Value ?? string.Empty;
        if (!path.StartsWith("/acquisition/batches/", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/api/acquisition/batches/", StringComparison.OrdinalIgnoreCase))
            return false;
        return path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length is 3 or 4;
    }

    private static bool IsBatchResource(PathString requestPath) =>
        requestPath.StartsWithSegments("/acquisition/batches", StringComparison.OrdinalIgnoreCase) ||
        requestPath.StartsWithSegments("/api/acquisition/batches", StringComparison.OrdinalIgnoreCase);

    private static bool IsCancelOrResend(PathString requestPath)
    {
        var path = requestPath.Value ?? string.Empty;
        return (path.StartsWith("/acquisition/requests/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/api/acquisition/requests/", StringComparison.OrdinalIgnoreCase)) &&
               (path.EndsWith("/cancel", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/resend", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasApiKey(HttpRequest request) => request.Headers.ContainsKey("X-Api-Key");
}
