using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketMafioso.Server.Persistence;
using Microsoft.Data.Sqlite;
using static MarketMafioso.Server.MarketAcquisition.MarketAcquisitionRequestPolicy;
using static MarketMafioso.Server.Persistence.MarketAcquisitionEventPersistence;
using static MarketMafioso.Server.Persistence.MarketAcquisitionLinePersistence;
using static MarketMafioso.Server.Persistence.MarketAcquisitionRecordMapper;
using static MarketMafioso.Server.Persistence.MarketAcquisitionRequestQueries;

namespace MarketMafioso.Server;

public sealed class MarketAcquisitionRequestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string connectionString;
    private readonly int minimumExpirySeconds;
    private readonly int maximumExpirySeconds;
    private readonly int claimExpirySeconds;

    public MarketAcquisitionRequestStore(IHostEnvironment environment, IConfiguration configuration)
    {
        var dataDirectory = Path.Combine(environment.ContentRootPath, "data");
        Directory.CreateDirectory(dataDirectory);
        var databasePath = Path.Combine(dataDirectory, "marketmafioso.db");
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
        }.ToString();
        minimumExpirySeconds = Math.Max(
            1,
            configuration.GetValue("MarketMafioso:AcquisitionMinimumExpirySeconds", 30));
        maximumExpirySeconds = Math.Max(
            minimumExpirySeconds,
            configuration.GetValue("MarketMafioso:AcquisitionMaximumExpirySeconds", 86400));
        claimExpirySeconds = Math.Max(
            1,
            configuration.GetValue("MarketMafioso:AcquisitionClaimExpirySeconds", 300));

        MarketAcquisitionSchema.Initialize(connectionString);
    }

    public async Task<MarketAcquisitionCreateResult> CreateAsync(
        MarketAcquisitionCreateRequest request,
        CancellationToken cancellationToken)
    {
        ValidateCreateRequest(request);

        return await CreateBatchAsync(ToBatchCreateRequest(request), cancellationToken).ConfigureAwait(false);
    }

    public async Task<MarketAcquisitionCreateResult> CreateBatchAsync(
        MarketAcquisitionBatchCreateRequest request,
        CancellationToken cancellationToken)
    {
        ValidateBatchCreateRequest(request);

        var now = DateTimeOffset.UtcNow;
        var expirySeconds = ClampPickupExpirySeconds(request.ExpiresInSeconds);
        var payloadJson = JsonSerializer.Serialize(request, JsonOptions);
        var primaryRequest = ToPrimaryCreateRequest(request);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var existing = await GetByIdempotencyKeyAsync(
            connection,
            request.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (existing != null)
        {
            if (!string.Equals(existing.Value.PayloadJson, payloadJson, StringComparison.Ordinal))
                throw new MarketAcquisitionIdempotencyConflictException();

            return new MarketAcquisitionCreateResult(existing.Value.View, true);
        }

        var view = ToView(
            id: $"{now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..26],
            revision: 1,
            status: MarketAcquisitionStatuses.PendingPickup,
            createdAtUtc: now,
            expiresAtUtc: now.AddSeconds(expirySeconds),
            claimedAtUtc: null,
            claimExpiresAtUtc: null,
            primaryRequest,
            latestEvent: null);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            INSERT INTO acquisition_requests (
                id,
                idempotency_key,
                status,
                created_at_utc,
                expires_at_utc,
                target_character_name,
                target_world,
                payload_json
            )
            VALUES (
                $id,
                $idempotencyKey,
                $status,
                $createdAtUtc,
                $expiresAtUtc,
                $targetCharacterName,
                $targetWorld,
                $payloadJson
            );
            """;
        command.Parameters.AddWithValue("$id", view.Id);
        command.Parameters.AddWithValue("$idempotencyKey", request.IdempotencyKey);
        command.Parameters.AddWithValue("$status", view.Status);
        command.Parameters.AddWithValue("$createdAtUtc", view.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$expiresAtUtc", view.ExpiresAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$targetCharacterName", request.TargetCharacterName.Trim());
        command.Parameters.AddWithValue("$targetWorld", request.TargetWorld.Trim());
        command.Parameters.AddWithValue("$payloadJson", payloadJson);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var lines = await InsertBatchLinesAsync(
            connection,
            transaction,
            view.Id,
            request.Lines,
            now,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new MarketAcquisitionCreateResult(view with { Lines = lines }, false);
    }

    public async Task<MarketAcquisitionRequestView?> AppendLinesAsync(
        string id,
        MarketAcquisitionBatchAppendLinesRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Request id is required.", nameof(id));
        ValidateBatchAppendLinesRequest(request);

        await ExpirePendingAsync(cancellationToken).ConfigureAwait(false);
        await ExpireClaimedAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var current = await GetByIdAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
        if (current == null)
            return null;

        if (!string.Equals(current.Status, MarketAcquisitionStatuses.PendingPickup, StringComparison.Ordinal))
            throw new MarketAcquisitionInvalidTransitionException(current.Status, MarketAcquisitionStatuses.PendingPickup);
        if (current.Revision != request.ExpectedRevision)
            throw new MarketAcquisitionRevisionConflictException(request.ExpectedRevision, current.Revision);

        var lines = current.Lines.Count == 1 && current.Lines[0].LineId.EndsWith("-fallback", StringComparison.Ordinal)
            ? []
            : current.Lines.ToList();
        var nextOrdinal = lines.Count == 0 ? 0 : lines.Max(line => line.Ordinal) + 1;

        foreach (var incoming in request.Lines)
        {
            var existing = lines.FirstOrDefault(line => CanCoalesce(line, incoming));
            if (existing == null)
            {
                var appended = await InsertBatchLineAsync(
                    connection,
                    transaction,
                    id,
                    incoming,
                    nextOrdinal++,
                    now,
                    cancellationToken).ConfigureAwait(false);
                lines.Add(appended);
                continue;
            }

            var coalesced = CoalesceLine(existing, incoming);
            await UpdateLineIntentAsync(
                connection,
                transaction,
                coalesced,
                now,
                cancellationToken).ConfigureAwait(false);
            var index = lines.FindIndex(line => string.Equals(line.LineId, existing.LineId, StringComparison.Ordinal));
            lines[index] = coalesced;
        }

        var requestedExpiresAtUtc = now.AddSeconds(ClampPickupExpirySeconds(request.ExpiresInSeconds));
        var expiresAtUtc = requestedExpiresAtUtc > current.ExpiresAtUtc
            ? requestedExpiresAtUtc
            : current.ExpiresAtUtc;
        var nextRevision = current.Revision + 1;

        await using var update = connection.CreateCommand();
        update.Transaction = (SqliteTransaction)transaction;
        update.CommandText =
            """
            UPDATE acquisition_requests
            SET revision = $revision,
                expires_at_utc = $expiresAtUtc
            WHERE id = $id
              AND revision = $expectedRevision
              AND status = $pendingStatus;
            """;
        update.Parameters.AddWithValue("$revision", nextRevision);
        update.Parameters.AddWithValue("$expiresAtUtc", expiresAtUtc.ToString("O"));
        update.Parameters.AddWithValue("$id", id);
        update.Parameters.AddWithValue("$expectedRevision", request.ExpectedRevision);
        update.Parameters.AddWithValue("$pendingStatus", MarketAcquisitionStatuses.PendingPickup);

        var affected = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 1)
            throw new MarketAcquisitionRevisionConflictException(request.ExpectedRevision, current.Revision);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return current with
        {
            Revision = nextRevision,
            ExpiresAtUtc = expiresAtUtc,
            Lines = lines.OrderBy(line => line.Ordinal).ToList(),
        };
    }

    public async Task<MarketAcquisitionRequestView?> ReplaceBatchAsync(
        string id,
        MarketAcquisitionBatchReplaceRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Request id is required.", nameof(id));
        ValidateBatchReplaceRequest(request);

        await ExpirePendingAsync(cancellationToken).ConfigureAwait(false);
        await ExpireClaimedAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var current = await GetByIdAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
        if (current == null)
            return null;

        EnsureCanReplaceBatch(current);
        if (current.Revision != request.ExpectedRevision)
            throw new MarketAcquisitionRevisionConflictException(request.ExpectedRevision, current.Revision);

        await DeleteBatchLinesAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
        var lines = await InsertBatchLinesAsync(
            connection,
            transaction,
            id,
            request.Lines,
            now,
            cancellationToken).ConfigureAwait(false);

        var nextRevision = current.Revision + 1;
        var expiresAtUtc = now.AddSeconds(ClampPickupExpirySeconds(request.ExpiresInSeconds));
        var replacementPayload = BuildReplacementPayloadJson(current, request);

        await using var update = connection.CreateCommand();
        update.Transaction = (SqliteTransaction)transaction;
        update.CommandText =
            """
            UPDATE acquisition_requests
            SET revision = $revision,
                expires_at_utc = $expiresAtUtc,
                payload_json = $payloadJson
            WHERE id = $id
              AND revision = $expectedRevision;
            """;
        update.Parameters.AddWithValue("$revision", nextRevision);
        update.Parameters.AddWithValue("$expiresAtUtc", expiresAtUtc.ToString("O"));
        update.Parameters.AddWithValue("$payloadJson", replacementPayload);
        update.Parameters.AddWithValue("$id", id);
        update.Parameters.AddWithValue("$expectedRevision", request.ExpectedRevision);

        var affected = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 1)
            throw new MarketAcquisitionRevisionConflictException(request.ExpectedRevision, current.Revision);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return current with
        {
            Revision = nextRevision,
            ExpiresAtUtc = expiresAtUtc,
            Region = NormalizeSupportedRegion(request.Region, nameof(request)),
            WorldMode = request.WorldMode.Trim(),
            SweepScope = string.IsNullOrWhiteSpace(request.SweepScope) ? "Region" : request.SweepScope.Trim(),
            SweepDataCenters = NormalizeSweepDataCenters(request.Region, request.SweepDataCenters),
            Lines = lines.OrderBy(line => line.Ordinal).ToList(),
        };
    }

    public async Task<MarketAcquisitionRequestView?> GetAsync(
        string id,
        CancellationToken cancellationToken)
    {
        await ExpirePendingAsync(cancellationToken).ConfigureAwait(false);
        await ExpireClaimedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await GetByIdAsync(connection, transaction: null, id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MarketAcquisitionRequestView>> ListPendingAsync(
        string characterName,
        string world,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(characterName))
            throw new ArgumentException("Character name is required.", nameof(characterName));
        if (string.IsNullOrWhiteSpace(world))
            throw new ArgumentException("World is required.", nameof(world));

        await ExpirePendingAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                requests.id,
                requests.revision,
                requests.status,
                requests.created_at_utc,
                requests.expires_at_utc,
                requests.claimed_at_utc,
                requests.claim_expires_at_utc,
                requests.payload_json,
                events.event_type,
                events.payload_json,
                events.created_at_utc,
                attempt_events.attempt_id,
                attempt_events.sequence,
                attempt_events.event_type,
                attempt_events.phase,
                attempt_events.world_name,
                attempt_events.result,
                attempt_events.plugin_version
            FROM acquisition_requests AS requests
            LEFT JOIN acquisition_request_events AS events
              ON events.id = (
                SELECT latest.id
                FROM acquisition_request_events AS latest
                WHERE latest.request_id = requests.id
                ORDER BY latest.id DESC
                LIMIT 1
              )
            LEFT JOIN acquisition_attempt_events AS attempt_events
              ON attempt_events.id = (
                SELECT latest_attempt.id
                FROM acquisition_attempt_events AS latest_attempt
                WHERE latest_attempt.request_id = requests.id
                ORDER BY latest_attempt.id DESC
                LIMIT 1
              )
            WHERE requests.status = $status
              AND lower(requests.target_character_name) = lower($targetCharacterName)
              AND lower(requests.target_world) = lower($targetWorld)
            ORDER BY requests.created_at_utc ASC;
            """;
        command.Parameters.AddWithValue("$status", MarketAcquisitionStatuses.PendingPickup);
        command.Parameters.AddWithValue("$targetCharacterName", characterName.Trim());
        command.Parameters.AddWithValue("$targetWorld", world.Trim());

        var requests = new List<MarketAcquisitionRequestView>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            requests.Add(ReadView(reader));

        await reader.DisposeAsync().ConfigureAwait(false);
        return await PopulateLinesAsync(connection, requests, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MarketAcquisitionRequestView>> ListRecentAsync(
        int limit,
        bool includeTerminal,
        CancellationToken cancellationToken)
    {
        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be one or greater.");

        await ExpirePendingAsync(cancellationToken).ConfigureAwait(false);
        await ExpireClaimedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                requests.id,
                requests.revision,
                requests.status,
                requests.created_at_utc,
                requests.expires_at_utc,
                requests.claimed_at_utc,
                requests.claim_expires_at_utc,
                requests.payload_json,
                events.event_type,
                events.payload_json,
                events.created_at_utc,
                attempt_events.attempt_id,
                attempt_events.sequence,
                attempt_events.event_type,
                attempt_events.phase,
                attempt_events.world_name,
                attempt_events.result,
                attempt_events.plugin_version
            FROM acquisition_requests AS requests
            LEFT JOIN acquisition_request_events AS events
              ON events.id = (
                SELECT latest.id
                FROM acquisition_request_events AS latest
                WHERE latest.request_id = requests.id
                ORDER BY latest.id DESC
                LIMIT 1
              )
            LEFT JOIN acquisition_attempt_events AS attempt_events
              ON attempt_events.id = (
                SELECT latest_attempt.id
                FROM acquisition_attempt_events AS latest_attempt
                WHERE latest_attempt.request_id = requests.id
                ORDER BY latest_attempt.id DESC
                LIMIT 1
              )
            WHERE $includeTerminal = 1
               OR requests.status NOT IN ($completeStatus, $failedStatus, $cancelledStatus, $rejectedStatus, $expiredStatus)
            ORDER BY requests.created_at_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$includeTerminal", includeTerminal ? 1 : 0);
        command.Parameters.AddWithValue("$completeStatus", MarketAcquisitionStatuses.Complete);
        command.Parameters.AddWithValue("$failedStatus", MarketAcquisitionStatuses.Failed);
        command.Parameters.AddWithValue("$cancelledStatus", MarketAcquisitionStatuses.Cancelled);
        command.Parameters.AddWithValue("$rejectedStatus", MarketAcquisitionStatuses.Rejected);
        command.Parameters.AddWithValue("$expiredStatus", MarketAcquisitionStatuses.Expired);

        var requests = new List<MarketAcquisitionRequestView>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            requests.Add(ReadView(reader));

        await reader.DisposeAsync().ConfigureAwait(false);
        return await PopulateLinesAsync(connection, requests, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MarketAcquisitionRequestTimelineView?> GetTimelineAsync(
        string id,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Request id is required.", nameof(id));

        await ExpirePendingAsync(cancellationToken).ConfigureAwait(false);
        await ExpireClaimedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var request = await GetByIdAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
        if (request == null)
            return null;

        var lifecycleEvents = await ListLifecycleEventsAsync(
            connection,
            transaction,
            id,
            cancellationToken).ConfigureAwait(false);
        var attemptEvents = await ListAttemptEventsAsync(
            connection,
            transaction,
            id,
            cancellationToken).ConfigureAwait(false);

        return new MarketAcquisitionRequestTimelineView
        {
            Request = request,
            LifecycleEvents = lifecycleEvents,
            AttemptEvents = attemptEvents,
        };
    }

    public async Task<MarketAcquisitionClaimView?> ClaimAsync(
        string id,
        MarketAcquisitionClaimRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CharacterName) ||
            string.IsNullOrWhiteSpace(request.World) ||
            string.IsNullOrWhiteSpace(request.PluginInstanceId))
            throw new ArgumentException("Claim requires character, world, and plugin instance.");

        await ExpirePendingAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var claimToken = CreateSecretToken();
        var claimExpiresAtUtc = now.AddSeconds(claimExpirySeconds);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var view = await GetForClaimAsync(
            connection,
            transaction,
            id,
            request.CharacterName,
            request.World,
            cancellationToken).ConfigureAwait(false);
        if (view == null)
            return null;

        await using var update = connection.CreateCommand();
        update.Transaction = (SqliteTransaction)transaction;
        update.CommandText =
            """
            UPDATE acquisition_requests
            SET status = $status,
                claimed_at_utc = $claimedAtUtc,
                claim_expires_at_utc = $claimExpiresAtUtc,
                claim_token = $claimToken,
                claimed_by = $claimedBy
            WHERE id = $id
              AND status = $pendingStatus;
            """;
        update.Parameters.AddWithValue("$status", MarketAcquisitionStatuses.Claimed);
        update.Parameters.AddWithValue("$claimedAtUtc", now.ToString("O"));
        update.Parameters.AddWithValue("$claimExpiresAtUtc", claimExpiresAtUtc.ToString("O"));
        update.Parameters.AddWithValue("$claimToken", claimToken);
        update.Parameters.AddWithValue("$claimedBy", request.PluginInstanceId.Trim());
        update.Parameters.AddWithValue("$id", id);
        update.Parameters.AddWithValue("$pendingStatus", MarketAcquisitionStatuses.PendingPickup);

        var affected = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 1)
            return null;

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToClaimView(view with
        {
            Status = MarketAcquisitionStatuses.Claimed,
            ClaimedAtUtc = now,
            ClaimExpiresAtUtc = claimExpiresAtUtc,
        }, claimToken);
    }

    public Task<MarketAcquisitionRequestView?> AcceptAsync(
        string id,
        MarketAcquisitionClaimTokenRequest request,
        CancellationToken cancellationToken) =>
        ApplyLifecycleAsync(
            id,
            "accept",
            MarketAcquisitionStatuses.AcceptedInPlugin,
            [MarketAcquisitionStatuses.Claimed],
            new MarketAcquisitionLifecycleRequest
            {
                ClaimToken = request.ClaimToken,
                IdempotencyKey = request.IdempotencyKey,
            },
            cancellationToken);

    public Task<MarketAcquisitionRequestView?> RejectAsync(
        string id,
        MarketAcquisitionLifecycleRequest request,
        CancellationToken cancellationToken) =>
        ApplyLifecycleAsync(
            id,
            "reject",
            MarketAcquisitionStatuses.Rejected,
            [MarketAcquisitionStatuses.Claimed],
            request,
            cancellationToken);

    public Task<MarketAcquisitionRequestView?> ReportProgressAsync(
        string id,
        MarketAcquisitionLifecycleRequest request,
        CancellationToken cancellationToken) =>
        ApplyLifecycleAsync(
            id,
            "progress",
            MarketAcquisitionStatuses.Running,
            [MarketAcquisitionStatuses.AcceptedInPlugin, MarketAcquisitionStatuses.Running],
            request,
            cancellationToken);

    public Task<MarketAcquisitionRequestView?> CompleteAsync(
        string id,
        MarketAcquisitionLifecycleRequest request,
        CancellationToken cancellationToken) =>
        ApplyLifecycleAsync(
            id,
            "complete",
            MarketAcquisitionStatuses.Complete,
            [MarketAcquisitionStatuses.AcceptedInPlugin, MarketAcquisitionStatuses.Running],
            request,
            cancellationToken);

    public Task<MarketAcquisitionRequestView?> FailAsync(
        string id,
        MarketAcquisitionLifecycleRequest request,
        CancellationToken cancellationToken) =>
        ApplyLifecycleAsync(
            id,
            "fail",
            MarketAcquisitionStatuses.Failed,
            [MarketAcquisitionStatuses.AcceptedInPlugin, MarketAcquisitionStatuses.Running],
            request,
            cancellationToken);

    public Task<MarketAcquisitionAttemptEventResult?> ReportAttemptProgressAsync(
        string id,
        MarketAcquisitionAttemptEventRequest request,
        CancellationToken cancellationToken) =>
        ApplyAttemptLifecycleAsync(
            id,
            MarketAcquisitionStatuses.Running,
            [MarketAcquisitionStatuses.AcceptedInPlugin, MarketAcquisitionStatuses.Running],
            request,
            cancellationToken);

    public Task<MarketAcquisitionAttemptEventResult?> CompleteAttemptAsync(
        string id,
        MarketAcquisitionAttemptEventRequest request,
        CancellationToken cancellationToken) =>
        ApplyAttemptLifecycleAsync(
            id,
            MarketAcquisitionStatuses.Complete,
            [MarketAcquisitionStatuses.AcceptedInPlugin, MarketAcquisitionStatuses.Running],
            request,
            cancellationToken);

    public Task<MarketAcquisitionAttemptEventResult?> FailAttemptAsync(
        string id,
        MarketAcquisitionAttemptEventRequest request,
        CancellationToken cancellationToken) =>
        ApplyAttemptLifecycleAsync(
            id,
            MarketAcquisitionStatuses.Failed,
            [MarketAcquisitionStatuses.AcceptedInPlugin, MarketAcquisitionStatuses.Running],
            request,
            cancellationToken);

    public async Task<MarketAcquisitionBatchLineView?> RecordLineProgressAsync(
        string requestId,
        string lineId,
        MarketAcquisitionLineProgressRequest request,
        CancellationToken cancellationToken)
    {
        ValidateLineProgressRequest(request);

        await ExpireClaimedAsync(cancellationToken).ConfigureAwait(false);

        var payloadJson = JsonSerializer.Serialize(request, JsonOptions);
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
        var eventCreatedAtUtc = DateTimeOffset.UtcNow;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var current = await GetByIdAsync(connection, transaction, requestId, cancellationToken).ConfigureAwait(false);
        if (current == null)
            return null;

        var storedClaimToken = await GetClaimTokenAsync(connection, transaction, requestId, cancellationToken).ConfigureAwait(false);
        if (!MatchesSecret(request.ClaimToken, storedClaimToken))
            throw new UnauthorizedAccessException("Claim token does not match.");

        await EnsureLineBelongsToRequestAsync(connection, transaction, requestId, lineId, cancellationToken).ConfigureAwait(false);

        var existingByKey = await GetLineProgressEventByIdempotencyKeyAsync(
            connection,
            transaction,
            request.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (existingByKey != null)
        {
            if (!string.Equals(existingByKey.Value.RequestId, requestId, StringComparison.Ordinal) ||
                !string.Equals(existingByKey.Value.LineId, lineId, StringComparison.Ordinal) ||
                !string.Equals(existingByKey.Value.AttemptId, request.AttemptId, StringComparison.Ordinal) ||
                existingByKey.Value.Sequence != request.Sequence ||
                !string.Equals(existingByKey.Value.PayloadJson, payloadJson, StringComparison.Ordinal))
                throw new MarketAcquisitionIdempotencyConflictException();

            return await LoadLineByIdAsync(connection, transaction, lineId, cancellationToken).ConfigureAwait(false);
        }

        var existingBySequence = await GetLineProgressEventBySequenceAsync(
            connection,
            transaction,
            requestId,
            lineId,
            request.AttemptId,
            request.Sequence,
            cancellationToken).ConfigureAwait(false);
        if (existingBySequence != null)
        {
            if (!string.Equals(existingBySequence.Value.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal) ||
                !string.Equals(existingBySequence.Value.PayloadJson, payloadJson, StringComparison.Ordinal))
                throw new MarketAcquisitionAttemptSequenceConflictException();

            return await LoadLineByIdAsync(connection, transaction, lineId, cancellationToken).ConfigureAwait(false);
        }

        if (current.Status is not (MarketAcquisitionStatuses.AcceptedInPlugin or MarketAcquisitionStatuses.Running))
            throw new MarketAcquisitionInvalidTransitionException(current.Status, MarketAcquisitionStatuses.Running);

        await using (var updateRequest = connection.CreateCommand())
        {
            updateRequest.Transaction = (SqliteTransaction)transaction;
            updateRequest.CommandText =
                """
                UPDATE acquisition_requests
                SET status = $status
                WHERE id = $requestId;
                """;
            updateRequest.Parameters.AddWithValue("$status", MarketAcquisitionStatuses.Running);
            updateRequest.Parameters.AddWithValue("$requestId", requestId);
            await updateRequest.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var updateLine = connection.CreateCommand())
        {
            updateLine.Transaction = (SqliteTransaction)transaction;
            updateLine.CommandText =
                """
                UPDATE acquisition_batch_lines
                SET status = $status,
                    purchased_quantity = $purchasedQuantity,
                    spent_gil = $spentGil,
                    latest_message = $latestMessage,
                    updated_at_utc = $updatedAtUtc
                WHERE request_id = $requestId
                  AND line_id = $lineId;
                """;
            updateLine.Parameters.AddWithValue("$status", request.Status);
            updateLine.Parameters.AddWithValue("$purchasedQuantity", request.PurchasedQuantity);
            updateLine.Parameters.AddWithValue("$spentGil", request.SpentGil);
            updateLine.Parameters.AddWithValue("$latestMessage", (object?)request.Message ?? DBNull.Value);
            updateLine.Parameters.AddWithValue("$updatedAtUtc", eventCreatedAtUtc.ToString("O"));
            updateLine.Parameters.AddWithValue("$requestId", requestId);
            updateLine.Parameters.AddWithValue("$lineId", lineId);
            await updateLine.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertLineProgressEventAsync(
            connection,
            transaction,
            requestId,
            lineId,
            request,
            payloadJson,
            payloadHash,
            eventCreatedAtUtc,
            cancellationToken).ConfigureAwait(false);

        var line = await LoadLineByIdAsync(connection, transaction, lineId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return line;
    }

    public async Task<MarketAcquisitionPurchaseAuditView?> RecordPurchaseAuditAsync(
        string requestId,
        MarketAcquisitionPurchaseAuditRequest request,
        CancellationToken cancellationToken)
    {
        ValidatePurchaseAuditRequest(request);

        await ExpireClaimedAsync(cancellationToken).ConfigureAwait(false);

        var payloadJson = JsonSerializer.Serialize(request, JsonOptions);
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
        var createdAtUtc = DateTimeOffset.UtcNow;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var current = await GetByIdAsync(connection, transaction, requestId, cancellationToken).ConfigureAwait(false);
        if (current == null)
            return null;

        var storedClaimToken = await GetClaimTokenAsync(connection, transaction, requestId, cancellationToken).ConfigureAwait(false);
        if (!MatchesSecret(request.ClaimToken, storedClaimToken))
            throw new UnauthorizedAccessException("Claim token does not match.");

        await EnsureLineBelongsToRequestAsync(connection, transaction, requestId, request.LineId, cancellationToken).ConfigureAwait(false);

        var existingByKey = await GetPurchaseAuditByIdempotencyKeyAsync(
            connection,
            transaction,
            request.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (existingByKey != null)
        {
            if (!string.Equals(existingByKey.Value.View.RequestId, requestId, StringComparison.Ordinal) ||
                !string.Equals(existingByKey.Value.PayloadJson, payloadJson, StringComparison.Ordinal))
                throw new MarketAcquisitionIdempotencyConflictException();

            return existingByKey.Value.View;
        }

        var existingBySequence = await GetPurchaseAuditBySequenceAsync(
            connection,
            transaction,
            requestId,
            request.AttemptId,
            request.Sequence,
            cancellationToken).ConfigureAwait(false);
        if (existingBySequence != null)
        {
            if (!string.Equals(existingBySequence.Value.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal) ||
                !string.Equals(existingBySequence.Value.PayloadJson, payloadJson, StringComparison.Ordinal))
                throw new MarketAcquisitionAttemptSequenceConflictException();

            return existingBySequence.Value.View;
        }

        var auditId = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..26];
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                INSERT INTO acquisition_purchase_audit (
                    audit_id,
                    request_id,
                    line_id,
                    attempt_id,
                    sequence,
                    idempotency_key,
                    world_name,
                    item_id,
                    item_name,
                    listing_id,
                    retainer_name,
                    retainer_id,
                    quantity,
                    unit_price,
                    total_gil,
                    is_hq,
                    result,
                    message,
                    payload_json,
                    payload_hash,
                    created_at_utc
                )
                VALUES (
                    $auditId,
                    $requestId,
                    $lineId,
                    $attemptId,
                    $sequence,
                    $idempotencyKey,
                    $worldName,
                    $itemId,
                    $itemName,
                    $listingId,
                    $retainerName,
                    $retainerId,
                    $quantity,
                    $unitPrice,
                    $totalGil,
                    $isHq,
                    $result,
                    $message,
                    $payloadJson,
                    $payloadHash,
                    $createdAtUtc
                );
                """;
            AddPurchaseAuditParameters(command, auditId, requestId, request, payloadJson, payloadHash, createdAtUtc);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var audit = await GetPurchaseAuditByIdAsync(connection, transaction, auditId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return audit;
    }

    public async Task<MarketAcquisitionRequestView?> CancelAsync(
        string id,
        CancellationToken cancellationToken)
    {
        await ExpirePendingAsync(cancellationToken).ConfigureAwait(false);
        await ExpireClaimedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var current = await GetByIdAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
        if (current == null)
            return null;

        if (current.Status is MarketAcquisitionStatuses.Complete
            or MarketAcquisitionStatuses.Failed
            or MarketAcquisitionStatuses.Cancelled)
            return current;

        await using var update = connection.CreateCommand();
        update.Transaction = (SqliteTransaction)transaction;
        update.CommandText =
            """
            UPDATE acquisition_requests
            SET status = $status,
                claimed_at_utc = NULL,
                claim_expires_at_utc = NULL,
                claim_token = NULL,
                claimed_by = NULL
            WHERE id = $id;
            """;
        update.Parameters.AddWithValue("$status", MarketAcquisitionStatuses.Cancelled);
        update.Parameters.AddWithValue("$id", id);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return current with
        {
            Status = MarketAcquisitionStatuses.Cancelled,
            ClaimedAtUtc = null,
            ClaimExpiresAtUtc = null,
        };
    }

    public async Task<MarketAcquisitionRequestView?> ResendAsync(
        string id,
        CancellationToken cancellationToken)
    {
        await ExpirePendingAsync(cancellationToken).ConfigureAwait(false);
        await ExpireClaimedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var current = await GetByIdAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
        if (current == null)
            return null;

        if (current.Status is MarketAcquisitionStatuses.Complete
            or MarketAcquisitionStatuses.Running)
            throw new MarketAcquisitionInvalidTransitionException(current.Status, MarketAcquisitionStatuses.PendingPickup);

        var expiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(ClampPickupExpirySeconds((int)current.ExpiresAtUtc.Subtract(current.CreatedAtUtc).TotalSeconds));

        await using var update = connection.CreateCommand();
        update.Transaction = (SqliteTransaction)transaction;
        update.CommandText =
            """
            UPDATE acquisition_requests
            SET status = $status,
                expires_at_utc = $expiresAtUtc,
                claimed_at_utc = NULL,
                claim_expires_at_utc = NULL,
                claim_token = NULL,
                claimed_by = NULL
            WHERE id = $id;
            """;
        update.Parameters.AddWithValue("$status", MarketAcquisitionStatuses.PendingPickup);
        update.Parameters.AddWithValue("$expiresAtUtc", expiresAtUtc.ToString("O"));
        update.Parameters.AddWithValue("$id", id);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return current with
        {
            Status = MarketAcquisitionStatuses.PendingPickup,
            ExpiresAtUtc = expiresAtUtc,
            ClaimedAtUtc = null,
            ClaimExpiresAtUtc = null,
        };
    }

    private int ClampPickupExpirySeconds(int expiresInSeconds) =>
        Math.Clamp(expiresInSeconds, minimumExpirySeconds, maximumExpirySeconds);

    private async Task<MarketAcquisitionRequestView?> ApplyLifecycleAsync(
        string id,
        string eventType,
        string targetStatus,
        IReadOnlyCollection<string> allowedSourceStatuses,
        MarketAcquisitionLifecycleRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ClaimToken))
            throw new UnauthorizedAccessException("Claim token is required.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Idempotency key is required.", nameof(request));

        await ExpireClaimedAsync(cancellationToken).ConfigureAwait(false);

        var payloadJson = JsonSerializer.Serialize(request, JsonOptions);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var current = await GetByIdAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
        if (current == null)
            return null;

        var storedClaimToken = await GetClaimTokenAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
        if (!MatchesSecret(request.ClaimToken, storedClaimToken))
            throw new UnauthorizedAccessException("Claim token does not match.");

        var existingEvent = await GetEventByIdempotencyKeyAsync(
            connection,
            transaction,
            request.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (existingEvent != null)
        {
            if (!string.Equals(existingEvent.Value.RequestId, id, StringComparison.Ordinal) ||
                !string.Equals(existingEvent.Value.EventType, eventType, StringComparison.Ordinal) ||
                !string.Equals(existingEvent.Value.PayloadJson, payloadJson, StringComparison.Ordinal))
                throw new MarketAcquisitionIdempotencyConflictException();

            return ApplyLatestLifecycleEvent(
                current,
                existingEvent.Value.ResultStatus,
                existingEvent.Value.EventType,
                existingEvent.Value.PayloadJson,
                DateTimeOffset.Parse(existingEvent.Value.CreatedAtUtc));
        }

        if (!allowedSourceStatuses.Contains(current.Status))
            throw new MarketAcquisitionInvalidTransitionException(current.Status, targetStatus);

        await using var update = connection.CreateCommand();
        update.Transaction = (SqliteTransaction)transaction;
        update.CommandText =
            """
            UPDATE acquisition_requests
            SET status = $status
            WHERE id = $id;
            """;
        update.Parameters.AddWithValue("$status", targetStatus);
        update.Parameters.AddWithValue("$id", id);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var eventCreatedAtUtc = DateTimeOffset.UtcNow;
        await using var insertEvent = connection.CreateCommand();
        insertEvent.Transaction = (SqliteTransaction)transaction;
        insertEvent.CommandText =
            """
            INSERT INTO acquisition_request_events (
                request_id,
                idempotency_key,
                event_type,
                payload_json,
                result_status,
                created_at_utc
            )
            VALUES (
                $requestId,
                $idempotencyKey,
                $eventType,
                $payloadJson,
                $resultStatus,
                $createdAtUtc
            );
            """;
        insertEvent.Parameters.AddWithValue("$requestId", id);
        insertEvent.Parameters.AddWithValue("$idempotencyKey", request.IdempotencyKey);
        insertEvent.Parameters.AddWithValue("$eventType", eventType);
        insertEvent.Parameters.AddWithValue("$payloadJson", payloadJson);
        insertEvent.Parameters.AddWithValue("$resultStatus", targetStatus);
        insertEvent.Parameters.AddWithValue("$createdAtUtc", eventCreatedAtUtc.ToString("O"));
        await insertEvent.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ApplyLatestLifecycleEvent(current, targetStatus, eventType, payloadJson, eventCreatedAtUtc);
    }

    private async Task<MarketAcquisitionAttemptEventResult?> ApplyAttemptLifecycleAsync(
        string id,
        string targetStatus,
        IReadOnlyCollection<string> allowedSourceStatuses,
        MarketAcquisitionAttemptEventRequest request,
        CancellationToken cancellationToken)
    {
        ValidateAttemptEventRequest(request);

        await ExpireClaimedAsync(cancellationToken).ConfigureAwait(false);

        var payloadJson = JsonSerializer.Serialize(request, JsonOptions);
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var current = await GetByIdAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
        if (current == null)
            return null;

        var storedClaimToken = await GetClaimTokenAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
        if (!MatchesSecret(request.ClaimToken, storedClaimToken))
            throw new UnauthorizedAccessException("Claim token does not match.");

        var existingByKey = await GetAttemptEventByIdempotencyKeyAsync(
            connection,
            transaction,
            request.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (existingByKey != null)
        {
            if (!string.Equals(existingByKey.Value.RequestId, id, StringComparison.Ordinal) ||
                !string.Equals(existingByKey.Value.AttemptId, request.AttemptId, StringComparison.Ordinal) ||
                existingByKey.Value.Sequence != request.EventSequence ||
                !string.Equals(existingByKey.Value.PayloadJson, payloadJson, StringComparison.Ordinal))
                throw new MarketAcquisitionIdempotencyConflictException();

            return new MarketAcquisitionAttemptEventResult
            {
                Request = ApplyLatestAttemptEvent(current, existingByKey.Value),
                Result = MarketAcquisitionAttemptEventResults.Replayed,
            };
        }

        var existingBySequence = await GetAttemptEventBySequenceAsync(
            connection,
            transaction,
            request.AttemptId,
            request.EventSequence,
            cancellationToken).ConfigureAwait(false);
        if (existingBySequence != null)
        {
            if (!string.Equals(existingBySequence.Value.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal) ||
                !string.Equals(existingBySequence.Value.PayloadJson, payloadJson, StringComparison.Ordinal))
                throw new MarketAcquisitionAttemptSequenceConflictException();

            return new MarketAcquisitionAttemptEventResult
            {
                Request = ApplyLatestAttemptEvent(current, existingBySequence.Value),
                Result = MarketAcquisitionAttemptEventResults.Replayed,
            };
        }

        var attemptExists = await AttemptExistsAsync(
            connection,
            transaction,
            request.AttemptId,
            cancellationToken).ConfigureAwait(false);
        var latestAttemptId = await GetLatestAttemptIdAsync(
            connection,
            transaction,
            id,
            cancellationToken).ConfigureAwait(false);
        var result = attemptExists &&
            latestAttemptId != null &&
            !string.Equals(latestAttemptId, request.AttemptId, StringComparison.Ordinal)
                ? MarketAcquisitionAttemptEventResults.StaleAttempt
                : MarketAcquisitionAttemptEventResults.Accepted;

        if (result == MarketAcquisitionAttemptEventResults.Accepted &&
            !allowedSourceStatuses.Contains(current.Status))
        {
            if (current.Status is MarketAcquisitionStatuses.Complete
                or MarketAcquisitionStatuses.Failed
                or MarketAcquisitionStatuses.Cancelled
                or MarketAcquisitionStatuses.Rejected
                or MarketAcquisitionStatuses.Expired)
            {
                result = MarketAcquisitionAttemptEventResults.RequestTerminal;
            }
            else
            {
                throw new MarketAcquisitionInvalidTransitionException(current.Status, targetStatus);
            }
        }

        var eventCreatedAtUtc = DateTimeOffset.UtcNow;
        await UpsertAttemptAsync(
            connection,
            transaction,
            id,
            targetStatus,
            request,
            result,
            eventCreatedAtUtc,
            cancellationToken).ConfigureAwait(false);

        if (result == MarketAcquisitionAttemptEventResults.Accepted)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText =
                """
                UPDATE acquisition_requests
                SET status = $status
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$status", targetStatus);
            update.Parameters.AddWithValue("$id", id);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertAttemptEventAsync(
            connection,
            transaction,
            id,
            request,
            payloadJson,
            payloadHash,
            result,
            eventCreatedAtUtc,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var eventRecord = new StoredAttemptEvent(
            id,
            request.AttemptId,
            request.EventSequence,
            request.IdempotencyKey,
            request.EventType,
            request.Phase,
            request.WorldName,
            request.PluginVersion,
            payloadJson,
            result,
            eventCreatedAtUtc.ToString("O"));
        return new MarketAcquisitionAttemptEventResult
        {
            Request = ApplyLatestAttemptEvent(
                result == MarketAcquisitionAttemptEventResults.Accepted
                    ? current with { Status = targetStatus }
                    : current,
                eventRecord),
            Result = result,
            Reason = result == MarketAcquisitionAttemptEventResults.StaleAttempt
                ? "A newer execution attempt is already active for this request."
                : result == MarketAcquisitionAttemptEventResults.RequestTerminal
                    ? "The request is already terminal."
                    : null,
        };
    }

    private async Task ExpirePendingAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE acquisition_requests
            SET status = 'Expired'
            WHERE status = $status
              AND expires_at_utc <= $now;
            """;
        command.Parameters.AddWithValue("$status", MarketAcquisitionStatuses.PendingPickup);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ExpireClaimedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE acquisition_requests
            SET status = 'Expired'
            WHERE status = $status
              AND claim_expires_at_utc <= $now;
            """;
        command.Parameters.AddWithValue("$status", MarketAcquisitionStatuses.Claimed);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

}
