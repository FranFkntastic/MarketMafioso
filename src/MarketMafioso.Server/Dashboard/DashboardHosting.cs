using System.Text;

namespace MarketMafioso.Server.Dashboard;

internal static class DashboardHosting
{
    public static void UseDashboardStaticAssets(this WebApplication app) =>
        app.Use(ServeDashboardStaticAssetAsync);

    public static void MapDashboardShellEndpoints(this WebApplication app, bool enableMarketAcquisition)
    {
        app.MapGet("/", ServeBlazorIndex);
        if (enableMarketAcquisition)
            app.MapGet("/acquisition", ServeBlazorIndex);
        app.MapGet("/inventory", ServeBlazorIndex);
        app.MapGet("/overview", ServeBlazorIndex);
        app.MapGet("/settings", ServeBlazorIndex);
    }

    private static async Task<IResult> ServeBlazorIndex(
        HttpRequest request,
        HttpResponse response,
        IWebHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        var file = environment.WebRootFileProvider.GetFileInfo("index.html");
        if (!file.Exists)
            return Results.NotFound();

        await using var stream = file.CreateReadStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var html = await reader.ReadToEndAsync(cancellationToken);
        var baseHref = string.IsNullOrWhiteSpace(request.PathBase)
            ? "/"
            : $"{request.PathBase.ToString().TrimEnd('/')}/";
        html = html.Replace("<base href=\"/\" />", $"<base href=\"{Html(baseHref)}\" />", StringComparison.Ordinal);
        html = html.Replace(
            "css/app.css",
            $"css/app.css?v={ResolveDashboardAssetVersion(environment, "css/app.css")}",
            StringComparison.Ordinal);
        html = html.Replace(
            "_framework/blazor.webassembly#[.{fingerprint}].js",
            ResolveBlazorBootScript(environment),
            StringComparison.Ordinal);
        response.Headers.CacheControl = "no-cache";
        return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8);
    }

    private static string ResolveDashboardAssetVersion(IWebHostEnvironment environment, string path)
    {
        var file = environment.WebRootFileProvider.GetFileInfo(path);
        if (!file.Exists)
            return "missing";

        return $"{file.Length:x}-{file.LastModified.UtcTicks:x}";
    }

    private static string ResolveBlazorBootScript(IWebHostEnvironment environment)
    {
        var directory = environment.WebRootFileProvider.GetDirectoryContents("_framework");
        if (!directory.Exists)
            throw new DirectoryNotFoundException("Dashboard framework assets were not found under wwwroot/_framework.");

        var bootScript = directory
            .Where(file => !file.IsDirectory &&
                           file.Name.StartsWith("blazor.webassembly.", StringComparison.Ordinal) &&
                           file.Name.EndsWith(".js", StringComparison.Ordinal))
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .FirstOrDefault();

        if (bootScript == null)
            throw new FileNotFoundException("Dashboard Blazor WebAssembly boot script was not found under wwwroot/_framework.");

        return $"_framework/{bootScript.Name}";
    }

    private static async Task ServeDashboardStaticAssetAsync(HttpContext context, RequestDelegate next)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            await next(context);
            return;
        }

        var path = context.Request.Path.Value?.TrimStart('/');
        if (string.IsNullOrWhiteSpace(path) || !IsDashboardStaticAssetPath(path))
        {
            await next(context);
            return;
        }

        var environment = context.RequestServices.GetRequiredService<IWebHostEnvironment>();
        var resolvedPath = ResolveDashboardStaticAssetPath(environment, path);
        var file = environment.WebRootFileProvider.GetFileInfo(resolvedPath);
        if (!file.Exists || file.IsDirectory)
        {
            await next(context);
            return;
        }

        context.Response.ContentType = ContentTypeForDashboardAsset(resolvedPath);
        if (path.StartsWith("css/", StringComparison.Ordinal) ||
            path.Equals("MarketMafioso.Dashboard.styles.css", StringComparison.Ordinal))
        {
            context.Response.Headers.CacheControl = "no-cache";
        }

        if (HttpMethods.IsHead(context.Request.Method))
            return;

        await using var stream = file.CreateReadStream();
        await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private static bool IsDashboardStaticAssetPath(string path) =>
        path.StartsWith("_framework/", StringComparison.Ordinal) ||
        path.StartsWith("_content/", StringComparison.Ordinal) ||
        path.StartsWith("css/", StringComparison.Ordinal) ||
        path.StartsWith("js/", StringComparison.Ordinal) ||
        path.Equals("favicon.png", StringComparison.Ordinal) ||
        path.Equals("icon-192.png", StringComparison.Ordinal) ||
        path.Equals("MarketMafioso.Dashboard.styles.css", StringComparison.Ordinal);

    private static string ResolveDashboardStaticAssetPath(IWebHostEnvironment environment, string path) =>
        path switch
        {
            "_framework/dotnet.js" => ResolveSingleDashboardAsset(environment, "_framework", "dotnet.", ".js", static name =>
                !name.StartsWith("dotnet.native.", StringComparison.Ordinal) &&
                !name.StartsWith("dotnet.runtime.", StringComparison.Ordinal)),
            _ => path,
        };

    private static string ResolveSingleDashboardAsset(
        IWebHostEnvironment environment,
        string directoryPath,
        string prefix,
        string suffix,
        Func<string, bool>? predicate = null)
    {
        var directory = environment.WebRootFileProvider.GetDirectoryContents(directoryPath);
        if (!directory.Exists)
            return $"{directoryPath}/{prefix.TrimEnd('.')}{suffix}";

        var asset = directory
            .Where(file => !file.IsDirectory &&
                           file.Name.StartsWith(prefix, StringComparison.Ordinal) &&
                           file.Name.EndsWith(suffix, StringComparison.Ordinal) &&
                           (predicate == null || predicate(file.Name)))
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .FirstOrDefault();

        return asset == null
            ? $"{directoryPath}/{prefix.TrimEnd('.')}{suffix}"
            : $"{directoryPath}/{asset.Name}";
    }

    private static string ContentTypeForDashboardAsset(string path)
    {
        var extension = Path.GetExtension(path);
        return extension switch
        {
            ".css" => "text/css; charset=utf-8",
            ".dat" => "application/octet-stream",
            ".js" => "text/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".map" => "application/json; charset=utf-8",
            ".png" => "image/png",
            ".wasm" => "application/wasm",
            _ => "application/octet-stream",
        };
    }

    private static string Html(string? value) =>
        (value ?? string.Empty)
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
}
