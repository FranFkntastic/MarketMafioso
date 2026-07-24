using System.Net.Http.Json;

namespace MarketMafioso.Server.MarketDiagnostics;

public sealed class MarketDiagnosticAlertSink(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<MarketDiagnosticAlertSink> log)
{
    public async Task SendAsync(
        MarketDiagnosticTransition transition,
        CancellationToken cancellationToken)
    {
        var webhookUrl = configuration["MarketMafioso:MarketDiagnostics:DiscordWebhookUrl"];
        if (string.IsNullOrWhiteSpace(webhookUrl))
            return;

        var item = string.IsNullOrWhiteSpace(transition.ItemName)
            ? $"item {transition.ItemId}"
            : transition.ItemName;
        var response = transition.ResponseUpperBoundMs.HasValue
            ? $" Observed response ceiling: {TimeSpan.FromMilliseconds(transition.ResponseUpperBoundMs.Value):g}."
            : string.Empty;
        var competitor = string.IsNullOrWhiteSpace(transition.CompetitorRetainerName)
            ? string.Empty
            : $" Competitor: {transition.CompetitorRetainerName} at {transition.CompetitorUnitPrice:N0} gil.";
        var content =
            $"{transition.Type}: {item} on {transition.World}; " +
            $"{transition.RetainerName} is listed at {transition.OwnUnitPrice:N0} gil." +
            competitor +
            response;

        try
        {
            using var result = await httpClientFactory
                .CreateClient(nameof(MarketDiagnosticAlertSink))
                .PostAsJsonAsync(webhookUrl, new { content }, cancellationToken)
                .ConfigureAwait(false);
            if (!result.IsSuccessStatusCode)
            {
                log.LogWarning(
                    "Market diagnostic webhook returned {StatusCode}.",
                    (int)result.StatusCode);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning(exception, "Market diagnostic webhook failed.");
        }
    }
}
