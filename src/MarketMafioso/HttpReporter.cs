using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using MarketMafioso.Automation.Items;
using MarketMafioso.RetainerRestock;

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
            chatGui.PrintError("[MarketMafioso] This hosted receiver requires a MarketMafioso Client Key. Open /mmf and set it under Server Connection.");
            return;
        }

        if (endpoint.RequiresApiKey && WorkshopHostApiKeyRouting.IsCraftArchitectKey(config.ApiKey))
        {
            LastStatus = "Wrong API key type";
            chatGui.PrintError("[MarketMafioso] A Craft Architect key cannot upload inventory. Move it to the Acquisition Key field and add a MarketMafioso Client Key.");
            return;
        }

        try
        {
            var ownerScope = new RetainerOwnerScope(
                playerState.CharacterName,
                playerState.HomeWorld.IsValid ? playerState.HomeWorld.Value.Name.ToString() : null);
            string? charName = null;
            string? homeWorld = null;

            if (config.IncludeCharacterInfo)
            {
                charName = ownerScope.CharacterName;
                homeWorld = ownerScope.HomeWorld;
            }

            var playerInventory = scanner.ScanPlayerInventory(config);
            var retainers = BuildRetainerReports(
                config,
                ownerScope,
                config.IncludeCharacterInfo,
                scanner.ResolveItemMetadata);

            var generatedAtUtc = DateTime.UtcNow.ToString("o");
            var report = new InventoryReport
            {
                Metadata = new InventoryReportMetadata
                {
                    SchemaVersion = 2,
                    SourcePlugin = "MarketMafioso",
                    PluginVersion = PluginBuildInfo.DisplayVersion,
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
                    chatGui.PrintError("[MarketMafioso] The hosted receiver rejected the MarketMafioso Client Key. Check the saved key for this endpoint.");
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

    public static List<RetainerReport> BuildRetainerReports(
        Configuration config,
        RetainerOwnerScope ownerScope,
        bool includeOwnerFields,
        Func<uint, AutomationItemMetadata>? resolveItemMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(ownerScope);

        return config.RetainerCache.Values
            .Where(r => ownerScope.Matches(r.OwnerCharacterName, r.OwnerHomeWorld))
            .Select(r => new RetainerReport
            {
                RetainerName = r.RetainerName,
                RetainerId = r.RetainerId,
                OwnerCharacterName = includeOwnerFields ? r.OwnerCharacterName : null,
                OwnerHomeWorld = includeOwnerFields ? r.OwnerHomeWorld : null,
                LastUpdated = r.LastUpdated.ToString("o"),
                Gil = r.Gil,
                Bags = r.Bags
                    .Select(b => new InventoryBag
                    {
                        BagName = b.BagName,
                        Location = b.Location,
                        Items = b.Items
                            .Select(i => MapCachedItem(i, resolveItemMetadata))
                            .ToList(),
                    })
                    .ToList(),
                MarketListings = r.MarketListings
                    .Select(i => MapCachedListing(i, resolveItemMetadata))
                    .ToList(),
            })
            .ToList();
    }

    private static ItemSlot MapCachedItem(
        CachedItem item,
        Func<uint, AutomationItemMetadata>? resolveItemMetadata)
    {
        var metadata = string.IsNullOrWhiteSpace(item.ItemType)
            ? resolveItemMetadata?.Invoke(item.ItemId)
            : null;
        return new ItemSlot
        {
            ItemId = item.ItemId,
            ItemName = item.ItemName,
            ItemType = string.IsNullOrWhiteSpace(item.ItemType) ? metadata?.ItemType : item.ItemType,
            Quantity = item.Quantity,
            IsHQ = item.IsHQ,
            Condition = item.Condition,
            ContainerKey = item.ContainerKey,
            SlotIndex = item.SlotIndex,
            ConditionPercent = item.ConditionPercent,
            Equipped = item.Equipped,
        };
    }

    private static RetainerMarketListing MapCachedListing(
        CachedMarketListing item,
        Func<uint, AutomationItemMetadata>? resolveItemMetadata)
    {
        var metadata = string.IsNullOrWhiteSpace(item.ItemType)
            ? resolveItemMetadata?.Invoke(item.ItemId)
            : null;
        return new RetainerMarketListing
        {
            ItemId = item.ItemId,
            ItemName = item.ItemName,
            ItemType = string.IsNullOrWhiteSpace(item.ItemType) ? metadata?.ItemType : item.ItemType,
            Quantity = item.Quantity,
            IsHQ = item.IsHQ,
            Condition = item.Condition,
            ContainerKey = item.ContainerKey,
            SlotIndex = item.SlotIndex,
            ConditionPercent = item.ConditionPercent,
            UnitPrice = item.UnitPrice,
            ListedAt = item.ListedAt,
        };
    }

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
