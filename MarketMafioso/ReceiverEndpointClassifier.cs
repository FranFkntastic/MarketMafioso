using System;

namespace MarketMafioso;

public enum ReceiverEndpointKind
{
    Invalid,
    Local,
    KnownHosted,
    CustomRemote,
}

public readonly record struct ReceiverEndpointInfo(
    ReceiverEndpointKind Kind,
    Uri? Uri,
    string? DashboardBaseUrl)
{
    public bool RequiresApiKey =>
        Kind is ReceiverEndpointKind.KnownHosted or ReceiverEndpointKind.CustomRemote;
}

public static class ReceiverEndpointClassifier
{
    public static ReceiverEndpointInfo Classify(string? serverUrl)
    {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return new ReceiverEndpointInfo(ReceiverEndpointKind.Invalid, null, null);

        if (IsLocalHost(uri.Host))
            return new ReceiverEndpointInfo(ReceiverEndpointKind.Local, uri, DeriveDashboardBaseUrl(uri));

        if (uri.Host.Equals("dev.xivcraftarchitect.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("xivcraftarchitect.com", StringComparison.OrdinalIgnoreCase))
        {
            var dashboardBaseUrl = $"{uri.Scheme}://{uri.Host}/api/marketmafioso";
            return new ReceiverEndpointInfo(ReceiverEndpointKind.KnownHosted, uri, dashboardBaseUrl);
        }

        return new ReceiverEndpointInfo(ReceiverEndpointKind.CustomRemote, uri, DeriveDashboardBaseUrl(uri));
    }

    public static string? BuildDashboardReportUrl(string? serverUrl, string? reportId)
    {
        if (string.IsNullOrWhiteSpace(reportId))
            return null;

        var endpoint = Classify(serverUrl);
        return string.IsNullOrWhiteSpace(endpoint.DashboardBaseUrl)
            ? null
            : $"{endpoint.DashboardBaseUrl}/reports/{Uri.EscapeDataString(reportId)}";
    }

    public static string? BuildDashboardUrl(string? serverUrl)
    {
        var endpoint = Classify(serverUrl);
        return string.IsNullOrWhiteSpace(endpoint.DashboardBaseUrl)
            ? null
            : $"{endpoint.DashboardBaseUrl}/";
    }

    private static bool IsLocalHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("::1", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("[::1]", StringComparison.OrdinalIgnoreCase);

    private static string? DeriveDashboardBaseUrl(Uri uri)
    {
        var path = uri.AbsolutePath;
        if (!path.EndsWith("/inventory", StringComparison.OrdinalIgnoreCase))
            return null;

        var basePath = path[..^"/inventory".Length].TrimEnd('/');
        return $"{uri.Scheme}://{uri.Authority}{basePath}";
    }
}
