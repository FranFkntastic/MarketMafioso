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
    public const string ClassifierVersion = "market-intelligence-v1";
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
        var canonicalListings = request.Listings
            .OrderBy(x => x.ListingId, StringComparer.Ordinal)
            .ThenBy(x => x.UnitPrice)
            .ThenBy(x => x.Quantity)
            .ToArray();
        var listingsJson = JsonSerializer.Serialize(canonicalListings, JsonOptions);
        var payloadHash = Sha256(listingsJson);
        var requestHash = Sha256(JsonSerializer.Serialize(request with { IdempotencyKey = string.Empty, Listings = canonicalListings }, JsonOptions));
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
            shelves[0].Price);
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
        if (request.SchemaVersion != 1) throw new ArgumentException("Only market evidence schema version 1 is supported.");
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
        double DominantShelfShare = 0, uint? DominantShelf = null);
}

public sealed class MarketEvidenceIdempotencyConflictException : Exception
{
    public MarketEvidenceIdempotencyConflictException() : base("The idempotency key is already bound to different market evidence.") { }
}
