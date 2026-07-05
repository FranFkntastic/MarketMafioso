using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MarketMafioso.CraftArchitectCompanion;

public sealed class WorkshopHostCraftQuoteProvider : ICraftQuoteProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient httpClient;
    private readonly Func<bool> readEnabled;
    private readonly Func<bool> readCapabilityAvailable;
    private readonly Func<string?> readServerUrl;
    private readonly Func<string?> readApiKey;

    public WorkshopHostCraftQuoteProvider(
        HttpClient httpClient,
        Func<bool> readEnabled,
        Func<bool> readCapabilityAvailable,
        Func<string?> readServerUrl,
        Func<string?> readApiKey)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.readEnabled = readEnabled ?? throw new ArgumentNullException(nameof(readEnabled));
        this.readCapabilityAvailable = readCapabilityAvailable ?? throw new ArgumentNullException(nameof(readCapabilityAvailable));
        this.readServerUrl = readServerUrl ?? throw new ArgumentNullException(nameof(readServerUrl));
        this.readApiKey = readApiKey ?? throw new ArgumentNullException(nameof(readApiKey));
    }

    public string ProviderId => "WorkshopHost";

    public bool IsConfigured =>
        readEnabled() &&
        readCapabilityAvailable() &&
        !string.IsNullOrWhiteSpace(BuildAppraiseUrl()) &&
        !string.IsNullOrWhiteSpace(readApiKey());

    public async Task<CraftAppraisalQuote?> GetQuoteAsync(
        MarketAppraisalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var appraiseUrl = BuildAppraiseUrl();
        var apiKey = readApiKey();
        if (!readEnabled() ||
            !readCapabilityAvailable() ||
            string.IsNullOrWhiteSpace(appraiseUrl) ||
            string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, appraiseUrl)
        {
            Content = JsonContent.Create(CreateRequest(request), options: JsonOptions),
        };
        httpRequest.Headers.Add("X-Api-Key", apiKey);
        httpRequest.Headers.Accept.ParseAdd("application/json");

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await CreateHttpExceptionAsync(response, appraiseUrl, cancellationToken).ConfigureAwait(false);

        var quote = await response.Content.ReadFromJsonAsync<CraftAppraisalQuote>(
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        if (quote is null)
            throw new InvalidOperationException("Workshop Host craft quote response was empty.");

        ValidateQuote(request, quote);
        return quote;
    }

    private string? BuildAppraiseUrl() =>
        ReceiverEndpointClassifier.BuildWorkshopHostCraftAppraiseUrl(readServerUrl());

    private static WorkshopHostCraftAppraisalRequest CreateRequest(MarketAppraisalRequest request) => new()
    {
        ItemId = request.ItemId,
        ItemName = request.ItemName,
        Quantity = request.Quantity,
        Scope = new WorkshopHostCraftAppraisalScope
        {
            Region = request.Region,
        },
        Options = new WorkshopHostCraftAppraisalOptions
        {
            HqPolicy = request.HqPolicy,
            PricingMode = "CurrentMarketEvidence",
        },
    };

    private static void ValidateQuote(MarketAppraisalRequest request, CraftAppraisalQuote quote)
    {
        if (quote.SchemaVersion != 1)
            throw new InvalidOperationException($"Unsupported Workshop Host craft quote schema version {quote.SchemaVersion}.");

        if (quote.ItemId != request.ItemId)
        {
            throw new InvalidOperationException(
                $"Workshop Host craft quote item {quote.ItemId} does not match selected item {request.ItemId}.");
        }

        if (quote.RequestedQuantity != request.Quantity)
        {
            throw new InvalidOperationException(
                $"Workshop Host craft quote quantity {quote.RequestedQuantity} does not match requested quantity {request.Quantity}.");
        }
    }

    private static async Task<WorkshopHostCraftQuoteHttpException> CreateHttpExceptionAsync(
        HttpResponseMessage response,
        string appraiseUrl,
        CancellationToken cancellationToken)
    {
        var body = response.Content == null
            ? null
            : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var error = TryReadErrorMessage(body) ?? body;
        return new WorkshopHostCraftQuoteHttpException(response.StatusCode, appraiseUrl, error, body);
    }

    private static string? TryReadErrorMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error) &&
                   error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record WorkshopHostCraftAppraisalRequest
    {
        public int SchemaVersion { get; init; } = 1;
        public uint ItemId { get; init; }
        public string ItemName { get; init; } = string.Empty;
        public uint Quantity { get; init; }
        public WorkshopHostCraftAppraisalScope Scope { get; init; } = new();
        public WorkshopHostCraftAppraisalOptions Options { get; init; } = new();
    }

    private sealed record WorkshopHostCraftAppraisalScope
    {
        public string Region { get; init; } = "North America";
        public string? DataCenter { get; init; }
        public string? World { get; init; }
    }

    private sealed record WorkshopHostCraftAppraisalOptions
    {
        public string HqPolicy { get; init; } = "Either";
        public string PricingMode { get; init; } = "CurrentMarketEvidence";
    }
}

public sealed class WorkshopHostCraftQuoteHttpException : HttpRequestException
{
    public WorkshopHostCraftQuoteHttpException(
        HttpStatusCode statusCode,
        string requestUri,
        string? error,
        string? responseBody)
        : base(BuildMessage(statusCode, error), null, statusCode)
    {
        RequestUri = requestUri;
        Error = error;
        ResponseBody = responseBody;
    }

    public string RequestUri { get; }

    public string? Error { get; }

    public string? ResponseBody { get; }

    private static string BuildMessage(HttpStatusCode statusCode, string? error) =>
        string.IsNullOrWhiteSpace(error)
            ? $"Workshop Host craft quote failed with {(int)statusCode} {statusCode}."
            : $"Workshop Host craft quote failed with {(int)statusCode} {statusCode}: {error}";
}
