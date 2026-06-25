using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MarketMafioso.MarketAcquisition;

public sealed class MarketAcquisitionRequestClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;

    public MarketAcquisitionRequestClient(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    public async Task<IReadOnlyList<MarketAcquisitionRequestView>> FetchPendingAsync(
        string serverUrl,
        string clientApiKey,
        string characterName,
        string world,
        CancellationToken cancellationToken)
    {
        var acquisitionBaseUrl = ResolveAcquisitionBaseUrl(serverUrl);
        if (string.IsNullOrWhiteSpace(clientApiKey))
            throw new InvalidOperationException("Client API key is required.");

        var url =
            $"{acquisitionBaseUrl}/requests/pending" +
            $"?characterName={Uri.EscapeDataString(characterName)}" +
            $"&world={Uri.EscapeDataString(world)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Api-Key", clientApiKey);

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var pending = await response.Content.ReadFromJsonAsync<MarketAcquisitionPendingResponse>(
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        return pending?.Requests ?? [];
    }

    public async Task<MarketAcquisitionClaimView> ClaimAsync(
        string serverUrl,
        string clientApiKey,
        string requestId,
        string characterName,
        string world,
        string pluginInstanceId,
        CancellationToken cancellationToken)
    {
        var acquisitionBaseUrl = ResolveAcquisitionBaseUrl(serverUrl);
        if (string.IsNullOrWhiteSpace(clientApiKey))
            throw new InvalidOperationException("Client API key is required.");

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{acquisitionBaseUrl}/requests/{Uri.EscapeDataString(requestId)}/claim")
        {
            Content = JsonContent.Create(
                new MarketAcquisitionClaimRequest
                {
                    CharacterName = characterName,
                    World = world,
                    PluginInstanceId = pluginInstanceId,
                },
                options: JsonOptions),
        };
        request.Headers.Add("X-Api-Key", clientApiKey);

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MarketAcquisitionClaimView>(
            JsonOptions,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Claim response was empty.");
    }

    public Task<MarketAcquisitionRequestView> AcceptAsync(
        string serverUrl,
        string clientApiKey,
        string requestId,
        string claimToken,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        PostLifecycleAsync(
            serverUrl,
            clientApiKey,
            requestId,
            "accept",
            new MarketAcquisitionClaimTokenRequest
            {
                ClaimToken = claimToken,
                IdempotencyKey = idempotencyKey,
            },
            cancellationToken);

    public Task<MarketAcquisitionRequestView> RejectAsync(
        string serverUrl,
        string clientApiKey,
        string requestId,
        string claimToken,
        string idempotencyKey,
        string reason,
        CancellationToken cancellationToken) =>
        PostLifecycleAsync(
            serverUrl,
            clientApiKey,
            requestId,
            "reject",
            new MarketAcquisitionLifecycleRequest
            {
                ClaimToken = claimToken,
                IdempotencyKey = idempotencyKey,
                Reason = reason,
            },
            cancellationToken);

    public Task<MarketAcquisitionRequestView> ReportProgressAsync(
        string serverUrl,
        string clientApiKey,
        string requestId,
        string claimToken,
        string idempotencyKey,
        string runnerState,
        string? message,
        CancellationToken cancellationToken) =>
        PostLifecycleAsync(
            serverUrl,
            clientApiKey,
            requestId,
            "progress",
            new MarketAcquisitionLifecycleRequest
            {
                ClaimToken = claimToken,
                IdempotencyKey = idempotencyKey,
                RunnerState = runnerState,
                Message = message,
            },
            cancellationToken);

    public Task<MarketAcquisitionRequestView> CompleteAsync(
        string serverUrl,
        string clientApiKey,
        string requestId,
        string claimToken,
        string idempotencyKey,
        string? message,
        CancellationToken cancellationToken) =>
        PostLifecycleAsync(
            serverUrl,
            clientApiKey,
            requestId,
            "complete",
            new MarketAcquisitionLifecycleRequest
            {
                ClaimToken = claimToken,
                IdempotencyKey = idempotencyKey,
                Message = message,
            },
            cancellationToken);

    public Task<MarketAcquisitionRequestView> FailAsync(
        string serverUrl,
        string clientApiKey,
        string requestId,
        string claimToken,
        string idempotencyKey,
        string reason,
        CancellationToken cancellationToken) =>
        PostLifecycleAsync(
            serverUrl,
            clientApiKey,
            requestId,
            "fail",
            new MarketAcquisitionLifecycleRequest
            {
                ClaimToken = claimToken,
                IdempotencyKey = idempotencyKey,
                Reason = reason,
            },
            cancellationToken);

    private async Task<MarketAcquisitionRequestView> PostLifecycleAsync<TRequest>(
        string serverUrl,
        string clientApiKey,
        string requestId,
        string action,
        TRequest body,
        CancellationToken cancellationToken)
    {
        var acquisitionBaseUrl = ResolveAcquisitionBaseUrl(serverUrl);
        if (string.IsNullOrWhiteSpace(clientApiKey))
            throw new InvalidOperationException("Client API key is required.");

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{acquisitionBaseUrl}/requests/{Uri.EscapeDataString(requestId)}/{action}")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        request.Headers.Add("X-Api-Key", clientApiKey);

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MarketAcquisitionRequestView>(
            JsonOptions,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Lifecycle response was empty.");
    }

    private static string ResolveAcquisitionBaseUrl(string serverUrl) =>
        ReceiverEndpointClassifier.BuildAcquisitionBaseUrl(serverUrl) ??
        throw new InvalidOperationException("The configured receiver URL cannot derive an acquisition endpoint.");
}

