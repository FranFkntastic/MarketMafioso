namespace MarketMafioso.Server.Sqlite;

using Microsoft.Data.Sqlite;

public sealed class SqliteSchemaMigrator
{
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly ILogger<SqliteSchemaMigrator> log;

    public SqliteSchemaMigrator(
        SqliteConnectionFactory connectionFactory,
        ILogger<SqliteSchemaMigrator> log)
    {
        this.connectionFactory = connectionFactory;
        this.log = log;
    }

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = MigrationSql;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await AddColumnIfMissingAsync(connection, transaction, "inventory_owners", "gil", "INTEGER NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "inventory_items", "item_type", "TEXT NULL", cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        log.LogInformation("SQLite schema is ready at {DatabasePath}.", connectionFactory.DatabasePath);
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        if (await ColumnExistsAsync(connection, transaction, tableName, columnName, cancellationToken))
            return;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition}";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({tableName})";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.GetString(1).Equals(columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private const string MigrationSql = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version INTEGER PRIMARY KEY,
            applied_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS dashboard_users (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            username TEXT NOT NULL UNIQUE COLLATE NOCASE,
            password_hash TEXT NOT NULL,
            is_admin INTEGER NOT NULL DEFAULT 1,
            created_at_utc TEXT NOT NULL,
            disabled_at_utc TEXT NULL,
            last_login_at_utc TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS accounts (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            display_name TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            disabled_at_utc TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS dashboard_user_accounts (
            dashboard_user_id INTEGER NOT NULL REFERENCES dashboard_users(id) ON DELETE CASCADE,
            account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            is_default INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (dashboard_user_id, account_id)
        );

        CREATE TABLE IF NOT EXISTS dashboard_sessions (
            id TEXT PRIMARY KEY,
            dashboard_user_id INTEGER NOT NULL REFERENCES dashboard_users(id) ON DELETE CASCADE,
            token_hash TEXT NOT NULL UNIQUE,
            created_at_utc TEXT NOT NULL,
            expires_at_utc TEXT NOT NULL,
            last_seen_at_utc TEXT NOT NULL,
            revoked_at_utc TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS diagnostic_events (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            occurred_at_utc TEXT NOT NULL,
            received_at_utc TEXT NOT NULL,
            source TEXT NOT NULL,
            category TEXT NOT NULL,
            type TEXT NOT NULL,
            severity TEXT NOT NULL,
            outcome TEXT NULL,
            message TEXT NOT NULL,
            correlation_id TEXT NULL,
            account_id INTEGER NULL,
            dashboard_user_id INTEGER NULL,
            dashboard_session_id TEXT NULL,
            plugin_instance_id TEXT NULL,
            acquisition_request_id TEXT NULL,
            route_run_id TEXT NULL,
            route_stop_id TEXT NULL,
            purchase_attempt_id TEXT NULL,
            snapshot_id TEXT NULL,
            item_id INTEGER NULL,
            item_name TEXT NULL,
            world TEXT NULL,
            character_name TEXT NULL,
            http_method TEXT NULL,
            route_pattern TEXT NULL,
            status_code INTEGER NULL,
            duration_ms INTEGER NULL,
            exception_type TEXT NULL,
            exception_message TEXT NULL,
            payload_summary_json TEXT NULL,
            payload_raw_json TEXT NULL,
            payload_size_bytes INTEGER NULL,
            payload_sha256 TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS ingest_keys (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            label TEXT NOT NULL,
            key_hash TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            disabled_at_utc TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS characters (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            character_name TEXT NOT NULL,
            home_world TEXT NULL,
            first_seen_at_utc TEXT NOT NULL,
            last_seen_at_utc TEXT NOT NULL,
            UNIQUE(account_id, character_name, home_world)
        );

        CREATE TABLE IF NOT EXISTS snapshots (
            id TEXT PRIMARY KEY,
            account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            character_id INTEGER NULL REFERENCES characters(id) ON DELETE SET NULL,
            received_at_utc TEXT NOT NULL,
            api_key_label TEXT NULL,
            character_name TEXT NULL,
            home_world TEXT NULL,
            report_timestamp TEXT NOT NULL,
            schema_version INTEGER NOT NULL,
            source_plugin TEXT NOT NULL,
            plugin_version TEXT NOT NULL,
            generated_at_utc TEXT NOT NULL,
            raw_report_json TEXT NULL,
            raw_json_retained_at_utc TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS inventory_owners (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            snapshot_id TEXT NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
            owner_type TEXT NOT NULL,
            owner_name TEXT NOT NULL,
            retainer_id INTEGER NULL,
            last_updated TEXT NULL,
            gil INTEGER NULL,
            sort_order INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS inventory_bags (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            owner_id INTEGER NOT NULL REFERENCES inventory_owners(id) ON DELETE CASCADE,
            bag_name TEXT NOT NULL,
            sort_order INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS inventory_items (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            bag_id INTEGER NOT NULL REFERENCES inventory_bags(id) ON DELETE CASCADE,
            item_id INTEGER NOT NULL,
            item_name TEXT NULL,
            item_type TEXT NULL,
            quantity INTEGER NOT NULL,
            is_hq INTEGER NOT NULL,
            condition REAL NOT NULL,
            sort_order INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS retainer_market_listings (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            owner_id INTEGER NOT NULL REFERENCES inventory_owners(id) ON DELETE CASCADE,
            item_id INTEGER NOT NULL,
            item_name TEXT NULL,
            item_type TEXT NULL,
            quantity INTEGER NOT NULL,
            is_hq INTEGER NOT NULL,
            condition REAL NOT NULL,
            unit_price INTEGER NULL,
            listed_at TEXT NULL,
            sort_order INTEGER NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_snapshots_account_received_at ON snapshots(account_id, received_at_utc DESC);
        CREATE INDEX IF NOT EXISTS idx_snapshots_character_received_at ON snapshots(character_id, received_at_utc DESC);
        CREATE INDEX IF NOT EXISTS idx_inventory_owners_snapshot ON inventory_owners(snapshot_id, sort_order);
        CREATE INDEX IF NOT EXISTS idx_inventory_bags_owner ON inventory_bags(owner_id, sort_order);
        CREATE INDEX IF NOT EXISTS idx_inventory_items_bag ON inventory_items(bag_id, sort_order);
        CREATE INDEX IF NOT EXISTS idx_inventory_items_item ON inventory_items(item_id);
        CREATE INDEX IF NOT EXISTS idx_retainer_market_listings_owner ON retainer_market_listings(owner_id, sort_order);
        CREATE INDEX IF NOT EXISTS idx_diagnostic_events_occurred ON diagnostic_events(occurred_at_utc DESC);
        CREATE INDEX IF NOT EXISTS idx_diagnostic_events_category ON diagnostic_events(category, occurred_at_utc DESC);
        CREATE INDEX IF NOT EXISTS idx_diagnostic_events_severity ON diagnostic_events(severity, occurred_at_utc DESC);
        CREATE INDEX IF NOT EXISTS idx_diagnostic_events_correlation ON diagnostic_events(correlation_id);
        CREATE INDEX IF NOT EXISTS idx_diagnostic_events_acquisition ON diagnostic_events(acquisition_request_id);
        CREATE INDEX IF NOT EXISTS idx_diagnostic_events_snapshot ON diagnostic_events(snapshot_id);

        INSERT OR IGNORE INTO schema_migrations (version, applied_at_utc)
        VALUES (1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
        """;
}
