using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace MarketMafioso;

public class HttpReporter : IDisposable
{
    private readonly HttpClient httpClient = new();
    private readonly Configuration config;
    private readonly IPlayerState playerState;
    private readonly IPluginLog log;
    private readonly IChatGui chatGui;
    private readonly InventoryScanner scanner;

    private static readonly JsonSerializerOptions SerialiserOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public DateTime? LastSentAt { get; private set; }
    public string LastStatus { get; private set; } = "Never sent";
    public string? LastPayload { get; private set; }
    public string? LastDashboardUrl { get; private set; }
    public string? LastDashboardReportUrl { get; private set; }

    public HttpReporter(
        Configuration config,
        IPlayerState playerState,
        IPluginLog log,
        IChatGui chatGui,
        InventoryScanner scanner)
    {
        this.config = config;
        this.playerState = playerState;
        this.log = log;
        this.chatGui = chatGui;
        this.scanner = scanner;
    }

    public async Task SendReportAsync()
    {
        if (string.IsNullOrWhiteSpace(config.ServerUrl))
        {
            chatGui.PrintError("[MarketMafioso] No server URL configured. Use /mmf to set one.");
            return;
        }

        var endpoint = ReceiverEndpointClassifier.Classify(config.ServerUrl);
        if (endpoint.Kind == ReceiverEndpointKind.Invalid)
        {
            LastStatus = "Invalid server URL";
            chatGui.PrintError("[MarketMafioso] Server URL is not a valid HTTP or HTTPS endpoint.");
            return;
        }

        if (endpoint.RequiresApiKey && string.IsNullOrWhiteSpace(config.ApiKey))
        {
            LastStatus = "API key required";
            chatGui.PrintError("[MarketMafioso] This hosted receiver requires an API key. Open /mmf and set the API Key field.");
            return;
        }

        try
        {
            string? charName = null;
            string? homeWorld = null;

            if (config.IncludeCharacterInfo)
            {
                charName = playerState.CharacterName;
                homeWorld = playerState.HomeWorld.IsValid ? playerState.HomeWorld.Value.Name.ToString() : null;
            }

            var playerInventory = scanner.ScanPlayerInventory(config);

            var retainers = config.RetainerCache.Values
                .Select(r => new RetainerReport
                {
                    RetainerName = r.RetainerName,
                    RetainerId = r.RetainerId,
                    LastUpdated = r.LastUpdated.ToString("o"),
                    Bags = r.Bags
                        .Select(b => new InventoryBag
                        {
                            BagName = b.BagName,
                            Items = b.Items
                                .Select(i => new ItemSlot
                                {
                                    ItemId = i.ItemId,
                                    ItemName = i.ItemName,
                                    ItemType = i.ItemType,
                                    Quantity = i.Quantity,
                                    IsHQ = i.IsHQ,
                                    Condition = i.Condition,
                                })
                                .ToList(),
                        })
                        .ToList(),
                })
                .ToList();

            var generatedAtUtc = DateTime.UtcNow.ToString("o");
            var report = new InventoryReport
            {
                Metadata = new InventoryReportMetadata
                {
                    SchemaVersion = 1,
                    SourcePlugin = "MarketMafioso",
                    PluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "Unknown",
                    GeneratedAtUtc = generatedAtUtc,
                },
                CharacterName = charName,
                HomeWorld = homeWorld,
                Timestamp = generatedAtUtc,
                PlayerInventory = playerInventory,
                Retainers = retainers,
            };

            LastPayload = JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, config.ServerUrl)
            {
                Content = JsonContent.Create(report, options: SerialiserOptions),
            };

            if (!string.IsNullOrWhiteSpace(config.ApiKey))
                request.Headers.Add("X-Api-Key", config.ApiKey);

            var response = await httpClient.SendAsync(request).ConfigureAwait(false);

            LastSentAt = DateTime.Now;
            LastStatus = $"{(int)response.StatusCode} {response.ReasonPhrase}";

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var reportResponse = ParseReportResponse(body);
                LastDashboardUrl = ResolveDashboardUrlForDisplay(reportResponse.DashboardUrl, config.ServerUrl);
                LastDashboardReportUrl = reportResponse.ResolveReportUrl(config.ServerUrl);
                var itemCount = playerInventory.Sum(b => b.Items.Count);
                var dashboardSuffix = string.IsNullOrWhiteSpace(LastDashboardReportUrl)
                    ? string.IsNullOrWhiteSpace(LastDashboardUrl)
                        ? string.Empty
                        : $" Dashboard: {LastDashboardUrl}"
                    : $" View: {LastDashboardReportUrl}";
                chatGui.Print(
                    $"[MarketMafioso] Sent {itemCount} player items + {retainers.Count} retainer(s). " +
                    $"Status: {LastStatus}.{dashboardSuffix}");
                log.Information($"[MarketMafioso] Report sent - {LastStatus}.{dashboardSuffix}");
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (endpoint.RequiresApiKey && response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    chatGui.PrintError("[MarketMafioso] The hosted receiver rejected the API key. Check the saved API Key for this endpoint.");
                    log.Warning($"[MarketMafioso] Hosted receiver rejected API key - {LastStatus}: {body}");
                    return;
                }

                chatGui.PrintError($"[MarketMafioso] Server error {LastStatus}: {body[..Math.Min(body.Length, 200)]}");
                log.Warning($"[MarketMafioso] Server returned {LastStatus}: {body}");
            }
        }
        catch (Exception ex)
        {
            LastStatus = $"Error: {ex.Message}";
            chatGui.PrintError($"[MarketMafioso] Failed to send: {ex.Message}");
            log.Error(ex, "[MarketMafioso] Error sending report");
        }
    }

    public void Dispose() => httpClient.Dispose();

    public static HttpReportResponse ParseReportResponse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return new HttpReportResponse(
                TryGetString(root, "id"),
                TryGetString(root, "dashboardUrl"),
                TryGetString(root, "reportUrl"));
        }
        catch (JsonException)
        {
            return new HttpReportResponse(null, null, null);
        }
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    public static string? ResolveDashboardUrlForDisplay(string? dashboardUrl, string? serverUrl) =>
        !string.IsNullOrWhiteSpace(dashboardUrl)
            ? dashboardUrl
            : ReceiverEndpointClassifier.BuildDashboardUrl(serverUrl);
}

public readonly record struct HttpReportResponse(
    string? ReportId,
    string? DashboardUrl,
    string? ReportUrl)
{
    public string? ResolveDashboardUrl(string? serverUrl) =>
        !string.IsNullOrWhiteSpace(DashboardUrl)
            ? DashboardUrl
            : ReceiverEndpointClassifier.BuildDashboardUrl(serverUrl);

    public string? ResolveReportUrl(string? serverUrl) =>
        !string.IsNullOrWhiteSpace(ReportUrl)
            ? ReportUrl
            : ReceiverEndpointClassifier.BuildDashboardReportUrl(serverUrl, ReportId);
}
