using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MarketMafioso.Server;

public sealed class MarketAcquisitionRequestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string connectionString;
    private readonly int minimumExpirySeconds;
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
        claimExpirySeconds = Math.Max(
            1,
            configuration.GetValue("MarketMafioso:AcquisitionClaimExpirySeconds", 300));

        Initialize();
    }

    public async Task<MarketAcquisitionCreateResult> CreateAsync(
        MarketAcquisitionCreateRequest request,
        CancellationToken cancellationToken)
    {
        ValidateCreateRequest(request);

        var now = DateTimeOffset.UtcNow;
        var expirySeconds = Math.Clamp(request.ExpiresInSeconds, minimumExpirySeconds, 300);
        var payloadJson = JsonSerializer.Serialize(request, JsonOptions);

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
            status: MarketAcquisitionStatuses.PendingPickup,
            createdAtUtc: now,
            expiresAtUtc: now.AddSeconds(expirySeconds),
            claimedAtUtc: null,
            claimExpiresAtUtc: null,
            request,
            latestEvent: null);

        await using var command = connection.CreateCommand();
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
        return new MarketAcquisitionCreateResult(view, false);
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

        return requests;
    }

    public async Task<IReadOnlyList<MarketAcquisitionRequestView>> ListRecentAsync(
        int limit,
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
            ORDER BY requests.created_at_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var requests = new List<MarketAcquisitionRequestView>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            requests.Add(ReadView(reader));

        return requests;
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

        var expiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp((int)current.ExpiresAtUtc.Subtract(current.CreatedAtUtc).TotalSeconds, minimumExpirySeconds, 300));

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

    private void Initialize()
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS acquisition_requests (
                id TEXT NOT NULL PRIMARY KEY,
                idempotency_key TEXT NOT NULL UNIQUE,
                status TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                expires_at_utc TEXT NOT NULL,
                claimed_at_utc TEXT NULL,
                claim_expires_at_utc TEXT NULL,
                claim_token TEXT NULL,
                claimed_by TEXT NULL,
                target_character_name TEXT NOT NULL,
                target_world TEXT NOT NULL,
                payload_json TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_acquisition_requests_pending_scope
                ON acquisition_requests(status, target_character_name, target_world);

            CREATE TABLE IF NOT EXISTS acquisition_request_events (
                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                request_id TEXT NOT NULL,
                idempotency_key TEXT NOT NULL UNIQUE,
                event_type TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                result_status TEXT NOT NULL,
                created_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS acquisition_request_attempts (
                attempt_id TEXT NOT NULL PRIMARY KEY,
                request_id TEXT NOT NULL,
                plugin_instance_id TEXT NOT NULL,
                status TEXT NOT NULL,
                started_at_utc TEXT NOT NULL,
                ended_at_utc TEXT NULL,
                latest_sequence INTEGER NOT NULL DEFAULT 0,
                latest_phase TEXT NULL,
                latest_world TEXT NULL,
                latest_message TEXT NULL,
                latest_result TEXT NULL,
                plugin_version TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_acquisition_attempts_request
                ON acquisition_request_attempts(request_id, started_at_utc DESC);

            CREATE TABLE IF NOT EXISTS acquisition_attempt_events (
                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                request_id TEXT NOT NULL,
                attempt_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                idempotency_key TEXT NOT NULL UNIQUE,
                plugin_instance_id TEXT NOT NULL,
                event_type TEXT NOT NULL,
                phase TEXT NOT NULL,
                route_stop_id TEXT NULL,
                world_name TEXT NULL,
                plugin_version TEXT NULL,
                payload_json TEXT NOT NULL,
                payload_hash TEXT NOT NULL,
                result TEXT NOT NULL,
                client_timestamp_utc TEXT NULL,
                created_at_utc TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS idx_acquisition_attempt_events_attempt_sequence
                ON acquisition_attempt_events(attempt_id, sequence);
            """;
        command.ExecuteNonQuery();
    }

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

    private static async Task<MarketAcquisitionRequestView?> GetForClaimAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string id,
        string characterName,
        string world,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            SELECT
                requests.id,
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
            WHERE requests.id = $id
              AND requests.status = $status
              AND lower(requests.target_character_name) = lower($targetCharacterName)
              AND lower(requests.target_world) = lower($targetWorld)
              AND requests.expires_at_utc > $now;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$status", MarketAcquisitionStatuses.PendingPickup);
        command.Parameters.AddWithValue("$targetCharacterName", characterName.Trim());
        command.Parameters.AddWithValue("$targetWorld", world.Trim());
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadView(reader)
            : null;
    }

    private static async Task<MarketAcquisitionRequestView?> GetByIdAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            SELECT
                requests.id,
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
            WHERE requests.id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadView(reader)
            : null;
    }

    private static async Task<(MarketAcquisitionRequestView View, string PayloadJson)?> GetByIdempotencyKeyAsync(
        SqliteConnection connection,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                requests.id,
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
            WHERE requests.idempotency_key = $idempotencyKey;
            """;
        command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var payloadJson = reader.GetString(6);
        return (ReadView(reader), payloadJson);
    }

    private static async Task<string?> GetClaimTokenAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "SELECT claim_token FROM acquisition_requests WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value as string;
    }

    private static async Task<(string RequestId, string EventType, string PayloadJson, string ResultStatus, string CreatedAtUtc)?> GetEventByIdempotencyKeyAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            SELECT request_id, event_type, payload_json, result_status, created_at_utc
            FROM acquisition_request_events
            WHERE idempotency_key = $idempotencyKey;
            """;
        command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4))
            : null;
    }

    private static async Task<IReadOnlyList<MarketAcquisitionLifecycleEventView>> ListLifecycleEventsAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            SELECT event_type, payload_json, result_status, created_at_utc
            FROM acquisition_request_events
            WHERE request_id = $requestId
            ORDER BY id ASC;
            """;
        command.Parameters.AddWithValue("$requestId", requestId);

        var events = new List<MarketAcquisitionLifecycleEventView>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var payload = JsonSerializer.Deserialize<MarketAcquisitionLifecycleRequest>(
                reader.GetString(1),
                JsonOptions);
            events.Add(new MarketAcquisitionLifecycleEventView
            {
                EventType = reader.GetString(0),
                ResultStatus = reader.GetString(2),
                RunnerState = payload?.RunnerState,
                Message = payload?.Message,
                Reason = payload?.Reason,
                CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(3)),
            });
        }

        return events;
    }

    private static async Task<IReadOnlyList<MarketAcquisitionAttemptEventView>> ListAttemptEventsAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            SELECT
                attempt_id,
                sequence,
                event_type,
                phase,
                route_stop_id,
                world_name,
                plugin_version,
                payload_json,
                result,
                client_timestamp_utc,
                created_at_utc
            FROM acquisition_attempt_events
            WHERE request_id = $requestId
            ORDER BY created_at_utc ASC, id ASC;
            """;
        command.Parameters.AddWithValue("$requestId", requestId);

        var events = new List<MarketAcquisitionAttemptEventView>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var payload = JsonSerializer.Deserialize<MarketAcquisitionAttemptEventRequest>(
                reader.GetString(7),
                JsonOptions);
            events.Add(new MarketAcquisitionAttemptEventView
            {
                AttemptId = reader.GetString(0),
                Sequence = reader.GetInt64(1),
                EventType = reader.GetString(2),
                Phase = reader.GetString(3),
                RouteStopId = reader.IsDBNull(4) ? null : reader.GetString(4),
                WorldName = reader.IsDBNull(5) ? null : reader.GetString(5),
                PluginVersion = reader.IsDBNull(6) ? null : reader.GetString(6),
                Result = reader.GetString(8),
                RunnerState = payload?.RunnerState,
                Message = payload?.Message,
                Reason = payload?.Reason,
                ClientTimestampUtc = reader.IsDBNull(9)
                    ? null
                    : DateTimeOffset.Parse(reader.GetString(9)),
                CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(10)),
            });
        }

        return events;
    }

    private static async Task<StoredAttemptEvent?> GetAttemptEventByIdempotencyKeyAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            SELECT
                request_id,
                attempt_id,
                sequence,
                idempotency_key,
                event_type,
                phase,
                world_name,
                plugin_version,
                payload_json,
                result,
                created_at_utc
            FROM acquisition_attempt_events
            WHERE idempotency_key = $idempotencyKey;
            """;
        command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadStoredAttemptEvent(reader)
            : null;
    }

    private static async Task<StoredAttemptEvent?> GetAttemptEventBySequenceAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string attemptId,
        long sequence,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            SELECT
                request_id,
                attempt_id,
                sequence,
                idempotency_key,
                event_type,
                phase,
                world_name,
                plugin_version,
                payload_json,
                result,
                created_at_utc
            FROM acquisition_attempt_events
            WHERE attempt_id = $attemptId
              AND sequence = $sequence;
            """;
        command.Parameters.AddWithValue("$attemptId", attemptId);
        command.Parameters.AddWithValue("$sequence", sequence);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadStoredAttemptEvent(reader)
            : null;
    }

    private static async Task<bool> AttemptExistsAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string attemptId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "SELECT 1 FROM acquisition_request_attempts WHERE attempt_id = $attemptId LIMIT 1;";
        command.Parameters.AddWithValue("$attemptId", attemptId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result != null;
    }

    private static async Task<string?> GetLatestAttemptIdAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            SELECT attempt_id
            FROM acquisition_request_attempts
            WHERE request_id = $requestId
            ORDER BY started_at_utc DESC, attempt_id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$requestId", requestId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value as string;
    }

    private static async Task UpsertAttemptAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string requestId,
        string status,
        MarketAcquisitionAttemptEventRequest request,
        string result,
        DateTimeOffset eventCreatedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            INSERT INTO acquisition_request_attempts (
                attempt_id,
                request_id,
                plugin_instance_id,
                status,
                started_at_utc,
                ended_at_utc,
                latest_sequence,
                latest_phase,
                latest_world,
                latest_message,
                latest_result,
                plugin_version
            )
            VALUES (
                $attemptId,
                $requestId,
                $pluginInstanceId,
                $status,
                $startedAtUtc,
                $endedAtUtc,
                $latestSequence,
                $latestPhase,
                $latestWorld,
                $latestMessage,
                $latestResult,
                $pluginVersion
            )
            ON CONFLICT(attempt_id) DO UPDATE SET
                status = $status,
                ended_at_utc = $endedAtUtc,
                latest_sequence = $latestSequence,
                latest_phase = $latestPhase,
                latest_world = $latestWorld,
                latest_message = $latestMessage,
                latest_result = $latestResult,
                plugin_version = $pluginVersion;
            """;
        command.Parameters.AddWithValue("$attemptId", request.AttemptId);
        command.Parameters.AddWithValue("$requestId", requestId);
        command.Parameters.AddWithValue("$pluginInstanceId", request.PluginInstanceId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$startedAtUtc", eventCreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$endedAtUtc", status is MarketAcquisitionStatuses.Complete or MarketAcquisitionStatuses.Failed
            ? eventCreatedAtUtc.ToString("O")
            : DBNull.Value);
        command.Parameters.AddWithValue("$latestSequence", request.EventSequence);
        command.Parameters.AddWithValue("$latestPhase", request.Phase);
        command.Parameters.AddWithValue("$latestWorld", (object?)request.WorldName ?? DBNull.Value);
        command.Parameters.AddWithValue("$latestMessage", (object?)request.Message ?? DBNull.Value);
        command.Parameters.AddWithValue("$latestResult", result);
        command.Parameters.AddWithValue("$pluginVersion", (object?)request.PluginVersion ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertAttemptEventAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string requestId,
        MarketAcquisitionAttemptEventRequest request,
        string payloadJson,
        string payloadHash,
        string result,
        DateTimeOffset eventCreatedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            INSERT INTO acquisition_attempt_events (
                request_id,
                attempt_id,
                sequence,
                idempotency_key,
                plugin_instance_id,
                event_type,
                phase,
                route_stop_id,
                world_name,
                plugin_version,
                payload_json,
                payload_hash,
                result,
                client_timestamp_utc,
                created_at_utc
            )
            VALUES (
                $requestId,
                $attemptId,
                $sequence,
                $idempotencyKey,
                $pluginInstanceId,
                $eventType,
                $phase,
                $routeStopId,
                $worldName,
                $pluginVersion,
                $payloadJson,
                $payloadHash,
                $result,
                $clientTimestampUtc,
                $createdAtUtc
            );
            """;
        command.Parameters.AddWithValue("$requestId", requestId);
        command.Parameters.AddWithValue("$attemptId", request.AttemptId);
        command.Parameters.AddWithValue("$sequence", request.EventSequence);
        command.Parameters.AddWithValue("$idempotencyKey", request.IdempotencyKey);
        command.Parameters.AddWithValue("$pluginInstanceId", request.PluginInstanceId);
        command.Parameters.AddWithValue("$eventType", request.EventType);
        command.Parameters.AddWithValue("$phase", request.Phase);
        command.Parameters.AddWithValue("$routeStopId", (object?)request.RouteStopId ?? DBNull.Value);
        command.Parameters.AddWithValue("$worldName", (object?)request.WorldName ?? DBNull.Value);
        command.Parameters.AddWithValue("$pluginVersion", (object?)request.PluginVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$payloadJson", payloadJson);
        command.Parameters.AddWithValue("$payloadHash", payloadHash);
        command.Parameters.AddWithValue("$result", result);
        command.Parameters.AddWithValue("$clientTimestampUtc", request.ClientTimestampUtc == default
            ? DBNull.Value
            : request.ClientTimestampUtc.ToString("O"));
        command.Parameters.AddWithValue("$createdAtUtc", eventCreatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static MarketAcquisitionRequestView ApplyLatestLifecycleEvent(
        MarketAcquisitionRequestView request,
        string status,
        string eventType,
        string payloadJson,
        DateTimeOffset eventCreatedAtUtc)
    {
        var payload = JsonSerializer.Deserialize<MarketAcquisitionLifecycleRequest>(payloadJson, JsonOptions);
        return request with
        {
            Status = status,
            LatestEventType = eventType,
            LatestRunnerState = payload?.RunnerState,
            LatestMessage = payload?.Message,
            LatestReason = payload?.Reason,
            LatestEventAtUtc = eventCreatedAtUtc,
        };
    }

    private static MarketAcquisitionRequestView ApplyLatestAttemptEvent(
        MarketAcquisitionRequestView request,
        StoredAttemptEvent attemptEvent) =>
        request with
        {
            LatestAttemptId = attemptEvent.AttemptId,
            LatestAttemptSequence = attemptEvent.Sequence,
            LatestAttemptEventType = attemptEvent.EventType,
            LatestAttemptPhase = attemptEvent.Phase,
            LatestAttemptWorld = attemptEvent.WorldName,
            LatestAttemptResult = attemptEvent.Result,
            LatestAttemptPluginVersion = attemptEvent.PluginVersion,
        };

    private static StoredAttemptEvent ReadStoredAttemptEvent(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10));

    private static MarketAcquisitionRequestView ReadView(SqliteDataReader reader)
    {
        var request = JsonSerializer.Deserialize<MarketAcquisitionCreateRequest>(
            reader.GetString(6),
            JsonOptions) ?? throw new InvalidOperationException("Stored acquisition payload is invalid.");
        var latestEvent = ReadLatestEvent(reader);
        var latestAttempt = ReadLatestAttempt(reader);

        return ToView(
            reader.GetString(0),
            reader.GetString(1),
            DateTimeOffset.Parse(reader.GetString(2)),
            DateTimeOffset.Parse(reader.GetString(3)),
            reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)),
            reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
            request,
            latestEvent,
            latestAttempt);
    }

    private static MarketAcquisitionLatestEvent? ReadLatestEvent(SqliteDataReader reader)
    {
        if (reader.FieldCount < 10 || reader.IsDBNull(7))
            return null;

        var eventPayload = reader.IsDBNull(8)
            ? null
            : JsonSerializer.Deserialize<MarketAcquisitionLifecycleRequest>(reader.GetString(8), JsonOptions);
        return new MarketAcquisitionLatestEvent(
            reader.GetString(7),
            eventPayload?.RunnerState,
            eventPayload?.Message,
            eventPayload?.Reason,
            DateTimeOffset.Parse(reader.GetString(9)));
    }

    private static MarketAcquisitionLatestAttempt? ReadLatestAttempt(SqliteDataReader reader)
    {
        if (reader.FieldCount < 17 || reader.IsDBNull(10))
            return null;

        return new MarketAcquisitionLatestAttempt(
            reader.GetString(10),
            reader.GetInt64(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16));
    }

    private static MarketAcquisitionRequestView ToView(
        string id,
        string status,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset? claimedAtUtc,
        DateTimeOffset? claimExpiresAtUtc,
        MarketAcquisitionCreateRequest request,
        MarketAcquisitionLatestEvent? latestEvent,
        MarketAcquisitionLatestAttempt? latestAttempt = null) =>
        new()
        {
            Id = id,
            Status = status,
            CreatedAtUtc = createdAtUtc,
            ExpiresAtUtc = expiresAtUtc,
            ClaimedAtUtc = claimedAtUtc,
            ClaimExpiresAtUtc = claimExpiresAtUtc,
            TargetCharacterName = request.TargetCharacterName,
            TargetWorld = request.TargetWorld,
            Region = request.Region,
            ItemId = request.ItemId,
            ItemName = request.ItemName,
            QuantityMode = request.QuantityMode,
            Quantity = request.Quantity,
            HqPolicy = request.HqPolicy,
            MaxUnitPrice = request.MaxUnitPrice,
            MaxTotalGil = request.MaxTotalGil,
            WorldMode = request.WorldMode,
            LatestEventType = latestEvent?.EventType,
            LatestRunnerState = latestEvent?.RunnerState,
            LatestMessage = latestEvent?.Message,
            LatestReason = latestEvent?.Reason,
            LatestEventAtUtc = latestEvent?.CreatedAtUtc,
            LatestAttemptId = latestAttempt?.AttemptId,
            LatestAttemptSequence = latestAttempt?.Sequence,
            LatestAttemptEventType = latestAttempt?.EventType,
            LatestAttemptPhase = latestAttempt?.Phase,
            LatestAttemptWorld = latestAttempt?.WorldName,
            LatestAttemptResult = latestAttempt?.Result,
            LatestAttemptPluginVersion = latestAttempt?.PluginVersion,
        };

    private static MarketAcquisitionClaimView ToClaimView(
        MarketAcquisitionRequestView request,
        string claimToken) =>
        new()
        {
            Id = request.Id,
            Status = request.Status,
            CreatedAtUtc = request.CreatedAtUtc,
            ExpiresAtUtc = request.ExpiresAtUtc,
            ClaimedAtUtc = request.ClaimedAtUtc,
            ClaimExpiresAtUtc = request.ClaimExpiresAtUtc,
            TargetCharacterName = request.TargetCharacterName,
            TargetWorld = request.TargetWorld,
            Region = request.Region,
            ItemId = request.ItemId,
            ItemName = request.ItemName,
            QuantityMode = request.QuantityMode,
            Quantity = request.Quantity,
            HqPolicy = request.HqPolicy,
            MaxUnitPrice = request.MaxUnitPrice,
            MaxTotalGil = request.MaxTotalGil,
            WorldMode = request.WorldMode,
            LatestEventType = request.LatestEventType,
            LatestRunnerState = request.LatestRunnerState,
            LatestMessage = request.LatestMessage,
            LatestReason = request.LatestReason,
            LatestEventAtUtc = request.LatestEventAtUtc,
            LatestAttemptId = request.LatestAttemptId,
            LatestAttemptSequence = request.LatestAttemptSequence,
            LatestAttemptEventType = request.LatestAttemptEventType,
            LatestAttemptPhase = request.LatestAttemptPhase,
            LatestAttemptWorld = request.LatestAttemptWorld,
            LatestAttemptResult = request.LatestAttemptResult,
            LatestAttemptPluginVersion = request.LatestAttemptPluginVersion,
            ClaimToken = claimToken,
        };

    private sealed record MarketAcquisitionLatestEvent(
        string EventType,
        string? RunnerState,
        string? Message,
        string? Reason,
        DateTimeOffset CreatedAtUtc);

    private sealed record MarketAcquisitionLatestAttempt(
        string AttemptId,
        long Sequence,
        string EventType,
        string Phase,
        string? WorldName,
        string Result,
        string? PluginVersion);

    private readonly record struct StoredAttemptEvent(
        string RequestId,
        string AttemptId,
        long Sequence,
        string IdempotencyKey,
        string EventType,
        string Phase,
        string? WorldName,
        string? PluginVersion,
        string PayloadJson,
        string Result,
        string CreatedAtUtc);

    private static void ValidateCreateRequest(MarketAcquisitionCreateRequest request)
    {
        if (request.SchemaVersion != 1)
            throw new ArgumentException("Schema version must be 1.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Idempotency key is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.TargetCharacterName))
            throw new ArgumentException("Target character name is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.TargetWorld))
            throw new ArgumentException("Target world is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Region))
            throw new ArgumentException("Region is required.", nameof(request));
        if (request.ItemId == 0)
            throw new ArgumentException("Item id is required.", nameof(request));
        if (request.MaxUnitPrice == 0)
            throw new ArgumentException("Max unit price is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.QuantityMode) ||
            string.IsNullOrWhiteSpace(request.HqPolicy) ||
            string.IsNullOrWhiteSpace(request.WorldMode))
            throw new ArgumentException("Quantity mode, HQ policy, and world mode are required.", nameof(request));
        if (request.QuantityMode is not ("TargetQuantity" or "AllBelowThreshold"))
            throw new ArgumentException("Quantity mode must be TargetQuantity or AllBelowThreshold.", nameof(request));
        if (request.QuantityMode == "TargetQuantity" && request.Quantity == 0)
            throw new ArgumentException("Target quantity is required.", nameof(request));
    }

    private static void ValidateAttemptEventRequest(MarketAcquisitionAttemptEventRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ClaimToken))
            throw new UnauthorizedAccessException("Claim token is required.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Idempotency key is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.PluginInstanceId))
            throw new ArgumentException("Plugin instance id is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.AttemptId))
            throw new ArgumentException("Attempt id is required.", nameof(request));
        if (request.EventSequence < 1)
            throw new ArgumentException("Attempt event sequence must be one or greater.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.EventType))
            throw new ArgumentException("Attempt event type is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Phase))
            throw new ArgumentException("Attempt phase is required.", nameof(request));
    }

    private static string CreateSecretToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal);
    }

    private static bool MatchesSecret(string supplied, string? stored)
    {
        if (string.IsNullOrWhiteSpace(supplied) || string.IsNullOrWhiteSpace(stored))
            return false;

        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        var storedBytes = Encoding.UTF8.GetBytes(stored);
        return suppliedBytes.Length == storedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(suppliedBytes, storedBytes);
    }
}
