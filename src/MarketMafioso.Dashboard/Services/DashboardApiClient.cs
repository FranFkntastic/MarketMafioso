using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MarketMafioso.Dashboard.Models;
using MarketMafioso.Contracts.Inventory;

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
        return await GetJsonAsync(
            "api/settings/storage",
            new ReceiverStorageSummaryView(),
            cancellationToken);
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
        return await GetJsonAsync(
            path,
            Array.Empty<MarketAcquisitionRequestView>(),
            cancellationToken);
    }

    public async Task<MarketAcquisitionRequestTimelineView> GetAcquisitionRequestTimelineAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        return await GetJsonAsync(
            $"api/acquisition/requests/{Uri.EscapeDataString(id)}/timeline",
            new MarketAcquisitionRequestTimelineView(),
            cancellationToken);
    }

    public async Task<MarketAcquisitionRequestView> CreateAcquisitionRequestAsync(
        MarketAcquisitionCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/acquisition/requests", request, JsonOptions, cancellationToken);
        EnsureAuthorizedSuccess(response);
        return await response.Content.ReadFromJsonAsync<MarketAcquisitionRequestView>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Acquisition create response was empty.");
    }

    public async Task<MarketAcquisitionRequestView> CreateAcquisitionBatchAsync(
        MarketAcquisitionBatchCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/acquisition/batches", request, JsonOptions, cancellationToken);
        EnsureAuthorizedSuccess(response);
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
        EnsureAuthorizedSuccess(response);
        return await response.Content.ReadFromJsonAsync<MarketAcquisitionRequestView>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Acquisition batch append response was empty.");
    }

    public async Task<MarketAcquisitionRequestView> ReplaceAcquisitionBatchAsync(
        string id,
        MarketAcquisitionBatchReplaceRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync(
            $"api/acquisition/batches/{Uri.EscapeDataString(id)}",
            request,
            JsonOptions,
            cancellationToken);
        EnsureAuthorizedSuccess(response);
        return await response.Content.ReadFromJsonAsync<MarketAcquisitionRequestView>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Acquisition batch replace response was empty.");
    }

    public async Task CancelAcquisitionRequestAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync($"api/acquisition/requests/{Uri.EscapeDataString(id)}/cancel", null, cancellationToken);
        EnsureAuthorizedSuccess(response);
    }

    public async Task ResendAcquisitionRequestAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync($"api/acquisition/requests/{Uri.EscapeDataString(id)}/resend", null, cancellationToken);
        EnsureAuthorizedSuccess(response);
    }

    public async Task ApplyAcquisitionWorkOrderCommandAsync(
        string id,
        string action,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(
            $"api/acquisition/work-orders/{Uri.EscapeDataString(id)}/{Uri.EscapeDataString(action)}",
            new { expectedRevision },
            JsonOptions,
            cancellationToken);
        EnsureAuthorizedSuccess(response);
    }

    public async Task CloneAcquisitionWorkOrderAsync(
        string id,
        int expectedRevision,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(
            $"api/acquisition/work-orders/{Uri.EscapeDataString(id)}/clone",
            new
            {
                expectedRevision,
                idempotencyKey = $"dashboard-clone-{id}-{Guid.NewGuid():N}",
            },
            JsonOptions,
            cancellationToken);
        EnsureAuthorizedSuccess(response);
    }

    public async Task<MarketAcquisitionWorkOrderMergePreview> PreviewAcquisitionWorkOrderMergeAsync(
        string targetId,
        string sourceId,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(
            $"api/acquisition/work-orders/{Uri.EscapeDataString(targetId)}/merge-preview/{Uri.EscapeDataString(sourceId)}",
            cancellationToken);
        EnsureAuthorizedSuccess(response);
        return await response.Content.ReadFromJsonAsync<MarketAcquisitionWorkOrderMergePreview>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Work-order merge preview was empty.");
    }

    public async Task MergeAcquisitionWorkOrdersAsync(
        MarketAcquisitionRequestView target,
        MarketAcquisitionRequestView source,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(
            $"api/acquisition/work-orders/{Uri.EscapeDataString(target.Id)}/merge",
            new
            {
                sourceWorkOrderId = source.Id,
                expectedTargetRevision = target.Revision,
                expectedSourceRevision = source.Revision,
            },
            JsonOptions,
            cancellationToken);
        EnsureAuthorizedSuccess(response);
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
        EnsureAuthorizedSuccess(response);

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
        return await GetJsonAsync(
            "api/inventory/characters",
            Array.Empty<DashboardCharacterOption>(),
            cancellationToken);
    }

    public async Task<InventoryBrowserView> GetInventoryBrowserAsync(
        long? characterId,
        string? filter,
        string? scope,
        InventoryBrowserMode mode = InventoryBrowserMode.Items,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (characterId != null)
            query.Add($"characterId={characterId.Value}");
        if (!string.IsNullOrWhiteSpace(filter))
            query.Add($"filter={Uri.EscapeDataString(filter)}");
        if (!string.IsNullOrWhiteSpace(scope))
            query.Add($"scope={Uri.EscapeDataString(scope)}");
        query.Add($"mode={mode}");

        var path = query.Count == 0
            ? "api/inventory/browser"
            : $"api/inventory/browser?{string.Join("&", query)}";

        return await GetJsonAsync(
            path,
            new InventoryBrowserView(),
            cancellationToken);
    }

    public async Task<IReadOnlyList<ReportSummaryView>> GetInventorySnapshotsAsync(
        long? characterId = null,
        CancellationToken cancellationToken = default)
    {
        var path = characterId == null
            ? "api/inventory/snapshots"
            : $"api/inventory/snapshots?characterId={characterId.Value}";

        return await GetJsonAsync(
            path,
            Array.Empty<ReportSummaryView>(),
            cancellationToken);
    }

    public async Task<DashboardSettingsView> GetDashboardSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        return await GetJsonAsync(
            "api/settings/dashboard",
            new DashboardSettingsView(),
            cancellationToken);
    }

    public async Task<DashboardSettingsView> SaveDashboardSettingsAsync(
        DashboardSettingsUpdate settings,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync("api/settings/dashboard", settings, JsonOptions, cancellationToken);
        EnsureAuthorizedSuccess(response);
        return await response.Content.ReadFromJsonAsync<DashboardSettingsView>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Dashboard settings response was empty.");
    }

    public async Task<IReadOnlyList<ClientCredentialView>> GetClientCredentialsAsync(
        CancellationToken cancellationToken = default)
    {
        return await GetJsonAsync(
            "api/settings/client-keys",
            Array.Empty<ClientCredentialView>(),
            cancellationToken);
    }

    public async Task<ClientCredentialCreatedView> CreateClientCredentialAsync(
        ClientCredentialCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(
            "api/settings/client-keys",
            request,
            JsonOptions,
            cancellationToken);
        EnsureAuthorizedSuccess(response);
        return await response.Content.ReadFromJsonAsync<ClientCredentialCreatedView>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Client key response was empty.");
    }

    public async Task RevokeClientCredentialAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync($"api/settings/client-keys/{id}", cancellationToken);
        EnsureAuthorizedSuccess(response);
    }

    public async Task<IReadOnlyList<DiagnosticEventView>> GetDiagnosticsAsync(
        CancellationToken cancellationToken = default)
    {
        return await GetJsonAsync(
            "api/diagnostics/events?limit=100",
            Array.Empty<DiagnosticEventView>(),
            cancellationToken);
    }

    private async Task<T> GetJsonAsync<T>(string path, T fallback, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(path, cancellationToken);
        EnsureAuthorizedSuccess(response);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken) ?? fallback;
    }

    private static void EnsureAuthorizedSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new DashboardUnauthorizedException();

        response.EnsureSuccessStatusCode();
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
