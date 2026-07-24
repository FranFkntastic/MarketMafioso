using System.Globalization;
using MarketMafioso.Server.Sqlite;
using Microsoft.Data.Sqlite;

namespace MarketMafioso.Server.MarketDiagnostics;

public sealed class MarketDiagnosticStore(SqliteConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<OwnedMarketListing>> SynchronizeOwnedListingsAsync(
        CancellationToken cancellationToken)
    {
        var current = await ReadLatestOwnedListingsAsync(cancellationToken);
        if (current.Count == 0)
            return [];

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
                    is_active = 1,
                    closed_at_utc = NULL,
                    close_reason = NULL;
                """;
            AddOwnedListingParameters(upsert, listing, observedAt);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
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
                last_observed_at_utc
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

    public async Task<MarketDiagnosticTransition?> RecordObservationAsync(
        MarketListingEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        var listing = evaluation.OwnedListing;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await InsertObservationAsync(connection, transaction, evaluation, cancellationToken);

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
                var listingStarted = listing.ListedAtUtc ?? listing.FirstObservedAtUtc;
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
        };

    private static void AddOwnedListingParameters(
        SqliteCommand command,
        OwnedMarketListing listing,
        DateTimeOffset observedAt)
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
        command.Parameters.AddWithValue("$firstObservedAt", Format(observedAt));
        command.Parameters.AddWithValue("$lastObservedAt", Format(observedAt));
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

    private static DateTimeOffset? ParseOptional(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private sealed record OpenEpisode(
        long Id,
        string? CurrentCompetitorListingId,
        string? CurrentCompetitorRetainerId,
        long ResponseUpperBoundMs);
}
