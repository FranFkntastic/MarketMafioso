namespace MarketMafioso.Server.MarketDiagnostics;

using System.Security.Cryptography;
using System.Text;

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
        var jitter = CalculateJitter(
            $"{AppContext.BaseDirectory}|{configuration["MarketMafioso:DatabasePath"]}",
            interval);
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
                TimeSpan.FromTicks((interval + jitter).Ticks * backoffMultiplier),
                stoppingToken);
        }
    }

    internal static TimeSpan CalculateJitter(string seed, TimeSpan interval)
    {
        var maximumMilliseconds = Math.Max(
            1,
            Math.Min(10_000, (int)Math.Floor(interval.TotalMilliseconds / 10)));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var value = BitConverter.ToUInt32(hash, 0);
        return TimeSpan.FromMilliseconds(value % (uint)(maximumMilliseconds + 1));
    }
}
