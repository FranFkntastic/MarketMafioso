using System.Text.Json;
using Microsoft.Data.Sqlite;
using static MarketMafioso.Server.MarketAcquisition.MarketAcquisitionRequestPolicy;

namespace MarketMafioso.Server.Persistence;

internal static class MarketAcquisitionRecordMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static StoredPurchaseAudit ReadStoredPurchaseAudit(SqliteDataReader reader)
    {
        var view = new MarketAcquisitionPurchaseAuditView
        {
            AuditId = reader.GetString(0),
            RequestId = reader.GetString(1),
            LineId = reader.GetString(2),
            AttemptId = reader.GetString(3),
            Sequence = reader.GetInt64(4),
            WorldName = reader.GetString(6),
            ItemId = checked((uint)reader.GetInt64(7)),
            ItemName = reader.IsDBNull(8) ? null : reader.GetString(8),
            ListingId = reader.GetString(9),
            RetainerName = reader.GetString(10),
            RetainerId = reader.GetString(11),
            Quantity = checked((uint)reader.GetInt64(12)),
            UnitPrice = checked((uint)reader.GetInt64(13)),
            TotalGil = checked((uint)reader.GetInt64(14)),
            IsHq = reader.GetInt64(15) != 0,
            Result = reader.GetString(16),
            Message = reader.IsDBNull(17) ? null : reader.GetString(17),
            CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(19)),
        };
        return new StoredPurchaseAudit(view, reader.GetString(5), reader.GetString(18));
    }

    public static void AddPurchaseAuditParameters(
        SqliteCommand command,
        string auditId,
        string requestId,
        MarketAcquisitionPurchaseAuditRequest request,
        string payloadJson,
        string payloadHash,
        DateTimeOffset createdAtUtc)
    {
        command.Parameters.AddWithValue("$auditId", auditId);
        command.Parameters.AddWithValue("$requestId", requestId);
        command.Parameters.AddWithValue("$lineId", request.LineId);
        command.Parameters.AddWithValue("$attemptId", request.AttemptId);
        command.Parameters.AddWithValue("$sequence", request.Sequence);
        command.Parameters.AddWithValue("$idempotencyKey", request.IdempotencyKey);
        command.Parameters.AddWithValue("$worldName", request.WorldName);
        command.Parameters.AddWithValue("$itemId", request.ItemId);
        command.Parameters.AddWithValue("$itemName", (object?)request.ItemName ?? DBNull.Value);
        command.Parameters.AddWithValue("$listingId", request.ListingId);
        command.Parameters.AddWithValue("$retainerName", request.RetainerName);
        command.Parameters.AddWithValue("$retainerId", request.RetainerId);
        command.Parameters.AddWithValue("$quantity", request.Quantity);
        command.Parameters.AddWithValue("$unitPrice", request.UnitPrice);
        command.Parameters.AddWithValue("$totalGil", request.TotalGil);
        command.Parameters.AddWithValue("$isHq", request.IsHq ? 1 : 0);
        command.Parameters.AddWithValue("$result", request.Result);
        command.Parameters.AddWithValue("$message", (object?)request.Message ?? DBNull.Value);
        command.Parameters.AddWithValue("$payloadJson", payloadJson);
        command.Parameters.AddWithValue("$payloadHash", payloadHash);
        command.Parameters.AddWithValue("$createdAtUtc", createdAtUtc.ToString("O"));
    }

    public static MarketAcquisitionBatchLineView ReadLineView(SqliteDataReader reader) =>
        new()
        {
            LineId = reader.GetString(0),
            BatchId = reader.GetString(1),
            Ordinal = reader.GetInt32(2),
            ItemId = checked((uint)reader.GetInt64(3)),
            ItemName = reader.IsDBNull(4) ? null : reader.GetString(4),
            ItemKind = reader.IsDBNull(5) ? null : reader.GetString(5),
            QuantityMode = reader.GetString(6),
            TargetQuantity = checked((uint)reader.GetInt64(7)),
            MaxQuantity = checked((uint)reader.GetInt64(8)),
            HqPolicy = reader.GetString(9),
            MaxUnitPrice = checked((uint)reader.GetInt64(10)),
            GilCap = checked((uint)reader.GetInt64(11)),
            Status = reader.GetString(12),
            PurchasedQuantity = checked((uint)reader.GetInt64(13)),
            SpentGil = checked((uint)reader.GetInt64(14)),
            LatestMessage = reader.IsDBNull(15) ? null : reader.GetString(15),
        };

    public static MarketAcquisitionRequestView ApplyLatestLifecycleEvent(
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

    public static MarketAcquisitionRequestView ApplyLatestAttemptEvent(
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

    public static StoredAttemptEvent ReadStoredAttemptEvent(SqliteDataReader reader) =>
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

    public static MarketAcquisitionRequestView ReadView(SqliteDataReader reader)
    {
        var request = ReadPrimaryCreateRequest(reader.GetString(7));
        var latestEvent = ReadLatestEvent(reader);
        var latestAttempt = ReadLatestAttempt(reader);

        return ToView(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3)),
            DateTimeOffset.Parse(reader.GetString(4)),
            reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)),
            reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)),
            request,
            latestEvent,
            latestAttempt);
    }

    public static MarketAcquisitionRequestView ToView(
        string id,
        int revision,
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
            Revision = revision,
            Status = status,
            Origin = NormalizeOrigin(request.Origin),
            CreatedByPluginInstanceId = string.IsNullOrWhiteSpace(request.CreatedByPluginInstanceId)
                ? null
                : request.CreatedByPluginInstanceId.Trim(),
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
            SelectedWorlds = request.SelectedWorlds,
            SweepScope = request.SweepScope,
            SweepDataCenters = request.SweepDataCenters,
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

    public static MarketAcquisitionClaimView ToClaimView(
        MarketAcquisitionRequestView request,
        string claimToken) =>
        new()
        {
            Id = request.Id,
            Revision = request.Revision,
            Status = request.Status,
            Origin = request.Origin,
            CreatedByPluginInstanceId = request.CreatedByPluginInstanceId,
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
            SelectedWorlds = request.SelectedWorlds,
            SweepScope = request.SweepScope,
            SweepDataCenters = request.SweepDataCenters,
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
            Lines = request.Lines,
            ClaimToken = claimToken,
        };

    public static MarketAcquisitionCreateRequest ReadPrimaryCreateRequest(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        if (document.RootElement.TryGetProperty("lines", out var linesElement) &&
            linesElement.ValueKind == JsonValueKind.Array)
        {
            var batch = document.Deserialize<MarketAcquisitionBatchCreateRequest>(JsonOptions)
                ?? throw new InvalidOperationException("Stored acquisition batch payload is invalid.");
            return ToPrimaryCreateRequest(batch);
        }

        return document.Deserialize<MarketAcquisitionCreateRequest>(JsonOptions)
            ?? throw new InvalidOperationException("Stored acquisition payload is invalid.");
    }

    public static MarketAcquisitionBatchCreateRequest ToBatchCreateRequest(MarketAcquisitionCreateRequest request) =>
        new()
        {
            SchemaVersion = request.SchemaVersion,
            IdempotencyKey = request.IdempotencyKey,
            Origin = request.Origin,
            CreatedByPluginInstanceId = request.CreatedByPluginInstanceId,
            TargetCharacterName = request.TargetCharacterName,
            TargetWorld = request.TargetWorld,
            Region = request.Region,
            WorldMode = request.WorldMode,
            SelectedWorlds = request.SelectedWorlds,
            SweepScope = request.SweepScope,
            SweepDataCenters = request.SweepDataCenters,
            ExpiresInSeconds = request.ExpiresInSeconds,
            Lines =
            [
                new MarketAcquisitionBatchLineCreateRequest
                {
                    ItemId = request.ItemId,
                    ItemName = request.ItemName,
                    QuantityMode = request.QuantityMode,
                    TargetQuantity = request.QuantityMode == "TargetQuantity" ? request.Quantity : 0,
                    MaxQuantity = request.QuantityMode == "AllBelowThreshold" ? request.Quantity : 0,
                    HqPolicy = request.HqPolicy,
                    MaxUnitPrice = request.MaxUnitPrice,
                    GilCap = request.MaxTotalGil,
                },
            ],
        };

    public static string BuildReplacementPayloadJson(
        MarketAcquisitionRequestView current,
        MarketAcquisitionBatchReplaceRequest request)
    {
        var region = NormalizeSupportedRegion(request.Region, nameof(request));
        var payload = new MarketAcquisitionBatchCreateRequest
        {
            SchemaVersion = 1,
            IdempotencyKey = current.Id,
            Origin = current.Origin,
            CreatedByPluginInstanceId = current.CreatedByPluginInstanceId,
            TargetCharacterName = current.TargetCharacterName,
            TargetWorld = current.TargetWorld,
            Region = region,
            WorldMode = request.WorldMode.Trim(),
            SelectedWorlds = NormalizeSelectedWorlds(request.SelectedWorlds),
            SweepScope = string.IsNullOrWhiteSpace(request.SweepScope) ? "Region" : request.SweepScope.Trim(),
            SweepDataCenters = NormalizeSweepDataCenters(region, request.SweepDataCenters),
            ExpiresInSeconds = request.ExpiresInSeconds,
            Lines = request.Lines,
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    public static MarketAcquisitionCreateRequest ToPrimaryCreateRequest(MarketAcquisitionBatchCreateRequest request)
    {
        var primaryLine = request.Lines.FirstOrDefault()
            ?? throw new InvalidOperationException("Stored acquisition batch has no lines.");
        return new MarketAcquisitionCreateRequest
        {
            SchemaVersion = request.SchemaVersion,
            IdempotencyKey = request.IdempotencyKey,
            Origin = request.Origin,
            CreatedByPluginInstanceId = request.CreatedByPluginInstanceId,
            TargetCharacterName = request.TargetCharacterName,
            TargetWorld = request.TargetWorld,
            Region = request.Region,
            ItemId = primaryLine.ItemId,
            ItemName = primaryLine.ItemName,
            QuantityMode = primaryLine.QuantityMode,
            Quantity = primaryLine.QuantityMode == "AllBelowThreshold"
                ? primaryLine.MaxQuantity
                : primaryLine.TargetQuantity,
            HqPolicy = primaryLine.HqPolicy,
            MaxUnitPrice = primaryLine.MaxUnitPrice,
            MaxTotalGil = primaryLine.GilCap,
            WorldMode = request.WorldMode,
            SelectedWorlds = request.SelectedWorlds,
            SweepScope = request.SweepScope,
            SweepDataCenters = request.SweepDataCenters,
            ExpiresInSeconds = request.ExpiresInSeconds,
        };
    }

    public static MarketAcquisitionBatchLineView ToFallbackLineView(MarketAcquisitionRequestView request) =>
        new()
        {
            LineId = $"{request.Id}-line-1",
            BatchId = request.Id,
            Ordinal = 0,
            ItemId = request.ItemId,
            ItemName = request.ItemName,
            QuantityMode = request.QuantityMode,
            TargetQuantity = request.QuantityMode == "TargetQuantity" ? request.Quantity : 0,
            MaxQuantity = request.QuantityMode == "AllBelowThreshold" ? request.Quantity : 0,
            HqPolicy = request.HqPolicy,
            MaxUnitPrice = request.MaxUnitPrice,
            GilCap = request.MaxTotalGil,
            Status = request.Status,
        };

    private static MarketAcquisitionLatestEvent? ReadLatestEvent(SqliteDataReader reader)
    {
        if (reader.FieldCount < 11 || reader.IsDBNull(8))
            return null;

        var eventPayload = reader.IsDBNull(9)
            ? null
            : JsonSerializer.Deserialize<MarketAcquisitionLifecycleRequest>(reader.GetString(9), JsonOptions);
        return new MarketAcquisitionLatestEvent(
            reader.GetString(8),
            eventPayload?.RunnerState,
            eventPayload?.Message,
            eventPayload?.Reason,
            DateTimeOffset.Parse(reader.GetString(10)));
    }

    private static MarketAcquisitionLatestAttempt? ReadLatestAttempt(SqliteDataReader reader)
    {
        if (reader.FieldCount < 18 || reader.IsDBNull(11))
            return null;

        return new MarketAcquisitionLatestAttempt(
            reader.GetString(11),
            reader.GetInt64(12),
            reader.GetString(13),
            reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17));
    }
}

internal sealed record MarketAcquisitionLatestEvent(
    string EventType,
    string? RunnerState,
    string? Message,
    string? Reason,
    DateTimeOffset CreatedAtUtc);

internal sealed record MarketAcquisitionLatestAttempt(
    string AttemptId,
    long Sequence,
    string EventType,
    string Phase,
    string? WorldName,
    string Result,
    string? PluginVersion);

internal readonly record struct StoredAttemptEvent(
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

internal readonly record struct StoredLineProgressEvent(
    string RequestId,
    string LineId,
    string AttemptId,
    long Sequence,
    string IdempotencyKey,
    string PayloadJson);

internal readonly record struct StoredPurchaseAudit(
    MarketAcquisitionPurchaseAuditView View,
    string IdempotencyKey,
    string PayloadJson);
