using System.Text.Json;
using Microsoft.Data.Sqlite;
using static MarketMafioso.Server.MarketAcquisition.MarketAcquisitionRequestPolicy;
using static MarketMafioso.Server.Persistence.MarketAcquisitionLinePersistence;
using static MarketMafioso.Server.Persistence.MarketAcquisitionRequestQueries;

namespace MarketMafioso.Server;

public sealed partial class MarketAcquisitionRequestStore
{
    public async Task<IReadOnlyList<MarketAcquisitionWorkOrderView>> ListWorkOrdersAsync(
        string? characterName,
        string? world,
        bool includeArchived,
        CancellationToken cancellationToken)
    {
        var requests = await ListRecentAsync(500, includeTerminal: true, cancellationToken).ConfigureAwait(false);
        var filtered = requests.Where(request =>
            (string.IsNullOrWhiteSpace(characterName) || request.TargetCharacterName.Equals(characterName.Trim(), StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(world) || request.TargetWorld.Equals(world.Trim(), StringComparison.OrdinalIgnoreCase)) &&
            (includeArchived || request.Status != MarketAcquisitionStatuses.Archived));

        var result = new List<MarketAcquisitionWorkOrderView>();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var request in filtered)
        {
            await EnsureWorkOrderRecordsAsync(connection, transaction: null, request, "migrated", cancellationToken).ConfigureAwait(false);
            result.Add(await BuildWorkOrderViewAsync(connection, transaction: null, request, cancellationToken).ConfigureAwait(false));
        }

        return result
            .OrderByDescending(workOrder => workOrder.Priority)
            .ThenBy(workOrder => WorkOrderStateRank(workOrder.State))
            .ThenByDescending(workOrder => workOrder.UpdatedAtUtc)
            .ToArray();
    }

    public async Task<MarketAcquisitionWorkOrderView?> GetWorkOrderAsync(
        string id,
        CancellationToken cancellationToken)
    {
        var request = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (request == null)
            return null;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureWorkOrderRecordsAsync(connection, transaction: null, request, "migrated", cancellationToken).ConfigureAwait(false);
        return await BuildWorkOrderViewAsync(connection, transaction: null, request, cancellationToken).ConfigureAwait(false);
    }

    public Task<MarketAcquisitionWorkOrderView?> ShelfWorkOrderAsync(
        string id,
        MarketAcquisitionWorkOrderCommand command,
        CancellationToken cancellationToken) =>
        ChangeWorkOrderStateAsync(
            id,
            command.ExpectedRevision,
            MarketAcquisitionStatuses.Shelved,
            [MarketAcquisitionStatuses.PendingPickup],
            "shelved",
            cancellationToken);

    public Task<MarketAcquisitionWorkOrderView?> RestoreWorkOrderAsync(
        string id,
        MarketAcquisitionWorkOrderCommand command,
        CancellationToken cancellationToken) =>
        ChangeWorkOrderStateAsync(
            id,
            command.ExpectedRevision,
            MarketAcquisitionStatuses.PendingPickup,
            [MarketAcquisitionStatuses.Shelved],
            "restored",
            cancellationToken);

    public Task<MarketAcquisitionWorkOrderView?> ArchiveWorkOrderAsync(
        string id,
        MarketAcquisitionWorkOrderCommand command,
        CancellationToken cancellationToken) =>
        ChangeWorkOrderStateAsync(
            id,
            command.ExpectedRevision,
            MarketAcquisitionStatuses.Archived,
            [
                MarketAcquisitionStatuses.PendingPickup,
                MarketAcquisitionStatuses.Shelved,
                MarketAcquisitionStatuses.Complete,
                MarketAcquisitionStatuses.Failed,
                MarketAcquisitionStatuses.Rejected,
                MarketAcquisitionStatuses.Cancelled,
            ],
            "archived",
            cancellationToken);

    public async Task<MarketAcquisitionWorkOrderView?> CloneWorkOrderAsync(
        string id,
        MarketAcquisitionWorkOrderCloneRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Idempotency key is required.", nameof(request));

        var source = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (source == null)
            return null;
        if (source.Revision != request.ExpectedRevision)
            throw new MarketAcquisitionRevisionConflictException(request.ExpectedRevision, source.Revision);

        var cloneRequest = ToWorkOrderBatchCreateRequest(source, request.IdempotencyKey);
        var created = await CreateBatchAsync(cloneRequest, cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureWorkOrderRecordsAsync(connection, (SqliteTransaction)transaction, created.Request, "cloned", cancellationToken).ConfigureAwait(false);
        await using var update = connection.CreateCommand();
        update.Transaction = (SqliteTransaction)transaction;
        update.CommandText =
            """
            UPDATE acquisition_work_order_metadata
            SET title = $title,
                parent_work_order_id = $parentId,
                updated_at_utc = $updatedAtUtc
            WHERE work_order_id = $id;
            """;
        update.Parameters.AddWithValue("$title", string.IsNullOrWhiteSpace(request.Title) ? DefaultWorkOrderTitle(created.Request) : request.Title.Trim());
        update.Parameters.AddWithValue("$parentId", source.Id);
        update.Parameters.AddWithValue("$updatedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$id", created.Request.Id);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return await GetWorkOrderAsync(created.Request.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MarketAcquisitionWorkOrderMergePreview?> PreviewWorkOrderMergeAsync(
        string targetId,
        string sourceId,
        CancellationToken cancellationToken)
    {
        var target = await GetAsync(targetId, cancellationToken).ConfigureAwait(false);
        var source = await GetAsync(sourceId, cancellationToken).ConfigureAwait(false);
        if (target == null || source == null)
            return null;

        return BuildMergePreview(target, source);
    }

    public async Task<MarketAcquisitionWorkOrderView?> MergeWorkOrdersAsync(
        string targetId,
        MarketAcquisitionWorkOrderMergeRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SourceWorkOrderId))
            throw new ArgumentException("Source work order id is required.", nameof(request));
        if (targetId.Equals(request.SourceWorkOrderId, StringComparison.Ordinal))
            throw new ArgumentException("A work order cannot be merged with itself.", nameof(request));

        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var target = await GetByIdAsync(connection, transaction, targetId, cancellationToken).ConfigureAwait(false);
        var source = await GetByIdAsync(connection, transaction, request.SourceWorkOrderId, cancellationToken).ConfigureAwait(false);
        if (target == null || source == null)
            return null;

        ValidateMergeRevision(target, request.ExpectedTargetRevision);
        ValidateMergeRevision(source, request.ExpectedSourceRevision);
        EnsureMergeableState(target);
        EnsureMergeableState(source);
        var preview = BuildMergePreview(target, source);
        if (!preview.CanMerge)
            throw new MarketAcquisitionMergeConflictException(preview);

        var mergedLines = target.Lines.ToList();
        var nextOrdinal = mergedLines.Count == 0 ? 0 : mergedLines.Max(line => line.Ordinal) + 1;
        foreach (var incoming in source.Lines.Select(ToBatchLineCreateRequest))
        {
            var existing = mergedLines.FirstOrDefault(line => CanCoalesce(line, incoming));
            if (existing == null)
            {
                var inserted = await InsertBatchLineAsync(connection, transaction, target.Id, incoming, nextOrdinal++, now, cancellationToken).ConfigureAwait(false);
                mergedLines.Add(inserted);
            }
            else
            {
                var coalesced = CoalesceLine(existing, incoming);
                await UpdateLineIntentAsync(connection, transaction, coalesced, now, cancellationToken).ConfigureAwait(false);
                mergedLines[mergedLines.FindIndex(line => line.LineId == existing.LineId)] = coalesced;
            }
        }

        var mergedTarget = target with { Revision = target.Revision + 1, Lines = mergedLines.OrderBy(line => line.Ordinal).ToArray() };
        var archivedSource = source with { Revision = source.Revision + 1, Status = MarketAcquisitionStatuses.Archived };
        await UpdateAggregateForMergeAsync(connection, (SqliteTransaction)transaction, mergedTarget, archivedSource, now, cancellationToken).ConfigureAwait(false);
        await EnsureWorkOrderRecordsAsync(connection, (SqliteTransaction)transaction, mergedTarget, "merged", cancellationToken).ConfigureAwait(false);
        await EnsureWorkOrderRecordsAsync(connection, (SqliteTransaction)transaction, archivedSource, "merged-into", cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetWorkOrderAsync(targetId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MarketAcquisitionExecutionLeaseView?> RenewLeaseAsync(
        string id,
        MarketAcquisitionLeaseRenewRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ClaimToken) || string.IsNullOrWhiteSpace(request.PluginInstanceId))
            throw new ArgumentException("Claim token and plugin instance id are required.", nameof(request));

        var now = DateTimeOffset.UtcNow;
        var expires = now.AddSeconds(executionLeaseSeconds);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var read = connection.CreateCommand();
        read.Transaction = (SqliteTransaction)transaction;
        read.CommandText = "SELECT status, claim_token, claimed_by FROM acquisition_requests WHERE id = $id;";
        read.Parameters.AddWithValue("$id", id);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var status = reader.GetString(0);
        var token = reader.IsDBNull(1) ? null : reader.GetString(1);
        var claimedBy = reader.IsDBNull(2) ? null : reader.GetString(2);
        await reader.DisposeAsync().ConfigureAwait(false);

        if (!MarketAcquisitionWorkOrderPolicy.IsLeaseRenewableStatus(status) ||
            !MatchesSecret(request.ClaimToken, token) ||
            !string.Equals(request.PluginInstanceId, claimedBy, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The execution lease is not owned by this plugin instance.");

        await using var update = connection.CreateCommand();
        update.Transaction = (SqliteTransaction)transaction;
        update.CommandText =
            """
            UPDATE acquisition_requests
            SET claimed_at_utc = $renewedAtUtc,
                claim_expires_at_utc = $expiresAtUtc
            WHERE id = $id;

            INSERT INTO acquisition_execution_leases (work_order_id, plugin_instance_id, renewed_at_utc, expires_at_utc)
            VALUES ($id, $pluginInstanceId, $renewedAtUtc, $expiresAtUtc)
            ON CONFLICT(work_order_id) DO UPDATE SET
                plugin_instance_id = excluded.plugin_instance_id,
                renewed_at_utc = excluded.renewed_at_utc,
                expires_at_utc = excluded.expires_at_utc;
            """;
        update.Parameters.AddWithValue("$id", id);
        update.Parameters.AddWithValue("$pluginInstanceId", request.PluginInstanceId);
        update.Parameters.AddWithValue("$renewedAtUtc", now.ToString("O"));
        update.Parameters.AddWithValue("$expiresAtUtc", expires.ToString("O"));
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new MarketAcquisitionExecutionLeaseView
        {
            WorkOrderId = id,
            PluginInstanceId = request.PluginInstanceId,
            RenewedAtUtc = now,
            ExpiresAtUtc = expires,
        };
    }

    public async Task<MarketAcquisitionWorkOrderHistoryView?> GetWorkOrderHistoryAsync(string id, CancellationToken cancellationToken)
    {
        var workOrder = await GetWorkOrderAsync(id, cancellationToken).ConfigureAwait(false);
        if (workOrder == null)
            return null;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return new MarketAcquisitionWorkOrderHistoryView
        {
            WorkOrder = workOrder,
            Revisions = await ReadRevisionsAsync(connection, id, cancellationToken).ConfigureAwait(false),
            ExecutionSnapshots = await ReadExecutionSnapshotsAsync(connection, id, cancellationToken).ConfigureAwait(false),
            Receipts = await ReadReceiptsAsync(connection, id, cancellationToken).ConfigureAwait(false),
        };
    }

    private async Task<MarketAcquisitionWorkOrderView?> ChangeWorkOrderStateAsync(
        string id,
        int expectedRevision,
        string targetStatus,
        IReadOnlyCollection<string> allowedStatuses,
        string changeKind,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var current = await GetByIdAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
        if (current == null)
            return null;
        if (current.Revision != expectedRevision)
            throw new MarketAcquisitionRevisionConflictException(expectedRevision, current.Revision);
        if (!allowedStatuses.Contains(current.Status))
            throw new MarketAcquisitionInvalidTransitionException(current.Status, targetStatus);

        var updated = current with { Revision = current.Revision + 1, Status = targetStatus };
        await using var update = connection.CreateCommand();
        update.Transaction = (SqliteTransaction)transaction;
        update.CommandText =
            """
            UPDATE acquisition_requests
            SET status = $status,
                revision = $revision,
                claimed_at_utc = NULL,
                claim_expires_at_utc = NULL,
                claim_token = NULL,
                claimed_by = NULL
            WHERE id = $id AND revision = $expectedRevision;
            """;
        update.Parameters.AddWithValue("$status", targetStatus);
        update.Parameters.AddWithValue("$revision", updated.Revision);
        update.Parameters.AddWithValue("$id", id);
        update.Parameters.AddWithValue("$expectedRevision", expectedRevision);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new MarketAcquisitionRevisionConflictException(expectedRevision, current.Revision);

        await EnsureWorkOrderRecordsAsync(connection, (SqliteTransaction)transaction, updated, changeKind, cancellationToken).ConfigureAwait(false);
        await using var metadata = connection.CreateCommand();
        metadata.Transaction = (SqliteTransaction)transaction;
        metadata.CommandText =
            """
            UPDATE acquisition_work_order_metadata
            SET updated_at_utc = $now,
                shelved_at_utc = CASE WHEN $status = 'Shelved' THEN $now ELSE NULL END,
                archived_at_utc = CASE WHEN $status = 'Archived' THEN $now ELSE archived_at_utc END
            WHERE work_order_id = $id;
            """;
        metadata.Parameters.AddWithValue("$now", now.ToString("O"));
        metadata.Parameters.AddWithValue("$status", targetStatus);
        metadata.Parameters.AddWithValue("$id", id);
        await metadata.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetWorkOrderAsync(id, cancellationToken).ConfigureAwait(false);
    }

    private static MarketAcquisitionWorkOrderMergePreview BuildMergePreview(MarketAcquisitionRequestView target, MarketAcquisitionRequestView source)
    {
        var conflicts = new List<MarketAcquisitionWorkOrderMergeConflict>();
        AddMergeConflict(conflicts, "targetCharacter", target.TargetCharacterName, source.TargetCharacterName);
        AddMergeConflict(conflicts, "targetWorld", target.TargetWorld, source.TargetWorld);
        AddMergeConflict(conflicts, "region", target.Region, source.Region);
        AddMergeConflict(conflicts, "worldMode", target.WorldMode, source.WorldMode);
        AddMergeConflict(conflicts, "selectedWorlds", JoinNormalized(target.SelectedWorlds), JoinNormalized(source.SelectedWorlds));
        AddMergeConflict(conflicts, "sweepScope", target.SweepScope, source.SweepScope);
        AddMergeConflict(conflicts, "sweepDataCenters", JoinNormalized(target.SweepDataCenters), JoinNormalized(source.SweepDataCenters));

        foreach (var targetLine in target.Lines)
        {
            foreach (var sourceLine in source.Lines.Where(line => line.ItemId == targetLine.ItemId))
            {
                AddMergeConflict(conflicts, $"item.{targetLine.ItemId}.quantityMode", targetLine.QuantityMode, sourceLine.QuantityMode);
                AddMergeConflict(conflicts, $"item.{targetLine.ItemId}.hqPolicy", targetLine.HqPolicy, sourceLine.HqPolicy);
                AddMergeConflict(conflicts, $"item.{targetLine.ItemId}.maxUnitPrice", targetLine.MaxUnitPrice.ToString(), sourceLine.MaxUnitPrice.ToString());
                AddMergeConflict(conflicts, $"item.{targetLine.ItemId}.maxQuantity", targetLine.MaxQuantity.ToString(), sourceLine.MaxQuantity.ToString());
                AddMergeConflict(conflicts, $"item.{targetLine.ItemId}.gilCap", targetLine.GilCap.ToString(), sourceLine.GilCap.ToString());
            }
        }

        return new MarketAcquisitionWorkOrderMergePreview
        {
            TargetWorkOrderId = target.Id,
            SourceWorkOrderId = source.Id,
            ResultLineCount = target.Lines.Concat(source.Lines).Select(line => line.ItemId).Distinct().Count(),
            Conflicts = conflicts,
        };
    }

    private static void AddMergeConflict(List<MarketAcquisitionWorkOrderMergeConflict> conflicts, string field, string target, string source)
    {
        if (string.Equals(target, source, StringComparison.OrdinalIgnoreCase))
            return;
        conflicts.Add(new MarketAcquisitionWorkOrderMergeConflict
        {
            Field = field,
            TargetValue = target,
            SourceValue = source,
            Message = $"{field} differs; choose the intended constraint before merging.",
        });
    }

    private static string JoinNormalized(IEnumerable<string> values) =>
        string.Join("|", values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));

    private static void ValidateMergeRevision(MarketAcquisitionRequestView workOrder, int expected)
    {
        if (workOrder.Revision != expected)
            throw new MarketAcquisitionRevisionConflictException(expected, workOrder.Revision);
    }

    private static void EnsureMergeableState(MarketAcquisitionRequestView workOrder)
    {
        if (workOrder.Status is not (MarketAcquisitionStatuses.PendingPickup or MarketAcquisitionStatuses.Shelved))
            throw new MarketAcquisitionInvalidTransitionException(workOrder.Status, "mergeable work order");
    }

    private static MarketAcquisitionBatchCreateRequest ToWorkOrderBatchCreateRequest(MarketAcquisitionRequestView request, string idempotencyKey) =>
        new()
        {
            SchemaVersion = 1,
            IdempotencyKey = idempotencyKey,
            Origin = request.Origin,
            CreatedByPluginInstanceId = request.CreatedByPluginInstanceId,
            TargetCharacterName = request.TargetCharacterName,
            TargetWorld = request.TargetWorld,
            Region = request.Region,
            WorldMode = request.WorldMode,
            SelectedWorlds = request.SelectedWorlds,
            SweepScope = request.SweepScope,
            SweepDataCenters = request.SweepDataCenters,
            ExpiresInSeconds = 86400,
            Lines = request.Lines.Select(ToBatchLineCreateRequest).ToArray(),
        };

    private static MarketAcquisitionBatchLineCreateRequest ToBatchLineCreateRequest(MarketAcquisitionBatchLineView line) =>
        new()
        {
            ItemId = line.ItemId,
            ItemName = line.ItemName,
            ItemKind = line.ItemKind,
            QuantityMode = line.QuantityMode,
            TargetQuantity = line.TargetQuantity,
            MaxQuantity = line.MaxQuantity,
            HqPolicy = line.HqPolicy,
            MaxUnitPrice = line.MaxUnitPrice,
            GilCap = line.GilCap,
        };

    private static int WorkOrderStateRank(string state) => state switch
    {
        MarketAcquisitionWorkOrderStates.Working => 0,
        MarketAcquisitionWorkOrderStates.Recovery => 1,
        MarketAcquisitionWorkOrderStates.Inbox => 2,
        MarketAcquisitionWorkOrderStates.Shelved => 3,
        _ => 4,
    };

    private static string ResolveWorkOrderState(string status) => status switch
    {
        MarketAcquisitionStatuses.PendingPickup => MarketAcquisitionWorkOrderStates.Inbox,
        MarketAcquisitionStatuses.Claimed or MarketAcquisitionStatuses.AcceptedInPlugin or MarketAcquisitionStatuses.Running => MarketAcquisitionWorkOrderStates.Working,
        MarketAcquisitionStatuses.RecoveryRequired or MarketAcquisitionStatuses.Failed => MarketAcquisitionWorkOrderStates.Recovery,
        MarketAcquisitionStatuses.Shelved => MarketAcquisitionWorkOrderStates.Shelved,
        MarketAcquisitionStatuses.Complete => MarketAcquisitionWorkOrderStates.Completed,
        MarketAcquisitionStatuses.Cancelled or MarketAcquisitionStatuses.Rejected => MarketAcquisitionWorkOrderStates.Cancelled,
        MarketAcquisitionStatuses.Archived => MarketAcquisitionWorkOrderStates.Archived,
        _ => MarketAcquisitionWorkOrderStates.Inbox,
    };

    private static string DefaultWorkOrderTitle(MarketAcquisitionRequestView request) => request.Lines.Count switch
    {
        0 => "Acquisition work order",
        1 => string.IsNullOrWhiteSpace(request.Lines[0].ItemName) ? $"Item {request.Lines[0].ItemId}" : request.Lines[0].ItemName!,
        _ => $"{request.Lines.Count:N0} item acquisition",
    };

    private static async Task EnsureWorkOrderRecordsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        MarketAcquisitionRequestView request,
        string changeKind,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT OR IGNORE INTO acquisition_work_order_metadata (work_order_id, title, priority, updated_at_utc)
            VALUES ($id, $title, 0, $updatedAtUtc);

            INSERT OR IGNORE INTO acquisition_work_order_revisions (work_order_id, revision, change_kind, snapshot_json, created_at_utc)
            VALUES ($id, $revision, $changeKind, $snapshotJson, $updatedAtUtc);
            """;
        command.Parameters.AddWithValue("$id", request.Id);
        command.Parameters.AddWithValue("$title", DefaultWorkOrderTitle(request));
        command.Parameters.AddWithValue("$revision", request.Revision);
        command.Parameters.AddWithValue("$changeKind", changeKind);
        command.Parameters.AddWithValue("$snapshotJson", JsonSerializer.Serialize(request, JsonOptions));
        command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<MarketAcquisitionWorkOrderView> BuildWorkOrderViewAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        MarketAcquisitionRequestView request,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT title, priority, updated_at_utc, shelved_at_utc, archived_at_utc, parent_work_order_id, merge_source_work_order_id
            FROM acquisition_work_order_metadata
            WHERE work_order_id = $id;
            """;
        command.Parameters.AddWithValue("$id", request.Id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException($"Work order metadata for {request.Id} was not initialized.");
        return new MarketAcquisitionWorkOrderView
        {
            Id = request.Id,
            Revision = request.Revision,
            State = ResolveWorkOrderState(request.Status),
            Title = reader.GetString(0),
            Priority = reader.GetInt32(1),
            UpdatedAtUtc = DateTimeOffset.Parse(reader.GetString(2)),
            ShelvedAtUtc = reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3)),
            ArchivedAtUtc = reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)),
            ParentWorkOrderId = reader.IsDBNull(5) ? null : reader.GetString(5),
            MergeSourceWorkOrderId = reader.IsDBNull(6) ? null : reader.GetString(6),
            Request = request,
        };
    }

    private static async Task UpdateAggregateForMergeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MarketAcquisitionRequestView target,
        MarketAcquisitionRequestView source,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var updateTarget = connection.CreateCommand();
        updateTarget.Transaction = transaction;
        updateTarget.CommandText = "UPDATE acquisition_requests SET revision = $revision WHERE id = $id;";
        updateTarget.Parameters.AddWithValue("$revision", target.Revision);
        updateTarget.Parameters.AddWithValue("$id", target.Id);
        await updateTarget.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var updateSource = connection.CreateCommand();
        updateSource.Transaction = transaction;
        updateSource.CommandText = "UPDATE acquisition_requests SET revision = $revision, status = $status WHERE id = $id;";
        updateSource.Parameters.AddWithValue("$revision", source.Revision);
        updateSource.Parameters.AddWithValue("$status", source.Status);
        updateSource.Parameters.AddWithValue("$id", source.Id);
        await updateSource.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var metadata = connection.CreateCommand();
        metadata.Transaction = transaction;
        metadata.CommandText =
            """
            UPDATE acquisition_work_order_metadata
            SET merge_source_work_order_id = $sourceId, updated_at_utc = $now
            WHERE work_order_id = $targetId;
            UPDATE acquisition_work_order_metadata
            SET archived_at_utc = $now, updated_at_utc = $now
            WHERE work_order_id = $sourceId;
            """;
        metadata.Parameters.AddWithValue("$targetId", target.Id);
        metadata.Parameters.AddWithValue("$sourceId", source.Id);
        metadata.Parameters.AddWithValue("$now", now.ToString("O"));
        await metadata.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RecordExecutionArtifactsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MarketAcquisitionRequestView request,
        string eventType,
        string? message,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        if (eventType == "accept")
        {
            await using var snapshot = connection.CreateCommand();
            snapshot.Transaction = transaction;
            snapshot.CommandText =
                """
                INSERT OR IGNORE INTO acquisition_execution_snapshots (snapshot_id, work_order_id, revision, request_json, created_at_utc)
                VALUES ($snapshotId, $workOrderId, $revision, $requestJson, $createdAtUtc);
                """;
            snapshot.Parameters.AddWithValue("$snapshotId", Guid.NewGuid().ToString("N"));
            snapshot.Parameters.AddWithValue("$workOrderId", request.Id);
            snapshot.Parameters.AddWithValue("$revision", request.Revision);
            snapshot.Parameters.AddWithValue("$requestJson", JsonSerializer.Serialize(request, JsonOptions));
            snapshot.Parameters.AddWithValue("$createdAtUtc", createdAtUtc.ToString("O"));
            await snapshot.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (eventType is not ("complete" or "fail"))
            return;

        await using (var clearLease = connection.CreateCommand())
        {
            clearLease.Transaction = transaction;
            clearLease.CommandText = "DELETE FROM acquisition_execution_leases WHERE work_order_id = $workOrderId;";
            clearLease.Parameters.AddWithValue("$workOrderId", request.Id);
            await clearLease.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var receipt = connection.CreateCommand();
        receipt.Transaction = transaction;
        receipt.CommandText =
            """
            INSERT INTO acquisition_run_receipts (
                receipt_id, work_order_id, outcome, purchased_quantity, spent_gil, message, created_at_utc)
            VALUES ($receiptId, $workOrderId, $outcome, $purchasedQuantity, $spentGil, $message, $createdAtUtc);
            """;
        receipt.Parameters.AddWithValue("$receiptId", Guid.NewGuid().ToString("N"));
        receipt.Parameters.AddWithValue("$workOrderId", request.Id);
        receipt.Parameters.AddWithValue("$outcome", eventType == "complete" ? "Completed" : "Failed");
        receipt.Parameters.AddWithValue("$purchasedQuantity", request.Lines.Sum(line => (long)line.PurchasedQuantity));
        receipt.Parameters.AddWithValue("$spentGil", request.Lines.Sum(line => (long)line.SpentGil));
        receipt.Parameters.AddWithValue("$message", (object?)message ?? DBNull.Value);
        receipt.Parameters.AddWithValue("$createdAtUtc", createdAtUtc.ToString("O"));
        await receipt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<MarketAcquisitionWorkOrderRevisionView>> ReadRevisionsAsync(SqliteConnection connection, string id, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT revision, change_kind, snapshot_json, created_at_utc FROM acquisition_work_order_revisions WHERE work_order_id = $id ORDER BY revision;";
        command.Parameters.AddWithValue("$id", id);
        var result = new List<MarketAcquisitionWorkOrderRevisionView>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            result.Add(new MarketAcquisitionWorkOrderRevisionView
            {
                WorkOrderId = id,
                Revision = reader.GetInt32(0),
                ChangeKind = reader.GetString(1),
                Snapshot = JsonSerializer.Deserialize<MarketAcquisitionRequestView>(reader.GetString(2), JsonOptions)!,
                CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(3)),
            });
        }
        return result;
    }

    private static async Task<IReadOnlyList<MarketAcquisitionExecutionSnapshotView>> ReadExecutionSnapshotsAsync(SqliteConnection connection, string id, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_id, revision, request_json, created_at_utc FROM acquisition_execution_snapshots WHERE work_order_id = $id ORDER BY created_at_utc;";
        command.Parameters.AddWithValue("$id", id);
        var result = new List<MarketAcquisitionExecutionSnapshotView>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            result.Add(new MarketAcquisitionExecutionSnapshotView
            {
                SnapshotId = reader.GetString(0), WorkOrderId = id, Revision = reader.GetInt32(1),
                Request = JsonSerializer.Deserialize<MarketAcquisitionRequestView>(reader.GetString(2), JsonOptions)!,
                CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(3)),
            });
        }
        return result;
    }

    private static async Task<IReadOnlyList<MarketAcquisitionRunReceiptView>> ReadReceiptsAsync(SqliteConnection connection, string id, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT receipt_id, outcome, purchased_quantity, spent_gil, message, created_at_utc FROM acquisition_run_receipts WHERE work_order_id = $id ORDER BY created_at_utc;";
        command.Parameters.AddWithValue("$id", id);
        var result = new List<MarketAcquisitionRunReceiptView>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            result.Add(new MarketAcquisitionRunReceiptView
            {
                ReceiptId = reader.GetString(0), WorkOrderId = id, Outcome = reader.GetString(1),
                PurchasedQuantity = checked((uint)reader.GetInt64(2)), SpentGil = checked((ulong)reader.GetInt64(3)),
                Message = reader.IsDBNull(4) ? null : reader.GetString(4), CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(5)),
            });
        }
        return result;
    }
}

public static class MarketAcquisitionWorkOrderPolicy
{
    public static bool IsLeaseRenewableStatus(string status) => status is
        MarketAcquisitionStatuses.Claimed or
        MarketAcquisitionStatuses.AcceptedInPlugin or
        MarketAcquisitionStatuses.Running or
        MarketAcquisitionStatuses.RecoveryRequired;
}

public sealed class MarketAcquisitionMergeConflictException : Exception
{
    public MarketAcquisitionMergeConflictException(MarketAcquisitionWorkOrderMergePreview preview)
        : base("Work orders contain constraints that require an explicit choice before merging.")
    {
        Preview = preview;
    }

    public MarketAcquisitionWorkOrderMergePreview Preview { get; }
}
