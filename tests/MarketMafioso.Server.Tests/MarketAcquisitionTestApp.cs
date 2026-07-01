using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using MarketMafioso.Server;

namespace MarketMafioso.Server.Tests;

internal static class MarketAcquisitionTestApp
{
    public const string ClientApiKey = "client-secret";
    public const string CharacterName = "Wei Ning";
    public const string WorldName = "Gilgamesh";
    public const string PluginInstanceId = "plugin-test-instance";

    public static Task<WebApplicationFactory<Program>> CreateAsync(
        string? contentRoot = null,
        params KeyValuePair<string, string?>[] extraConfiguration)
    {
        contentRoot ??= Path.Combine(Path.GetTempPath(), "MarketMafioso.Server.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);

        var values = new Dictionary<string, string?>
        {
            ["MarketMafioso:RequireApiKey"] = "true",
            ["MarketMafioso:ClientApiKey"] = ClientApiKey,
            ["MarketMafioso:BasePath"] = "/marketmafioso",
        };
        foreach (var item in extraConfiguration)
            values[item.Key] = item.Value;

        var application = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(contentRoot);
                builder.ConfigureAppConfiguration(config =>
                {
                    config.AddInMemoryCollection(values);
                });
            });

        return Task.FromResult(application);
    }

    public static HttpClient CreateAuthenticatedClient(this WebApplicationFactory<Program> application) =>
        application.CreateClient();

    public static async Task<MarketAcquisitionTestBatch> CreateClaimedBatchAsync(
        this WebApplicationFactory<Program> application,
        HttpClient client,
        string idempotencyKey,
        int lineCount = 1,
        CancellationToken cancellationToken = default)
    {
        var created = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/marketmafioso/api/acquisition/batches",
            CreateBatchRequest(idempotencyKey, lineCount),
            cancellationToken).ConfigureAwait(false);
        created.EnsureSuccessStatusCode();
        var createdView = await created.Content.ReadFromJsonAsync<MarketAcquisitionRequestView>(cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("Batch creation did not return a request view.");

        var claim = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/requests/{createdView.Id}/claim",
            new MarketAcquisitionClaimRequest
            {
                CharacterName = CharacterName,
                World = WorldName,
                PluginInstanceId = PluginInstanceId,
            },
            cancellationToken).ConfigureAwait(false);
        claim.EnsureSuccessStatusCode();
        var claimedView = await claim.Content.ReadFromJsonAsync<MarketAcquisitionClaimView>(cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("Batch claim did not return a claim view.");

        return MarketAcquisitionTestBatch.FromClaim(claimedView);
    }

    public static async Task<MarketAcquisitionTestBatch> CreateAcceptedBatchAsync(
        this WebApplicationFactory<Program> application,
        HttpClient client,
        string idempotencyKey,
        int lineCount = 1,
        CancellationToken cancellationToken = default)
    {
        var claimed = await application.CreateClaimedBatchAsync(
            client,
            idempotencyKey,
            lineCount,
            cancellationToken).ConfigureAwait(false);

        var accept = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/requests/{claimed.Id}/accept",
            new MarketAcquisitionClaimTokenRequest
            {
                ClaimToken = claimed.ClaimToken,
                IdempotencyKey = $"{idempotencyKey}-accept",
            },
            cancellationToken).ConfigureAwait(false);
        accept.EnsureSuccessStatusCode();
        var acceptedView = await accept.Content.ReadFromJsonAsync<MarketAcquisitionRequestView>(cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("Batch accept did not return a request view.");

        return claimed with
        {
            Status = acceptedView.Status,
            Lines = acceptedView.Lines,
        };
    }

    public static async Task<MarketAcquisitionRequestView> GetBatchAsync(
        this WebApplicationFactory<Program> application,
        HttpClient client,
        string batchId,
        CancellationToken cancellationToken = default)
    {
        var response = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            $"/marketmafioso/api/acquisition/requests/{batchId}/timeline",
            body: null,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var timeline = await response.Content.ReadFromJsonAsync<MarketAcquisitionRequestTimelineView>(cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("Timeline did not return a request view.");

        return timeline.Request;
    }

    public static object CreateBatchRequest(string idempotencyKey, int lineCount = 1)
    {
        if (lineCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(lineCount), "Line count must be positive.");

        return new
        {
            schemaVersion = 1,
            idempotencyKey,
            targetCharacterName = CharacterName,
            targetWorld = WorldName,
            region = "North America",
            worldMode = "Recommended",
            expiresInSeconds = 90,
            lines = Enumerable.Range(0, lineCount)
                .Select(index => new
                {
                    itemId = (uint)(2 + index),
                    itemName = index == 0 ? "Fire Shard" : $"Test Item {index + 1}",
                    itemKind = "Crystal",
                    quantityMode = index % 2 == 0 ? "TargetQuantity" : "AllBelowThreshold",
                    targetQuantity = index % 2 == 0 ? 10u : 0u,
                    maxQuantity = index % 2 == 0 ? 10u : 999u,
                    hqPolicy = "Either",
                    maxUnitPrice = (uint)(99 + index),
                    gilCap = index % 2 == 0 ? 990u : 0u,
                })
                .ToArray(),
        };
    }

    public static Task<HttpResponseMessage> SendWithKeyAsync(
        HttpClient client,
        HttpMethod method,
        string requestUri,
        object? body = null,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Add("X-Api-Key", ClientApiKey);
        request.Headers.Accept.ParseAdd("application/json");
        if (body != null)
            request.Content = JsonContent.Create(body);

        return client.SendAsync(request, cancellationToken);
    }
}

internal sealed record MarketAcquisitionTestBatch
{
    public string Id { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string ClaimToken { get; init; } = string.Empty;
    public IReadOnlyList<MarketAcquisitionBatchLineView> Lines { get; init; } = [];

    public static MarketAcquisitionTestBatch FromClaim(MarketAcquisitionClaimView claim) =>
        new()
        {
            Id = claim.Id,
            Status = claim.Status,
            ClaimToken = claim.ClaimToken,
            Lines = claim.Lines,
        };
}
