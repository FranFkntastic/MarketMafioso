namespace MarketMafioso.Server.Sqlite;

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
        await EnsureItemTypeColumnAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        log.LogInformation("SQLite schema is ready at {DatabasePath}.", connectionFactory.DatabasePath);
    }

    private static async Task EnsureItemTypeColumnAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (await ColumnExistsAsync(connection, transaction, "inventory_items", "item_type", cancellationToken))
            return;

        await using var alterCommand = connection.CreateCommand();
        alterCommand.Transaction = transaction;
        alterCommand.CommandText = "ALTER TABLE inventory_items ADD COLUMN item_type TEXT NULL;";
        await alterCommand.ExecuteNonQueryAsync(cancellationToken);

        await using var migrationCommand = connection.CreateCommand();
        migrationCommand.Transaction = transaction;
        migrationCommand.CommandText = """
            INSERT OR IGNORE INTO schema_migrations (version, applied_at_utc)
            VALUES (2, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> ColumnExistsAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
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
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
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

        CREATE INDEX IF NOT EXISTS idx_snapshots_account_received_at ON snapshots(account_id, received_at_utc DESC);
        CREATE INDEX IF NOT EXISTS idx_snapshots_character_received_at ON snapshots(character_id, received_at_utc DESC);
        CREATE INDEX IF NOT EXISTS idx_inventory_owners_snapshot ON inventory_owners(snapshot_id, sort_order);
        CREATE INDEX IF NOT EXISTS idx_inventory_bags_owner ON inventory_bags(owner_id, sort_order);
        CREATE INDEX IF NOT EXISTS idx_inventory_items_bag ON inventory_items(bag_id, sort_order);
        CREATE INDEX IF NOT EXISTS idx_inventory_items_item ON inventory_items(item_id);

        INSERT OR IGNORE INTO schema_migrations (version, applied_at_utc)
        VALUES (1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
        """;
}
