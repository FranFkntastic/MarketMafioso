using Microsoft.Data.Sqlite;
using static MarketMafioso.Server.Persistence.MarketAcquisitionLinePersistence;
using static MarketMafioso.Server.Persistence.MarketAcquisitionRecordMapper;

namespace MarketMafioso.Server.Persistence;

internal static class MarketAcquisitionRequestQueries
{
    public static async Task<MarketAcquisitionRequestView?> GetForClaimAsync(
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
            WHERE requests.id = $id
              AND requests.status = $status
              AND lower(requests.target_character_name) = lower($targetCharacterName)
              AND lower(requests.target_world) = lower($targetWorld);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$status", MarketAcquisitionStatuses.PendingPickup);
        command.Parameters.AddWithValue("$targetCharacterName", characterName.Trim());
        command.Parameters.AddWithValue("$targetWorld", world.Trim());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var view = ReadView(reader);
        await reader.DisposeAsync().ConfigureAwait(false);
        return await PopulateLinesAsync(connection, transaction, view, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<MarketAcquisitionRequestView?> GetByIdAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction? transaction,
        string id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        if (transaction != null)
            command.Transaction = (SqliteTransaction)transaction;
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
            WHERE requests.id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var view = ReadView(reader);
        await reader.DisposeAsync().ConfigureAwait(false);
        return await PopulateLinesAsync(connection, transaction, view, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<(MarketAcquisitionRequestView View, string PayloadJson)?> GetByIdempotencyKeyAsync(
        SqliteConnection connection,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
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
            WHERE requests.idempotency_key = $idempotencyKey;
            """;
        command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var payloadJson = reader.GetString(7);
        var view = ReadView(reader);
        await reader.DisposeAsync().ConfigureAwait(false);
        return (await PopulateLinesAsync(connection, transaction: null, view, cancellationToken).ConfigureAwait(false), payloadJson);
    }

    public static async Task<string?> GetClaimTokenAsync(
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

    public static async Task EnsureLineBelongsToRequestAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string requestId,
        string lineId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM acquisition_batch_lines
            WHERE request_id = $requestId AND line_id = $lineId;
            """;
        command.Parameters.AddWithValue("$requestId", requestId);
        command.Parameters.AddWithValue("$lineId", lineId);

        var count = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
        if (count == 0)
            throw new MarketAcquisitionInvalidLineException(requestId, lineId);
    }

    public static async Task<StoredLineProgressEvent?> GetLineProgressEventByIdempotencyKeyAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            SELECT request_id, line_id, attempt_id, sequence, idempotency_key, payload_json
            FROM acquisition_line_progress_events
            WHERE idempotency_key = $idempotencyKey;
            """;
        command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new StoredLineProgressEvent(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.GetString(5))
            : null;
    }

    public static async Task<StoredLineProgressEvent?> GetLineProgressEventBySequenceAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string requestId,
        string lineId,
        string attemptId,
        long sequence,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            SELECT request_id, line_id, attempt_id, sequence, idempotency_key, payload_json
            FROM acquisition_line_progress_events
            WHERE request_id = $requestId
              AND line_id = $lineId
              AND attempt_id = $attemptId
              AND sequence = $sequence;
            """;
        command.Parameters.AddWithValue("$requestId", requestId);
        command.Parameters.AddWithValue("$lineId", lineId);
        command.Parameters.AddWithValue("$attemptId", attemptId);
        command.Parameters.AddWithValue("$sequence", sequence);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new StoredLineProgressEvent(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.GetString(5))
            : null;
    }

    public static async Task InsertLineProgressEventAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string requestId,
        string lineId,
        MarketAcquisitionLineProgressRequest request,
        string payloadJson,
        string payloadHash,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            INSERT INTO acquisition_line_progress_events (
                request_id,
                line_id,
                attempt_id,
                sequence,
                idempotency_key,
                status,
                purchased_quantity,
                spent_gil,
                payload_json,
                payload_hash,
                created_at_utc
            )
            VALUES (
                $requestId,
                $lineId,
                $attemptId,
                $sequence,
                $idempotencyKey,
                $status,
                $purchasedQuantity,
                $spentGil,
                $payloadJson,
                $payloadHash,
                $createdAtUtc
            );
            """;
        command.Parameters.AddWithValue("$requestId", requestId);
        command.Parameters.AddWithValue("$lineId", lineId);
        command.Parameters.AddWithValue("$attemptId", request.AttemptId);
        command.Parameters.AddWithValue("$sequence", request.Sequence);
        command.Parameters.AddWithValue("$idempotencyKey", request.IdempotencyKey);
        command.Parameters.AddWithValue("$status", request.Status);
        command.Parameters.AddWithValue("$purchasedQuantity", request.PurchasedQuantity);
        command.Parameters.AddWithValue("$spentGil", request.SpentGil);
        command.Parameters.AddWithValue("$payloadJson", payloadJson);
        command.Parameters.AddWithValue("$payloadHash", payloadHash);
        command.Parameters.AddWithValue("$createdAtUtc", createdAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<StoredPurchaseAudit?> GetPurchaseAuditByIdempotencyKeyAsync(
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
                created_at_utc
            FROM acquisition_purchase_audit
            WHERE idempotency_key = $idempotencyKey;
            """;
        command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadStoredPurchaseAudit(reader)
            : null;
    }

    public static async Task<StoredPurchaseAudit?> GetPurchaseAuditBySequenceAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string requestId,
        string attemptId,
        long sequence,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            SELECT
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
                created_at_utc
            FROM acquisition_purchase_audit
            WHERE request_id = $requestId
              AND attempt_id = $attemptId
              AND sequence = $sequence;
            """;
        command.Parameters.AddWithValue("$requestId", requestId);
        command.Parameters.AddWithValue("$attemptId", attemptId);
        command.Parameters.AddWithValue("$sequence", sequence);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadStoredPurchaseAudit(reader)
            : null;
    }

    public static async Task<MarketAcquisitionPurchaseAuditView?> GetPurchaseAuditByIdAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string auditId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            SELECT
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
                created_at_utc
            FROM acquisition_purchase_audit
            WHERE audit_id = $auditId;
            """;
        command.Parameters.AddWithValue("$auditId", auditId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadStoredPurchaseAudit(reader).View
            : null;
    }

    public static async Task<(string RequestId, string EventType, string PayloadJson, string ResultStatus, string CreatedAtUtc)?> GetEventByIdempotencyKeyAsync(
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

    public static async Task<IReadOnlyList<MarketAcquisitionRequestView>> PopulateLinesAsync(
        SqliteConnection connection,
        IReadOnlyList<MarketAcquisitionRequestView> requests,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
            return requests;

        var populated = new List<MarketAcquisitionRequestView>(requests.Count);
        foreach (var request in requests)
            populated.Add(await PopulateLinesAsync(connection, transaction: null, request, cancellationToken).ConfigureAwait(false));

        return populated;
    }

    public static async Task<MarketAcquisitionRequestView> PopulateLinesAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction? transaction,
        MarketAcquisitionRequestView request,
        CancellationToken cancellationToken)
    {
        var lines = await LoadLinesAsync(connection, transaction, request.Id, cancellationToken).ConfigureAwait(false);
        return request with
        {
            Lines = lines.Count == 0
                ? [ToFallbackLineView(request)]
                : lines,
        };
    }
}
