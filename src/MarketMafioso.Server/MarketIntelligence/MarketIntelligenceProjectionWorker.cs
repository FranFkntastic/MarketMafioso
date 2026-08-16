namespace MarketMafioso.Server.MarketIntelligence;

public sealed class MarketIntelligenceProjectionWorker(
    MarketIntelligenceStore store,
    ILogger<MarketIntelligenceProjectionWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await store.ProjectPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                log.LogError(exception, "Market intelligence projection failed; durable outbox entries remain pending.");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }
}
