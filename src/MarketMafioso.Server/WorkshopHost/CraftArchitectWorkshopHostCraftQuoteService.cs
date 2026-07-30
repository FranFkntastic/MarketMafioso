using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MarketMafioso.Server.WorkshopHost;

public sealed class CraftArchitectWorkshopHostCraftQuoteService : IWorkshopHostCraftQuoteService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;
    private readonly Uri? appraiseEndpoint;

    public CraftArchitectWorkshopHostCraftQuoteService(
        HttpClient httpClient,
        IConfiguration configuration)
    {
        this.httpClient = httpClient;
        appraiseEndpoint = ParseLoopbackEndpoint(
            configuration["MarketMafioso:CraftArchitectAppraiseUrl"]);
    }

    public bool IsAvailable => appraiseEndpoint != null;

    public async Task<CraftAppraisalQuote?> AppraiseAsync(
        CraftAppraisalRequest request,
        CancellationToken cancellationToken)
    {
        if (appraiseEndpoint == null)
            return null;

        using var response = await httpClient.PostAsJsonAsync(
            appraiseEndpoint,
            request,
            JsonOptions,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new CraftArchitectApiException(response.StatusCode, body);
        }

        var quote = await response.Content.ReadFromJsonAsync<CraftAppraisalQuote>(
            JsonOptions,
            cancellationToken);
        if (quote == null)
            throw new CraftArchitectApiException(response.StatusCode, "Craft Architect returned an empty quote.");
        if (quote.SchemaVersion != 1 ||
            quote.ItemId != request.ItemId ||
            quote.RequestedQuantity != request.Quantity)
        {
            throw new CraftArchitectApiException(
                response.StatusCode,
                "Craft Architect returned a quote that does not match the requested contract.");
        }

        return quote;
    }

    private static Uri? ParseLoopbackEndpoint(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttp ||
            !endpoint.IsLoopback)
        {
            return null;
        }

        return endpoint;
    }
}

public sealed class CraftArchitectApiException(
    HttpStatusCode statusCode,
    string responseBody)
    : HttpRequestException(
        $"Craft Architect appraisal API returned {(int)statusCode} {statusCode}.",
        inner: null,
        statusCode)
{
    public string ResponseBody { get; } = responseBody;
}
