using System.Globalization;
using System.Text.Json;
using MarketMafioso.Contracts;
using MarketMafioso.Server.Sqlite;
using Microsoft.Data.Sqlite;

namespace MarketMafioso.Server.MarketDiagnostics;

public sealed class MarketDiagnosticStore(SqliteConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<OwnedMarketListing>> SynchronizeOwnedListingsAsync(
        CancellationToken cancellationToken)
    {
        var scopes = await ReadLatestListingScopesAsync(cancellationToken);
        if (scopes.Count == 0)
            return [];

        var current = await ReadLatestOwnedListingsAsync(cancellationToken);
        var observedAt = DateTimeOffset.UtcNow;
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        foreach (var listing in current)
        {
            await using (var close = connection.CreateCommand())
            {
                close.Transaction = transaction;
                close.CommandText = """
                    UPDATE market_owned_listing_versions
                    SET is_active = 0,
                        closed_at_utc = $closedAt,
                        close_reason = 'Replaced'
                    WHERE account_id = $accountId
                      AND listing_key = $listingKey
                      AND version_key <> $versionKey
                      AND is_active = 1;
                    """;
                close.Parameters.AddWithValue("$closedAt", Format(observedAt));
                close.Parameters.AddWithValue("$accountId", listing.AccountId);
                close.Parameters.AddWithValue("$listingKey", listing.ListingKey);
                close.Parameters.AddWithValue("$versionKey", listing.VersionKey);
                await close.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO market_owned_listing_versions (
                    account_id,
                    version_key,
                    listing_key,
                    source_snapshot_id,
                    character_name,
                    world,
                    retainer_id,
                    retainer_name,
                    item_id,
                    item_name,
                    quantity,
                    is_hq,
                    unit_price,
                    listed_at_utc,
                    listings_observed_at_utc,
                    first_observed_at_utc,
                    last_observed_at_utc,
                    is_active
                )
                VALUES (
                    $accountId,
                    $versionKey,
                    $listingKey,
                    $snapshotId,
                    $characterName,
                    $world,
                    $retainerId,
                    $retainerName,
                    $itemId,
                    $itemName,
                    $quantity,
                    $isHq,
                    $unitPrice,
                    $listedAt,
                    $listingsObservedAt,
                    $firstObservedAt,
                    $lastObservedAt,
                    1
                )
                ON CONFLICT(account_id, version_key) DO UPDATE SET
                    source_snapshot_id = excluded.source_snapshot_id,
                    item_name = COALESCE(excluded.item_name, market_owned_listing_versions.item_name),
                    listings_observed_at_utc = excluded.listings_observed_at_utc,
                    last_observed_at_utc = excluded.last_observed_at_utc,
                    is_active = CASE
                        WHEN excluded.listings_observed_at_utc > market_owned_listing_versions.listings_observed_at_utc
                        THEN 1
                        ELSE market_owned_listing_versions.is_active
                    END,
                    closed_at_utc = CASE
                        WHEN excluded.listings_observed_at_utc > market_owned_listing_versions.listings_observed_at_utc
                        THEN NULL
                        ELSE market_owned_listing_versions.closed_at_utc
                    END,
                    close_reason = CASE
                        WHEN excluded.listings_observed_at_utc > market_owned_listing_versions.listings_observed_at_utc
                        THEN NULL
                        ELSE market_owned_listing_versions.close_reason
                    END;
                """;
            AddOwnedListingParameters(upsert, listing);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var scope in scopes)
        {
            var currentVersionKeys = current
                .Where(listing =>
                    listing.AccountId == scope.AccountId &&
                    string.Equals(listing.CharacterName, scope.CharacterName, StringComparison.Ordinal) &&
                    string.Equals(listing.World, scope.World, StringComparison.OrdinalIgnoreCase))
                .Select(listing => listing.VersionKey)
                .ToHashSet(StringComparer.Ordinal);
            await CloseMissingListingsAsync(
                connection,
                transaction,
                scope,
                currentVersionKeys,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return await ListActiveOwnedListingsAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OwnedMarketListing>> ListActiveOwnedListingsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                account_id,
                version_key,
                listing_key,
                source_snapshot_id,
                character_name,
                world,
                retainer_id,
                retainer_name,
                item_id,
                item_name,
                quantity,
                is_hq,
                unit_price,
                listed_at_utc,
                listings_observed_at_utc,
                first_observed_at_utc,
                last_observed_at_utc,
                last_publicly_seen_at_utc,
                publicly_missing_since_utc,
                sale_history_checked_at_utc
            FROM market_owned_listing_versions
            WHERE is_active = 1
            ORDER BY account_id, world, item_id, unit_price, id;
            """;

        var listings = new List<OwnedMarketListing>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            listings.Add(ReadOwnedListing(reader));

        return listings;
    }

    public async Task<IReadOnlyList<MarketDiagnosticListingRow>> ListWorkbenchListingsAsync(
        IReadOnlyList<long> accountIds,
        CancellationToken cancellationToken)
    {
        return await ListWorkbenchListingsAsync(
            accountIds,
            activeOnly: true,
            listingId: null,
            cancellationToken);
    }

    private async Task<IReadOnlyList<MarketDiagnosticListingRow>> ListWorkbenchListingsAsync(
        IReadOnlyList<long> accountIds,
        bool activeOnly,
        long? listingId,
        CancellationToken cancellationToken)
    {
        if (accountIds.Count == 0)
            return [];

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var accountParameters = accountIds.Select((_, index) => $"$account{index}").ToArray();
        var listingPredicate = listingId is not null
            ? "AND listing.id = $listingId"
            : activeOnly
                ? "AND listing.is_active = 1"
                : string.Empty;
        command.CommandText = $"""
            WITH latest_observation AS (
                SELECT
                    observation.*,
                    ROW_NUMBER() OVER (
                        PARTITION BY observation.owned_listing_version_id
                        ORDER BY observation.observed_at_utc DESC, observation.id DESC
                    ) AS rank
                FROM market_observations observation
            ),
            open_episode AS (
                SELECT
                    episode.*,
                    ROW_NUMBER() OVER (
                        PARTITION BY episode.owned_listing_version_id
                        ORDER BY episode.first_detected_at_utc DESC, episode.id DESC
                    ) AS rank
                FROM market_undercut_episodes episode
                WHERE episode.cleared_at_utc IS NULL
            )
            SELECT
                listing.id,
                listing.account_id,
                listing.character_name,
                listing.world,
                listing.retainer_id,
                listing.retainer_name,
                listing.item_id,
                listing.item_name,
                listing.quantity,
                listing.is_hq,
                listing.unit_price,
                listing.listed_at_utc,
                listing.first_observed_at_utc,
                listing.last_observed_at_utc,
                observation.classification,
                observation.observed_at_utc,
                observation.source_upload_at_utc,
                observation.source_age_seconds,
                observation.source_freshness,
                observation.own_listing_visible,
                observation.competitor_retainer_name,
                observation.competitor_unit_price,
                observation.undercut_delta,
                episode.id,
                episode.last_clear_observed_at_utc,
                episode.first_detected_at_utc,
                episode.last_seen_at_utc,
                episode.response_lower_bound_ms,
                episode.response_upper_bound_ms
            FROM market_owned_listing_versions listing
            LEFT JOIN latest_observation observation
              ON observation.owned_listing_version_id = listing.id
             AND observation.rank = 1
            LEFT JOIN open_episode episode
              ON episode.owned_listing_version_id = listing.id
             AND episode.rank = 1
            WHERE listing.account_id IN ({string.Join(", ", accountParameters)})
              {listingPredicate}
            ORDER BY
                CASE observation.classification
                    WHEN 'Undercut' THEN 0
                    WHEN 'UnknownStale' THEN 1
                    WHEN 'UnknownMissing' THEN 1
                    ELSE 2
                END,
                listing.world,
                listing.item_name,
                listing.id;
            """;
        for (var index = 0; index < accountIds.Count; index++)
            command.Parameters.AddWithValue(accountParameters[index], accountIds[index]);
        if (listingId is not null)
            command.Parameters.AddWithValue("$listingId", listingId.Value);

        var rows = new List<MarketDiagnosticListingRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new MarketDiagnosticListingRow
            {
                Id = reader.GetInt64(0),
                AccountId = reader.GetInt64(1),
                CharacterName = reader.IsDBNull(2) ? null : reader.GetString(2),
                World = reader.GetString(3),
                RetainerId = checked((ulong)reader.GetInt64(4)),
                RetainerName = reader.GetString(5),
                ItemId = checked((uint)reader.GetInt64(6)),
                ItemName = reader.IsDBNull(7) ? null : reader.GetString(7),
                Quantity = checked((uint)reader.GetInt64(8)),
                IsHq = reader.GetInt64(9) != 0,
                UnitPrice = checked((uint)reader.GetInt64(10)),
                ListedAtUtc = reader.IsDBNull(11) ? null : ParseRequired(reader.GetString(11)),
                FirstObservedAtUtc = ParseRequired(reader.GetString(12)),
                LastObservedAtUtc = ParseRequired(reader.GetString(13)),
                Classification = reader.IsDBNull(14) ? "UnknownMissing" : reader.GetString(14),
                MarketObservedAtUtc = reader.IsDBNull(15) ? null : ParseRequired(reader.GetString(15)),
                SourceUploadedAtUtc = reader.IsDBNull(16) ? null : ParseRequired(reader.GetString(16)),
                SourceAgeSeconds = reader.IsDBNull(17) ? null : reader.GetInt64(17),
                SourceFreshness = reader.IsDBNull(18) ? null : reader.GetString(18),
                OwnListingVisible = reader.IsDBNull(19) ? null : reader.GetInt64(19) != 0,
                CompetitorRetainerName = reader.IsDBNull(20) ? null : reader.GetString(20),
                CompetitorUnitPrice = reader.IsDBNull(21) ? null : checked((uint)reader.GetInt64(21)),
                UndercutDelta = reader.IsDBNull(22) ? null : checked((uint)reader.GetInt64(22)),
                EpisodeId = reader.IsDBNull(23) ? null : reader.GetInt64(23),
                LastClearObservedAtUtc = reader.IsDBNull(24) ? null : ParseRequired(reader.GetString(24)),
                FirstDetectedAtUtc = reader.IsDBNull(25) ? null : ParseRequired(reader.GetString(25)),
                EpisodeLastSeenAtUtc = reader.IsDBNull(26) ? null : ParseRequired(reader.GetString(26)),
                ResponseLowerBoundMs = reader.IsDBNull(27) ? null : reader.GetInt64(27),
                ResponseUpperBoundMs = reader.IsDBNull(28) ? null : reader.GetInt64(28),
            });
        }

        return rows;
    }

    public async Task<MarketDiagnosticListingDetailView?> GetListingDetailAsync(
        IReadOnlyList<long> accountIds,
        long listingId,
        CancellationToken cancellationToken)
    {
        var listing = (await ListWorkbenchListingsAsync(
                accountIds,
                activeOnly: false,
                listingId,
                cancellationToken))
            .SingleOrDefault(candidate => candidate.Id == listingId);
        if (listing == null)
            return null;

        var timeline = new List<MarketDiagnosticTimelineEventView>
        {
            new()
            {
                OccurredAtUtc = listing.FirstObservedAtUtc,
                Title = "Listing version first observed",
                Detail = $"{listing.UnitPrice:N0} gil · {listing.Quantity:N0} {(listing.IsHq ? "HQ" : "NQ")}.",
            },
        };
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using (var observations = connection.CreateCommand())
        {
            observations.CommandText = """
                SELECT
                    observed_at_utc,
                    classification,
                    source_freshness,
                    source_age_seconds,
                    competitor_retainer_name,
                    competitor_unit_price,
                    undercut_delta
                FROM market_observations
                WHERE owned_listing_version_id = $listingId
                ORDER BY observed_at_utc DESC, id DESC
                LIMIT 100;
                """;
            observations.Parameters.AddWithValue("$listingId", listingId);
            await using var reader = await observations.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var classification = reader.GetString(1);
                var competitorName = reader.IsDBNull(4) ? null : reader.GetString(4);
                var competitorPrice = reader.IsDBNull(5) ? null : checked((uint?)reader.GetInt64(5));
                long? age = reader.IsDBNull(3) ? null : reader.GetInt64(3);
                timeline.Add(new MarketDiagnosticTimelineEventView
                {
                    OccurredAtUtc = ParseRequired(reader.GetString(0)),
                    Tone = classification == "Undercut"
                        ? "Danger"
                        : classification.StartsWith("Unknown", StringComparison.Ordinal)
                            ? "Warning"
                            : "Success",
                    Title = classification switch
                    {
                        "Undercut" => "Undercut observed",
                        "Clear" => "Listing clear",
                        _ => "Market evidence unknown",
                    },
                    Detail = classification == "Undercut"
                        ? $"{competitorName ?? "Unknown retainer"} · {competitorPrice:N0} gil" +
                          (reader.IsDBNull(6) ? string.Empty : $" · {reader.GetInt64(6):N0} gil below")
                        : $"{reader.GetString(2)} evidence" +
                          (age is null ? string.Empty : $" · {age:N0}s old"),
                });
            }
        }

        await using (var sales = connection.CreateCommand())
        {
            sales.CommandText = """
                SELECT
                    COALESCE(event_at_utc, observed_at_utc),
                    confidence,
                    source,
                    total_gil,
                    earliest_event_at_utc,
                    latest_event_at_utc
                FROM retainer_sale_events
                WHERE owned_listing_version_id = $listingId
                ORDER BY COALESCE(event_at_utc, observed_at_utc) DESC;
                """;
            sales.Parameters.AddWithValue("$listingId", listingId);
            await using var reader = await sales.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var confidence = reader.GetString(1);
                var source = reader.GetString(2);
                var total = reader.IsDBNull(3) ? null : checked((ulong?)reader.GetInt64(3));
                timeline.Add(new MarketDiagnosticTimelineEventView
                {
                    OccurredAtUtc = ParseRequired(reader.GetString(0)),
                    Tone = confidence == "Confirmed"
                        ? "Success"
                        : confidence == "Unresolved"
                            ? "Warning"
                            : "Info",
                    Title = $"{confidence} sale evidence",
                    Detail = $"{FriendlySaleSource(source)}" +
                             (total is null ? string.Empty : $" · {total:N0} gil") +
                             ReadIntervalSuffix(reader, 4, 5),
                });
            }
        }

        MarketDiagnosticRegionContextView? region = null;
        await using (var regionCommand = connection.CreateCommand())
        {
            regionCommand.CommandText = """
                SELECT
                    region,
                    observed_at_utc,
                    min_listing_price,
                    min_listing_world_id,
                    average_sale_price,
                    daily_sale_velocity
                FROM market_region_observations
                WHERE account_id = $accountId
                  AND item_id = $itemId
                  AND is_hq = $isHq
                ORDER BY observed_at_utc DESC, id DESC
                LIMIT 1;
                """;
            regionCommand.Parameters.AddWithValue("$accountId", listing.AccountId);
            regionCommand.Parameters.AddWithValue("$itemId", listing.ItemId);
            regionCommand.Parameters.AddWithValue("$isHq", listing.IsHq ? 1 : 0);
            await using var reader = await regionCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                region = new MarketDiagnosticRegionContextView
                {
                    Region = reader.GetString(0),
                    ObservedAtUtc = ParseRequired(reader.GetString(1)),
                    MinimumListingPrice = reader.IsDBNull(2) ? null : checked((uint)reader.GetInt64(2)),
                    MinimumListingWorldId = reader.IsDBNull(3) ? null : checked((uint)reader.GetInt64(3)),
                    AverageSalePrice = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                    DailySaleVelocity = reader.IsDBNull(5) ? null : reader.GetDouble(5),
                };
            }
        }

        MarketDiagnosticCompetitorProfileView? competitor = null;
        if (!string.IsNullOrWhiteSpace(listing.CompetitorRetainerName))
        {
            await using var profile = connection.CreateCommand();
            profile.CommandText = """
                SELECT
                    COUNT(*),
                    SUM(exact_one_gil),
                    AVG(response_upper_bound_ms)
                FROM market_undercut_episodes
                WHERE account_id = $accountId
                  AND (
                        current_competitor_retainer_name = $retainerName COLLATE NOCASE OR
                        first_competitor_retainer_name = $retainerName COLLATE NOCASE
                      );
                """;
            profile.Parameters.AddWithValue("$accountId", listing.AccountId);
            profile.Parameters.AddWithValue("$retainerName", listing.CompetitorRetainerName);
            await using var reader = await profile.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken) && reader.GetInt64(0) > 0)
            {
                competitor = new MarketDiagnosticCompetitorProfileView
                {
                    RetainerName = listing.CompetitorRetainerName,
                    EpisodeCount = checked((int)reader.GetInt64(0)),
                    ExactOneGilCount = reader.IsDBNull(1) ? 0 : checked((int)reader.GetInt64(1)),
                    AverageResponseUpperBoundSeconds =
                        reader.IsDBNull(2) ? null : reader.GetDouble(2) / 1000d,
                };
            }
        }

        return new MarketDiagnosticListingDetailView
        {
            Listing = listing,
            Timeline = timeline
                .OrderByDescending(entry => entry.OccurredAtUtc)
                .ToArray(),
            Region = region,
            Competitor = competitor,
        };
    }

    public async Task<MarketDiagnosticTransition?> RecordObservationAsync(
        MarketListingEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        var listing = evaluation.OwnedListing;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await InsertObservationAsync(connection, transaction, evaluation, cancellationToken);
        await UpdatePublicVisibilityAsync(connection, transaction, evaluation, cancellationToken);

        MarketDiagnosticTransition? transition = null;
        if (evaluation.Classification == MarketObservationClassification.Undercut &&
            evaluation.Competitor is { } competitor &&
            evaluation.UndercutDelta is { } delta)
        {
            var openEpisode = await ReadOpenEpisodeAsync(connection, transaction, listing.Id, cancellationToken);
            if (openEpisode == null)
            {
                var lastClear = await ReadLastClearObservationAsync(
                    connection,
                    transaction,
                    listing.Id,
                    evaluation.ObservedAtUtc,
                    cancellationToken);
                var listingStarted = listing.FirstObservedAtUtc;
                var lowerBound = lastClear.HasValue
                    ? Math.Max(0, (long)(lastClear.Value - listingStarted).TotalMilliseconds)
                    : 0;
                var upperBound = Math.Max(lowerBound, (long)(evaluation.ObservedAtUtc - listingStarted).TotalMilliseconds);
                await InsertEpisodeAsync(
                    connection,
                    transaction,
                    evaluation,
                    lastClear,
                    lowerBound,
                    upperBound,
                    cancellationToken);
                transition = BuildTransition("UndercutStarted", evaluation, upperBound);
            }
            else
            {
                var competitorChanged =
                    !string.Equals(openEpisode.CurrentCompetitorListingId, competitor.ListingId, StringComparison.Ordinal) ||
                    !string.Equals(openEpisode.CurrentCompetitorRetainerId, competitor.RetainerId, StringComparison.Ordinal);
                await UpdateEpisodeAsync(connection, transaction, openEpisode.Id, evaluation, cancellationToken);
                if (competitorChanged)
                    transition = BuildTransition("UndercutterChanged", evaluation, openEpisode.ResponseUpperBoundMs);
            }
        }
        else if (evaluation.Classification == MarketObservationClassification.Clear)
        {
            var openEpisode = await ReadOpenEpisodeAsync(connection, transaction, listing.Id, cancellationToken);
            if (openEpisode != null)
            {
                await CloseEpisodeAsync(
                    connection,
                    transaction,
                    openEpisode.Id,
                    evaluation.ObservedAtUtc,
                    "ObservedClear",
                    cancellationToken);
                transition = BuildTransition("UndercutCleared", evaluation, openEpisode.ResponseUpperBoundMs);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return transition;
    }

    public async Task<IReadOnlyList<MarketDiagnosticEpisodeView>> ListEpisodesAsync(
        IReadOnlyList<long> accountIds,
        bool openOnly,
        int limit,
        CancellationToken cancellationToken)
    {
        if (accountIds.Count == 0)
            return [];

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var accountParameters = accountIds.Select((_, index) => $"$account{index}").ToArray();
        command.CommandText = $"""
            SELECT
                e.id,
                e.account_id,
                e.owned_listing_version_id,
                l.world,
                l.item_id,
                l.item_name,
                l.retainer_name,
                e.own_unit_price,
                e.competitor_unit_price,
                e.current_competitor_retainer_name,
                e.undercut_delta,
                e.exact_one_gil,
                e.started_at_utc,
                e.first_detected_at_utc,
                e.last_seen_at_utc,
                e.response_lower_bound_ms,
                e.response_upper_bound_ms,
                e.cleared_at_utc,
                e.close_reason
            FROM market_undercut_episodes e
            JOIN market_owned_listing_versions l ON l.id = e.owned_listing_version_id
            WHERE e.account_id IN ({string.Join(", ", accountParameters)})
              AND ($openOnly = 0 OR e.cleared_at_utc IS NULL)
            ORDER BY e.first_detected_at_utc DESC
            LIMIT $limit;
            """;
        for (var index = 0; index < accountIds.Count; index++)
            command.Parameters.AddWithValue(accountParameters[index], accountIds[index]);
        command.Parameters.AddWithValue("$openOnly", openOnly ? 1 : 0);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));

        var episodes = new List<MarketDiagnosticEpisodeView>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            episodes.Add(new MarketDiagnosticEpisodeView
            {
                Id = reader.GetInt64(0),
                AccountId = reader.GetInt64(1),
                OwnedListingVersionId = reader.GetInt64(2),
                World = reader.GetString(3),
                ItemId = checked((uint)reader.GetInt64(4)),
                ItemName = reader.IsDBNull(5) ? null : reader.GetString(5),
                RetainerName = reader.GetString(6),
                OwnUnitPrice = checked((uint)reader.GetInt64(7)),
                CompetitorUnitPrice = checked((uint)reader.GetInt64(8)),
                CompetitorRetainerName = reader.IsDBNull(9) ? null : reader.GetString(9),
                UndercutDelta = checked((uint)reader.GetInt64(10)),
                ExactOneGil = reader.GetInt64(11) != 0,
                StartedAtUtc = ParseRequired(reader.GetString(12)),
                FirstDetectedAtUtc = ParseRequired(reader.GetString(13)),
                LastSeenAtUtc = ParseRequired(reader.GetString(14)),
                ResponseLowerBoundMs = reader.GetInt64(15),
                ResponseUpperBoundMs = reader.GetInt64(16),
                ClearedAtUtc = reader.IsDBNull(17) ? null : ParseRequired(reader.GetString(17)),
                CloseReason = reader.IsDBNull(18) ? null : reader.GetString(18),
            });
        }

        return episodes;
    }

    public async Task<bool> ShouldCollectRegionAsync(
        long accountId,
        string region,
        DateTimeOffset observedAtUtc,
        TimeSpan minimumInterval,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MAX(observed_at_utc)
            FROM market_region_observations
            WHERE account_id = $accountId
              AND region = $region COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$region", region);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not string timestamp)
            return true;

        return observedAtUtc - ParseRequired(timestamp) >= minimumInterval;
    }

    public async Task RecordRegionConditionsAsync(
        long accountId,
        string region,
        IReadOnlyDictionary<uint, string?> itemNames,
        IReadOnlyCollection<RegionMarketCondition> conditions,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        if (conditions.Count == 0)
            return;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var condition in conditions)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO market_region_observations (
                    account_id,
                    region,
                    item_id,
                    item_name,
                    is_hq,
                    observed_at_utc,
                    min_listing_price,
                    min_listing_world_id,
                    average_sale_price,
                    daily_sale_velocity,
                    recent_purchase_price,
                    recent_purchase_world_id,
                    recent_purchase_at_utc,
                    freshest_world_upload_at_utc,
                    source_age_seconds
                )
                VALUES (
                    $accountId,
                    $region,
                    $itemId,
                    $itemName,
                    $isHq,
                    $observedAt,
                    $minimumListingPrice,
                    $minimumListingWorldId,
                    $averageSalePrice,
                    $dailySaleVelocity,
                    $recentPurchasePrice,
                    $recentPurchaseWorldId,
                    $recentPurchaseAt,
                    $freshestWorldUploadAt,
                    $sourceAgeSeconds
                );
                """;
            command.Parameters.AddWithValue("$accountId", accountId);
            command.Parameters.AddWithValue("$region", region);
            command.Parameters.AddWithValue("$itemId", condition.ItemId);
            command.Parameters.AddWithValue(
                "$itemName",
                itemNames.TryGetValue(condition.ItemId, out var itemName) ? Db(itemName) : DBNull.Value);
            command.Parameters.AddWithValue("$isHq", condition.IsHq ? 1 : 0);
            command.Parameters.AddWithValue("$observedAt", Format(observedAtUtc));
            command.Parameters.AddWithValue("$minimumListingPrice", Db(condition.MinimumListingPrice));
            command.Parameters.AddWithValue("$minimumListingWorldId", Db(condition.MinimumListingWorldId));
            command.Parameters.AddWithValue("$averageSalePrice", Db(condition.AverageSalePrice));
            command.Parameters.AddWithValue("$dailySaleVelocity", Db(condition.DailySaleVelocity));
            command.Parameters.AddWithValue("$recentPurchasePrice", Db(condition.RecentPurchasePrice));
            command.Parameters.AddWithValue("$recentPurchaseWorldId", Db(condition.RecentPurchaseWorldId));
            command.Parameters.AddWithValue("$recentPurchaseAt", Db(condition.RecentPurchaseAtUtc));
            command.Parameters.AddWithValue("$freshestWorldUploadAt", Db(condition.FreshestWorldUploadAtUtc));
            command.Parameters.AddWithValue(
                "$sourceAgeSeconds",
                condition.FreshestWorldUploadAtUtc is { } uploadedAt
                    ? Math.Max(0, (long)(observedAtUtc - uploadedAt).TotalSeconds)
                    : DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RegionMarketConditionView>> ListRegionConditionsAsync(
        IReadOnlyList<long> accountIds,
        uint? itemId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (accountIds.Count == 0)
            return [];

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var accountParameters = accountIds.Select((_, index) => $"$account{index}").ToArray();
        command.CommandText = $"""
            SELECT
                id,
                account_id,
                region,
                item_id,
                item_name,
                is_hq,
                observed_at_utc,
                min_listing_price,
                min_listing_world_id,
                average_sale_price,
                daily_sale_velocity,
                recent_purchase_price,
                recent_purchase_world_id,
                recent_purchase_at_utc,
                freshest_world_upload_at_utc,
                source_age_seconds
            FROM market_region_observations
            WHERE account_id IN ({string.Join(", ", accountParameters)})
              AND ($itemId IS NULL OR item_id = $itemId)
            ORDER BY observed_at_utc DESC, item_id, is_hq
            LIMIT $limit;
            """;
        for (var index = 0; index < accountIds.Count; index++)
            command.Parameters.AddWithValue(accountParameters[index], accountIds[index]);
        command.Parameters.AddWithValue("$itemId", itemId is null ? DBNull.Value : itemId.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));

        var conditions = new List<RegionMarketConditionView>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            conditions.Add(new RegionMarketConditionView
            {
                Id = reader.GetInt64(0),
                AccountId = reader.GetInt64(1),
                Region = reader.GetString(2),
                ItemId = checked((uint)reader.GetInt64(3)),
                ItemName = reader.IsDBNull(4) ? null : reader.GetString(4),
                IsHq = reader.GetInt64(5) != 0,
                ObservedAtUtc = ParseRequired(reader.GetString(6)),
                MinimumListingPrice = reader.IsDBNull(7) ? null : checked((uint)reader.GetInt64(7)),
                MinimumListingWorldId = reader.IsDBNull(8) ? null : checked((uint)reader.GetInt64(8)),
                AverageSalePrice = reader.IsDBNull(9) ? null : reader.GetDouble(9),
                DailySaleVelocity = reader.IsDBNull(10) ? null : reader.GetDouble(10),
                RecentPurchasePrice = reader.IsDBNull(11) ? null : checked((uint)reader.GetInt64(11)),
                RecentPurchaseWorldId = reader.IsDBNull(12) ? null : checked((uint)reader.GetInt64(12)),
                RecentPurchaseAtUtc = reader.IsDBNull(13) ? null : ParseRequired(reader.GetString(13)),
                FreshestWorldUploadAtUtc = reader.IsDBNull(14) ? null : ParseRequired(reader.GetString(14)),
                SourceAgeSeconds = reader.IsDBNull(15) ? null : reader.GetInt64(15),
            });
        }

        return conditions;
    }

    public async Task<IReadOnlyList<RetainerSaleEventView>> ListSaleEventsAsync(
        IReadOnlyList<long> accountIds,
        string? confidence,
        int limit,
        CancellationToken cancellationToken)
    {
        if (accountIds.Count == 0)
            return [];

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var accountParameters = accountIds.Select((_, index) => $"$account{index}").ToArray();
        command.CommandText = $"""
            SELECT
                id,
                account_id,
                owned_listing_version_id,
                source,
                confidence,
                retainer_id,
                retainer_name,
                character_name,
                world,
                item_id,
                item_name,
                quantity,
                is_hq,
                unit_price,
                total_gil,
                event_at_utc,
                earliest_event_at_utc,
                latest_event_at_utc,
                candidate_count,
                observed_at_utc
            FROM retainer_sale_events
            WHERE account_id IN ({string.Join(", ", accountParameters)})
              AND ($confidence IS NULL OR confidence = $confidence COLLATE NOCASE)
            ORDER BY COALESCE(event_at_utc, observed_at_utc) DESC
            LIMIT $limit;
            """;
        for (var index = 0; index < accountIds.Count; index++)
            command.Parameters.AddWithValue(accountParameters[index], accountIds[index]);
        command.Parameters.AddWithValue(
            "$confidence",
            string.IsNullOrWhiteSpace(confidence) ? DBNull.Value : confidence.Trim());
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));

        var events = new List<RetainerSaleEventView>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new RetainerSaleEventView
            {
                Id = reader.GetInt64(0),
                AccountId = reader.GetInt64(1),
                OwnedListingVersionId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                Source = reader.GetString(3),
                Confidence = reader.GetString(4),
                RetainerId = reader.IsDBNull(5) ? null : checked((ulong)reader.GetInt64(5)),
                RetainerName = reader.IsDBNull(6) ? null : reader.GetString(6),
                CharacterName = reader.IsDBNull(7) ? null : reader.GetString(7),
                World = reader.IsDBNull(8) ? null : reader.GetString(8),
                ItemId = checked((uint)reader.GetInt64(9)),
                ItemName = reader.IsDBNull(10) ? null : reader.GetString(10),
                Quantity = reader.IsDBNull(11) ? null : checked((uint)reader.GetInt64(11)),
                IsHq = reader.IsDBNull(12) ? null : reader.GetInt64(12) != 0,
                UnitPrice = reader.IsDBNull(13) ? null : checked((uint)reader.GetInt64(13)),
                TotalGil = reader.IsDBNull(14) ? null : checked((ulong)reader.GetInt64(14)),
                EventAtUtc = reader.IsDBNull(15) ? null : ParseRequired(reader.GetString(15)),
                EarliestEventAtUtc = reader.IsDBNull(16) ? null : ParseRequired(reader.GetString(16)),
                LatestEventAtUtc = reader.IsDBNull(17) ? null : ParseRequired(reader.GetString(17)),
                CandidateCount = reader.IsDBNull(18) ? null : checked((int)reader.GetInt64(18)),
                ObservedAtUtc = ParseRequired(reader.GetString(19)),
            });
        }

        return events;
    }

    public async Task<RetainerSaleEvidenceCreateResponse> RecordConfirmedSaleAsync(
        long accountId,
        RetainerSaleEvidenceCreateRequest evidence,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var evidenceId = evidence.EvidenceId.Trim();
        var source = string.IsNullOrWhiteSpace(evidence.Source)
            ? "RetainerSaleChat"
            : evidence.Source.Trim();
        var rawEvidence = JsonSerializer.Serialize(evidence);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var matchedListing = await FindConfirmedSaleListingAsync(
            connection,
            transaction,
            accountId,
            evidence,
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO retainer_sale_events (
                account_id,
                owned_listing_version_id,
                source,
                confidence,
                retainer_id,
                retainer_name,
                character_name,
                world,
                item_id,
                item_name,
                quantity,
                is_hq,
                unit_price,
                total_gil,
                event_at_utc,
                earliest_event_at_utc,
                latest_event_at_utc,
                candidate_count,
                observed_at_utc,
                evidence_hash,
                raw_evidence_json
            )
            VALUES (
                $accountId,
                $ownedListingVersionId,
                $source,
                'Confirmed',
                $retainerId,
                $retainerName,
                $characterName,
                $world,
                $itemId,
                $itemName,
                $quantity,
                $isHq,
                $unitPrice,
                $totalGil,
                $eventAt,
                $earliestEventAt,
                $latestEventAt,
                1,
                $observedAt,
                $evidenceHash,
                $rawEvidence
            );
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$ownedListingVersionId", Db(matchedListing?.Id));
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$retainerId", Db(evidence.RetainerId ?? matchedListing?.RetainerId));
        command.Parameters.AddWithValue("$retainerName", Db(evidence.RetainerName ?? matchedListing?.RetainerName));
        command.Parameters.AddWithValue("$characterName", Db(evidence.CharacterName ?? matchedListing?.CharacterName));
        command.Parameters.AddWithValue("$world", Db(evidence.HomeWorld ?? matchedListing?.World));
        command.Parameters.AddWithValue("$itemId", evidence.ItemId);
        command.Parameters.AddWithValue("$itemName", Db(evidence.ItemName ?? matchedListing?.ItemName));
        command.Parameters.AddWithValue("$quantity", Db(evidence.Quantity ?? matchedListing?.Quantity));
        command.Parameters.AddWithValue("$isHq", evidence.IsHq ? 1 : 0);
        command.Parameters.AddWithValue("$unitPrice", Db(evidence.UnitPrice ?? matchedListing?.UnitPrice));
        command.Parameters.AddWithValue("$totalGil", checked((long)evidence.TotalGil));
        command.Parameters.AddWithValue("$eventAt", Format(evidence.EventAtUtc));
        command.Parameters.AddWithValue(
            "$earliestEventAt",
            Format(evidence.EarliestEventAtUtc ?? evidence.EventAtUtc));
        command.Parameters.AddWithValue(
            "$latestEventAt",
            Format(evidence.LatestEventAtUtc ?? evidence.EventAtUtc));
        command.Parameters.AddWithValue("$observedAt", Format(observedAtUtc));
        command.Parameters.AddWithValue("$evidenceHash", evidenceId);
        command.Parameters.AddWithValue("$rawEvidence", rawEvidence);
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken) > 0;

        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = """
            SELECT id, owned_listing_version_id
            FROM retainer_sale_events
            WHERE account_id = $accountId
              AND evidence_hash = $evidenceHash
            LIMIT 1;
            """;
        read.Parameters.AddWithValue("$accountId", accountId);
        read.Parameters.AddWithValue("$evidenceHash", evidenceId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Confirmed sale evidence was not persisted.");
        var id = reader.GetInt64(0);
        var linkedListingId = reader.IsDBNull(1) ? matchedListing?.Id : reader.GetInt64(1);
        await reader.DisposeAsync();

        if (linkedListingId is { } listingId)
        {
            await ReconcileConfirmedSaleAsync(
                connection,
                transaction,
                id,
                listingId,
                evidence.EventAtUtc,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new RetainerSaleEvidenceCreateResponse
        {
            Id = id,
            Duplicate = !inserted,
            OwnedListingVersionId = linkedListingId,
        };
    }

    public async Task<MarketSaleHistoryProbe?> GetDueSaleHistoryProbeAsync(
        long listingVersionId,
        DateTimeOffset observedAtUtc,
        TimeSpan retryInterval,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                id,
                account_id,
                version_key,
                listing_key,
                source_snapshot_id,
                character_name,
                world,
                retainer_id,
                retainer_name,
                item_id,
                item_name,
                quantity,
                is_hq,
                unit_price,
                listed_at_utc,
                listings_observed_at_utc,
                first_observed_at_utc,
                last_observed_at_utc,
                last_publicly_seen_at_utc,
                publicly_missing_since_utc,
                sale_history_checked_at_utc
            FROM market_owned_listing_versions
            WHERE id = $listingVersionId
              AND is_active = 1
              AND last_publicly_seen_at_utc IS NOT NULL
              AND publicly_missing_since_utc IS NOT NULL
              AND (
                    sale_history_checked_at_utc IS NULL OR
                    sale_history_checked_at_utc <= $retryBefore
                  )
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$listingVersionId", listingVersionId);
        command.Parameters.AddWithValue("$retryBefore", Format(observedAtUtc - retryInterval));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var listing = ReadOwnedListing(reader);
        var earliestAt = listing.LastPubliclySeenAtUtc!.Value;
        var latestAt = listing.PubliclyMissingSinceUtc!.Value;
        await reader.DisposeAsync();

        await using var mark = connection.CreateCommand();
        mark.Transaction = transaction;
        mark.CommandText = """
            UPDATE market_owned_listing_versions
            SET sale_history_checked_at_utc = $checkedAt
            WHERE id = $listingVersionId;
            """;
        mark.Parameters.AddWithValue("$checkedAt", Format(observedAtUtc));
        mark.Parameters.AddWithValue("$listingVersionId", listingVersionId);
        await mark.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new MarketSaleHistoryProbe
        {
            Listing = listing,
            EarliestAtUtc = earliestAt,
            LatestAtUtc = latestAt,
        };
    }

    public async Task<RetainerSaleEventView?> RecordPublicSaleHistoryAsync(
        MarketSaleHistoryProbe probe,
        IReadOnlyCollection<UniversalisSaleEvidence> candidates,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (candidates.Count == 0)
            return null;

        var ordered = candidates.OrderBy(candidate => candidate.SoldAtUtc).ToArray();
        var exact = ordered.Length == 1 ? ordered[0] : null;
        var confidence = exact == null ? "Ambiguous" : "Probable";
        var evidenceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(
                $"{probe.Listing.AccountId}|{probe.Listing.Id}|UniversalisHistory|" +
                string.Join(",", ordered.Select(candidate => candidate.SoldAtUtc.ToUnixTimeSeconds())))));
        var rawEvidence = JsonSerializer.Serialize(ordered);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT OR IGNORE INTO retainer_sale_events (
                account_id,
                owned_listing_version_id,
                source,
                confidence,
                retainer_id,
                retainer_name,
                character_name,
                world,
                item_id,
                item_name,
                quantity,
                is_hq,
                unit_price,
                total_gil,
                event_at_utc,
                earliest_event_at_utc,
                latest_event_at_utc,
                candidate_count,
                observed_at_utc,
                evidence_hash,
                raw_evidence_json
            )
            VALUES (
                $accountId,
                $listingId,
                'UniversalisHistory',
                $confidence,
                $retainerId,
                $retainerName,
                $characterName,
                $world,
                $itemId,
                $itemName,
                $quantity,
                $isHq,
                $unitPrice,
                $totalGil,
                $eventAt,
                $earliestAt,
                $latestAt,
                $candidateCount,
                $observedAt,
                $evidenceHash,
                $rawEvidence
            );
            """;
        insert.Parameters.AddWithValue("$accountId", probe.Listing.AccountId);
        insert.Parameters.AddWithValue("$listingId", probe.Listing.Id);
        insert.Parameters.AddWithValue("$confidence", confidence);
        insert.Parameters.AddWithValue("$retainerId", checked((long)probe.Listing.RetainerId));
        insert.Parameters.AddWithValue("$retainerName", probe.Listing.RetainerName);
        insert.Parameters.AddWithValue("$characterName", Db(probe.Listing.CharacterName));
        insert.Parameters.AddWithValue("$world", probe.Listing.World);
        insert.Parameters.AddWithValue("$itemId", probe.Listing.ItemId);
        insert.Parameters.AddWithValue("$itemName", Db(probe.Listing.ItemName));
        insert.Parameters.AddWithValue("$quantity", probe.Listing.Quantity);
        insert.Parameters.AddWithValue("$isHq", probe.Listing.IsHq ? 1 : 0);
        insert.Parameters.AddWithValue("$unitPrice", probe.Listing.UnitPrice);
        insert.Parameters.AddWithValue(
            "$totalGil",
            exact == null
                ? DBNull.Value
                : checked((long)((ulong)exact.UnitPrice * exact.Quantity)));
        insert.Parameters.AddWithValue("$eventAt", Db(exact?.SoldAtUtc));
        insert.Parameters.AddWithValue("$earliestAt", Format(probe.EarliestAtUtc));
        insert.Parameters.AddWithValue("$latestAt", Format(probe.LatestAtUtc));
        insert.Parameters.AddWithValue("$candidateCount", ordered.Length);
        insert.Parameters.AddWithValue("$observedAt", Format(observedAtUtc));
        insert.Parameters.AddWithValue("$evidenceHash", evidenceHash);
        insert.Parameters.AddWithValue("$rawEvidence", rawEvidence);
        await insert.ExecuteNonQueryAsync(cancellationToken);

        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = """
            SELECT id
            FROM retainer_sale_events
            WHERE account_id = $accountId
              AND evidence_hash = $evidenceHash;
            """;
        read.Parameters.AddWithValue("$accountId", probe.Listing.AccountId);
        read.Parameters.AddWithValue("$evidenceHash", evidenceHash);
        var eventId = Convert.ToInt64(
            await read.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);

        if (exact != null)
        {
            await CloseListingForSaleAsync(
                connection,
                transaction,
                probe.Listing.Id,
                exact.SoldAtUtc,
                "ProbableSale",
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new RetainerSaleEventView
        {
            Id = eventId,
            AccountId = probe.Listing.AccountId,
            OwnedListingVersionId = probe.Listing.Id,
            Source = "UniversalisHistory",
            Confidence = confidence,
            RetainerId = probe.Listing.RetainerId,
            RetainerName = probe.Listing.RetainerName,
            CharacterName = probe.Listing.CharacterName,
            World = probe.Listing.World,
            ItemId = probe.Listing.ItemId,
            ItemName = probe.Listing.ItemName,
            Quantity = probe.Listing.Quantity,
            IsHq = probe.Listing.IsHq,
            UnitPrice = probe.Listing.UnitPrice,
            TotalGil = exact == null ? null : (ulong)exact.UnitPrice * exact.Quantity,
            EventAtUtc = exact?.SoldAtUtc,
            EarliestEventAtUtc = probe.EarliestAtUtc,
            LatestEventAtUtc = probe.LatestAtUtc,
            CandidateCount = ordered.Length,
            ObservedAtUtc = observedAtUtc,
        };
    }

    public async Task PruneObservationHistoryAsync(
        DateTimeOffset observedAtUtc,
        TimeSpan worldRetention,
        TimeSpan regionRetention,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var world = connection.CreateCommand())
        {
            world.Transaction = transaction;
            world.CommandText = """
                DELETE FROM market_observations
                WHERE observed_at_utc < $cutoff;
                """;
            world.Parameters.AddWithValue("$cutoff", Format(observedAtUtc - worldRetention));
            await world.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var region = connection.CreateCommand())
        {
            region.Transaction = transaction;
            region.CommandText = """
                DELETE FROM market_region_observations
                WHERE observed_at_utc < $cutoff;
                """;
            region.Parameters.AddWithValue("$cutoff", Format(observedAtUtc - regionRetention));
            await region.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<OwnedMarketListing?> FindConfirmedSaleListingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long accountId,
        RetainerSaleEvidenceCreateRequest evidence,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                id,
                account_id,
                version_key,
                listing_key,
                source_snapshot_id,
                character_name,
                world,
                retainer_id,
                retainer_name,
                item_id,
                item_name,
                quantity,
                is_hq,
                unit_price,
                listed_at_utc,
                listings_observed_at_utc,
                first_observed_at_utc,
                last_observed_at_utc,
                last_publicly_seen_at_utc,
                publicly_missing_since_utc,
                sale_history_checked_at_utc
            FROM market_owned_listing_versions
            WHERE account_id = $accountId
              AND item_id = $itemId
              AND is_hq = $isHq
              AND ($world IS NULL OR world = $world COLLATE NOCASE)
              AND ($characterName IS NULL OR character_name = $characterName)
              AND ($retainerId IS NULL OR retainer_id = $retainerId)
              AND ($retainerName IS NULL OR retainer_name = $retainerName COLLATE NOCASE)
              AND first_observed_at_utc <= $latestAt
              AND COALESCE(closed_at_utc, $latestAt) >= $earliestAt
            ORDER BY is_active DESC, last_observed_at_utc DESC, id DESC;
            """;
        var earliestAt = evidence.EarliestEventAtUtc ?? evidence.EventAtUtc.AddMinutes(-5);
        var latestAt = evidence.LatestEventAtUtc ?? evidence.EventAtUtc.AddMinutes(5);
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$itemId", evidence.ItemId);
        command.Parameters.AddWithValue("$isHq", evidence.IsHq ? 1 : 0);
        command.Parameters.AddWithValue("$world", Db(evidence.HomeWorld));
        command.Parameters.AddWithValue("$characterName", Db(evidence.CharacterName));
        command.Parameters.AddWithValue("$retainerId", Db(evidence.RetainerId));
        command.Parameters.AddWithValue("$retainerName", Db(evidence.RetainerName));
        command.Parameters.AddWithValue("$earliestAt", Format(earliestAt));
        command.Parameters.AddWithValue("$latestAt", Format(latestAt));

        var candidates = new List<OwnedMarketListing>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var listing = ReadOwnedListing(reader);
            if (evidence.Quantity is { } quantity && listing.Quantity != quantity)
                continue;
            if (evidence.UnitPrice is { } unitPrice && listing.UnitPrice != unitPrice)
                continue;

            if (evidence.Quantity is null && evidence.UnitPrice is null)
            {
                var gross = (ulong)listing.UnitPrice * listing.Quantity;
                var minimumPlausibleNet = gross * 85 / 100;
                if (evidence.TotalGil > gross || evidence.TotalGil < minimumPlausibleNet)
                    continue;
            }

            candidates.Add(listing);
        }

        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static async Task ReconcileConfirmedSaleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long saleEventId,
        long listingVersionId,
        DateTimeOffset eventAtUtc,
        CancellationToken cancellationToken)
    {
        await using (var enrich = connection.CreateCommand())
        {
            enrich.Transaction = transaction;
            enrich.CommandText = """
                UPDATE retainer_sale_events
                SET owned_listing_version_id = $listingId,
                    retainer_id = COALESCE(retainer_id, (
                        SELECT retainer_id FROM market_owned_listing_versions WHERE id = $listingId
                    )),
                    retainer_name = COALESCE(retainer_name, (
                        SELECT retainer_name FROM market_owned_listing_versions WHERE id = $listingId
                    )),
                    character_name = COALESCE(character_name, (
                        SELECT character_name FROM market_owned_listing_versions WHERE id = $listingId
                    )),
                    world = COALESCE(world, (
                        SELECT world FROM market_owned_listing_versions WHERE id = $listingId
                    )),
                    item_name = COALESCE(item_name, (
                        SELECT item_name FROM market_owned_listing_versions WHERE id = $listingId
                    )),
                    quantity = COALESCE(quantity, (
                        SELECT quantity FROM market_owned_listing_versions WHERE id = $listingId
                    )),
                    unit_price = COALESCE(unit_price, (
                        SELECT unit_price FROM market_owned_listing_versions WHERE id = $listingId
                    ))
                WHERE id = $saleEventId;
                """;
            enrich.Parameters.AddWithValue("$listingId", listingVersionId);
            enrich.Parameters.AddWithValue("$saleEventId", saleEventId);
            await enrich.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var corroborate = connection.CreateCommand())
        {
            corroborate.Transaction = transaction;
            corroborate.CommandText = """
                UPDATE retainer_sale_events
                SET confidence = 'Corroborated'
                WHERE owned_listing_version_id = $listingId
                  AND id <> $saleEventId
                  AND confidence IN ('Unresolved', 'Probable', 'Ambiguous');
                """;
            corroborate.Parameters.AddWithValue("$listingId", listingVersionId);
            corroborate.Parameters.AddWithValue("$saleEventId", saleEventId);
            await corroborate.ExecuteNonQueryAsync(cancellationToken);
        }

        await CloseListingForSaleAsync(
            connection,
            transaction,
            listingVersionId,
            eventAtUtc,
            "ConfirmedSale",
            cancellationToken);
    }

    private static async Task CloseListingForSaleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long listingVersionId,
        DateTimeOffset eventAtUtc,
        string reason,
        CancellationToken cancellationToken)
    {
        await using (var closeEpisodes = connection.CreateCommand())
        {
            closeEpisodes.Transaction = transaction;
            closeEpisodes.CommandText = """
                UPDATE market_undercut_episodes
                SET cleared_at_utc = $closedAt,
                    close_reason = $reason
                WHERE owned_listing_version_id = $listingId
                  AND cleared_at_utc IS NULL;
                """;
            closeEpisodes.Parameters.AddWithValue("$closedAt", Format(eventAtUtc));
            closeEpisodes.Parameters.AddWithValue("$reason", reason);
            closeEpisodes.Parameters.AddWithValue("$listingId", listingVersionId);
            await closeEpisodes.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var closeListing = connection.CreateCommand();
        closeListing.Transaction = transaction;
        closeListing.CommandText = """
            UPDATE market_owned_listing_versions
            SET is_active = 0,
                closed_at_utc = $closedAt,
                close_reason = $reason
            WHERE id = $listingId;
            """;
        closeListing.Parameters.AddWithValue("$closedAt", Format(eventAtUtc));
        closeListing.Parameters.AddWithValue("$reason", reason);
        closeListing.Parameters.AddWithValue("$listingId", listingVersionId);
        await closeListing.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<List<OwnedMarketListing>> ReadLatestOwnedListingsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH latest_snapshots AS (
                SELECT
                    s.id,
                    s.account_id,
                    s.character_id,
                    s.character_name,
                    s.home_world,
                    s.received_at_utc,
                    ROW_NUMBER() OVER (
                        PARTITION BY s.account_id, COALESCE(s.character_id, -1)
                        ORDER BY s.received_at_utc DESC, s.id DESC
                    ) AS rank
                FROM snapshots s
                WHERE EXISTS (
                    SELECT 1
                    FROM inventory_owners observed_owner
                    WHERE observed_owner.snapshot_id = s.id
                      AND observed_owner.owner_type = 'retainer'
                      AND observed_owner.listings_observed_at_utc IS NOT NULL
                )
            )
            SELECT
                latest.id,
                latest.account_id,
                latest.character_name,
                latest.home_world,
                owner.retainer_id,
                owner.owner_name,
                owner.listings_observed_at_utc,
                listing.item_id,
                listing.item_name,
                listing.quantity,
                listing.is_hq,
                listing.unit_price,
                listing.listed_at,
                listing.container_key,
                listing.slot_index
            FROM latest_snapshots latest
            JOIN inventory_owners owner ON owner.snapshot_id = latest.id
            JOIN retainer_market_listings listing ON listing.owner_id = owner.id
            WHERE latest.rank = 1
              AND owner.owner_type = 'retainer'
              AND owner.retainer_id IS NOT NULL
              AND owner.listings_observed_at_utc IS NOT NULL
              AND latest.home_world IS NOT NULL
              AND TRIM(latest.home_world) <> ''
              AND listing.item_id > 0
              AND listing.quantity > 0
              AND listing.unit_price > 0
            ORDER BY latest.account_id, latest.home_world, owner.retainer_id, listing.slot_index;
            """;

        var listings = new List<OwnedMarketListing>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var accountId = reader.GetInt64(1);
            var snapshotId = reader.GetString(0);
            var retainerId = checked((ulong)reader.GetInt64(4));
            var itemId = checked((uint)reader.GetInt64(7));
            var quantity = checked((uint)reader.GetInt64(9));
            var isHq = reader.GetInt64(10) != 0;
            var unitPrice = checked((uint)reader.GetInt64(11));
            var listedAt = reader.IsDBNull(12) ? null : reader.GetString(12);
            var containerKey = reader.IsDBNull(13) ? "RetainerMarket" : reader.GetString(13);
            var slotIndex = reader.IsDBNull(14) ? -1 : reader.GetInt32(14);
            var listingKey = $"{retainerId}:{containerKey}:{slotIndex}";
            var versionKey = $"{listingKey}:{itemId}:{quantity}:{isHq}:{unitPrice}:{listedAt}";
            var listingsObservedAt = ParseRequired(reader.GetString(6));

            listings.Add(new OwnedMarketListing
            {
                AccountId = accountId,
                VersionKey = versionKey,
                ListingKey = listingKey,
                SnapshotId = snapshotId,
                CharacterName = reader.IsDBNull(2) ? null : reader.GetString(2),
                World = reader.GetString(3),
                RetainerId = retainerId,
                RetainerName = reader.GetString(5),
                ItemId = itemId,
                ItemName = reader.IsDBNull(8) ? null : reader.GetString(8),
                Quantity = quantity,
                IsHq = isHq,
                UnitPrice = unitPrice,
                ListedAtUtc = ParseOptional(listedAt),
                ListingsObservedAtUtc = listingsObservedAt,
                FirstObservedAtUtc = listingsObservedAt,
                LastObservedAtUtc = listingsObservedAt,
            });
        }

        return listings;
    }

    private async Task<List<ListingScope>> ReadLatestListingScopesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH latest_snapshots AS (
                SELECT
                    s.id,
                    s.account_id,
                    s.character_id,
                    s.character_name,
                    s.home_world,
                    ROW_NUMBER() OVER (
                        PARTITION BY s.account_id, COALESCE(s.character_id, -1)
                        ORDER BY s.received_at_utc DESC, s.id DESC
                    ) AS rank
                FROM snapshots s
                WHERE EXISTS (
                    SELECT 1
                    FROM inventory_owners observed_owner
                    WHERE observed_owner.snapshot_id = s.id
                      AND observed_owner.owner_type = 'retainer'
                      AND observed_owner.listings_observed_at_utc IS NOT NULL
                )
            )
            SELECT
                latest.account_id,
                latest.character_name,
                latest.home_world,
                MAX(observed_owner.listings_observed_at_utc)
            FROM latest_snapshots latest
            JOIN inventory_owners observed_owner
              ON observed_owner.snapshot_id = latest.id
             AND observed_owner.owner_type = 'retainer'
             AND observed_owner.listings_observed_at_utc IS NOT NULL
            WHERE latest.rank = 1
              AND latest.home_world IS NOT NULL
              AND TRIM(latest.home_world) <> ''
            GROUP BY
                latest.account_id,
                latest.character_name,
                latest.home_world;
            """;

        var scopes = new List<ListingScope>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            scopes.Add(new ListingScope(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                ParseRequired(reader.GetString(3))));
        }

        return scopes;
    }

    private static async Task CloseMissingListingsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ListingScope scope,
        IReadOnlySet<string> currentVersionKeys,
        CancellationToken cancellationToken)
    {
        var missingIds = new List<long>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT id, version_key
                FROM market_owned_listing_versions
                WHERE account_id = $accountId
                  AND world = $world COLLATE NOCASE
                  AND (
                      character_name = $characterName OR
                      (character_name IS NULL AND $characterName IS NULL)
                  )
                  AND is_active = 1;
                """;
            select.Parameters.AddWithValue("$accountId", scope.AccountId);
            select.Parameters.AddWithValue("$world", scope.World);
            select.Parameters.AddWithValue("$characterName", Db(scope.CharacterName));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!currentVersionKeys.Contains(reader.GetString(1)))
                    missingIds.Add(reader.GetInt64(0));
            }
        }

        foreach (var listingId in missingIds)
        {
            await using (var saleEvent = connection.CreateCommand())
            {
                saleEvent.Transaction = transaction;
                saleEvent.CommandText = """
                    INSERT INTO retainer_sale_events (
                        account_id,
                        owned_listing_version_id,
                        source,
                        confidence,
                        retainer_id,
                        retainer_name,
                        character_name,
                        world,
                        item_id,
                        item_name,
                        quantity,
                        is_hq,
                        unit_price,
                        earliest_event_at_utc,
                        latest_event_at_utc,
                        observed_at_utc
                    )
                    SELECT
                        account_id,
                        id,
                        'LocalListingDiff',
                        'Unresolved',
                        retainer_id,
                        retainer_name,
                        character_name,
                        world,
                        item_id,
                        item_name,
                        quantity,
                        is_hq,
                        unit_price,
                        last_observed_at_utc,
                        $observedAt,
                        $observedAt
                    FROM market_owned_listing_versions
                    WHERE id = $listingId;
                    """;
                saleEvent.Parameters.AddWithValue("$observedAt", Format(scope.ListingsObservedAtUtc));
                saleEvent.Parameters.AddWithValue("$listingId", listingId);
                await saleEvent.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var closeEpisodes = connection.CreateCommand())
            {
                closeEpisodes.Transaction = transaction;
                closeEpisodes.CommandText = """
                    UPDATE market_undercut_episodes
                    SET cleared_at_utc = $closedAt,
                        close_reason = 'OwnedListingDisappeared'
                    WHERE owned_listing_version_id = $listingId
                      AND cleared_at_utc IS NULL;
                    """;
                closeEpisodes.Parameters.AddWithValue("$closedAt", Format(scope.ListingsObservedAtUtc));
                closeEpisodes.Parameters.AddWithValue("$listingId", listingId);
                await closeEpisodes.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var closeListing = connection.CreateCommand();
            closeListing.Transaction = transaction;
            closeListing.CommandText = """
                UPDATE market_owned_listing_versions
                SET is_active = 0,
                    closed_at_utc = $closedAt,
                    close_reason = 'LocallyDisappeared'
                WHERE id = $listingId;
                """;
            closeListing.Parameters.AddWithValue("$closedAt", Format(scope.ListingsObservedAtUtc));
            closeListing.Parameters.AddWithValue("$listingId", listingId);
            await closeListing.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertObservationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MarketListingEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO market_observations (
                account_id,
                owned_listing_version_id,
                observed_at_utc,
                source_upload_at_utc,
                source_age_seconds,
                source_freshness,
                classification,
                own_listing_visible,
                own_unit_price,
                competitor_listing_id,
                competitor_retainer_id,
                competitor_retainer_name,
                competitor_unit_price,
                competitor_quantity,
                competitor_reviewed_at_utc,
                undercut_delta
            )
            VALUES (
                $accountId,
                $ownedListingVersionId,
                $observedAt,
                $sourceUploadedAt,
                $sourceAgeSeconds,
                $sourceFreshness,
                $classification,
                $ownListingVisible,
                $ownUnitPrice,
                $competitorListingId,
                $competitorRetainerId,
                $competitorRetainerName,
                $competitorUnitPrice,
                $competitorQuantity,
                $competitorReviewedAt,
                $undercutDelta
            );
            """;
        command.Parameters.AddWithValue("$accountId", evaluation.OwnedListing.AccountId);
        command.Parameters.AddWithValue("$ownedListingVersionId", evaluation.OwnedListing.Id);
        command.Parameters.AddWithValue("$observedAt", Format(evaluation.ObservedAtUtc));
        command.Parameters.AddWithValue("$sourceUploadedAt", Db(evaluation.SourceUploadedAtUtc));
        command.Parameters.AddWithValue("$sourceAgeSeconds", Db(evaluation.SourceAgeSeconds));
        command.Parameters.AddWithValue("$sourceFreshness", evaluation.SourceFreshness);
        command.Parameters.AddWithValue("$classification", evaluation.Classification.ToString());
        command.Parameters.AddWithValue(
            "$ownListingVisible",
            evaluation.OwnListingVisible.HasValue
                ? evaluation.OwnListingVisible.Value ? 1 : 0
                : DBNull.Value);
        command.Parameters.AddWithValue("$ownUnitPrice", evaluation.OwnedListing.UnitPrice);
        command.Parameters.AddWithValue("$competitorListingId", Db(evaluation.Competitor?.ListingId));
        command.Parameters.AddWithValue("$competitorRetainerId", Db(evaluation.Competitor?.RetainerId));
        command.Parameters.AddWithValue("$competitorRetainerName", Db(evaluation.Competitor?.RetainerName));
        command.Parameters.AddWithValue("$competitorUnitPrice", Db(evaluation.Competitor?.UnitPrice));
        command.Parameters.AddWithValue("$competitorQuantity", Db(evaluation.Competitor?.Quantity));
        command.Parameters.AddWithValue("$competitorReviewedAt", Db(evaluation.Competitor?.ReviewedAtUtc));
        command.Parameters.AddWithValue("$undercutDelta", Db(evaluation.UndercutDelta));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdatePublicVisibilityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MarketListingEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        if (evaluation.OwnListingVisible is null)
            return;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = evaluation.OwnListingVisible.Value
            ? """
              UPDATE market_owned_listing_versions
              SET last_publicly_seen_at_utc = $observedAt,
                  publicly_missing_since_utc = NULL,
                  sale_history_checked_at_utc = NULL
              WHERE id = $listingId;
              """
            : """
              UPDATE market_owned_listing_versions
              SET publicly_missing_since_utc = COALESCE(publicly_missing_since_utc, $observedAt)
              WHERE id = $listingId;
              """;
        command.Parameters.AddWithValue("$observedAt", Format(evaluation.ObservedAtUtc));
        command.Parameters.AddWithValue("$listingId", evaluation.OwnedListing.Id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<DateTimeOffset?> ReadLastClearObservationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long listingVersionId,
        DateTimeOffset beforeUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT observed_at_utc
            FROM market_observations
            WHERE owned_listing_version_id = $listingVersionId
              AND classification = 'Clear'
              AND observed_at_utc < $beforeUtc
            ORDER BY observed_at_utc DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$listingVersionId", listingVersionId);
        command.Parameters.AddWithValue("$beforeUtc", Format(beforeUtc));
        return await command.ExecuteScalarAsync(cancellationToken) is string value
            ? ParseRequired(value)
            : null;
    }

    private static async Task<OpenEpisode?> ReadOpenEpisodeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long listingVersionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                id,
                current_competitor_listing_id,
                current_competitor_retainer_id,
                response_upper_bound_ms
            FROM market_undercut_episodes
            WHERE owned_listing_version_id = $listingVersionId
              AND cleared_at_utc IS NULL
            ORDER BY id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$listingVersionId", listingVersionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new OpenEpisode(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt64(3));
    }

    private static async Task InsertEpisodeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MarketListingEvaluation evaluation,
        DateTimeOffset? lastClear,
        long lowerBoundMs,
        long upperBoundMs,
        CancellationToken cancellationToken)
    {
        var competitor = evaluation.Competitor!;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO market_undercut_episodes (
                account_id,
                owned_listing_version_id,
                started_at_utc,
                first_detected_at_utc,
                last_seen_at_utc,
                last_clear_observed_at_utc,
                response_lower_bound_ms,
                response_upper_bound_ms,
                first_competitor_listing_id,
                first_competitor_retainer_id,
                first_competitor_retainer_name,
                current_competitor_listing_id,
                current_competitor_retainer_id,
                current_competitor_retainer_name,
                own_unit_price,
                competitor_unit_price,
                undercut_delta,
                exact_one_gil
            )
            VALUES (
                $accountId,
                $ownedListingVersionId,
                $startedAt,
                $firstDetectedAt,
                $lastSeenAt,
                $lastClearObservedAt,
                $responseLowerBoundMs,
                $responseUpperBoundMs,
                $firstCompetitorListingId,
                $firstCompetitorRetainerId,
                $firstCompetitorRetainerName,
                $currentCompetitorListingId,
                $currentCompetitorRetainerId,
                $currentCompetitorRetainerName,
                $ownUnitPrice,
                $competitorUnitPrice,
                $undercutDelta,
                $exactOneGil
            );
            """;
        command.Parameters.AddWithValue("$accountId", evaluation.OwnedListing.AccountId);
        command.Parameters.AddWithValue("$ownedListingVersionId", evaluation.OwnedListing.Id);
        command.Parameters.AddWithValue("$startedAt", Format(evaluation.ObservedAtUtc));
        command.Parameters.AddWithValue("$firstDetectedAt", Format(evaluation.ObservedAtUtc));
        command.Parameters.AddWithValue("$lastSeenAt", Format(evaluation.ObservedAtUtc));
        command.Parameters.AddWithValue("$lastClearObservedAt", Db(lastClear));
        command.Parameters.AddWithValue("$responseLowerBoundMs", lowerBoundMs);
        command.Parameters.AddWithValue("$responseUpperBoundMs", upperBoundMs);
        command.Parameters.AddWithValue("$firstCompetitorListingId", Db(competitor.ListingId));
        command.Parameters.AddWithValue("$firstCompetitorRetainerId", Db(competitor.RetainerId));
        command.Parameters.AddWithValue("$firstCompetitorRetainerName", Db(competitor.RetainerName));
        command.Parameters.AddWithValue("$currentCompetitorListingId", Db(competitor.ListingId));
        command.Parameters.AddWithValue("$currentCompetitorRetainerId", Db(competitor.RetainerId));
        command.Parameters.AddWithValue("$currentCompetitorRetainerName", Db(competitor.RetainerName));
        command.Parameters.AddWithValue("$ownUnitPrice", evaluation.OwnedListing.UnitPrice);
        command.Parameters.AddWithValue("$competitorUnitPrice", competitor.UnitPrice);
        command.Parameters.AddWithValue("$undercutDelta", evaluation.UndercutDelta!.Value);
        command.Parameters.AddWithValue("$exactOneGil", evaluation.UndercutDelta == 1 ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateEpisodeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long episodeId,
        MarketListingEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        var competitor = evaluation.Competitor!;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE market_undercut_episodes
            SET last_seen_at_utc = $lastSeenAt,
                current_competitor_listing_id = $competitorListingId,
                current_competitor_retainer_id = $competitorRetainerId,
                current_competitor_retainer_name = $competitorRetainerName,
                competitor_unit_price = $competitorUnitPrice,
                undercut_delta = $undercutDelta,
                exact_one_gil = $exactOneGil
            WHERE id = $episodeId;
            """;
        command.Parameters.AddWithValue("$lastSeenAt", Format(evaluation.ObservedAtUtc));
        command.Parameters.AddWithValue("$competitorListingId", Db(competitor.ListingId));
        command.Parameters.AddWithValue("$competitorRetainerId", Db(competitor.RetainerId));
        command.Parameters.AddWithValue("$competitorRetainerName", Db(competitor.RetainerName));
        command.Parameters.AddWithValue("$competitorUnitPrice", competitor.UnitPrice);
        command.Parameters.AddWithValue("$undercutDelta", evaluation.UndercutDelta!.Value);
        command.Parameters.AddWithValue("$exactOneGil", evaluation.UndercutDelta == 1 ? 1 : 0);
        command.Parameters.AddWithValue("$episodeId", episodeId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CloseEpisodeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long episodeId,
        DateTimeOffset clearedAtUtc,
        string reason,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE market_undercut_episodes
            SET cleared_at_utc = $clearedAt,
                close_reason = $reason
            WHERE id = $episodeId;
            """;
        command.Parameters.AddWithValue("$clearedAt", Format(clearedAtUtc));
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$episodeId", episodeId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static MarketDiagnosticTransition BuildTransition(
        string type,
        MarketListingEvaluation evaluation,
        long? responseUpperBoundMs) =>
        new()
        {
            Type = type,
            AccountId = evaluation.OwnedListing.AccountId,
            OwnedListingVersionId = evaluation.OwnedListing.Id,
            CharacterName = evaluation.OwnedListing.CharacterName,
            World = evaluation.OwnedListing.World,
            ItemId = evaluation.OwnedListing.ItemId,
            ItemName = evaluation.OwnedListing.ItemName,
            RetainerName = evaluation.OwnedListing.RetainerName,
            OwnUnitPrice = evaluation.OwnedListing.UnitPrice,
            CompetitorRetainerName = evaluation.Competitor?.RetainerName,
            CompetitorUnitPrice = evaluation.Competitor?.UnitPrice,
            UndercutDelta = evaluation.UndercutDelta,
            ResponseUpperBoundMs = responseUpperBoundMs,
        };

    private static OwnedMarketListing ReadOwnedListing(SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetInt64(0),
            AccountId = reader.GetInt64(1),
            VersionKey = reader.GetString(2),
            ListingKey = reader.GetString(3),
            SnapshotId = reader.GetString(4),
            CharacterName = reader.IsDBNull(5) ? null : reader.GetString(5),
            World = reader.GetString(6),
            RetainerId = checked((ulong)reader.GetInt64(7)),
            RetainerName = reader.GetString(8),
            ItemId = checked((uint)reader.GetInt64(9)),
            ItemName = reader.IsDBNull(10) ? null : reader.GetString(10),
            Quantity = checked((uint)reader.GetInt64(11)),
            IsHq = reader.GetInt64(12) != 0,
            UnitPrice = checked((uint)reader.GetInt64(13)),
            ListedAtUtc = reader.IsDBNull(14) ? null : ParseOptional(reader.GetString(14)),
            ListingsObservedAtUtc = ParseRequired(reader.GetString(15)),
            FirstObservedAtUtc = ParseRequired(reader.GetString(16)),
            LastObservedAtUtc = ParseRequired(reader.GetString(17)),
            LastPubliclySeenAtUtc = reader.IsDBNull(18) ? null : ParseRequired(reader.GetString(18)),
            PubliclyMissingSinceUtc = reader.IsDBNull(19) ? null : ParseRequired(reader.GetString(19)),
            SaleHistoryCheckedAtUtc = reader.IsDBNull(20) ? null : ParseRequired(reader.GetString(20)),
        };

    private static void AddOwnedListingParameters(
        SqliteCommand command,
        OwnedMarketListing listing)
    {
        command.Parameters.AddWithValue("$accountId", listing.AccountId);
        command.Parameters.AddWithValue("$versionKey", listing.VersionKey);
        command.Parameters.AddWithValue("$listingKey", listing.ListingKey);
        command.Parameters.AddWithValue("$snapshotId", listing.SnapshotId);
        command.Parameters.AddWithValue("$characterName", Db(listing.CharacterName));
        command.Parameters.AddWithValue("$world", listing.World);
        command.Parameters.AddWithValue("$retainerId", checked((long)listing.RetainerId));
        command.Parameters.AddWithValue("$retainerName", listing.RetainerName);
        command.Parameters.AddWithValue("$itemId", listing.ItemId);
        command.Parameters.AddWithValue("$itemName", Db(listing.ItemName));
        command.Parameters.AddWithValue("$quantity", listing.Quantity);
        command.Parameters.AddWithValue("$isHq", listing.IsHq ? 1 : 0);
        command.Parameters.AddWithValue("$unitPrice", listing.UnitPrice);
        command.Parameters.AddWithValue("$listedAt", Db(listing.ListedAtUtc));
        command.Parameters.AddWithValue("$listingsObservedAt", Format(listing.ListingsObservedAtUtc));
        command.Parameters.AddWithValue("$firstObservedAt", Format(listing.ListingsObservedAtUtc));
        command.Parameters.AddWithValue("$lastObservedAt", Format(listing.ListingsObservedAtUtc));
    }

    private static object Db(object? value) =>
        value switch
        {
            null => DBNull.Value,
            DateTimeOffset timestamp => Format(timestamp),
            _ => value,
        };

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseRequired(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string FriendlySaleSource(string source) => source switch
    {
        "RetainerHistory" => "Retainer History",
        "RetainerSaleChat" => "Sale notification",
        "UniversalisHistory" => "Universalis sale history",
        "LocalListingDiff" => "Local listing disappearance",
        _ => source,
    };

    private static string ReadIntervalSuffix(SqliteDataReader reader, int earliestIndex, int latestIndex)
    {
        if (reader.IsDBNull(earliestIndex) || reader.IsDBNull(latestIndex))
            return string.Empty;

        var earliest = ParseRequired(reader.GetString(earliestIndex));
        var latest = ParseRequired(reader.GetString(latestIndex));
        return earliest == latest
            ? string.Empty
            : $" · between {earliest.ToLocalTime():g} and {latest.ToLocalTime():g}";
    }

    private static DateTimeOffset? ParseOptional(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private sealed record OpenEpisode(
        long Id,
        string? CurrentCompetitorListingId,
        string? CurrentCompetitorRetainerId,
        long ResponseUpperBoundMs);

    private sealed record ListingScope(
        long AccountId,
        string? CharacterName,
        string World,
        DateTimeOffset ListingsObservedAtUtc);
}
