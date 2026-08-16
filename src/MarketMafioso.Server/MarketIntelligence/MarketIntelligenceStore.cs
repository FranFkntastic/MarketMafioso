using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketMafioso.Contracts.MarketIntelligence;
using MarketMafioso.Server.Sqlite;
using Microsoft.Data.Sqlite;

namespace MarketMafioso.Server.MarketIntelligence;

public sealed class MarketIntelligenceStore
{
    public const string ClassifierVersion = "market-intelligence-v2";
    public const string ActorKeyScheme = "account-content-id-sha256-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> SupportedCoverage =
    [
        MarketEvidenceCoverage.Complete,
        MarketEvidenceCoverage.Partial,
        MarketEvidenceCoverage.LegacyMissing,
        MarketEvidenceCoverage.Empty,
        MarketEvidenceCoverage.Unavailable,
        MarketEvidenceCoverage.AggregateOnly,
    ];

    private readonly SqliteConnectionFactory connectionFactory;

    public MarketIntelligenceStore(SqliteConnectionFactory connectionFactory)
    {
        this.connectionFactory = connectionFactory;
    }

    public async Task<MarketEvidenceReceipt> IngestAsync(
        long accountId,
        MarketEvidenceUploadRequest request,
        CancellationToken cancellationToken,
        bool projectImmediately = true)
    {
        Validate(request);
        var canonicalUploadListings = request.Listings
            .OrderBy(x => x.ListingId, StringComparer.Ordinal)
            .ThenBy(x => x.UnitPrice)
            .ThenBy(x => x.Quantity)
            .ToArray();
        var canonicalListings = canonicalUploadListings
            .Select(listing => NormalizeListing(accountId, listing))
            .ToArray();
        var listingsJson = JsonSerializer.Serialize(canonicalListings, JsonOptions);
        var payloadHash = Sha256(listingsJson);
        var requestHash = Sha256(JsonSerializer.Serialize(request with { IdempotencyKey = string.Empty, Listings = canonicalUploadListings }, JsonOptions));
        var now = DateTimeOffset.UtcNow;
        string observationId;

        await using (var connection = await connectionFactory.OpenConnectionAsync(cancellationToken))
        await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken))
        {
            await using (var existing = connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText = """
                    SELECT observation_id, request_hash
                    FROM market_evidence_observations
                    WHERE account_id = $accountId
                      AND (idempotency_key = $key OR (source_kind = $sourceKind AND occurrence_id = $occurrenceId))
                    """;
                existing.Parameters.AddWithValue("$accountId", accountId);
                existing.Parameters.AddWithValue("$key", request.IdempotencyKey.Trim());
                existing.Parameters.AddWithValue("$sourceKind", request.SourceKind.Trim());
                existing.Parameters.AddWithValue("$occurrenceId", request.OccurrenceId.Trim());
                await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    observationId = reader.GetString(0);
                    if (!reader.GetString(1).Equals(requestHash, StringComparison.Ordinal))
                        throw new MarketEvidenceIdempotencyConflictException();
                    await transaction.CommitAsync(cancellationToken);
                    var existingRevision = await GetRevisionAsync(accountId, cancellationToken);
                    return new MarketEvidenceReceipt
                    {
                        ObservationId = observationId,
                        PayloadHash = payloadHash,
                        Duplicate = true,
                        ProjectionRevision = existingRevision,
                    };
                }
            }

            observationId = $"mi-{now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..34];
            await using (var payload = connection.CreateCommand())
            {
                payload.Transaction = transaction;
                payload.CommandText = "INSERT OR IGNORE INTO market_evidence_payloads(payload_hash, listing_count, listings_json, created_at_utc) VALUES($hash, $count, $json, $now)";
                payload.Parameters.AddWithValue("$hash", payloadHash);
                payload.Parameters.AddWithValue("$count", canonicalListings.Length);
                payload.Parameters.AddWithValue("$json", listingsJson);
                payload.Parameters.AddWithValue("$now", now.ToString("O"));
                await payload.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var observation = connection.CreateCommand())
            {
                observation.Transaction = transaction;
                observation.CommandText = """
                    INSERT INTO market_evidence_observations(
                        observation_id, account_id, idempotency_key, occurrence_id,
                        source_kind, source_version, source_instance_id, source_build, capture_mode,
                        item_id, item_name, data_center, world_name,
                        observed_at_utc, received_at_utc, coverage,
                        reported_listing_count, listing_capacity, is_truncated,
                        source_freshness, payload_hash, request_hash, aggregate_json, provenance_json)
                    VALUES(
                        $observationId, $accountId, $key, $occurrenceId,
                        $sourceKind, $sourceVersion, $sourceInstanceId, $sourceBuild, $captureMode,
                        $itemId, $itemName, $dataCenter, $worldName,
                        $observedAtUtc, $receivedAtUtc, $coverage,
                        $reportedListingCount, $listingCapacity, $isTruncated,
                        $sourceFreshness, $payloadHash, $requestHash, $aggregateJson, $provenanceJson)
                    """;
                observation.Parameters.AddWithValue("$observationId", observationId);
                observation.Parameters.AddWithValue("$accountId", accountId);
                observation.Parameters.AddWithValue("$key", request.IdempotencyKey.Trim());
                observation.Parameters.AddWithValue("$occurrenceId", request.OccurrenceId.Trim());
                observation.Parameters.AddWithValue("$sourceKind", request.SourceKind.Trim());
                observation.Parameters.AddWithValue("$sourceVersion", request.SourceVersion.Trim());
                observation.Parameters.AddWithValue("$sourceInstanceId", Db(request.SourceInstanceId));
                observation.Parameters.AddWithValue("$sourceBuild", Db(request.SourceBuild));
                observation.Parameters.AddWithValue("$captureMode", Db(request.CaptureMode));
                observation.Parameters.AddWithValue("$itemId", checked((long)request.ItemId));
                observation.Parameters.AddWithValue("$itemName", Db(request.ItemName));
                observation.Parameters.AddWithValue("$dataCenter", request.DataCenter.Trim());
                observation.Parameters.AddWithValue("$worldName", request.WorldName.Trim());
                observation.Parameters.AddWithValue("$observedAtUtc", request.ObservedAtUtc.ToString("O"));
                observation.Parameters.AddWithValue("$receivedAtUtc", now.ToString("O"));
                observation.Parameters.AddWithValue("$coverage", request.Coverage);
                observation.Parameters.AddWithValue("$reportedListingCount", Db(request.ReportedListingCount));
                observation.Parameters.AddWithValue("$listingCapacity", Db(request.ListingCapacity));
                observation.Parameters.AddWithValue("$isTruncated", request.IsTruncated.HasValue ? request.IsTruncated.Value ? 1 : 0 : DBNull.Value);
                observation.Parameters.AddWithValue("$sourceFreshness", Db(request.SourceFreshness));
                observation.Parameters.AddWithValue("$payloadHash", payloadHash);
                observation.Parameters.AddWithValue("$requestHash", requestHash);
                observation.Parameters.AddWithValue("$aggregateJson", request.Aggregate is null ? DBNull.Value : JsonSerializer.Serialize(request.Aggregate, JsonOptions));
                observation.Parameters.AddWithValue("$provenanceJson", Db(request.ProvenanceJson));
                await observation.ExecuteNonQueryAsync(cancellationToken);
            }

            await PersistActorListingEvidenceAsync(
                connection,
                transaction,
                accountId,
                observationId,
                request,
                canonicalListings,
                cancellationToken);

            await using (var outbox = connection.CreateCommand())
            {
                outbox.Transaction = transaction;
                outbox.CommandText = "INSERT INTO market_intelligence_outbox(account_id, observation_id, status, created_at_utc) VALUES($accountId, $observationId, $status, $now)";
                outbox.Parameters.AddWithValue("$accountId", accountId);
                outbox.Parameters.AddWithValue("$observationId", observationId);
                outbox.Parameters.AddWithValue("$status", projectImmediately ? "Pending" : "Deferred");
                outbox.Parameters.AddWithValue("$now", now.ToString("O"));
                await outbox.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        var revision = await GetRevisionAsync(accountId, cancellationToken);
        return new MarketEvidenceReceipt
        {
            ObservationId = observationId,
            PayloadHash = payloadHash,
            Duplicate = false,
            ProjectionRevision = revision,
        };
    }

    private static MarketEvidenceListing NormalizeListing(long accountId, MarketEvidenceUploadListing listing) => new()
    {
        ListingId = listing.ListingId.Trim(),
        RetainerId = listing.RetainerId.Trim(),
        RetainerName = NormalizeOptional(listing.RetainerName),
        RetainerNameSource = NormalizeOptional(listing.RetainerNameSource),
        SellerOwnerActorKey = ActorKey(accountId, listing.SellerOwnerContentId),
        SellerOwnerIdentityState = IdentityState(listing.SellerOwnerContentId),
        ArtisanActorKey = ActorKey(accountId, listing.ArtisanContentId),
        ArtisanIdentityState = IdentityState(listing.ArtisanContentId),
        Quantity = listing.Quantity,
        UnitPrice = listing.UnitPrice,
        IsHq = listing.IsHq,
    };

    private static string? ActorKey(long accountId, ulong? contentId)
    {
        if (contentId is not > 0) return null;
        var hash = Sha256($"market-actor|{ActorKeyScheme}|{accountId.ToString(CultureInfo.InvariantCulture)}|{contentId.Value.ToString(CultureInfo.InvariantCulture)}");
        return $"actor-a1-{hash[..24]}";
    }

    private static string IdentityState(ulong? contentId) => contentId switch
    {
        null => MarketActorIdentityStates.NotCaptured,
        0 => MarketActorIdentityStates.Absent,
        _ => MarketActorIdentityStates.Observed,
    };

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task PersistActorListingEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long accountId,
        string observationId,
        MarketEvidenceUploadRequest request,
        IReadOnlyList<MarketEvidenceListing> listings,
        CancellationToken cancellationToken)
    {
        foreach (var listing in listings)
        {
            var selfCrafted = listing.SellerOwnerActorKey is not null &&
                              listing.SellerOwnerActorKey.Equals(listing.ArtisanActorKey, StringComparison.Ordinal);
            foreach (var actor in ActorRoles(listing))
            {
                await using (var upsertActor = connection.CreateCommand())
                {
                    upsertActor.Transaction = transaction;
                    upsertActor.CommandText = """
                        INSERT INTO market_actors(account_id, actor_key, key_scheme, first_observed_at_utc, last_observed_at_utc)
                        VALUES($accountId, $actorKey, $scheme, $observedAt, $observedAt)
                        ON CONFLICT(account_id, actor_key) DO UPDATE SET
                            first_observed_at_utc = MIN(first_observed_at_utc, excluded.first_observed_at_utc),
                            last_observed_at_utc = MAX(last_observed_at_utc, excluded.last_observed_at_utc)
                        """;
                    upsertActor.Parameters.AddWithValue("$accountId", accountId);
                    upsertActor.Parameters.AddWithValue("$actorKey", actor.ActorKey);
                    upsertActor.Parameters.AddWithValue("$scheme", ActorKeyScheme);
                    upsertActor.Parameters.AddWithValue("$observedAt", request.ObservedAtUtc.ToString("O"));
                    await upsertActor.ExecuteNonQueryAsync(cancellationToken);
                }

                await using var evidence = connection.CreateCommand();
                evidence.Transaction = transaction;
                evidence.CommandText = """
                    INSERT INTO market_actor_listing_evidence(
                        account_id, observation_id, listing_id, actor_key, role,
                        item_id, item_name, world_name, retainer_id, retainer_name,
                        observed_at_utc, is_self_crafted_sale)
                    VALUES(
                        $accountId, $observationId, $listingId, $actorKey, $role,
                        $itemId, $itemName, $worldName, $retainerId, $retainerName,
                        $observedAt, $selfCrafted)
                    """;
                evidence.Parameters.AddWithValue("$accountId", accountId);
                evidence.Parameters.AddWithValue("$observationId", observationId);
                evidence.Parameters.AddWithValue("$listingId", listing.ListingId);
                evidence.Parameters.AddWithValue("$actorKey", actor.ActorKey);
                evidence.Parameters.AddWithValue("$role", actor.Role);
                evidence.Parameters.AddWithValue("$itemId", checked((long)request.ItemId));
                evidence.Parameters.AddWithValue("$itemName", Db(request.ItemName));
                evidence.Parameters.AddWithValue("$worldName", request.WorldName.Trim());
                evidence.Parameters.AddWithValue("$retainerId", listing.RetainerId);
                evidence.Parameters.AddWithValue("$retainerName", Db(listing.RetainerName));
                evidence.Parameters.AddWithValue("$observedAt", request.ObservedAtUtc.ToString("O"));
                evidence.Parameters.AddWithValue("$selfCrafted", selfCrafted ? 1 : 0);
                await evidence.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    private static IEnumerable<(string ActorKey, string Role)> ActorRoles(MarketEvidenceListing listing)
    {
        if (listing.SellerOwnerActorKey is not null)
            yield return (listing.SellerOwnerActorKey, MarketActorRoles.SellerOwner);
        if (listing.ArtisanActorKey is not null)
            yield return (listing.ArtisanActorKey, MarketActorRoles.Artisan);
    }

    public async Task<int> ProjectPendingAsync(CancellationToken cancellationToken)
    {
        var accounts = new List<long>();
        await using (var connection = await connectionFactory.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT DISTINCT account_id FROM market_intelligence_outbox WHERE status = 'Pending' ORDER BY account_id";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                accounts.Add(reader.GetInt64(0));
        }

        foreach (var account in accounts)
            await ProjectAccountAsync(account, cancellationToken);
        return accounts.Count;
    }

    public async Task<long> ProjectDeferredAccountAsync(long accountId, CancellationToken cancellationToken)
    {
        await using (var connection = await connectionFactory.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE market_intelligence_outbox SET status = 'Pending' WHERE account_id = $accountId AND status = 'Deferred'";
            command.Parameters.AddWithValue("$accountId", accountId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        return await ProjectAccountAsync(accountId, cancellationToken);
    }

    public async Task<MarketIntelligenceLedgerView> GetLedgerAsync(
        IReadOnlyList<long> accountIds,
        CancellationToken cancellationToken)
    {
        var rows = new List<MarketIntelligenceMarketRow>();
        long revision = 0;
        var classifierVersion = ClassifierVersion;
        DateTimeOffset? updated = null;
        foreach (var accountId in accountIds.Distinct())
        {
            await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT current.revision, current.updated_at_utc, generation.classifier_version, rows.row_json,
                       annotations.note, annotations.reviewed
                FROM market_intelligence_current_projection current
                JOIN market_intelligence_projection_generations generation
                  ON generation.generation_id = current.generation_id
                JOIN market_intelligence_market_rows rows
                  ON rows.generation_id = current.generation_id AND rows.account_id = current.account_id
                LEFT JOIN market_intelligence_annotations annotations
                  ON annotations.account_id = rows.account_id
                 AND annotations.item_id = rows.item_id
                 AND lower(annotations.world_name) = lower(rows.world_name)
                WHERE current.account_id = $accountId
                ORDER BY json_extract(rows.row_json, '$.itemName'), rows.world_name
                """;
            command.Parameters.AddWithValue("$accountId", accountId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                revision = Math.Max(revision, reader.GetInt64(0));
                var rowUpdated = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
                updated = updated == null || rowUpdated > updated ? rowUpdated : updated;
                classifierVersion = reader.GetString(2);
                var row = JsonSerializer.Deserialize<MarketIntelligenceMarketRow>(reader.GetString(3), JsonOptions)!;
                rows.Add(row with
                {
                    Note = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Reviewed = !reader.IsDBNull(5) && reader.GetInt64(5) != 0,
                });
            }
        }

        return new MarketIntelligenceLedgerView
        {
            Revision = revision,
            ClassifierVersion = classifierVersion,
            UpdatedAtUtc = updated,
            Rows = rows,
        };
    }

    public async Task<MarketIntelligenceMarketDetailView?> GetDetailAsync(
        IReadOnlyList<long> accountIds,
        uint itemId,
        string worldName,
        CancellationToken cancellationToken)
    {
        var observations = new List<MarketIntelligenceObservationView>();
        string itemName = string.Empty;
        foreach (var accountId in accountIds.Distinct())
        {
            await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT o.observation_id, o.occurrence_id, o.source_kind, o.source_version,
                       o.source_build, o.capture_mode, o.item_name, o.observed_at_utc, o.received_at_utc, o.coverage,
                       o.reported_listing_count, o.listing_capacity, o.is_truncated,
                       o.payload_hash, o.aggregate_json, p.listings_json
                FROM market_evidence_observations o
                JOIN market_evidence_payloads p ON p.payload_hash = o.payload_hash
                WHERE o.account_id = $accountId AND o.item_id = $itemId
                  AND lower(o.world_name) = lower($worldName)
                ORDER BY o.observed_at_utc DESC, o.received_at_utc DESC
                """;
            command.Parameters.AddWithValue("$accountId", accountId);
            command.Parameters.AddWithValue("$itemId", checked((long)itemId));
            command.Parameters.AddWithValue("$worldName", worldName.Trim());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                itemName = reader.IsDBNull(6) ? itemName : reader.GetString(6);
                observations.Add(new MarketIntelligenceObservationView
                {
                    ObservationId = reader.GetString(0),
                    OccurrenceId = reader.GetString(1),
                    SourceKind = reader.GetString(2),
                    SourceVersion = reader.GetString(3),
                    SourceBuild = reader.IsDBNull(4) ? null : reader.GetString(4),
                    CaptureMode = reader.IsDBNull(5) ? null : reader.GetString(5),
                    ObservedAtUtc = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                    ReceivedAtUtc = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
                    Coverage = reader.GetString(9),
                    ReportedListingCount = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                    ListingCapacity = reader.IsDBNull(11) ? null : reader.GetInt32(11),
                    IsTruncated = reader.IsDBNull(12) ? null : reader.GetInt64(12) != 0,
                    PayloadHash = reader.GetString(13),
                    Aggregate = reader.IsDBNull(14) ? null : JsonSerializer.Deserialize<MarketEvidenceAggregate>(reader.GetString(14), JsonOptions),
                    Listings = JsonSerializer.Deserialize<MarketEvidenceListing[]>(reader.GetString(15), JsonOptions) ?? [],
                });
            }
        }

        return observations.Count == 0 ? null : new MarketIntelligenceMarketDetailView
        {
            ItemId = itemId,
            ItemName = itemName,
            WorldName = worldName,
            Observations = observations,
        };
    }

    public async Task UpdateAnnotationAsync(
        long accountId,
        uint itemId,
        string worldName,
        MarketIntelligenceAnnotationUpdate update,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(worldName))
            throw new ArgumentException("World name is required.");
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO market_intelligence_annotations(account_id, item_id, world_name, note, reviewed, updated_at_utc)
            VALUES($accountId, $itemId, $worldName, $note, $reviewed, $now)
            ON CONFLICT(account_id, item_id, world_name) DO UPDATE SET
                note = excluded.note, reviewed = excluded.reviewed, updated_at_utc = excluded.updated_at_utc
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$itemId", checked((long)itemId));
        command.Parameters.AddWithValue("$worldName", worldName.Trim());
        command.Parameters.AddWithValue("$note", Db(update.Note));
        command.Parameters.AddWithValue("$reviewed", update.Reviewed ? 1 : 0);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<MarketActorNameObservationReceipt> RecordActorNameAsync(
        long accountId,
        MarketActorNameObservationUploadRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SchemaVersion != 1) throw new ArgumentException("Actor name observation schema version must be 1.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)) throw new ArgumentException("Actor name observation idempotency key is required.");
        if (request.ContentId == 0) throw new ArgumentException("Actor name observation content id must be nonzero.");
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 64) throw new ArgumentException("Actor name must contain at most 64 characters.");
        if (string.IsNullOrWhiteSpace(request.ResolutionMethod) || request.ResolutionMethod.Trim().Length > 64) throw new ArgumentException("Actor name resolution method is required.");
        if (request.ObservedAtUtc == default) throw new ArgumentException("Actor name observation time is required.");
        if (request.ObservedAtUtc > DateTimeOffset.UtcNow.AddMinutes(10)) throw new ArgumentException("Actor name observation time is implausibly far in the future.");

        var actorKey = ActorKey(accountId, request.ContentId)!;
        var normalizedSourceObservationId = NormalizeOptional(request.SourceObservationId);
        var now = DateTimeOffset.UtcNow;
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT name_observation_id, actor_key, name, resolution_method, observed_at_utc, source_observation_id FROM market_actor_name_observations WHERE account_id = $accountId AND idempotency_key = $key";
            existing.Parameters.AddWithValue("$accountId", accountId);
            existing.Parameters.AddWithValue("$key", request.IdempotencyKey.Trim());
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.GetString(1).Equals(actorKey, StringComparison.Ordinal) ||
                    !reader.GetString(2).Equals(request.Name.Trim(), StringComparison.Ordinal) ||
                    !reader.GetString(3).Equals(request.ResolutionMethod.Trim(), StringComparison.Ordinal) ||
                    !reader.GetString(4).Equals(request.ObservedAtUtc.ToString("O"), StringComparison.Ordinal) ||
                    !string.Equals(reader.IsDBNull(5) ? null : reader.GetString(5), normalizedSourceObservationId, StringComparison.Ordinal))
                    throw new MarketEvidenceIdempotencyConflictException();
                var existingId = reader.GetString(0);
                await transaction.CommitAsync(cancellationToken);
                return new() { ActorKey = actorKey, NameObservationId = existingId, Duplicate = true };
            }
        }

        if (normalizedSourceObservationId is not null)
        {
            await using var source = connection.CreateCommand();
            source.Transaction = transaction;
            source.CommandText = "SELECT COUNT(*) FROM market_evidence_observations WHERE account_id = $accountId AND observation_id = $observationId";
            source.Parameters.AddWithValue("$accountId", accountId);
            source.Parameters.AddWithValue("$observationId", normalizedSourceObservationId);
            if ((long)(await source.ExecuteScalarAsync(cancellationToken))! == 0)
                throw new ArgumentException("Actor name source observation is unavailable for this account.");
        }

        await using (var actor = connection.CreateCommand())
        {
            actor.Transaction = transaction;
            actor.CommandText = """
                INSERT INTO market_actors(account_id, actor_key, key_scheme, first_observed_at_utc, last_observed_at_utc)
                VALUES($accountId, $actorKey, $scheme, $observedAt, $observedAt)
                ON CONFLICT(account_id, actor_key) DO UPDATE SET
                    first_observed_at_utc = MIN(first_observed_at_utc, excluded.first_observed_at_utc),
                    last_observed_at_utc = MAX(last_observed_at_utc, excluded.last_observed_at_utc)
                """;
            actor.Parameters.AddWithValue("$accountId", accountId);
            actor.Parameters.AddWithValue("$actorKey", actorKey);
            actor.Parameters.AddWithValue("$scheme", ActorKeyScheme);
            actor.Parameters.AddWithValue("$observedAt", request.ObservedAtUtc.ToString("O"));
            await actor.ExecuteNonQueryAsync(cancellationToken);
        }

        var nameObservationId = $"man-{now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..35];
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO market_actor_name_observations(
                    name_observation_id, account_id, actor_key, idempotency_key, name,
                    resolution_method, observed_at_utc, received_at_utc, source_observation_id)
                VALUES($id, $accountId, $actorKey, $key, $name, $method, $observedAt, $receivedAt, $sourceObservationId)
                """;
            insert.Parameters.AddWithValue("$id", nameObservationId);
            insert.Parameters.AddWithValue("$accountId", accountId);
            insert.Parameters.AddWithValue("$actorKey", actorKey);
            insert.Parameters.AddWithValue("$key", request.IdempotencyKey.Trim());
            insert.Parameters.AddWithValue("$name", request.Name.Trim());
            insert.Parameters.AddWithValue("$method", request.ResolutionMethod.Trim());
            insert.Parameters.AddWithValue("$observedAt", request.ObservedAtUtc.ToString("O"));
            insert.Parameters.AddWithValue("$receivedAt", now.ToString("O"));
            insert.Parameters.AddWithValue("$sourceObservationId", Db(normalizedSourceObservationId));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new() { ActorKey = actorKey, NameObservationId = nameObservationId, Duplicate = false };
    }

    public async Task<MarketActorDetailView?> GetActorDetailAsync(
        IReadOnlyList<long> accountIds,
        string actorKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actorKey)) return null;
        foreach (var accountId in accountIds.Distinct())
        {
            await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
            await using var actor = connection.CreateCommand();
            actor.CommandText = """
                SELECT first_observed_at_utc, last_observed_at_utc,
                       (SELECT name FROM market_actor_name_observations n WHERE n.account_id = a.account_id AND n.actor_key = a.actor_key ORDER BY observed_at_utc DESC, received_at_utc DESC LIMIT 1),
                       (SELECT COUNT(DISTINCT observation_id) FROM market_actor_listing_evidence e WHERE e.account_id = a.account_id AND e.actor_key = a.actor_key),
                       (SELECT COUNT(DISTINCT observation_id || '|' || listing_id) FROM market_actor_listing_evidence e WHERE e.account_id = a.account_id AND e.actor_key = a.actor_key),
                       (SELECT COUNT(DISTINCT world_name || '|' || retainer_id) FROM market_actor_listing_evidence e WHERE e.account_id = a.account_id AND e.actor_key = a.actor_key AND role = 'SellerOwner'),
                       (SELECT COUNT(DISTINCT observation_id || '|' || listing_id) FROM market_actor_listing_evidence e WHERE e.account_id = a.account_id AND e.actor_key = a.actor_key AND role = 'Artisan'),
                       (SELECT COUNT(DISTINCT observation_id || '|' || listing_id) FROM market_actor_listing_evidence e WHERE e.account_id = a.account_id AND e.actor_key = a.actor_key AND role = 'SellerOwner'),
                       (SELECT COUNT(DISTINCT observation_id || '|' || listing_id) FROM market_actor_listing_evidence e WHERE e.account_id = a.account_id AND e.actor_key = a.actor_key AND is_self_crafted_sale = 1)
                FROM market_actors a WHERE account_id = $accountId AND actor_key = $actorKey
                """;
            actor.Parameters.AddWithValue("$accountId", accountId);
            actor.Parameters.AddWithValue("$actorKey", actorKey.Trim());
            await using var actorReader = await actor.ExecuteReaderAsync(cancellationToken);
            if (!await actorReader.ReadAsync(cancellationToken)) continue;
            var summary = new MarketActorSummaryView
            {
                ActorKey = actorKey.Trim(),
                FirstObservedAtUtc = DateTimeOffset.Parse(actorReader.GetString(0), CultureInfo.InvariantCulture),
                LastObservedAtUtc = DateTimeOffset.Parse(actorReader.GetString(1), CultureInfo.InvariantCulture),
                CurrentName = actorReader.IsDBNull(2) ? null : actorReader.GetString(2),
                ObservationCount = actorReader.GetInt32(3),
                ListingCount = actorReader.GetInt32(4),
                RetainerCount = actorReader.GetInt32(5),
                CraftedListingCount = actorReader.GetInt32(6),
                SoldListingCount = actorReader.GetInt32(7),
                SelfCraftedListingCount = actorReader.GetInt32(8),
            };
            await actorReader.DisposeAsync();

            var names = new List<MarketActorNameObservationView>();
            await using (var namesCommand = connection.CreateCommand())
            {
                namesCommand.CommandText = "SELECT name, resolution_method, observed_at_utc, source_observation_id FROM market_actor_name_observations WHERE account_id = $accountId AND actor_key = $actorKey ORDER BY observed_at_utc DESC, received_at_utc DESC";
                namesCommand.Parameters.AddWithValue("$accountId", accountId);
                namesCommand.Parameters.AddWithValue("$actorKey", actorKey.Trim());
                await using var reader = await namesCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) names.Add(new()
                {
                    Name = reader.GetString(0), ResolutionMethod = reader.GetString(1),
                    ObservedAtUtc = DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                    SourceObservationId = reader.IsDBNull(3) ? null : reader.GetString(3),
                });
            }

            var listings = new List<MarketActorListingEvidenceView>();
            await using (var listingsCommand = connection.CreateCommand())
            {
                listingsCommand.CommandText = "SELECT observation_id, listing_id, role, item_id, item_name, world_name, retainer_id, retainer_name, observed_at_utc, is_self_crafted_sale FROM market_actor_listing_evidence WHERE account_id = $accountId AND actor_key = $actorKey ORDER BY observed_at_utc DESC, observation_id DESC, listing_id";
                listingsCommand.Parameters.AddWithValue("$accountId", accountId);
                listingsCommand.Parameters.AddWithValue("$actorKey", actorKey.Trim());
                await using var reader = await listingsCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) listings.Add(new()
                {
                    ObservationId = reader.GetString(0), ListingId = reader.GetString(1), Role = reader.GetString(2),
                    ItemId = checked((uint)reader.GetInt64(3)), ItemName = reader.IsDBNull(4) ? $"Item {reader.GetInt64(3)}" : reader.GetString(4),
                    WorldName = reader.GetString(5), RetainerId = reader.GetString(6), RetainerName = reader.IsDBNull(7) ? null : reader.GetString(7),
                    ObservedAtUtc = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture), IsSelfCraftedSale = reader.GetInt64(9) != 0,
                });
            }
            return new() { Actor = summary, Names = names, Listings = listings };
        }
        return null;
    }

    public async Task RecordImportReceiptAsync(
        long accountId,
        MarketIntelligenceImportReceiptRequest receipt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(receipt.SourcePathHash) || string.IsNullOrWhiteSpace(receipt.SourceFingerprint))
            throw new ArgumentException("Import receipt source hashes are required.");
        if (receipt.Status is not ("Imported" or "Quarantined"))
            throw new ArgumentException("Import receipt status must be Imported or Quarantined.");
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO market_intelligence_import_receipts(
                account_id, source_path_hash, source_fingerprint, status,
                imported_observations, error, updated_at_utc)
            VALUES($accountId, $pathHash, $fingerprint, $status, $count, $error, $now)
            ON CONFLICT(account_id, source_path_hash) DO UPDATE SET
                source_fingerprint = excluded.source_fingerprint,
                status = excluded.status,
                imported_observations = excluded.imported_observations,
                error = excluded.error,
                updated_at_utc = excluded.updated_at_utc
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        command.Parameters.AddWithValue("$pathHash", receipt.SourcePathHash.Trim());
        command.Parameters.AddWithValue("$fingerprint", receipt.SourceFingerprint.Trim());
        command.Parameters.AddWithValue("$status", receipt.Status);
        command.Parameters.AddWithValue("$count", receipt.ImportedObservations);
        command.Parameters.AddWithValue("$error", Db(receipt.Error));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<long> RebuildAccountAsync(
        long accountId,
        string classifierVersion,
        bool failBeforePublish,
        CancellationToken cancellationToken) =>
        ProjectAccountAsync(accountId, cancellationToken, classifierVersion, failBeforePublish);

    private async Task<long> ProjectAccountAsync(
        long accountId,
        CancellationToken cancellationToken,
        string classifierVersion = ClassifierVersion,
        bool failBeforePublish = false)
    {
        var evidence = await LoadEvidenceAsync(accountId, cancellationToken);
        var rows = evidence
            .GroupBy(x => (x.ItemId, World: x.WorldName.ToUpperInvariant()))
            .Select(group => BuildRow(group, classifierVersion))
            .OrderBy(x => x.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.WorldName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var now = DateTimeOffset.UtcNow;
        var generationId = $"mig-{now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..35];

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        long revision;
        await using (var revisionCommand = connection.CreateCommand())
        {
            revisionCommand.Transaction = transaction;
            revisionCommand.CommandText = "SELECT COALESCE(MAX(revision), 0) + 1 FROM market_intelligence_projection_generations WHERE account_id = $accountId";
            revisionCommand.Parameters.AddWithValue("$accountId", accountId);
            revision = (long)(await revisionCommand.ExecuteScalarAsync(cancellationToken))!;
        }

        await using (var generation = connection.CreateCommand())
        {
            generation.Transaction = transaction;
            generation.CommandText = "INSERT INTO market_intelligence_projection_generations(generation_id, account_id, classifier_version, revision, status, created_at_utc) VALUES($id, $accountId, $version, $revision, 'Building', $now)";
            generation.Parameters.AddWithValue("$id", generationId);
            generation.Parameters.AddWithValue("$accountId", accountId);
            generation.Parameters.AddWithValue("$version", classifierVersion);
            generation.Parameters.AddWithValue("$revision", revision);
            generation.Parameters.AddWithValue("$now", now.ToString("O"));
            await generation.ExecuteNonQueryAsync(cancellationToken);
        }

        if (failBeforePublish)
        {
            await using var fail = connection.CreateCommand();
            fail.Transaction = transaction;
            fail.CommandText = """
                UPDATE market_intelligence_projection_generations
                SET status = 'Failed', error = 'Configured projection failure.'
                WHERE generation_id = $generationId;
                UPDATE market_intelligence_outbox
                SET attempts = attempts + 1, last_error = 'Configured projection failure.'
                WHERE account_id = $accountId AND status = 'Pending';
                """;
            fail.Parameters.AddWithValue("$generationId", generationId);
            fail.Parameters.AddWithValue("$accountId", accountId);
            await fail.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await GetRevisionAsync(accountId, cancellationToken);
        }

        foreach (var row in rows)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO market_intelligence_market_rows(generation_id, account_id, item_id, world_name, row_json) VALUES($generationId, $accountId, $itemId, $worldName, $rowJson)";
            insert.Parameters.AddWithValue("$generationId", generationId);
            insert.Parameters.AddWithValue("$accountId", accountId);
            insert.Parameters.AddWithValue("$itemId", checked((long)row.ItemId));
            insert.Parameters.AddWithValue("$worldName", row.WorldName);
            insert.Parameters.AddWithValue("$rowJson", JsonSerializer.Serialize(row, JsonOptions));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var publish = connection.CreateCommand())
        {
            publish.Transaction = transaction;
            publish.CommandText = """
                UPDATE market_intelligence_projection_generations
                SET status = 'Published', published_at_utc = $now WHERE generation_id = $generationId;
                INSERT INTO market_intelligence_current_projection(account_id, generation_id, revision, updated_at_utc)
                VALUES($accountId, $generationId, $revision, $now)
                ON CONFLICT(account_id) DO UPDATE SET
                    generation_id = excluded.generation_id,
                    revision = excluded.revision,
                    updated_at_utc = excluded.updated_at_utc;
                UPDATE market_intelligence_outbox
                SET status = 'Completed', attempts = attempts + 1, completed_at_utc = $now, last_error = NULL
                WHERE account_id = $accountId AND status = 'Pending';
                """;
            publish.Parameters.AddWithValue("$now", now.ToString("O"));
            publish.Parameters.AddWithValue("$generationId", generationId);
            publish.Parameters.AddWithValue("$accountId", accountId);
            publish.Parameters.AddWithValue("$revision", revision);
            await publish.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return revision;
    }

    private async Task<List<Evidence>> LoadEvidenceAsync(long accountId, CancellationToken cancellationToken)
    {
        var result = new List<Evidence>();
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.observation_id, o.source_kind, o.item_id, o.item_name, o.data_center, o.world_name,
                   o.observed_at_utc, o.coverage, o.reported_listing_count, o.listing_capacity,
                   o.is_truncated, o.aggregate_json, p.listings_json
            FROM market_evidence_observations o
            JOIN market_evidence_payloads p ON p.payload_hash = o.payload_hash
            WHERE o.account_id = $accountId
            ORDER BY o.observed_at_utc, o.received_at_utc, o.observation_id
            """;
        command.Parameters.AddWithValue("$accountId", accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new Evidence(
                reader.GetString(0), reader.GetString(1), checked((uint)reader.GetInt64(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture), reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetInt64(10) != 0,
                reader.IsDBNull(11) ? null : JsonSerializer.Deserialize<MarketEvidenceAggregate>(reader.GetString(11), JsonOptions),
                JsonSerializer.Deserialize<MarketEvidenceListing[]>(reader.GetString(12), JsonOptions) ?? []));
        }
        return result;
    }

    private static MarketIntelligenceMarketRow BuildRow(
        IGrouping<(uint ItemId, string World), Evidence> group,
        string classifierVersion)
    {
        var ordered = group.OrderBy(x => x.ObservedAtUtc).ThenBy(x => x.ObservationId).ToArray();
        var latest = ordered[^1];
        var measuredBooks = ordered.Where(x => x.Coverage is MarketEvidenceCoverage.Complete or MarketEvidenceCoverage.Partial or MarketEvidenceCoverage.LegacyMissing or MarketEvidenceCoverage.Empty).ToArray();
        var latestBook = measuredBooks.LastOrDefault() ?? latest;
        var previous = measuredBooks.Length > 1 ? measuredBooks[^2] : null;
        var metrics = Measure(latestBook.Listings, latestBook.Aggregate);
        var previousIds = previous?.Listings.Select(x => x.ListingId).Where(x => x.Length > 0).ToHashSet(StringComparer.Ordinal) ?? [];
        var latestIds = latestBook.Listings.Select(x => x.ListingId).Where(x => x.Length > 0).ToHashSet(StringComparer.Ordinal);
        var deep = measuredBooks.Where(x => x.Listings.Count > 0).Select(x => (Evidence: x, Metrics: Measure(x.Listings, x.Aggregate)))
            .Where(x => x.Metrics.ListingCount >= 80 && x.Metrics.DominantShelfShare >= .40)
            .ToArray();
        var bulk = deep.Where(x => x.Metrics.FullStackShare >= .80 && x.Metrics.TopTwoSellerShare >= .35).ToArray();
        var findings = new List<MarketIntelligenceFinding>();
        if (deep.Length > 0)
            findings.Add(Finding("DeepDominantShelf", classifierVersion, deep.Select(x => x.Evidence.ObservationId), $"{deep.Length} observed book{Plural(deep.Length)} with at least 80 listings and 40% of visible quantity on one price."));
        if (bulk.Length > 0)
            findings.Add(Finding("BulkShelfDominance", classifierVersion, bulk.Select(x => x.Evidence.ObservationId), $"{bulk.Length} deep book{Plural(bulk.Length)} also concentrated in full stacks and the two largest sellers."));
        var bulkDays = bulk.Select(x => x.Evidence.ObservedAtUtc.Date).Distinct().Count();
        if (bulkDays >= 2)
            findings.Add(Finding("RepeatedBulkShelfDominance", classifierVersion, bulk.Select(x => x.Evidence.ObservationId), $"Bulk shelf dominance recurred on {bulkDays} distinct dates."));
        if (previous != null && previousIds.Count > 0 && latestIds.Count > 0)
        {
            var removed = previousIds.Except(latestIds).Count();
            var added = latestIds.Except(previousIds).Count();
            if (removed > 0 && added > 0 && IsComplete(previous) && IsComplete(latestBook))
                findings.Add(Finding("ReplacementDepth", classifierVersion, [previous.ObservationId, latestBook.ObservationId], $"Between the last two complete books, {removed} listings left and {added} newly visible listings replaced depth."));
        }
        if (IsComplete(latestBook) && metrics.SellerOwnerIdentityCoverage >= .80 && metrics.TopTwoSellerOwnerShare >= .35)
            findings.Add(Finding("SellerOwnerConcentration", classifierVersion, [latestBook.ObservationId], $"The two largest known seller owners account for {metrics.TopTwoSellerOwnerShare:P0} of visible listings, with owner identity present on {metrics.SellerOwnerIdentityCoverage:P0}."));
        if (IsComplete(latestBook) && metrics.ArtisanIdentityCoverage >= .80 && metrics.TopTwoArtisanShare >= .35)
            findings.Add(Finding("ProducerConcentration", classifierVersion, [latestBook.ObservationId], $"The two largest known makers account for {metrics.TopTwoArtisanShare:P0} of visible listings, with maker identity present on {metrics.ArtisanIdentityCoverage:P0}."));
        if (IsComplete(latestBook) && metrics.SellerOwnerIdentityCoverage >= .80 && metrics.MultiRetainerOwnerCount > 0)
            findings.Add(Finding("MultiRetainerOwner", classifierVersion, [latestBook.ObservationId], $"{metrics.MultiRetainerOwnerCount} known seller owner{Plural(metrics.MultiRetainerOwnerCount)} supply this book through multiple retainers."));
        if (IsComplete(latestBook) && metrics.SellerOwnerIdentityCoverage >= .80 && metrics.ArtisanIdentityCoverage >= .80 && metrics.SelfCraftedListingShare >= .50)
            findings.Add(Finding("SelfCraftedSupply", classifierVersion, [latestBook.ObservationId], $"Known seller-owner and maker identities match on {metrics.SelfCraftedListingShare:P0} of visible listings."));
        var sellerEvidence = bulk.Length >= 2
            ? bulk.Select(x => x.Evidence)
            : ordered.Where(IsComplete);
        var completeSellerSets = sellerEvidence
            .Select(x => x.Listings.Select(y => y.RetainerId).Where(y => y.Length > 0).ToHashSet(StringComparer.Ordinal))
            .Where(x => x.Count > 0).ToArray();
        if (completeSellerSets.Length >= 2)
        {
            var persistent = new HashSet<string>(completeSellerSets[0], StringComparer.Ordinal);
            foreach (var set in completeSellerSets.Skip(1)) persistent.IntersectWith(set);
            if (persistent.Count > 0)
                findings.Add(Finding("SellerPersistence", classifierVersion, sellerEvidence.Select(x => x.ObservationId), $"{persistent.Count} world-scoped seller ID{Plural(persistent.Count)} recur across every qualifying observed book."));
        }
        var producerBooks = ordered.Where(IsComplete)
            .Select(x => (Evidence: x, Metrics: Measure(x.Listings, x.Aggregate)))
            .Where(x => x.Metrics.ArtisanIdentityCoverage >= .80)
            .ToArray();
        if (producerBooks.Length >= 2)
        {
            var persistent = producerBooks[0].Evidence.Listings.Select(x => x.ArtisanActorKey).OfType<string>().ToHashSet(StringComparer.Ordinal);
            foreach (var book in producerBooks.Skip(1))
                persistent.IntersectWith(book.Evidence.Listings.Select(x => x.ArtisanActorKey).OfType<string>());
            if (persistent.Count > 0)
                findings.Add(Finding("ProducerPersistence", classifierVersion, producerBooks.Select(x => x.Evidence.ObservationId), $"{persistent.Count} protected maker identit{(persistent.Count == 1 ? "y" : "ies")} recur across every identity-complete book."));
        }
        if (previous is not null && IsComplete(previous) && IsComplete(latestBook) &&
            Measure(previous.Listings, previous.Aggregate).ArtisanIdentityCoverage >= .80 && metrics.ArtisanIdentityCoverage >= .80)
        {
            var removedProducers = previous.Listings.Where(x => !latestIds.Contains(x.ListingId)).Select(x => x.ArtisanActorKey).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var addedProducers = latestBook.Listings.Where(x => !previousIds.Contains(x.ListingId)).Select(x => x.ArtisanActorKey).OfType<string>().ToHashSet(StringComparer.Ordinal);
            removedProducers.IntersectWith(addedProducers);
            if (removedProducers.Count > 0)
                findings.Add(Finding("ProducerReplacement", classifierVersion, [previous.ObservationId, latestBook.ObservationId], $"{removedProducers.Count} maker identit{(removedProducers.Count == 1 ? "y" : "ies")} appear on newly visible listings after their earlier listings left the complete book."));
        }
        var prices = latestBook.Listings.Select(x => x.UnitPrice).Distinct().Order().ToArray();
        if (prices.Length > 1)
        {
            var cliff = prices.Zip(prices.Skip(1), (a, b) => (From: a, To: b, Gap: (long)b - a)).OrderByDescending(x => x.Gap).First();
            if (cliff.Gap >= Math.Max(100, cliff.From / 4))
                findings.Add(Finding("PriceShelfCliff", classifierVersion, [latestBook.ObservationId], $"Largest visible price gap is {cliff.Gap:N0} gil, from {cliff.From:N0} to {cliff.To:N0}."));
        }
        if (latestBook.IsTruncated == true || (latestBook.ListingCapacity > 0 && latestBook.Listings.Count >= latestBook.ListingCapacity))
            findings.Add(Finding("VisibleDepthLowerBound", classifierVersion, [latestBook.ObservationId], $"The visible {latestBook.Listings.Count:N0} listings are a lower bound because the book reached its capture limit."));

        var previousQuantity = previous?.Listings.Sum(x => (long)x.Quantity) ?? 0;
        return new MarketIntelligenceMarketRow
        {
            ItemId = latestBook.ItemId,
            ItemName = ordered.LastOrDefault(x => !string.IsNullOrWhiteSpace(x.ItemName))?.ItemName ?? $"Item {latest.ItemId}",
            WorldName = latestBook.WorldName,
            DataCenter = latestBook.DataCenter,
            ObservationCount = ordered.Length,
            DistinctDays = ordered.Select(x => x.ObservedAtUtc.Date).Distinct().Count(),
            FirstObservedAtUtc = ordered[0].ObservedAtUtc,
            LastObservedAtUtc = latest.ObservedAtUtc,
            LatestCoverage = latest.Coverage,
            SourceKinds = ordered.Select(x => x.SourceKind).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            VisibleListings = metrics.ListingCount,
            VisibleQuantity = metrics.Quantity,
            LowestUnitPrice = metrics.LowestPrice,
            HighestUnitPrice = metrics.HighestPrice,
            DistinctSellers = metrics.SellerCount,
            DistinctSellerOwners = metrics.SellerOwnerCount,
            DistinctArtisans = metrics.ArtisanCount,
            SellerOwnerIdentityCoverage = metrics.SellerOwnerIdentityCoverage,
            ArtisanIdentityCoverage = metrics.ArtisanIdentityCoverage,
            TopTwoSellerOwnerShare = metrics.TopTwoSellerOwnerShare,
            TopTwoArtisanShare = metrics.TopTwoArtisanShare,
            SelfCraftedListingShare = metrics.SelfCraftedListingShare,
            MultiRetainerOwnerCount = metrics.MultiRetainerOwnerCount,
            FullStackShare = metrics.FullStackShare,
            TopTwoSellerShare = metrics.TopTwoSellerShare,
            DominantPriceShelfShare = metrics.DominantShelfShare,
            DominantPriceShelf = metrics.DominantShelf,
            AddedListings = previous == null ? 0 : latestIds.Except(previousIds).Count(),
            RemovedListings = previous == null ? 0 : previousIds.Except(latestIds).Count(),
            VisibleQuantityChange = previous == null ? 0 : metrics.Quantity - previousQuantity,
            Findings = findings,
        };
    }

    private static Metrics Measure(IReadOnlyList<MarketEvidenceListing> listings, MarketEvidenceAggregate? aggregate = null)
    {
        if (listings.Count == 0)
            return aggregate is null ? new Metrics() : new Metrics(
                aggregate.VisibleListingCount ?? 0,
                aggregate.VisibleQuantity ?? 0,
                aggregate.LowestUnitPrice,
                aggregate.HighestUnitPrice);
        var sellerCounts = listings.Where(x => x.RetainerId.Length > 0)
            .GroupBy(x => x.RetainerId, StringComparer.Ordinal).Select(x => x.Count()).OrderDescending().ToArray();
        var sellerOwnerCounts = listings.Where(x => x.SellerOwnerActorKey is not null)
            .GroupBy(x => x.SellerOwnerActorKey!, StringComparer.Ordinal).Select(x => x.Count()).OrderDescending().ToArray();
        var artisanCounts = listings.Where(x => x.ArtisanActorKey is not null)
            .GroupBy(x => x.ArtisanActorKey!, StringComparer.Ordinal).Select(x => x.Count()).OrderDescending().ToArray();
        var multiRetainerOwners = listings.Where(x => x.SellerOwnerActorKey is not null && x.RetainerId.Length > 0)
            .GroupBy(x => x.SellerOwnerActorKey!, StringComparer.Ordinal)
            .Count(group => group.Select(x => x.RetainerId).Distinct(StringComparer.Ordinal).Count() > 1);
        var selfCrafted = listings.Count(x => x.SellerOwnerActorKey is not null && x.SellerOwnerActorKey.Equals(x.ArtisanActorKey, StringComparison.Ordinal));
        var shelves = listings.GroupBy(x => x.UnitPrice)
            .Select(x => (Price: x.Key, Quantity: x.Sum(y => (long)y.Quantity))).OrderByDescending(x => x.Quantity).ThenBy(x => x.Price).ToArray();
        var quantity = listings.Sum(x => (long)x.Quantity);
        return new Metrics(
            listings.Count,
            quantity,
            listings.Min(x => x.UnitPrice),
            listings.Max(x => x.UnitPrice),
            sellerCounts.Length,
            listings.Count(x => x.Quantity == 99) / (double)listings.Count,
            sellerCounts.Take(2).Sum() / (double)listings.Count,
            quantity == 0 ? 0 : shelves[0].Quantity / (double)quantity,
            shelves[0].Price,
            sellerOwnerCounts.Length,
            artisanCounts.Length,
            sellerOwnerCounts.Sum() / (double)listings.Count,
            artisanCounts.Sum() / (double)listings.Count,
            sellerOwnerCounts.Take(2).Sum() / (double)listings.Count,
            artisanCounts.Take(2).Sum() / (double)listings.Count,
            selfCrafted / (double)listings.Count,
            multiRetainerOwners);
    }

    private async Task<long> GetRevisionAsync(long accountId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(revision, 0) FROM market_intelligence_current_projection WHERE account_id = $accountId";
        command.Parameters.AddWithValue("$accountId", accountId);
        return (long?)await command.ExecuteScalarAsync(cancellationToken) ?? 0;
    }

    private static void Validate(MarketEvidenceUploadRequest request)
    {
        if (request.SchemaVersion is not (1 or 2)) throw new ArgumentException("Only market evidence schema versions 1 and 2 are supported.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)) throw new ArgumentException("Idempotency key is required.");
        if (string.IsNullOrWhiteSpace(request.OccurrenceId)) throw new ArgumentException("Occurrence ID is required.");
        if (!MarketEvidenceSourceRegistry.TryGet(request.SourceKind, out var source)) throw new ArgumentException("Unsupported market evidence source kind.");
        if (string.IsNullOrWhiteSpace(request.SourceVersion)) throw new ArgumentException("Source version is required.");
        if (!SupportedCoverage.Contains(request.Coverage)) throw new ArgumentException("Unsupported market evidence coverage state.");
        if (request.ItemId == 0) throw new ArgumentException("Item ID is required.");
        if (string.IsNullOrWhiteSpace(request.WorldName)) throw new ArgumentException("World name is required.");
        if (request.ObservedAtUtc == default) throw new ArgumentException("Observed timestamp is required.");
        if (request.ObservedAtUtc > DateTimeOffset.UtcNow.AddMinutes(10)) throw new ArgumentException("Observed timestamp is implausibly far in the future.");
        if (request.ReportedListingCount < 0 || request.ListingCapacity < 0) throw new ArgumentException("Listing counts cannot be negative.");
        if (request.Coverage == MarketEvidenceCoverage.Complete && request.IsTruncated == true)
            throw new ArgumentException("A truncated observation cannot declare complete coverage.");
        if ((request.Coverage is MarketEvidenceCoverage.Complete or MarketEvidenceCoverage.Empty) &&
            !MarketEvidenceSourceRegistry.Has(source, "CompleteReadEvidence") &&
            !MarketEvidenceSourceRegistry.Has(source, "CompleteReadEvidenceWhenCaptured"))
            throw new ArgumentException($"{request.SourceKind} cannot declare exhaustive local-book coverage.");
        if ((request.Coverage is MarketEvidenceCoverage.Empty or MarketEvidenceCoverage.Unavailable or MarketEvidenceCoverage.AggregateOnly) && request.Listings.Count > 0)
            throw new ArgumentException($"{request.Coverage} evidence cannot contain detailed listings.");
        if (request.Listings.Count > 0 && !MarketEvidenceSourceRegistry.Has(source, "DetailedListings"))
            throw new ArgumentException($"{request.SourceKind} cannot submit detailed listings.");
        if (request.Coverage == MarketEvidenceCoverage.AggregateOnly && request.Aggregate is null)
            throw new ArgumentException("Aggregate-only evidence requires aggregate measurements.");
        if (request.Listings.GroupBy(x => x.ListingId, StringComparer.Ordinal).Any(x => x.Key.Length > 0 && x.Count() > 1))
            throw new ArgumentException("Listing IDs must be unique within an observation.");
        if (request.Listings.Any(x => string.IsNullOrWhiteSpace(x.ListingId) || x.Quantity == 0 || x.UnitPrice == 0))
            throw new ArgumentException("Detailed listings require an ID, positive quantity, and positive unit price.");
        if (request.SchemaVersion == 1 && request.Listings.Any(x => x.SellerOwnerContentId.HasValue || x.ArtisanContentId.HasValue || !string.IsNullOrWhiteSpace(x.RetainerNameSource)))
            throw new ArgumentException("Market evidence schema version 1 cannot assert actor identity or name provenance.");
        if (request.Listings.Any(x => x.SellerOwnerContentId.HasValue) &&
            !MarketEvidenceSourceRegistry.Has(source, "SellerOwnerContentIds") &&
            !MarketEvidenceSourceRegistry.Has(source, "SellerOwnerContentIdsWhenCorrelated"))
            throw new ArgumentException($"{request.SourceKind} cannot submit seller-owner content identities.");
        if (request.Listings.Any(x => x.ArtisanContentId.HasValue) &&
            !MarketEvidenceSourceRegistry.Has(source, "ArtisanContentIds") &&
            !MarketEvidenceSourceRegistry.Has(source, "ArtisanContentIdsWhenCorrelated"))
            throw new ArgumentException($"{request.SourceKind} cannot submit artisan content identities.");
        if (request.Listings.Any(x => !string.IsNullOrWhiteSpace(x.RetainerNameSource)) &&
            !MarketEvidenceSourceRegistry.Has(source, "SellerNameProvenance"))
            throw new ArgumentException($"{request.SourceKind} cannot submit seller-name provenance.");
        if (request.Aggregate?.VisibleQuantity < 0 || request.Aggregate?.VisibleListingCount < 0)
            throw new ArgumentException("Aggregate listing measurements cannot be negative.");
    }

    private static bool IsComplete(Evidence evidence) =>
        evidence.Coverage.Equals(MarketEvidenceCoverage.Complete, StringComparison.Ordinal) && evidence.IsTruncated != true;
    private static MarketIntelligenceFinding Finding(string kind, string classifierVersion, IEnumerable<string> observationIds, string summary)
    {
        var consumed = observationIds.Distinct(StringComparer.Ordinal).ToArray();
        return new()
        {
            Kind = kind,
            ClassifierVersion = classifierVersion,
            ObservationId = consumed.LastOrDefault() ?? string.Empty,
            ObservationIds = consumed,
            Summary = summary,
        };
    }
    private static string Plural(int count) => count == 1 ? string.Empty : "s";
    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static object Db(object? value) => value ?? DBNull.Value;

    private sealed record Evidence(
        string ObservationId, string SourceKind, uint ItemId, string? ItemName, string DataCenter,
        string WorldName, DateTimeOffset ObservedAtUtc, string Coverage, int? ReportedListingCount,
        int? ListingCapacity, bool? IsTruncated, MarketEvidenceAggregate? Aggregate,
        IReadOnlyList<MarketEvidenceListing> Listings);
    private sealed record Metrics(
        int ListingCount = 0, long Quantity = 0, uint? LowestPrice = null, uint? HighestPrice = null,
        int SellerCount = 0, double FullStackShare = 0, double TopTwoSellerShare = 0,
        double DominantShelfShare = 0, uint? DominantShelf = null,
        int SellerOwnerCount = 0, int ArtisanCount = 0,
        double SellerOwnerIdentityCoverage = 0, double ArtisanIdentityCoverage = 0,
        double TopTwoSellerOwnerShare = 0, double TopTwoArtisanShare = 0,
        double SelfCraftedListingShare = 0, int MultiRetainerOwnerCount = 0);
}

public sealed class MarketEvidenceIdempotencyConflictException : Exception
{
    public MarketEvidenceIdempotencyConflictException() : base("The idempotency key is already bound to different market evidence.") { }
}
