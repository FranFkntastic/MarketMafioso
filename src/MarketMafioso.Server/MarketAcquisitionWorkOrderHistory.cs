using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MarketMafioso.Server;

public sealed partial class MarketAcquisitionRequestStore
{
    public async Task<MarketAcquisitionWorkOrderHistoryView?> GetWorkOrderHistoryAsync(
        string id,
        CancellationToken cancellationToken)
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

    private static async Task<IReadOnlyList<MarketAcquisitionWorkOrderRevisionView>> ReadRevisionsAsync(
        SqliteConnection connection,
        string id,
        CancellationToken token)
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

    private static async Task<IReadOnlyList<MarketAcquisitionExecutionSnapshotView>> ReadExecutionSnapshotsAsync(
        SqliteConnection connection,
        string id,
        CancellationToken token)
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
                SnapshotId = reader.GetString(0),
                WorkOrderId = id,
                Revision = reader.GetInt32(1),
                Request = JsonSerializer.Deserialize<MarketAcquisitionRequestView>(reader.GetString(2), JsonOptions)!,
                CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(3)),
            });
        }

        return result;
    }

    private static async Task<IReadOnlyList<MarketAcquisitionRunReceiptView>> ReadReceiptsAsync(
        SqliteConnection connection,
        string id,
        CancellationToken token)
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
                ReceiptId = reader.GetString(0),
                WorkOrderId = id,
                Outcome = reader.GetString(1),
                PurchasedQuantity = checked((uint)reader.GetInt64(2)),
                SpentGil = checked((ulong)reader.GetInt64(3)),
                Message = reader.IsDBNull(4) ? null : reader.GetString(4),
                CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(5)),
            });
        }

        return result;
    }
}
