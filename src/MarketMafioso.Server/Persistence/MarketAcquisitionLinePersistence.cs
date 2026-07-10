using Microsoft.Data.Sqlite;
using static MarketMafioso.Server.Persistence.MarketAcquisitionRecordMapper;

namespace MarketMafioso.Server.Persistence;

internal static class MarketAcquisitionLinePersistence
{
    public static async Task<IReadOnlyList<MarketAcquisitionBatchLineView>> InsertBatchLinesAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string requestId,
        IReadOnlyList<MarketAcquisitionBatchLineCreateRequest> lines,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var views = new List<MarketAcquisitionBatchLineView>(lines.Count);
        for (var index = 0; index < lines.Count; index++)
        {
            views.Add(await InsertBatchLineAsync(
                connection,
                transaction,
                requestId,
                lines[index],
                index,
                now,
                cancellationToken).ConfigureAwait(false));
        }

        return views;
    }

    public static async Task<MarketAcquisitionBatchLineView> InsertBatchLineAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string requestId,
        MarketAcquisitionBatchLineCreateRequest line,
        int ordinal,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var view = new MarketAcquisitionBatchLineView
        {
            LineId = $"{requestId}-line-{ordinal + 1}",
            BatchId = requestId,
            Ordinal = ordinal,
            ItemId = line.ItemId,
            ItemName = line.ItemName,
            ItemKind = line.ItemKind,
            QuantityMode = line.QuantityMode,
            TargetQuantity = line.TargetQuantity,
            MaxQuantity = line.MaxQuantity,
            HqPolicy = line.HqPolicy,
            MaxUnitPrice = line.MaxUnitPrice,
            GilCap = line.GilCap,
            Status = MarketAcquisitionStatuses.PendingPickup,
        };

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            INSERT INTO acquisition_batch_lines (
                line_id,
                request_id,
                ordinal,
                item_id,
                item_name,
                item_kind,
                quantity_mode,
                target_quantity,
                max_quantity,
                hq_policy,
                max_unit_price,
                gil_cap,
                status,
                purchased_quantity,
                spent_gil,
                latest_message,
                created_at_utc,
                updated_at_utc
            )
            VALUES (
                $lineId,
                $requestId,
                $ordinal,
                $itemId,
                $itemName,
                $itemKind,
                $quantityMode,
                $targetQuantity,
                $maxQuantity,
                $hqPolicy,
                $maxUnitPrice,
                $gilCap,
                $status,
                0,
                0,
                NULL,
                $createdAtUtc,
                $updatedAtUtc
            );
            """;
        command.Parameters.AddWithValue("$lineId", view.LineId);
        command.Parameters.AddWithValue("$requestId", requestId);
        command.Parameters.AddWithValue("$ordinal", view.Ordinal);
        command.Parameters.AddWithValue("$itemId", view.ItemId);
        command.Parameters.AddWithValue("$itemName", (object?)view.ItemName ?? DBNull.Value);
        command.Parameters.AddWithValue("$itemKind", (object?)view.ItemKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$quantityMode", view.QuantityMode);
        command.Parameters.AddWithValue("$targetQuantity", view.TargetQuantity);
        command.Parameters.AddWithValue("$maxQuantity", view.MaxQuantity);
        command.Parameters.AddWithValue("$hqPolicy", view.HqPolicy);
        command.Parameters.AddWithValue("$maxUnitPrice", view.MaxUnitPrice);
        command.Parameters.AddWithValue("$gilCap", view.GilCap);
        command.Parameters.AddWithValue("$status", view.Status);
        command.Parameters.AddWithValue("$createdAtUtc", now.ToString("O"));
        command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return view;
    }

    public static bool CanCoalesce(
        MarketAcquisitionBatchLineView existing,
        MarketAcquisitionBatchLineCreateRequest incoming) =>
        existing.Status == MarketAcquisitionStatuses.PendingPickup &&
        existing.ItemId == incoming.ItemId &&
        string.Equals(existing.QuantityMode, incoming.QuantityMode, StringComparison.Ordinal) &&
        string.Equals(existing.HqPolicy, incoming.HqPolicy, StringComparison.Ordinal) &&
        existing.MaxUnitPrice == incoming.MaxUnitPrice &&
        existing.GilCap == incoming.GilCap;

    public static MarketAcquisitionBatchLineView CoalesceLine(
        MarketAcquisitionBatchLineView existing,
        MarketAcquisitionBatchLineCreateRequest incoming) =>
        existing with
        {
            TargetQuantity = checked(existing.TargetQuantity + incoming.TargetQuantity),
            MaxQuantity = CoalesceMaxQuantity(existing.MaxQuantity, incoming.MaxQuantity),
            ItemName = string.IsNullOrWhiteSpace(existing.ItemName) ? incoming.ItemName : existing.ItemName,
            ItemKind = string.IsNullOrWhiteSpace(existing.ItemKind) ? incoming.ItemKind : existing.ItemKind,
        };

    public static uint CoalesceMaxQuantity(uint existing, uint incoming)
    {
        if (existing == 0 || incoming == 0)
            return 0;

        return checked(existing + incoming);
    }

    public static async Task UpdateLineIntentAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        MarketAcquisitionBatchLineView line,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            UPDATE acquisition_batch_lines
            SET item_name = $itemName,
                item_kind = $itemKind,
                target_quantity = $targetQuantity,
                max_quantity = $maxQuantity,
                updated_at_utc = $updatedAtUtc
            WHERE line_id = $lineId;
            """;
        command.Parameters.AddWithValue("$itemName", (object?)line.ItemName ?? DBNull.Value);
        command.Parameters.AddWithValue("$itemKind", (object?)line.ItemKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$targetQuantity", line.TargetQuantity);
        command.Parameters.AddWithValue("$maxQuantity", line.MaxQuantity);
        command.Parameters.AddWithValue("$updatedAtUtc", now.ToString("O"));
        command.Parameters.AddWithValue("$lineId", line.LineId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task DeleteBatchLinesAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "DELETE FROM acquisition_batch_lines WHERE request_id = $requestId;";
        command.Parameters.AddWithValue("$requestId", requestId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<MarketAcquisitionBatchLineView?> LoadLineByIdAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string lineId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            SELECT
                line_id,
                request_id,
                ordinal,
                item_id,
                item_name,
                item_kind,
                quantity_mode,
                target_quantity,
                max_quantity,
                hq_policy,
                max_unit_price,
                gil_cap,
                status,
                purchased_quantity,
                spent_gil,
                latest_message
            FROM acquisition_batch_lines
            WHERE line_id = $lineId;
            """;
        command.Parameters.AddWithValue("$lineId", lineId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadLineView(reader)
            : null;
    }

    public static async Task<IReadOnlyList<MarketAcquisitionBatchLineView>> LoadLinesAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction? transaction,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        if (transaction != null)
            command.Transaction = (SqliteTransaction)transaction;

        command.CommandText =
            """
            SELECT
                line_id,
                request_id,
                ordinal,
                item_id,
                item_name,
                item_kind,
                quantity_mode,
                target_quantity,
                max_quantity,
                hq_policy,
                max_unit_price,
                gil_cap,
                status,
                purchased_quantity,
                spent_gil,
                latest_message
            FROM acquisition_batch_lines
            WHERE request_id = $requestId
            ORDER BY ordinal ASC;
            """;
        command.Parameters.AddWithValue("$requestId", requestId);

        var lines = new List<MarketAcquisitionBatchLineView>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            lines.Add(ReadLineView(reader));

        return lines;
    }
}
