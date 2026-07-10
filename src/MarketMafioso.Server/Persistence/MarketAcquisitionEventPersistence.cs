using System.Text.Json;
using Microsoft.Data.Sqlite;
using static MarketMafioso.Server.Persistence.MarketAcquisitionRecordMapper;

namespace MarketMafioso.Server.Persistence;

internal static class MarketAcquisitionEventPersistence
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<IReadOnlyList<MarketAcquisitionLifecycleEventView>> ListLifecycleEventsAsync(
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

    public static async Task<IReadOnlyList<MarketAcquisitionAttemptEventView>> ListAttemptEventsAsync(
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

    public static async Task<StoredAttemptEvent?> GetAttemptEventByIdempotencyKeyAsync(
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

    public static async Task<StoredAttemptEvent?> GetAttemptEventBySequenceAsync(
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

    public static async Task<bool> AttemptExistsAsync(
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

    public static async Task<string?> GetLatestAttemptIdAsync(
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

    public static async Task UpsertAttemptAsync(
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

    public static async Task InsertAttemptEventAsync(
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
}
