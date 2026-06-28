using System.Globalization;
using Microsoft.Data.Sqlite;
using MarketMafioso.Server.Sqlite;

namespace MarketMafioso.Server;

public sealed class DiagnosticEventStore
{
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly IConfiguration configuration;

    public DiagnosticEventStore(
        SqliteConnectionFactory connectionFactory,
        IConfiguration configuration)
    {
        this.connectionFactory = connectionFactory;
        this.configuration = configuration;
    }

    public async Task<DiagnosticEventView> WriteAsync(
        DiagnosticEventCreate entry,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var occurred = entry.OccurredAtUtc ?? now;
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO diagnostic_events (
                occurred_at_utc,
                received_at_utc,
                source,
                category,
                type,
                severity,
                outcome,
                message,
                correlation_id,
                account_id,
                dashboard_user_id,
                dashboard_session_id,
                plugin_instance_id,
                acquisition_request_id,
                route_run_id,
                route_stop_id,
                purchase_attempt_id,
                snapshot_id,
                item_id,
                item_name,
                world,
                character_name,
                http_method,
                route_pattern,
                status_code,
                duration_ms,
                exception_type,
                exception_message,
                payload_summary_json,
                payload_raw_json,
                payload_size_bytes,
                payload_sha256
            )
            VALUES (
                $occurredAt,
                $receivedAt,
                $source,
                $category,
                $type,
                $severity,
                $outcome,
                $message,
                $correlationId,
                $accountId,
                $dashboardUserId,
                $dashboardSessionId,
                $pluginInstanceId,
                $acquisitionRequestId,
                $routeRunId,
                $routeStopId,
                $purchaseAttemptId,
                $snapshotId,
                $itemId,
                $itemName,
                $world,
                $characterName,
                $httpMethod,
                $routePattern,
                $statusCode,
                $durationMs,
                $exceptionType,
                $exceptionMessage,
                $payloadSummaryJson,
                $payloadRawJson,
                $payloadSizeBytes,
                $payloadSha256
            )
            RETURNING id;
            """;
        AddParameters(command, entry, occurred, now);
        var id = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        await PruneAsync(connection, cancellationToken);

        return new DiagnosticEventView
        {
            Id = id,
            OccurredAtUtc = occurred,
            ReceivedAtUtc = now,
            Source = entry.Source,
            Category = entry.Category,
            Type = entry.Type,
            Severity = entry.Severity,
            Outcome = entry.Outcome,
            Message = entry.Message,
            CorrelationId = entry.CorrelationId,
            AccountId = entry.AccountId,
            DashboardUserId = entry.DashboardUserId,
            DashboardSessionId = entry.DashboardSessionId,
            PluginInstanceId = entry.PluginInstanceId,
            AcquisitionRequestId = entry.AcquisitionRequestId,
            RouteRunId = entry.RouteRunId,
            RouteStopId = entry.RouteStopId,
            PurchaseAttemptId = entry.PurchaseAttemptId,
            SnapshotId = entry.SnapshotId,
            ItemId = entry.ItemId,
            ItemName = entry.ItemName,
            World = entry.World,
            CharacterName = entry.CharacterName,
            HttpMethod = entry.HttpMethod,
            RoutePattern = entry.RoutePattern,
            StatusCode = entry.StatusCode,
            DurationMs = entry.DurationMs,
            ExceptionType = entry.ExceptionType,
            ExceptionMessage = entry.ExceptionMessage,
            PayloadSummaryJson = entry.PayloadSummaryJson,
            PayloadRawJson = entry.PayloadRawJson,
            PayloadSizeBytes = entry.PayloadSizeBytes,
            PayloadSha256 = entry.PayloadSha256,
        };
    }

    public async Task<IReadOnlyList<DiagnosticEventView>> ListRecentAsync(
        int limit,
        string? category,
        string? severity,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                occurred_at_utc,
                received_at_utc,
                source,
                category,
                type,
                severity,
                outcome,
                message,
                correlation_id,
                account_id,
                dashboard_user_id,
                dashboard_session_id,
                plugin_instance_id,
                acquisition_request_id,
                route_run_id,
                route_stop_id,
                purchase_attempt_id,
                snapshot_id,
                item_id,
                item_name,
                world,
                character_name,
                http_method,
                route_pattern,
                status_code,
                duration_ms,
                exception_type,
                exception_message,
                payload_summary_json,
                payload_raw_json,
                payload_size_bytes,
                payload_sha256
            FROM diagnostic_events
            WHERE ($category IS NULL OR category = $category)
              AND ($severity IS NULL OR severity = $severity)
              AND ($correlationId IS NULL OR correlation_id = $correlationId)
            ORDER BY id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$category", string.IsNullOrWhiteSpace(category) ? DBNull.Value : category);
        command.Parameters.AddWithValue("$severity", string.IsNullOrWhiteSpace(severity) ? DBNull.Value : severity);
        command.Parameters.AddWithValue("$correlationId", string.IsNullOrWhiteSpace(correlationId) ? DBNull.Value : correlationId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var events = new List<DiagnosticEventView>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            events.Add(ReadEvent(reader));

        return events;
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM diagnostic_events";
        return checked((int)(long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L));
    }

    private async Task PruneAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var retention = Math.Max(1, configuration.GetValue("MarketMafioso:DiagnosticEventRetention", 5000));
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM diagnostic_events
            WHERE id NOT IN (
                SELECT id
                FROM diagnostic_events
                ORDER BY id DESC
                LIMIT $retention
            );
            """;
        command.Parameters.AddWithValue("$retention", retention);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameters(
        SqliteCommand command,
        DiagnosticEventCreate entry,
        DateTimeOffset occurredAt,
        DateTimeOffset receivedAt)
    {
        command.Parameters.AddWithValue("$occurredAt", occurredAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$receivedAt", receivedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$source", RequireValue(entry.Source, "source"));
        command.Parameters.AddWithValue("$category", RequireValue(entry.Category, "category"));
        command.Parameters.AddWithValue("$type", RequireValue(entry.Type, "type"));
        command.Parameters.AddWithValue("$severity", RequireValue(entry.Severity, "severity"));
        command.Parameters.AddWithValue("$outcome", Db(entry.Outcome));
        command.Parameters.AddWithValue("$message", RequireValue(entry.Message, "message"));
        command.Parameters.AddWithValue("$correlationId", Db(entry.CorrelationId));
        command.Parameters.AddWithValue("$accountId", Db(entry.AccountId));
        command.Parameters.AddWithValue("$dashboardUserId", Db(entry.DashboardUserId));
        command.Parameters.AddWithValue("$dashboardSessionId", Db(entry.DashboardSessionId));
        command.Parameters.AddWithValue("$pluginInstanceId", Db(entry.PluginInstanceId));
        command.Parameters.AddWithValue("$acquisitionRequestId", Db(entry.AcquisitionRequestId));
        command.Parameters.AddWithValue("$routeRunId", Db(entry.RouteRunId));
        command.Parameters.AddWithValue("$routeStopId", Db(entry.RouteStopId));
        command.Parameters.AddWithValue("$purchaseAttemptId", Db(entry.PurchaseAttemptId));
        command.Parameters.AddWithValue("$snapshotId", Db(entry.SnapshotId));
        command.Parameters.AddWithValue("$itemId", Db(entry.ItemId));
        command.Parameters.AddWithValue("$itemName", Db(entry.ItemName));
        command.Parameters.AddWithValue("$world", Db(entry.World));
        command.Parameters.AddWithValue("$characterName", Db(entry.CharacterName));
        command.Parameters.AddWithValue("$httpMethod", Db(entry.HttpMethod));
        command.Parameters.AddWithValue("$routePattern", Db(entry.RoutePattern));
        command.Parameters.AddWithValue("$statusCode", Db(entry.StatusCode));
        command.Parameters.AddWithValue("$durationMs", Db(entry.DurationMs));
        command.Parameters.AddWithValue("$exceptionType", Db(entry.ExceptionType));
        command.Parameters.AddWithValue("$exceptionMessage", Db(entry.ExceptionMessage));
        command.Parameters.AddWithValue("$payloadSummaryJson", Db(entry.PayloadSummaryJson));
        command.Parameters.AddWithValue("$payloadRawJson", Db(entry.PayloadRawJson));
        command.Parameters.AddWithValue("$payloadSizeBytes", Db(entry.PayloadSizeBytes));
        command.Parameters.AddWithValue("$payloadSha256", Db(entry.PayloadSha256));
    }

    private static DiagnosticEventView ReadEvent(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        OccurredAtUtc = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
        ReceivedAtUtc = DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
        Source = reader.GetString(3),
        Category = reader.GetString(4),
        Type = reader.GetString(5),
        Severity = reader.GetString(6),
        Outcome = NullableString(reader, 7),
        Message = reader.GetString(8),
        CorrelationId = NullableString(reader, 9),
        AccountId = NullableInt64(reader, 10),
        DashboardUserId = NullableInt64(reader, 11),
        DashboardSessionId = NullableString(reader, 12),
        PluginInstanceId = NullableString(reader, 13),
        AcquisitionRequestId = NullableString(reader, 14),
        RouteRunId = NullableString(reader, 15),
        RouteStopId = NullableString(reader, 16),
        PurchaseAttemptId = NullableString(reader, 17),
        SnapshotId = NullableString(reader, 18),
        ItemId = NullableUInt32(reader, 19),
        ItemName = NullableString(reader, 20),
        World = NullableString(reader, 21),
        CharacterName = NullableString(reader, 22),
        HttpMethod = NullableString(reader, 23),
        RoutePattern = NullableString(reader, 24),
        StatusCode = NullableInt32(reader, 25),
        DurationMs = NullableInt64(reader, 26),
        ExceptionType = NullableString(reader, 27),
        ExceptionMessage = NullableString(reader, 28),
        PayloadSummaryJson = NullableString(reader, 29),
        PayloadRawJson = NullableString(reader, 30),
        PayloadSizeBytes = NullableInt64(reader, 31),
        PayloadSha256 = NullableString(reader, 32),
    };

    private static string RequireValue(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"Diagnostic event {name} is required.") : value.Trim();

    private static object Db(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private static object Db(long? value) => value.HasValue ? value.Value : DBNull.Value;

    private static object Db(int? value) => value.HasValue ? value.Value : DBNull.Value;

    private static object Db(uint? value) => value.HasValue ? value.Value : DBNull.Value;

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? NullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static int? NullableInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static uint? NullableUInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : checked((uint)reader.GetInt64(ordinal));
}
