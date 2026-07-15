using Microsoft.Data.Sqlite;

namespace MarketMafioso.Server.Persistence;

internal static class MarketAcquisitionSchema
{
    public static void Initialize(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS acquisition_requests (
                id TEXT NOT NULL PRIMARY KEY,
                revision INTEGER NOT NULL DEFAULT 1,
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

            CREATE TABLE IF NOT EXISTS acquisition_batch_lines (
                line_id TEXT NOT NULL PRIMARY KEY,
                request_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                item_id INTEGER NOT NULL,
                item_name TEXT NULL,
                item_kind TEXT NULL,
                quantity_mode TEXT NOT NULL,
                target_quantity INTEGER NOT NULL,
                max_quantity INTEGER NOT NULL,
                hq_policy TEXT NOT NULL,
                max_unit_price INTEGER NOT NULL,
                gil_cap INTEGER NOT NULL,
                status TEXT NOT NULL,
                purchased_quantity INTEGER NOT NULL DEFAULT 0,
                spent_gil INTEGER NOT NULL DEFAULT 0,
                latest_message TEXT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_acquisition_batch_lines_request
                ON acquisition_batch_lines(request_id, ordinal);

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

            CREATE TABLE IF NOT EXISTS acquisition_line_progress_events (
                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                request_id TEXT NOT NULL,
                line_id TEXT NOT NULL,
                attempt_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                idempotency_key TEXT NOT NULL UNIQUE,
                status TEXT NOT NULL,
                purchased_quantity INTEGER NOT NULL,
                spent_gil INTEGER NOT NULL,
                payload_json TEXT NOT NULL,
                payload_hash TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                UNIQUE(request_id, line_id, attempt_id, sequence),
                FOREIGN KEY(line_id) REFERENCES acquisition_batch_lines(line_id)
            );

            CREATE TABLE IF NOT EXISTS acquisition_purchase_audit (
                audit_id TEXT PRIMARY KEY,
                request_id TEXT NOT NULL,
                line_id TEXT NOT NULL,
                attempt_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                idempotency_key TEXT NOT NULL UNIQUE,
                world_name TEXT NOT NULL,
                item_id INTEGER NOT NULL,
                item_name TEXT NULL,
                listing_id TEXT NOT NULL,
                retainer_name TEXT NOT NULL,
                retainer_id TEXT NOT NULL,
                quantity INTEGER NOT NULL,
                unit_price INTEGER NOT NULL,
                total_gil INTEGER NOT NULL,
                is_hq INTEGER NOT NULL,
                result TEXT NOT NULL,
                message TEXT NULL,
                payload_json TEXT NOT NULL,
                payload_hash TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                UNIQUE(request_id, attempt_id, sequence),
                FOREIGN KEY(line_id) REFERENCES acquisition_batch_lines(line_id)
            );

            CREATE TABLE IF NOT EXISTS acquisition_market_observations (
                observation_id TEXT PRIMARY KEY,
                request_id TEXT NOT NULL,
                line_id TEXT NOT NULL,
                attempt_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                idempotency_key TEXT NOT NULL UNIQUE,
                item_id INTEGER NOT NULL,
                item_name TEXT NULL,
                data_center TEXT NOT NULL,
                world_name TEXT NOT NULL,
                read_state TEXT NOT NULL,
                reported_listing_count INTEGER NOT NULL,
                listing_capacity INTEGER NOT NULL,
                is_truncated INTEGER NOT NULL,
                observed_at_utc TEXT NOT NULL,
                listings_json TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                payload_hash TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                UNIQUE(request_id, attempt_id, sequence),
                FOREIGN KEY(line_id) REFERENCES acquisition_batch_lines(line_id)
            );

            CREATE INDEX IF NOT EXISTS idx_acquisition_market_observations_request
                ON acquisition_market_observations(request_id, created_at_utc);

            CREATE TABLE IF NOT EXISTS acquisition_work_order_metadata (
                work_order_id TEXT NOT NULL PRIMARY KEY,
                title TEXT NOT NULL,
                priority INTEGER NOT NULL DEFAULT 0,
                updated_at_utc TEXT NOT NULL,
                shelved_at_utc TEXT NULL,
                archived_at_utc TEXT NULL,
                parent_work_order_id TEXT NULL,
                merge_source_work_order_id TEXT NULL,
                FOREIGN KEY(work_order_id) REFERENCES acquisition_requests(id)
            );

            CREATE TABLE IF NOT EXISTS acquisition_work_order_revisions (
                work_order_id TEXT NOT NULL,
                revision INTEGER NOT NULL,
                change_kind TEXT NOT NULL,
                snapshot_json TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                PRIMARY KEY(work_order_id, revision),
                FOREIGN KEY(work_order_id) REFERENCES acquisition_requests(id)
            );

            CREATE TABLE IF NOT EXISTS acquisition_execution_leases (
                work_order_id TEXT NOT NULL PRIMARY KEY,
                plugin_instance_id TEXT NOT NULL,
                renewed_at_utc TEXT NOT NULL,
                expires_at_utc TEXT NOT NULL,
                FOREIGN KEY(work_order_id) REFERENCES acquisition_requests(id)
            );

            CREATE TABLE IF NOT EXISTS acquisition_execution_snapshots (
                snapshot_id TEXT NOT NULL PRIMARY KEY,
                work_order_id TEXT NOT NULL,
                revision INTEGER NOT NULL,
                request_json TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                UNIQUE(work_order_id, revision),
                FOREIGN KEY(work_order_id) REFERENCES acquisition_requests(id)
            );

            CREATE TABLE IF NOT EXISTS acquisition_run_receipts (
                receipt_id TEXT NOT NULL PRIMARY KEY,
                work_order_id TEXT NOT NULL,
                outcome TEXT NOT NULL,
                purchased_quantity INTEGER NOT NULL,
                spent_gil INTEGER NOT NULL,
                message TEXT NULL,
                created_at_utc TEXT NOT NULL,
                UNIQUE(work_order_id, outcome, created_at_utc),
                FOREIGN KEY(work_order_id) REFERENCES acquisition_requests(id)
            );
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "acquisition_requests", "revision", "INTEGER NOT NULL DEFAULT 1");

        using var migrate = connection.CreateCommand();
        migrate.CommandText =
            """
            UPDATE acquisition_requests
            SET status = 'PendingPickup',
                claimed_at_utc = NULL,
                claim_expires_at_utc = NULL,
                claim_token = NULL,
                claimed_by = NULL
            WHERE status = 'Expired';

            INSERT OR IGNORE INTO acquisition_work_order_metadata (
                work_order_id,
                title,
                priority,
                updated_at_utc
            )
            SELECT
                id,
                'Acquisition work order',
                0,
                created_at_utc
            FROM acquisition_requests;
            """;
        migrate.ExecuteNonQuery();
    }

    private static void EnsureColumn(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string definition)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};";
        alter.ExecuteNonQuery();
    }
}
