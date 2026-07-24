namespace MarketMafioso.Server.MarketDiagnostics;

public sealed class MarketDiagnosticBackgroundService(
    MarketDiagnosticCollector collector,
    IConfiguration configuration,
    ILogger<MarketDiagnosticBackgroundService> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue("MarketMafioso:MarketDiagnostics:PollSeconds", 60),
            30,
            3600));
        var consecutiveFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var observations = await collector.CollectOnceAsync(stoppingToken);
                consecutiveFailures = 0;
                log.LogDebug(
                    "Market diagnostics recorded {ObservationCount} observation(s).",
                    observations);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                consecutiveFailures++;
                log.LogError(exception, "Market diagnostic collection failed.");
            }

            var backoffMultiplier = Math.Min(8, 1 << Math.Min(consecutiveFailures, 3));
            await Task.Delay(
                TimeSpan.FromTicks(interval.Ticks * backoffMultiplier),
                stoppingToken);
        }
    }
}
