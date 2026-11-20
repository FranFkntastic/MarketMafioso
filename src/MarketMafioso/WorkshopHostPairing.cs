using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MarketMafioso;

/// <summary>
/// Connection codes issued by a Workshop Host dashboard: <c>mmf1_&lt;base64url(hostBaseUrl)&gt;.&lt;token&gt;</c>.
/// Redeeming one returns a MarketMafioso client key for that host.
/// </summary>
public static class WorkshopHostPairing
{
    public const string CodePrefix = "mmf1_";
    private const string RedeemPath = "/api/settings/client-pairing/redeem";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private const int MaxResponseBytes = 64 * 1024;

    public readonly record struct ParsedCode(Uri HostBaseUrl, string Token);

    public sealed record RedeemResult(bool Succeeded, string? ApiKey, string Message, string? ErrorCode);

    public static bool TryParse(string? code, out ParsedCode parsed, out string error)
    {
        parsed = default;
        var trimmed = (code ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            error = "Paste a connection code first.";
            return false;
        }

        if (!trimmed.StartsWith(CodePrefix, StringComparison.Ordinal))
        {
            error = WorkshopHostApiKeyRouting.IsMarketMafiosoClientKey(trimmed) || WorkshopHostApiKeyRouting.IsCraftArchitectKey(trimmed)
                ? "That is a client key, not a connection code. Use the manual setup below for keys."
                : "That is not a MarketMafioso connection code. It should start with \"mmf1_\".";
            return false;
        }

        var body = trimmed[CodePrefix.Length..];
        var separator = body.IndexOf('.');
        if (separator <= 0 || separator == body.Length - 1 || body.IndexOf('.', separator + 1) >= 0)
        {
            error = "The connection code is incomplete. Copy the whole code from the dashboard.";
            return false;
        }

        var token = body[(separator + 1)..];
        if (token.Length is < 16 or > 128 || !IsBase64Url(token))
        {
            error = "The connection code is damaged. Copy it again from the dashboard.";
            return false;
        }

        if (!TryDecodeBase64Url(body[..separator], out var hostText) ||
            !Uri.TryCreate(hostText, UriKind.Absolute, out var host) ||
            (host.Scheme != Uri.UriSchemeHttp && host.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(host.Query) ||
            !string.IsNullOrEmpty(host.Fragment))
        {
            error = "The connection code does not contain a valid server address. Copy it again from the dashboard.";
            return false;
        }

        parsed = new ParsedCode(host, token);
        error = string.Empty;
        return true;
    }

    /// <summary>The receiver URL form the plugin stores, matching the hosted presets.</summary>
    public static string BuildServerUrl(Uri hostBaseUrl) =>
        $"{hostBaseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/')}/api/inventory";

    public static string DescribeHost(Uri hostBaseUrl) =>
        $"{hostBaseUrl.Authority}{hostBaseUrl.AbsolutePath.TrimEnd('/')}";

    public static async Task<RedeemResult> RedeemAsync(ParsedCode code, string? clientName, CancellationToken cancellationToken)
    {
        var url = $"{code.HostBaseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/')}{RedeemPath}";
        var host = DescribeHost(code.HostBaseUrl);
        try
        {
            using var http = new HttpClient { Timeout = RequestTimeout, MaxResponseContentBufferSize = MaxResponseBytes };
            using var response = await http.PostAsJsonAsync(
                url,
                new { token = code.Token, clientName },
                cancellationToken).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            JsonElement? body = null;
            if (bytes.Length > 0)
            {
                try
                {
                    using var document = JsonDocument.Parse(bytes);
                    if (document.RootElement.ValueKind == JsonValueKind.Object)
                        body = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                }
            }

            var status = $"HTTP {(int)response.StatusCode}";
            if (response.IsSuccessStatusCode)
            {
                var apiKey = ReadString(body, "apiKey");
                return string.IsNullOrWhiteSpace(apiKey)
                    ? new RedeemResult(false, null, $"{host} accepted the code but returned no client key ({status}).", "missing_key")
                    : new RedeemResult(true, apiKey, $"Connected to {host}.", null);
            }

            var errorCode = ReadString(body, "error");
            var message = ReadString(body, "message");
            if (message is not null)
                return new RedeemResult(false, null, message, errorCode);
            if (errorCode is not null)
                return new RedeemResult(false, null, $"{host} refused the code: {errorCode} ({status}).", errorCode);
            return (int)response.StatusCode is 404 or 405
                ? new RedeemResult(false, null, $"{host} does not accept connection codes ({status}). Use the manual setup below.", "unsupported")
                : new RedeemResult(false, null, $"{host} could not redeem the code ({status}).", "http_error");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RedeemResult(false, null, $"Could not reach {host}: the request timed out.", "unreachable");
        }
        catch (HttpRequestException ex)
        {
            return new RedeemResult(false, null, $"Could not reach {host}: {ex.Message}", "unreachable");
        }
    }

    private static string? ReadString(JsonElement? body, string property) =>
        body is { } element &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static bool IsBase64Url(string value)
    {
        foreach (var character in value)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
                return false;
        }

        return true;
    }

    private static bool TryDecodeBase64Url(string value, out string decoded)
    {
        decoded = string.Empty;
        if (value.Length == 0 || !IsBase64Url(value))
            return false;

        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
