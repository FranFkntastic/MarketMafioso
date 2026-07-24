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
        var saleHistoryCache = new Dictionary<(string World, uint ItemId), IReadOnlyList<UniversalisSaleEvidence>>();

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
                    if (transition != null)
                    {
                        await WriteTransitionAsync(transition, cancellationToken);
                        await alertSink.SendAsync(transition, cancellationToken);
                    }

                    if (evaluation.OwnListingVisible == false)
                    {
                        await TryCorrelatePublicSaleAsync(
                            listing,
                            observedAt,
                            saleHistoryCache,
                            cancellationToken);
                    }
                }
            }
        }

        await CollectRegionConditionsAsync(ownedListings, cancellationToken);
        await store.PruneObservationHistoryAsync(
            DateTimeOffset.UtcNow,
            TimeSpan.FromDays(Math.Clamp(
                configuration.GetValue("MarketMafioso:MarketDiagnostics:WorldObservationRetentionDays", 14),
                1,
                3650)),
            TimeSpan.FromDays(Math.Clamp(
                configuration.GetValue("MarketMafioso:MarketDiagnostics:RegionObservationRetentionDays", 90),
                1,
                3650)),
            cancellationToken);
        return observationCount;
    }

    private async Task TryCorrelatePublicSaleAsync(
        OwnedMarketListing listing,
        DateTimeOffset observedAtUtc,
        IDictionary<(string World, uint ItemId), IReadOnlyList<UniversalisSaleEvidence>> cache,
        CancellationToken cancellationToken)
    {
        var retryInterval = TimeSpan.FromMinutes(Math.Clamp(
            configuration.GetValue("MarketMafioso:MarketDiagnostics:SaleHistoryRetryMinutes", 5),
            1,
            1440));
        var probe = await store.GetDueSaleHistoryProbeAsync(
            listing.Id,
            observedAtUtc,
            retryInterval,
            cancellationToken);
        if (probe == null)
            return;

        var key = (probe.Listing.World.ToUpperInvariant(), probe.Listing.ItemId);
        if (!cache.TryGetValue(key, out var history))
        {
            try
            {
                history = await universalis.FetchSaleHistoryAsync(
                    probe.Listing.World,
                    probe.Listing.ItemId,
                    cancellationToken);
                cache[key] = history;
            }
            catch (Exception exception) when (
                exception is HttpRequestException or
                JsonException or
                InvalidOperationException)
            {
                log.LogWarning(
                    exception,
                    "Universalis sale-history request failed for {ItemId} on {World}.",
                    probe.Listing.ItemId,
                    probe.Listing.World);
                return;
            }
        }

        var candidates = history
            .Where(sale =>
                sale.ItemId == probe.Listing.ItemId &&
                sale.IsHq == probe.Listing.IsHq &&
                sale.UnitPrice == probe.Listing.UnitPrice &&
                sale.Quantity == probe.Listing.Quantity &&
                sale.SoldAtUtc >= probe.EarliestAtUtc &&
                sale.SoldAtUtc <= probe.LatestAtUtc)
            .ToArray();
        var saleEvent = await store.RecordPublicSaleHistoryAsync(
            probe,
            candidates,
            observedAtUtc,
            cancellationToken);
        if (saleEvent == null)
            return;

        var type = saleEvent.Confidence == "Probable"
            ? "SaleProbablyMatched"
            : "SaleHistoryAmbiguous";
        await diagnosticEvents.WriteAsync(
            new DiagnosticEventCreate
            {
                Source = "WorkshopHost",
                Category = "MarketDiagnostics",
                Type = type,
                Severity = saleEvent.Confidence == "Probable" ? "Info" : "Warning",
                Outcome = saleEvent.Confidence,
                Message =
                    $"{type}: {saleEvent.ItemName ?? $"item {saleEvent.ItemId}"} on {saleEvent.World}; " +
                    $"{saleEvent.CandidateCount ?? 0} matching public sale record(s).",
                CorrelationId = $"market-listing:{saleEvent.OwnedListingVersionId}",
                AccountId = saleEvent.AccountId,
                ItemId = saleEvent.ItemId,
                ItemName = saleEvent.ItemName,
                World = saleEvent.World,
                PayloadSummaryJson = JsonSerializer.Serialize(new
                {
                    saleEvent.RetainerName,
                    saleEvent.UnitPrice,
                    saleEvent.Quantity,
                    saleEvent.EventAtUtc,
                    saleEvent.EarliestEventAtUtc,
                    saleEvent.LatestEventAtUtc,
                    saleEvent.CandidateCount,
                }),
            },
            cancellationToken);
    }

    private async Task CollectRegionConditionsAsync(
        IReadOnlyList<OwnedMarketListing> ownedListings,
        CancellationToken cancellationToken)
    {
        var region = configuration["MarketMafioso:MarketDiagnostics:Region"];
        if (string.IsNullOrWhiteSpace(region))
            region = "North-America";
        var interval = TimeSpan.FromMinutes(Math.Clamp(
            configuration.GetValue("MarketMafioso:MarketDiagnostics:RegionPollMinutes", 5),
            1,
            1440));

        foreach (var account in ownedListings.GroupBy(listing => listing.AccountId))
        {
            var observedAt = DateTimeOffset.UtcNow;
            if (!await store.ShouldCollectRegionAsync(
                    account.Key,
                    region,
                    observedAt,
                    interval,
                    cancellationToken))
            {
                continue;
            }

            var itemNames = account
                .GroupBy(listing => listing.ItemId)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(listing => listing.ItemName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)));
            var allConditions = new List<RegionMarketCondition>();
            var complete = true;
            foreach (var batch in itemNames.Keys.Chunk(100))
            {
                try
                {
                    var conditions = await universalis.FetchRegionConditionsAsync(
                        region,
                        batch,
                        cancellationToken);
                    allConditions.AddRange(conditions);
                }
                catch (Exception exception) when (
                    exception is HttpRequestException or
                    JsonException or
                    InvalidOperationException)
                {
                    complete = false;
                    log.LogWarning(
                        exception,
                        "Universalis regional market diagnostic request failed for {Region}.",
                        region);
                    await diagnosticEvents.WriteAsync(
                        new DiagnosticEventCreate
                        {
                            Source = "WorkshopHost",
                            Category = "MarketDiagnostics",
                            Type = "UniversalisRegionFetchFailed",
                            Severity = "Warning",
                            Outcome = "Unknown",
                            Message = $"Universalis regional market diagnostic request failed for {region}.",
                            AccountId = account.Key,
                            ExceptionType = exception.GetType().Name,
                            ExceptionMessage = exception.Message,
                        },
                        cancellationToken);
                }
            }

            if (complete)
            {
                await store.RecordRegionConditionsAsync(
                    account.Key,
                    region,
                    itemNames,
                    allConditions,
                    observedAt,
                    cancellationToken);
            }
        }
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
