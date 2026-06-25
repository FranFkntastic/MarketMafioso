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
            request);

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
            SELECT id, status, created_at_utc, expires_at_utc, claimed_at_utc, claim_expires_at_utc, payload_json
            FROM acquisition_requests
            WHERE status = $status
              AND lower(target_character_name) = lower($targetCharacterName)
              AND lower(target_world) = lower($targetWorld)
            ORDER BY created_at_utc ASC;
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

            return current with { Status = existingEvent.Value.ResultStatus };
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
        insertEvent.Parameters.AddWithValue("$createdAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        await insertEvent.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return current with { Status = targetStatus };
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
            SELECT id, status, created_at_utc, expires_at_utc, claimed_at_utc, claim_expires_at_utc, payload_json
            FROM acquisition_requests
            WHERE id = $id
              AND status = $status
              AND lower(target_character_name) = lower($targetCharacterName)
              AND lower(target_world) = lower($targetWorld)
              AND expires_at_utc > $now;
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
            SELECT id, status, created_at_utc, expires_at_utc, claimed_at_utc, claim_expires_at_utc, payload_json
            FROM acquisition_requests
            WHERE id = $id;
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
            SELECT id, status, created_at_utc, expires_at_utc, claimed_at_utc, claim_expires_at_utc, payload_json
            FROM acquisition_requests
            WHERE idempotency_key = $idempotencyKey;
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

    private static async Task<(string RequestId, string EventType, string PayloadJson, string ResultStatus)?> GetEventByIdempotencyKeyAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            SELECT request_id, event_type, payload_json, result_status
            FROM acquisition_request_events
            WHERE idempotency_key = $idempotencyKey;
            """;
        command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))
            : null;
    }

    private static MarketAcquisitionRequestView ReadView(SqliteDataReader reader)
    {
        var request = JsonSerializer.Deserialize<MarketAcquisitionCreateRequest>(
            reader.GetString(6),
            JsonOptions) ?? throw new InvalidOperationException("Stored acquisition payload is invalid.");

        return ToView(
            reader.GetString(0),
            reader.GetString(1),
            DateTimeOffset.Parse(reader.GetString(2)),
            DateTimeOffset.Parse(reader.GetString(3)),
            reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)),
            reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
            request);
    }

    private static MarketAcquisitionRequestView ToView(
        string id,
        string status,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset? claimedAtUtc,
        DateTimeOffset? claimExpiresAtUtc,
        MarketAcquisitionCreateRequest request) =>
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
            ClaimToken = claimToken,
        };

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
        if (request.Quantity == 0)
            throw new ArgumentException("Quantity is required.", nameof(request));
        if (request.MaxUnitPrice == 0)
            throw new ArgumentException("Max unit price is required.", nameof(request));
        if (request.MaxTotalGil == 0)
            throw new ArgumentException("Max total gil is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.QuantityMode) ||
            string.IsNullOrWhiteSpace(request.HqPolicy) ||
            string.IsNullOrWhiteSpace(request.WorldMode))
            throw new ArgumentException("Quantity mode, HQ policy, and world mode are required.", nameof(request));
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
