using System.Text.Json;

namespace MarketMafioso.Server.MarketDiagnostics;

public sealed class MarketDiagnosticCollector(
    MarketDiagnosticStore store,
    UniversalisMarketDiagnosticClient universalis,
    DiagnosticEventStore diagnosticEvents,
    MarketDiagnosticAlertSink alertSink,
    IConfiguration configuration,
    ILogger<MarketDiagnosticCollector> log)
{
    public async Task<int> CollectOnceAsync(CancellationToken cancellationToken)
    {
        var ownedListings = await store.SynchronizeOwnedListingsAsync(cancellationToken);
        if (ownedListings.Count == 0)
            return 0;

        var maximumEvidenceAge = TimeSpan.FromMinutes(Math.Clamp(
            configuration.GetValue("MarketMafioso:MarketDiagnostics:MaximumEvidenceAgeMinutes", 15),
            1,
            1440));
        var listingLimit = Math.Clamp(
            configuration.GetValue("MarketMafioso:MarketDiagnostics:ListingLimit", 100),
            1,
            100);
        var observationCount = 0;

        foreach (var group in ownedListings.GroupBy(listing => new { listing.AccountId, listing.World }))
        {
            var ownedRetainerNames = group
                .Select(listing => listing.RetainerName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ownedRetainerIds = group
                .Select(listing => listing.RetainerId.ToString())
                .ToHashSet(StringComparer.Ordinal);

            foreach (var batch in group.Select(listing => listing.ItemId).Distinct().Chunk(100))
            {
                IReadOnlyDictionary<uint, UniversalisItemEvidence> evidence;
                try
                {
                    evidence = await universalis.FetchWorldAsync(
                        group.Key.World,
                        batch,
                        listingLimit,
                        cancellationToken);
                }
                catch (Exception exception) when (
                    exception is HttpRequestException or
                    JsonException or
                    InvalidOperationException)
                {
                    log.LogWarning(
                        exception,
                        "Universalis market diagnostic request failed for {World}.",
                        group.Key.World);
                    await diagnosticEvents.WriteAsync(
                        new DiagnosticEventCreate
                        {
                            Source = "WorkshopHost",
                            Category = "MarketDiagnostics",
                            Type = "UniversalisFetchFailed",
                            Severity = "Warning",
                            Outcome = "Unknown",
                            Message = $"Universalis market diagnostic request failed for {group.Key.World}.",
                            AccountId = group.Key.AccountId,
                            World = group.Key.World,
                            ExceptionType = exception.GetType().Name,
                            ExceptionMessage = exception.Message,
                        },
                        cancellationToken);
                    evidence = new Dictionary<uint, UniversalisItemEvidence>();
                }

                var observedAt = DateTimeOffset.UtcNow;
                var batchItems = batch.ToHashSet();
                foreach (var listing in group.Where(listing => batchItems.Contains(listing.ItemId)))
                {
                    evidence.TryGetValue(listing.ItemId, out var itemEvidence);
                    var evaluation = MarketUndercutClassifier.Evaluate(
                        listing,
                        itemEvidence,
                        ownedRetainerNames,
                        ownedRetainerIds,
                        observedAt,
                        maximumEvidenceAge);
                    var transition = await store.RecordObservationAsync(evaluation, cancellationToken);
                    observationCount++;
                    if (transition == null)
                        continue;

                    await WriteTransitionAsync(transition, cancellationToken);
                    await alertSink.SendAsync(transition, cancellationToken);
                }
            }
        }

        return observationCount;
    }

    private async Task WriteTransitionAsync(
        MarketDiagnosticTransition transition,
        CancellationToken cancellationToken)
    {
        var severity = transition.Type == "UndercutStarted" ? "Warning" : "Info";
        var competitor = string.IsNullOrWhiteSpace(transition.CompetitorRetainerName)
            ? string.Empty
            : $" {transition.CompetitorRetainerName} is at {transition.CompetitorUnitPrice:N0} gil.";
        var message =
            $"{transition.Type}: {transition.ItemName ?? $"item {transition.ItemId}"} on {transition.World}; " +
            $"owned price {transition.OwnUnitPrice:N0} gil.{competitor}";
        await diagnosticEvents.WriteAsync(
            new DiagnosticEventCreate
            {
                Source = "WorkshopHost",
                Category = "MarketDiagnostics",
                Type = transition.Type,
                Severity = severity,
                Outcome = transition.Type == "UndercutCleared" ? "Clear" : "Undercut",
                Message = message,
                CorrelationId = $"market-listing:{transition.OwnedListingVersionId}",
                AccountId = transition.AccountId,
                ItemId = transition.ItemId,
                ItemName = transition.ItemName,
                World = transition.World,
                CharacterName = transition.CharacterName,
                PayloadSummaryJson = JsonSerializer.Serialize(new
                {
                    transition.RetainerName,
                    transition.OwnUnitPrice,
                    transition.CompetitorRetainerName,
                    transition.CompetitorUnitPrice,
                    transition.UndercutDelta,
                    transition.ResponseUpperBoundMs,
                }),
            },
            cancellationToken);
    }
}
