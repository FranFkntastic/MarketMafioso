using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketMafioso.Server.Persistence;
using MarketMafioso.Server.Sqlite;
using Microsoft.Data.Sqlite;
using static MarketMafioso.Server.MarketAcquisition.MarketAcquisitionRequestPolicy;
using static MarketMafioso.Server.Persistence.MarketAcquisitionEventPersistence;
using static MarketMafioso.Server.Persistence.MarketAcquisitionLinePersistence;
using static MarketMafioso.Server.Persistence.MarketAcquisitionRecordMapper;
using static MarketMafioso.Server.Persistence.MarketAcquisitionRequestQueries;

namespace MarketMafioso.Server;

public sealed partial class MarketAcquisitionRequestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly int minimumExpirySeconds;
    private readonly int maximumExpirySeconds;
    private readonly int claimExpirySeconds;
    private readonly int executionLeaseSeconds;
    private readonly int recoveryPickupExpirySeconds;

    public MarketAcquisitionRequestStore(
        SqliteConnectionFactory connectionFactory,
        IConfiguration configuration)
    {
        this.connectionFactory = connectionFactory;
        minimumExpirySeconds = Math.Max(
            1,
            configuration.GetValue("MarketMafioso:AcquisitionMinimumExpirySeconds", 30));
        maximumExpirySeconds = Math.Max(
            minimumExpirySeconds,
            configuration.GetValue("MarketMafioso:AcquisitionMaximumExpirySeconds", 86400));
        claimExpirySeconds = Math.Max(
            1,
            configuration.GetValue("MarketMafioso:AcquisitionClaimExpirySeconds", 300));
        executionLeaseSeconds = Math.Max(
            1,
            configuration.GetValue("MarketMafioso:AcquisitionExecutionLeaseSeconds", 900));
        recoveryPickupExpirySeconds = Math.Max(
            minimumExpirySeconds,
            configuration.GetValue("MarketMafioso:AcquisitionRecoveryPickupExpirySeconds", 3600));

        MarketAcquisitionSchema.Initialize(connectionFactory.DatabasePath);
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

        var createdView = view with { Lines = lines };
        await EnsureWorkOrderRecordsAsync(
            connection,
            (SqliteTransaction)transaction,
            createdView,
            "created",
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new MarketAcquisitionCreateResult(createdView, false);
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

        var appendedView = current with
        {
            Revision = nextRevision,
            ExpiresAtUtc = expiresAtUtc,
            Lines = lines.OrderBy(line => line.Ordinal).ToList(),
        };
        await EnsureWorkOrderRecordsAsync(
            connection,
            (SqliteTransaction)transaction,
            appendedView,
            "lines-appended",
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return appendedView;
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
            WHERE id = $id;
            """;
        update.Parameters.AddWithValue("$revision", nextRevision);
        update.Parameters.AddWithValue("$expiresAtUtc", expiresAtUtc.ToString("O"));
        update.Parameters.AddWithValue("$payloadJson", replacementPayload);
        update.Parameters.AddWithValue("$id", id);

        var affected = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != 1)
            return null;

        var replacedView = current with
        {
            Revision = nextRevision,
            ExpiresAtUtc = expiresAtUtc,
            Region = NormalizeSupportedRegion(request.Region, nameof(request)),
            WorldMode = request.WorldMode.Trim(),
            SelectedWorlds = NormalizeSelectedWorlds(request.SelectedWorlds),
            SweepScope = string.IsNullOrWhiteSpace(request.SweepScope) ? "Region" : request.SweepScope.Trim(),
            SweepDataCenters = NormalizeSweepDataCenters(request.Region, request.SweepDataCenters),
            Lines = lines.OrderBy(line => line.Ordinal).ToList(),
        };
        await EnsureWorkOrderRecordsAsync(
            connection,
            (SqliteTransaction)transaction,
            replacedView,
            "intent-replaced",
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return replacedView;
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
               OR requests.status NOT IN ($completeStatus, $failedStatus, $cancelledStatus, $rejectedStatus, $expiredStatus, $archivedStatus)
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
        command.Parameters.AddWithValue("$archivedStatus", MarketAcquisitionStatuses.Archived);

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
        var marketObservations = await ListMarketObservationsAsync(
            connection,
            transaction,
            id,
            cancellationToken).ConfigureAwait(false);

        return new MarketAcquisitionRequestTimelineView
        {
            Request = request,
            LifecycleEvents = lifecycleEvents,
            AttemptEvents = attemptEvents,
            MarketObservations = marketObservations,
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

        await using var lease = connection.CreateCommand();
        lease.Transaction = (SqliteTransaction)transaction;
        lease.CommandText =
            """
            INSERT INTO acquisition_execution_leases (work_order_id, plugin_instance_id, renewed_at_utc, expires_at_utc)
            VALUES ($id, $pluginInstanceId, $renewedAtUtc, $expiresAtUtc)
            ON CONFLICT(work_order_id) DO UPDATE SET
                plugin_instance_id = excluded.plugin_instance_id,
                renewed_at_utc = excluded.renewed_at_utc,
                expires_at_utc = excluded.expires_at_utc;
            """;
        lease.Parameters.AddWithValue("$id", id);
        lease.Parameters.AddWithValue("$pluginInstanceId", request.PluginInstanceId.Trim());
        lease.Parameters.AddWithValue("$renewedAtUtc", now.ToString("O"));
        lease.Parameters.AddWithValue("$expiresAtUtc", claimExpiresAtUtc.ToString("O"));
        await lease.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

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
            [MarketAcquisitionStatuses.AcceptedInPlugin, MarketAcquisitionStatuses.Running, MarketAcquisitionStatuses.RecoveryRequired],
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
            [MarketAcquisitionStatuses.AcceptedInPlugin, MarketAcquisitionStatuses.Running, MarketAcquisitionStatuses.RecoveryRequired],
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
            [MarketAcquisitionStatuses.AcceptedInPlugin, MarketAcquisitionStatuses.Running, MarketAcquisitionStatuses.RecoveryRequired],
            request,
            cancellationToken);

    public Task<MarketAcquisitionAttemptEventResult?> ReportAttemptProgressAsync(
        string id,
        MarketAcquisitionAttemptEventRequest request,
        CancellationToken cancellationToken) =>
        ApplyAttemptLifecycleAsync(
            id,
            MarketAcquisitionStatuses.Running,
            [MarketAcquisitionStatuses.AcceptedInPlugin, MarketAcquisitionStatuses.Running, MarketAcquisitionStatuses.RecoveryRequired],
            request,
            cancellationToken);

    public Task<MarketAcquisitionAttemptEventResult?> CompleteAttemptAsync(
        string id,
        MarketAcquisitionAttemptEventRequest request,
        CancellationToken cancellationToken) =>
        ApplyAttemptLifecycleAsync(
            id,
            MarketAcquisitionStatuses.Complete,
            [MarketAcquisitionStatuses.AcceptedInPlugin, MarketAcquisitionStatuses.Running, MarketAcquisitionStatuses.RecoveryRequired],
            request,
            cancellationToken);

    public Task<MarketAcquisitionAttemptEventResult?> FailAttemptAsync(
        string id,
        MarketAcquisitionAttemptEventRequest request,
        CancellationToken cancellationToken) =>
        ApplyAttemptLifecycleAsync(
            id,
            MarketAcquisitionStatuses.Failed,
            [MarketAcquisitionStatuses.AcceptedInPlugin, MarketAcquisitionStatuses.Running, MarketAcquisitionStatuses.RecoveryRequired],
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

        if (current.Status is not (MarketAcquisitionStatuses.AcceptedInPlugin or MarketAcquisitionStatuses.Running or MarketAcquisitionStatuses.RecoveryRequired))
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

    public async Task<MarketAcquisitionMarketObservationView?> RecordMarketObservationAsync(
        string requestId,
        MarketAcquisitionMarketObservationRequest request,
        CancellationToken cancellationToken)
    {
        ValidateMarketObservationRequest(request);
        await ExpireClaimedAsync(cancellationToken).ConfigureAwait(false);

        var payloadJson = JsonSerializer.Serialize(request, JsonOptions);
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
        var listingsJson = JsonSerializer.Serialize(request.Listings, JsonOptions);
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

        var existingByKey = await GetMarketObservationAsync(
            connection,
            transaction,
            "idempotency_key = $value",
            request.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (existingByKey != null)
        {
            if (!string.Equals(existingByKey.RequestId, requestId, StringComparison.Ordinal) ||
                !string.Equals(existingByKey.PayloadJson, payloadJson, StringComparison.Ordinal))
                throw new MarketAcquisitionIdempotencyConflictException();

            return existingByKey.View;
        }

        var existingBySequence = await GetMarketObservationBySequenceAsync(
            connection,
            transaction,
            requestId,
            request.AttemptId,
            request.Sequence,
            cancellationToken).ConfigureAwait(false);
        if (existingBySequence != null)
        {
            if (!string.Equals(existingBySequence.IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal) ||
                !string.Equals(existingBySequence.PayloadJson, payloadJson, StringComparison.Ordinal))
                throw new MarketAcquisitionAttemptSequenceConflictException();

            return existingBySequence.View;
        }

        var observationId = $"{createdAtUtc:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"[..26];
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                INSERT INTO acquisition_market_observations (
                    observation_id, request_id, line_id, attempt_id, sequence, idempotency_key,
                    item_id, item_name, data_center, world_name, read_state,
                    reported_listing_count, listing_capacity, is_truncated,
                    observed_at_utc, listings_json, payload_json, payload_hash, created_at_utc)
                VALUES (
                    $observationId, $requestId, $lineId, $attemptId, $sequence, $idempotencyKey,
                    $itemId, $itemName, $dataCenter, $worldName, $readState,
                    $reportedListingCount, $listingCapacity, $isTruncated,
                    $observedAtUtc, $listingsJson, $payloadJson, $payloadHash, $createdAtUtc);
                """;
            command.Parameters.AddWithValue("$observationId", observationId);
            command.Parameters.AddWithValue("$requestId", requestId);
            command.Parameters.AddWithValue("$lineId", request.LineId);
            command.Parameters.AddWithValue("$attemptId", request.AttemptId);
            command.Parameters.AddWithValue("$sequence", request.Sequence);
            command.Parameters.AddWithValue("$idempotencyKey", request.IdempotencyKey);
            command.Parameters.AddWithValue("$itemId", checked((long)request.ItemId));
            command.Parameters.AddWithValue("$itemName", (object?)request.ItemName ?? DBNull.Value);
            command.Parameters.AddWithValue("$dataCenter", request.DataCenter.Trim());
            command.Parameters.AddWithValue("$worldName", request.WorldName.Trim());
            command.Parameters.AddWithValue("$readState", request.ReadState);
            command.Parameters.AddWithValue("$reportedListingCount", request.ReportedListingCount);
            command.Parameters.AddWithValue("$listingCapacity", request.ListingCapacity);
            command.Parameters.AddWithValue("$isTruncated", request.IsTruncated ? 1 : 0);
            command.Parameters.AddWithValue("$observedAtUtc", request.ObservedAtUtc.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$listingsJson", listingsJson);
            command.Parameters.AddWithValue("$payloadJson", payloadJson);
            command.Parameters.AddWithValue("$payloadHash", payloadHash);
            command.Parameters.AddWithValue("$createdAtUtc", createdAtUtc.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var stored = await GetMarketObservationAsync(
            connection,
            transaction,
            "observation_id = $value",
            observationId,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return stored?.View;
    }

    private static void ValidateMarketObservationRequest(MarketAcquisitionMarketObservationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SchemaVersion != 1)
            throw new ArgumentException("Market observation schema version 1 is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.ClaimToken) ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            string.IsNullOrWhiteSpace(request.AttemptId) ||
            string.IsNullOrWhiteSpace(request.LineId))
            throw new ArgumentException("Market observation claim, idempotency, attempt, and line identity are required.", nameof(request));
        if (request.Sequence <= 0 || request.ItemId == 0)
            throw new ArgumentException("Market observation sequence and item id must be positive.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.DataCenter) || string.IsNullOrWhiteSpace(request.WorldName))
            throw new ArgumentException("Market observation data center and world are required.", nameof(request));
        if (request.ReadState is not ("Complete" or "Partial" or "Unavailable"))
            throw new ArgumentException("Market observation read state must be Complete, Partial, or Unavailable.", nameof(request));
        if (request.ReportedListingCount < 0 || request.ListingCapacity < 0 ||
            request.ReportedListingCount < request.Listings.Count)
            throw new ArgumentException("Market observation listing counts are inconsistent.", nameof(request));
        if (request.ObservedAtUtc == default)
            throw new ArgumentException("Market observation timestamp is required.", nameof(request));
        if (request.Listings.Any(listing => listing.Quantity == 0 || listing.UnitPrice == 0))
            throw new ArgumentException("Market observation listings require positive quantity and unit price.", nameof(request));
    }

    private static async Task<IReadOnlyList<MarketAcquisitionMarketObservationView>> ListMarketObservationsAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = $"{MarketObservationSelectSql} WHERE request_id = $requestId ORDER BY created_at_utc, sequence;";
        command.Parameters.AddWithValue("$requestId", requestId);
        var observations = new List<MarketAcquisitionMarketObservationView>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            observations.Add(ReadMarketObservation(reader).View);
        return observations;
    }

    private static async Task<StoredMarketObservation?> GetMarketObservationAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string predicate,
        string value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = $"{MarketObservationSelectSql} WHERE {predicate} LIMIT 1;";
        command.Parameters.AddWithValue("$value", value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadMarketObservation(reader)
            : null;
    }

    private static async Task<StoredMarketObservation?> GetMarketObservationBySequenceAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string requestId,
        string attemptId,
        long sequence,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = $"{MarketObservationSelectSql} WHERE request_id = $requestId AND attempt_id = $attemptId AND sequence = $sequence LIMIT 1;";
        command.Parameters.AddWithValue("$requestId", requestId);
        command.Parameters.AddWithValue("$attemptId", attemptId);
        command.Parameters.AddWithValue("$sequence", sequence);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadMarketObservation(reader)
            : null;
    }

    private static StoredMarketObservation ReadMarketObservation(SqliteDataReader reader)
    {
        var listings = JsonSerializer.Deserialize<List<MarketAcquisitionMarketObservationListing>>(
            reader.GetString(15), JsonOptions) ?? [];
        var view = new MarketAcquisitionMarketObservationView
        {
            ObservationId = reader.GetString(0),
            RequestId = reader.GetString(1),
            LineId = reader.GetString(2),
            AttemptId = reader.GetString(3),
            Sequence = reader.GetInt64(4),
            ItemId = checked((uint)reader.GetInt64(6)),
            ItemName = reader.IsDBNull(7) ? null : reader.GetString(7),
            DataCenter = reader.GetString(8),
            WorldName = reader.GetString(9),
            ReadState = reader.GetString(10),
            ReportedListingCount = reader.GetInt32(11),
            ListingCapacity = reader.GetInt32(12),
            IsTruncated = reader.GetInt32(13) != 0,
            ObservedAtUtc = DateTimeOffset.Parse(reader.GetString(14)),
            Listings = listings,
            CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(18)),
        };
        return new StoredMarketObservation(view, reader.GetString(5), reader.GetString(16));
    }

    private const string MarketObservationSelectSql =
        """
        SELECT observation_id, request_id, line_id, attempt_id, sequence, idempotency_key,
               item_id, item_name, data_center, world_name, read_state,
               reported_listing_count, listing_capacity, is_truncated,
               observed_at_utc, listings_json, payload_json, payload_hash, created_at_utc
        FROM acquisition_market_observations
        """;

    private sealed record StoredMarketObservation(
        MarketAcquisitionMarketObservationView View,
        string IdempotencyKey,
        string PayloadJson)
    {
        public string RequestId => View.RequestId;
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
            or MarketAcquisitionStatuses.Running
            or MarketAcquisitionStatuses.Shelved
            or MarketAcquisitionStatuses.Archived)
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

        var lifecycleView = ApplyLatestLifecycleEvent(current, targetStatus, eventType, payloadJson, eventCreatedAtUtc);
        await RecordExecutionArtifactsAsync(
            connection,
            (SqliteTransaction)transaction,
            lifecycleView,
            eventType,
            request.Message,
            eventCreatedAtUtc,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return lifecycleView;
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
        // Work orders are durable. The legacy pickup deadline remains on the wire for
        // older clients, but it no longer destroys unclaimed intent.
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task ExpireClaimedAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var staleExecutionCutoff = now.AddSeconds(-executionLeaseSeconds).ToString("O");
        var recoveryExpiresAt = now.AddSeconds(recoveryPickupExpirySeconds).ToString("O");
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
            """
            UPDATE acquisition_requests
            SET status = $pendingStatus,
                expires_at_utc = $recoveryExpiresAt,
                claimed_at_utc = NULL,
                claim_expires_at_utc = NULL,
                claim_token = NULL,
                claimed_by = NULL
            WHERE status = $status
              AND claim_expires_at_utc <= $now;
            """;
            command.Parameters.AddWithValue("$pendingStatus", MarketAcquisitionStatuses.PendingPickup);
            command.Parameters.AddWithValue("$recoveryExpiresAt", recoveryExpiresAt);
            command.Parameters.AddWithValue("$status", MarketAcquisitionStatuses.Claimed);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        const string stalePredicate =
            """
            claimed_at_utc <= $staleCutoff
            AND NOT EXISTS (SELECT 1 FROM acquisition_request_events e WHERE e.request_id = acquisition_requests.id AND e.created_at_utc > $staleCutoff)
            AND NOT EXISTS (SELECT 1 FROM acquisition_attempt_events e WHERE e.request_id = acquisition_requests.id AND e.created_at_utc > $staleCutoff)
            AND NOT EXISTS (SELECT 1 FROM acquisition_line_progress_events e WHERE e.request_id = acquisition_requests.id AND e.created_at_utc > $staleCutoff)
            AND NOT EXISTS (SELECT 1 FROM acquisition_purchase_audit e WHERE e.request_id = acquisition_requests.id AND e.created_at_utc > $staleCutoff)
            AND NOT EXISTS (SELECT 1 FROM acquisition_market_observations e WHERE e.request_id = acquisition_requests.id AND e.created_at_utc > $staleCutoff)
            """;
        const string noExecutionEvidence =
            """
            NOT EXISTS (SELECT 1 FROM acquisition_request_events e WHERE e.request_id = acquisition_requests.id AND e.event_type <> 'accept')
            AND NOT EXISTS (SELECT 1 FROM acquisition_attempt_events e WHERE e.request_id = acquisition_requests.id)
            AND NOT EXISTS (SELECT 1 FROM acquisition_line_progress_events e WHERE e.request_id = acquisition_requests.id)
            AND NOT EXISTS (SELECT 1 FROM acquisition_purchase_audit e WHERE e.request_id = acquisition_requests.id)
            AND NOT EXISTS (SELECT 1 FROM acquisition_market_observations e WHERE e.request_id = acquisition_requests.id)
            """;

        await using (var requeue = connection.CreateCommand())
        {
            requeue.Transaction = (SqliteTransaction)transaction;
            requeue.CommandText =
                $"""
                UPDATE acquisition_requests
                SET status = $pendingStatus,
                    expires_at_utc = $recoveryExpiresAt,
                    claimed_at_utc = NULL,
                    claim_expires_at_utc = NULL,
                    claim_token = NULL,
                    claimed_by = NULL
                WHERE status = $acceptedStatus
                  AND {stalePredicate}
                  AND {noExecutionEvidence};
                """;
            requeue.Parameters.AddWithValue("$pendingStatus", MarketAcquisitionStatuses.PendingPickup);
            requeue.Parameters.AddWithValue("$acceptedStatus", MarketAcquisitionStatuses.AcceptedInPlugin);
            requeue.Parameters.AddWithValue("$recoveryExpiresAt", recoveryExpiresAt);
            requeue.Parameters.AddWithValue("$staleCutoff", staleExecutionCutoff);
            await requeue.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var quarantine = connection.CreateCommand())
        {
            quarantine.Transaction = (SqliteTransaction)transaction;
            quarantine.CommandText =
                $"""
                UPDATE acquisition_requests
                SET status = $recoveryStatus
                WHERE status IN ($acceptedStatus, $runningStatus)
                  AND {stalePredicate};
                """;
            quarantine.Parameters.AddWithValue("$recoveryStatus", MarketAcquisitionStatuses.RecoveryRequired);
            quarantine.Parameters.AddWithValue("$acceptedStatus", MarketAcquisitionStatuses.AcceptedInPlugin);
            quarantine.Parameters.AddWithValue("$runningStatus", MarketAcquisitionStatuses.Running);
            quarantine.Parameters.AddWithValue("$staleCutoff", staleExecutionCutoff);
            await quarantine.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var clearExpiredLeases = connection.CreateCommand())
        {
            clearExpiredLeases.Transaction = (SqliteTransaction)transaction;
            clearExpiredLeases.CommandText = "DELETE FROM acquisition_execution_leases WHERE expires_at_utc <= $now;";
            clearExpiredLeases.Parameters.AddWithValue("$now", now.ToString("O"));
            await clearExpiredLeases.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        return await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    }

}
