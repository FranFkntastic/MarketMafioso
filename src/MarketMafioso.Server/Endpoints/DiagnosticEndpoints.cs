using System.Text.Json;

namespace MarketMafioso.Server.Endpoints;

internal static class DiagnosticEndpoints
{
    public static void MapDiagnosticEndpoints(this WebApplication app)
    {
        app.MapGet("/api/diagnostics/events", ListEvents);
        app.MapGet("/api/diagnostics/events/stream", StreamEventSnapshot);
        app.MapGet("/api/events/stream", StreamDashboardEvents);
    }

    private static async Task<IResult> ListEvents(
        DiagnosticEventStore diagnostics,
        int? limit,
        string? category,
        string? severity,
        string? correlationId,
        CancellationToken token)
    {
        var events = await diagnostics.ListRecentAsync(limit ?? 100, category, severity, correlationId, token);
        return Results.Ok(events);
    }

    private static async Task StreamEventSnapshot(
        HttpResponse response,
        DiagnosticEventStore diagnostics,
        CancellationToken token)
    {
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        var events = await diagnostics.ListRecentAsync(100, null, null, null, token);
        await WriteSseEventAsync(response, "snapshot", events, token);
    }

    private static async Task StreamDashboardEvents(
        HttpResponse response,
        MarketAcquisitionRequestStore acquisitionStore,
        DiagnosticEventStore diagnostics,
        CancellationToken token)
    {
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";

        while (!token.IsCancellationRequested)
        {
            var requests = await acquisitionStore.ListRecentAsync(100, includeTerminal: false, token);
            await WriteSseEventAsync(response, "acquisition", requests, token);

            var events = await diagnostics.ListRecentAsync(25, null, null, null, token);
            await WriteSseEventAsync(response, "diagnostics", events, token);

            await Task.Delay(TimeSpan.FromSeconds(3), token);
        }
    }

    private static async Task WriteSseEventAsync<T>(
        HttpResponse response,
        string eventName,
        T payload,
        CancellationToken token)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await response.WriteAsync($"event: {eventName}\n", token);
        await response.WriteAsync($"data: {json}\n\n", token);
        await response.Body.FlushAsync(token);
    }
}
