using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MarketMafioso.Dashboard.Models;

namespace MarketMafioso.Dashboard.Services;

public sealed class DashboardApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient http;

    public DashboardApiClient(HttpClient http)
    {
        this.http = http;
    }

    public async Task<DashboardSessionResponse?> GetSessionAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("auth/session", cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DashboardSessionResponse>(JsonOptions, cancellationToken);
    }

    public async Task LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("auth/login", new { username, password }, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync("auth/logout", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<ReceiverHealthView> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        return await http.GetFromJsonAsync<ReceiverHealthView>(
            "health",
            JsonOptions,
            cancellationToken) ?? new ReceiverHealthView();
    }

    public async Task<ReceiverStorageSummaryView> GetStorageSummaryAsync(CancellationToken cancellationToken = default)
    {
        return await http.GetFromJsonAsync<ReceiverStorageSummaryView>(
            "api/settings/storage",
            JsonOptions,
            cancellationToken) ?? new ReceiverStorageSummaryView();
    }

    public async Task<DashboardFeatureFlagsView> GetFeatureFlagsAsync(CancellationToken cancellationToken = default)
    {
        return await http.GetFromJsonAsync<DashboardFeatureFlagsView>(
            "api/settings/features",
            JsonOptions,
            cancellationToken) ?? new DashboardFeatureFlagsView();
    }

    public async Task<IReadOnlyList<MarketAcquisitionRequestView>> GetAcquisitionRequestsAsync(
        bool includeTerminal = false,
        CancellationToken cancellationToken = default)
    {
        var path = includeTerminal
            ? "api/acquisition/requests?includeTerminal=true"
            : "api/acquisition/requests";
        return await http.GetFromJsonAsync<IReadOnlyList<MarketAcquisitionRequestView>>(
            path,
            JsonOptions,
            cancellationToken) ?? [];
    }

    public async Task<MarketAcquisitionRequestTimelineView> GetAcquisitionRequestTimelineAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        return await http.GetFromJsonAsync<MarketAcquisitionRequestTimelineView>(
            $"api/acquisition/requests/{Uri.EscapeDataString(id)}/timeline",
            JsonOptions,
            cancellationToken) ?? new MarketAcquisitionRequestTimelineView();
    }

    public async Task<MarketAcquisitionRequestView> CreateAcquisitionRequestAsync(
        MarketAcquisitionCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/acquisition/requests", request, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MarketAcquisitionRequestView>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Acquisition create response was empty.");
    }

    public async Task<MarketAcquisitionRequestView> CreateAcquisitionBatchAsync(
        MarketAcquisitionBatchCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/acquisition/batches", request, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MarketAcquisitionRequestView>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Acquisition batch create response was empty.");
    }

    public async Task<MarketAcquisitionRequestView> AppendAcquisitionBatchLinesAsync(
        string id,
        MarketAcquisitionBatchAppendLinesRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(
            $"api/acquisition/batches/{Uri.EscapeDataString(id)}/lines",
            request,
            JsonOptions,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MarketAcquisitionRequestView>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Acquisition batch append response was empty.");
    }

    public async Task CancelAcquisitionRequestAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync($"api/acquisition/requests/{Uri.EscapeDataString(id)}/cancel", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task ResendAcquisitionRequestAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync($"api/acquisition/requests/{Uri.EscapeDataString(id)}/resend", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<XivItemSearchResult>> SearchItemsAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        using var response = await http.GetAsync(
            $"api/xivdata/items/search?q={Uri.EscapeDataString(query)}&limit=12",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        IEnumerable<JsonElement> items = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray()
            : root.TryGetProperty("items", out var itemArray) && itemArray.ValueKind == JsonValueKind.Array
                ? itemArray.EnumerateArray()
                : [];

        return items
            .Select(ReadItem)
            .Where(item => item.ItemId != 0 && !string.IsNullOrWhiteSpace(item.Name))
            .ToArray();
    }

    public async Task<IReadOnlyList<DashboardCharacterOption>> GetCharactersAsync(
        CancellationToken cancellationToken = default)
    {
        return await http.GetFromJsonAsync<IReadOnlyList<DashboardCharacterOption>>(
            "api/inventory/characters",
            JsonOptions,
            cancellationToken) ?? [];
    }

    public async Task<InventoryBrowserView> GetInventoryBrowserAsync(
        long? characterId,
        string? search,
        string? scope,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (characterId != null)
            query.Add($"characterId={characterId.Value}");
        if (!string.IsNullOrWhiteSpace(search))
            query.Add($"search={Uri.EscapeDataString(search)}");
        if (!string.IsNullOrWhiteSpace(scope))
            query.Add($"scope={Uri.EscapeDataString(scope)}");

        var path = query.Count == 0
            ? "api/inventory/browser"
            : $"api/inventory/browser?{string.Join("&", query)}";

        return await http.GetFromJsonAsync<InventoryBrowserView>(
            path,
            JsonOptions,
            cancellationToken) ?? new InventoryBrowserView();
    }

    public async Task<IReadOnlyList<ReportSummaryView>> GetInventorySnapshotsAsync(
        long? characterId = null,
        CancellationToken cancellationToken = default)
    {
        var path = characterId == null
            ? "api/inventory/snapshots"
            : $"api/inventory/snapshots?characterId={characterId.Value}";

        return await http.GetFromJsonAsync<IReadOnlyList<ReportSummaryView>>(
            path,
            JsonOptions,
            cancellationToken) ?? [];
    }

    public async Task<DashboardSettingsView> GetDashboardSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        return await http.GetFromJsonAsync<DashboardSettingsView>(
            "api/settings/dashboard",
            JsonOptions,
            cancellationToken) ?? new DashboardSettingsView();
    }

    public async Task<DashboardSettingsView> SaveDashboardSettingsAsync(
        DashboardSettingsUpdate settings,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync("api/settings/dashboard", settings, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DashboardSettingsView>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Dashboard settings response was empty.");
    }

    public async Task<IReadOnlyList<DiagnosticEventView>> GetDiagnosticsAsync(
        CancellationToken cancellationToken = default)
    {
        return await http.GetFromJsonAsync<IReadOnlyList<DiagnosticEventView>>(
            "api/diagnostics/events?limit=100",
            JsonOptions,
            cancellationToken) ?? [];
    }

    private static XivItemSearchResult ReadItem(JsonElement element)
    {
        var id = ReadUInt(element, "itemId") ?? ReadUInt(element, "id") ?? ReadUInt(element, "rowId") ?? 0;
        var name = ReadString(element, "name") ?? ReadString(element, "itemName") ?? string.Empty;
        var type = ReadString(element, "itemType") ??
                   ReadString(element, "categoryName") ??
                   ReadString(element, "uiCategoryName");
        return new XivItemSearchResult
        {
            ItemId = id,
            Name = name,
            Type = type,
        };
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static uint? ReadUInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetUInt32(out var number)
            ? number
            : null;
}
