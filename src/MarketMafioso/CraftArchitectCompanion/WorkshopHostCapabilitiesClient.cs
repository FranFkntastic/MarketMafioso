using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MarketMafioso.CraftArchitectCompanion;

public sealed class WorkshopHostCapabilitiesClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;

    public WorkshopHostCapabilitiesClient(HttpClient httpClient)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<bool> SupportsCraftAppraiseV1Async(
        string? serverUrl,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        var capabilitiesUrl = ReceiverEndpointClassifier.BuildWorkshopHostCapabilitiesUrl(serverUrl);
        if (string.IsNullOrWhiteSpace(capabilitiesUrl))
            return false;

        using var request = new HttpRequestMessage(HttpMethod.Get, capabilitiesUrl);
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Add("X-Api-Key", apiKey);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return false;

        var capabilities = await response.Content.ReadFromJsonAsync<WorkshopHostCapabilitiesResponse>(
            JsonOptions,
            cancellationToken).ConfigureAwait(false);

        return capabilities?.Capabilities.Any(capability =>
            capability.Id.Equals("craft.appraise", StringComparison.OrdinalIgnoreCase) &&
            capability.Status.Equals("available", StringComparison.OrdinalIgnoreCase) &&
            capability.SupportedSchemaVersions.Contains(1)) == true;
    }
}

public sealed record WorkshopHostCapabilitiesResponse
{
    public string Service { get; init; } = string.Empty;
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<WorkshopHostCapability> Capabilities { get; init; } = [];
}

public sealed record WorkshopHostCapability
{
    public string Id { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public IReadOnlyList<int> SupportedSchemaVersions { get; init; } = [];
    public IReadOnlyList<string> RequiredScopes { get; init; } = [];
}
