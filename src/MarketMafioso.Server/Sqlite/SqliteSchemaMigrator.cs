namespace MarketMafioso.Server.Sqlite;

using MarketMafioso.Server.Inventory;
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
        await using (var journalCommand = connection.CreateCommand())
        {
            journalCommand.CommandText = "PRAGMA journal_mode = WAL;";
            var journalMode = (string?)await journalCommand.ExecuteScalarAsync(cancellationToken);
            if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"SQLite refused write-ahead logging for {connectionFactory.DatabasePath}; reported mode was '{journalMode ?? "unknown"}'.");
            }
        }

        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = MigrationSql;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await AddColumnIfMissingAsync(connection, transaction, "inventory_owners", "gil", "INTEGER NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "inventory_owners", "requested_sources_json", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "inventory_owners", "observed_sources_json", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "inventory_owners", "gil_observed_at_utc", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "inventory_owners", "listings_observed_at_utc", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "characters", "service_account_key", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "characters", "service_account_number", "INTEGER NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "snapshots", "service_account_key", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "snapshots", "service_account_number", "INTEGER NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "snapshots", "player_gil", "INTEGER NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "snapshots", "retainer_management_json", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "snapshots", "is_current", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "snapshots", "semantic_revision", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "snapshots", "semantic_hash", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "snapshots", "last_delivery_at_utc", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "inventory_bags", "location", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "inventory_bags", "observed_at_utc", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "inventory_items", "item_type", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "inventory_items", "container_key", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "inventory_items", "slot_index", "INTEGER NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "inventory_items", "condition_percent", "REAL NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "inventory_items", "equipped", "INTEGER NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "retainer_market_listings", "container_key", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "retainer_market_listings", "slot_index", "INTEGER NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "retainer_market_listings", "condition_percent", "REAL NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "ingest_keys", "purpose", "TEXT NOT NULL DEFAULT 'LegacyClient'", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "ingest_keys", "key_prefix", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "ingest_keys", "last_used_at_utc", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "market_owned_listing_versions", "last_publicly_seen_at_utc", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "market_owned_listing_versions", "publicly_missing_since_utc", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "market_owned_listing_versions", "sale_history_checked_at_utc", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "market_observations", "own_listing_visible", "INTEGER NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "retainer_sale_events", "earliest_event_at_utc", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "retainer_sale_events", "latest_event_at_utc", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "retainer_sale_events", "candidate_count", "INTEGER NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "retainer_sale_events", "character_name", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "market_evidence_observations", "aggregate_json", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "market_evidence_observations", "source_build", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(connection, transaction, "market_evidence_observations", "capture_mode", "TEXT NULL", cancellationToken);

        var invalidActorRepair = await RepairInvalidLocalSellerOwnerSemanticsAsync(connection, transaction, cancellationToken);
        var characterRepair = await RepairIncompleteCharacterIdentitiesAsync(connection, transaction, cancellationToken);
        await CreateNormalizedCharacterIdentityIndexAsync(connection, transaction, cancellationToken);
        await SeedInventoryCurrentHeadsAsync(connection, transaction, cancellationToken);
        await CreateInventoryCurrentHeadIndexAsync(connection, transaction, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        await BackfillCurrentSemanticHashesAsync(cancellationToken);

        if (characterRepair.Repaired > 0)
            log.LogInformation("Reconciled {CharacterCount} incomplete character identities.", characterRepair.Repaired);
        if (invalidActorRepair > 0)
            log.LogWarning("Quarantined seller-owner relationships from {ObservationCount} local evidence observations whose ContentId identified the observer.", invalidActorRepair);
        if (characterRepair.Preserved > 0)
        {
            log.LogWarning(
                "Preserved {CharacterCount} ambiguous or unmatched incomplete character identities outside dashboard selection.",
                characterRepair.Preserved);
        }
        log.LogInformation("SQLite schema is ready at {DatabasePath}.", connectionFactory.DatabasePath);
    }

    private static async Task<int> RepairInvalidLocalSellerOwnerSemanticsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var alreadyApplied = connection.CreateCommand())
        {
            alreadyApplied.Transaction = transaction;
            alreadyApplied.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version = 2";
            if ((long)(await alreadyApplied.ExecuteScalarAsync(cancellationToken))! > 0)
                return 0;
        }

        int affected;
        await using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = """
                SELECT COUNT(*)
                FROM market_evidence_observations
                WHERE source_version = '2'
                  AND source_kind IN ('MarketAcquisition', 'PassiveMarketBoard');
                """;
            affected = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken));
        }

        await using var repair = connection.CreateCommand();
        repair.Transaction = transaction;
        repair.CommandText = """
            CREATE TEMP TABLE invalid_local_seller_actors(actor_key TEXT PRIMARY KEY);
            INSERT OR IGNORE INTO invalid_local_seller_actors(actor_key)
            SELECT DISTINCT e.actor_key
            FROM market_actor_listing_evidence e
            JOIN market_evidence_observations o ON o.observation_id = e.observation_id
            WHERE e.role = 'SellerOwner'
              AND o.source_version = '2'
              AND o.source_kind IN ('MarketAcquisition', 'PassiveMarketBoard');

            DELETE FROM market_actor_listing_evidence
            WHERE role = 'SellerOwner'
              AND observation_id IN (
                  SELECT observation_id
                  FROM market_evidence_observations
                  WHERE source_version = '2'
                    AND source_kind IN ('MarketAcquisition', 'PassiveMarketBoard'));

            UPDATE market_actor_listing_evidence
            SET is_self_crafted_sale = 0
            WHERE observation_id IN (
                SELECT observation_id
                FROM market_evidence_observations
                WHERE source_version = '2'
                  AND source_kind IN ('MarketAcquisition', 'PassiveMarketBoard'));

            DELETE FROM market_actors
            WHERE actor_key IN (SELECT actor_key FROM invalid_local_seller_actors)
              AND NOT EXISTS (
                  SELECT 1 FROM market_actor_listing_evidence e
                  WHERE e.account_id = market_actors.account_id
                    AND e.actor_key = market_actors.actor_key);

            UPDATE market_intelligence_outbox
            SET status = 'Pending', completed_at_utc = NULL, last_error = NULL
            WHERE observation_id IN (
                SELECT observation_id
                FROM market_evidence_observations
                WHERE source_version = '2'
                  AND source_kind IN ('MarketAcquisition', 'PassiveMarketBoard'));

            DROP TABLE invalid_local_seller_actors;
            INSERT INTO schema_migrations(version, applied_at_utc)
            VALUES (2, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await repair.ExecuteNonQueryAsync(cancellationToken);
        return affected;
    }

    private async Task BackfillCurrentSemanticHashesAsync(CancellationToken cancellationToken)
    {
        var heads = new List<(long AccountId, string SnapshotId)>();
        await using (var connection = await connectionFactory.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT account_id, id
                FROM snapshots
                WHERE is_current = 1 AND semantic_hash IS NULL
                ORDER BY account_id, character_id;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                heads.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        if (heads.Count == 0)
            return;

        var queries = new InventoryReportReadQueries(connectionFactory);
        foreach (var (accountId, snapshotId) in heads)
        {
            var stored = await queries.GetAsync(accountId, snapshotId, cancellationToken);
            if (stored is null)
                continue;

            await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE snapshots
                SET semantic_hash = $semanticHash
                WHERE account_id = $accountId
                  AND id = $snapshotId
                  AND is_current = 1
                  AND semantic_hash IS NULL;
                """;
            command.Parameters.AddWithValue("$semanticHash", InventorySemanticFingerprint.Compute(stored.Report));
            command.Parameters.AddWithValue("$accountId", accountId);
            command.Parameters.AddWithValue("$snapshotId", snapshotId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<CharacterRepairResult> RepairIncompleteCharacterIdentitiesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var repairs = new List<(long IncompleteId, long CanonicalId)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT incomplete.id, min(canonical.id)
                FROM characters incomplete
                JOIN characters canonical
                  ON canonical.account_id = incomplete.account_id
                 AND lower(trim(canonical.character_name)) = lower(trim(incomplete.character_name))
                 AND canonical.home_world IS NOT NULL
                 AND trim(canonical.home_world) <> ''
                WHERE incomplete.home_world IS NULL OR trim(incomplete.home_world) = ''
                GROUP BY incomplete.id
                HAVING count(canonical.id) = 1
                   AND (
                       max(incomplete.service_account_number) IS NULL
                       OR max(canonical.service_account_number) IS NULL
                       OR max(incomplete.service_account_number) = max(canonical.service_account_number));
                """;
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                repairs.Add((reader.GetInt64(0), reader.GetInt64(1)));
        }

        foreach (var (incompleteId, canonicalId) in repairs)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE snapshots SET character_id = $canonicalId WHERE character_id = $incompleteId;
                UPDATE dashboard_preferences
                SET preferences_json = json_set(preferences_json, '$.defaultCharacterId', $canonicalId)
                WHERE json_valid(preferences_json)
                  AND json_extract(preferences_json, '$.defaultCharacterId') = $incompleteId;
                UPDATE characters
                SET first_seen_at_utc = min(first_seen_at_utc, (SELECT first_seen_at_utc FROM characters WHERE id = $incompleteId)),
                    last_seen_at_utc = max(last_seen_at_utc, (SELECT last_seen_at_utc FROM characters WHERE id = $incompleteId)),
                    service_account_key = COALESCE(service_account_key, (SELECT service_account_key FROM characters WHERE id = $incompleteId)),
                    service_account_number = COALESCE(service_account_number, (SELECT service_account_number FROM characters WHERE id = $incompleteId))
                WHERE id = $canonicalId;
                DELETE FROM characters WHERE id = $incompleteId;
                """,
                cancellationToken,
                ("$canonicalId", canonicalId),
                ("$incompleteId", incompleteId));
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE dashboard_preferences
            SET preferences_json = json_set(preferences_json, '$.defaultCharacterId', NULL)
            WHERE json_valid(preferences_json)
              AND EXISTS (
                  SELECT 1
                  FROM characters
                  WHERE id = json_extract(dashboard_preferences.preferences_json, '$.defaultCharacterId')
                    AND (home_world IS NULL OR trim(home_world) = ''));
            """,
            cancellationToken);

        await using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "SELECT count(*) FROM characters WHERE home_world IS NULL OR trim(home_world) = ''";
        var preserved = checked((int)(long)(await count.ExecuteScalarAsync(cancellationToken))!);
        return new CharacterRepairResult(repairs.Count, preserved);
    }

    private sealed record CharacterRepairResult(int Repaired, int Preserved);

    private static Task CreateNormalizedCharacterIdentityIndexAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS ix_characters_complete_identity_normalized
            ON characters (account_id, lower(trim(character_name)), lower(trim(home_world)))
            WHERE home_world IS NOT NULL AND trim(home_world) <> '';
            """,
            cancellationToken);

    private static async Task SeedInventoryCurrentHeadsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction,
            """
            WITH ranked AS (
                SELECT
                    id,
                    account_id,
                    character_id,
                    ROW_NUMBER() OVER (
                        PARTITION BY account_id, character_id
                        ORDER BY received_at_utc DESC, id DESC
                    ) AS newest_position,
                    ROW_NUMBER() OVER (
                        PARTITION BY account_id, character_id
                        ORDER BY received_at_utc, id
                    ) AS semantic_position
                FROM snapshots
                WHERE character_id IS NOT NULL
            )
            UPDATE snapshots
            SET semantic_revision = COALESCE(
                    NULLIF(semantic_revision, 0),
                    (SELECT semantic_position FROM ranked WHERE ranked.id = snapshots.id)),
                last_delivery_at_utc = COALESCE(last_delivery_at_utc, received_at_utc),
                is_current = CASE
                    WHEN EXISTS (
                        SELECT 1
                        FROM snapshots existing
                        WHERE existing.account_id = snapshots.account_id
                          AND existing.character_id = snapshots.character_id
                          AND existing.is_current = 1)
                    THEN is_current
                    WHEN (SELECT newest_position FROM ranked WHERE ranked.id = snapshots.id) = 1
                    THEN 1
                    ELSE 0
                END
            WHERE character_id IS NOT NULL;

            INSERT INTO inventory_account_revisions (account_id, revision, updated_at_utc)
            SELECT
                account_id,
                MAX(1, COALESCE(MAX(semantic_revision), 0)),
                COALESCE(MAX(last_delivery_at_utc), MAX(received_at_utc))
            FROM snapshots
            WHERE is_current = 1
            GROUP BY account_id
            ON CONFLICT(account_id) DO NOTHING;
            """,
            cancellationToken);
    }

    private static Task CreateInventoryCurrentHeadIndexAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            """
            CREATE UNIQUE INDEX IF NOT EXISTS ix_snapshots_current_character
            ON snapshots(account_id, character_id)
            WHERE is_current = 1 AND character_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_snapshots_account_current_received
            ON snapshots(account_id, is_current, received_at_utc DESC);
            """,
            cancellationToken);

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(cancellationToken);
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

        CREATE TABLE IF NOT EXISTS dashboard_preferences (
            owner_kind TEXT NOT NULL,
            owner_key TEXT NOT NULL,
            scope TEXT NOT NULL,
            preferences_json TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            PRIMARY KEY (owner_kind, owner_key, scope)
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
            purpose TEXT NOT NULL DEFAULT 'LegacyClient',
            key_prefix TEXT NULL,
            created_at_utc TEXT NOT NULL,
            last_used_at_utc TEXT NULL,
            disabled_at_utc TEXT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ix_ingest_keys_key_hash
            ON ingest_keys (key_hash);

        CREATE TABLE IF NOT EXISTS characters (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            character_name TEXT NOT NULL,
            home_world TEXT NULL,
            service_account_key TEXT NULL,
            service_account_number INTEGER NULL,
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
            service_account_key TEXT NULL,
            service_account_number INTEGER NULL,
            player_gil INTEGER NULL,
            report_timestamp TEXT NOT NULL,
            schema_version INTEGER NOT NULL,
            source_plugin TEXT NOT NULL,
            plugin_version TEXT NOT NULL,
            generated_at_utc TEXT NOT NULL,
            raw_report_json TEXT NULL,
            raw_json_retained_at_utc TEXT NULL,
            retainer_management_json TEXT NULL,
            is_current INTEGER NOT NULL DEFAULT 0,
            semantic_revision INTEGER NOT NULL DEFAULT 0,
            semantic_hash TEXT NULL,
            last_delivery_at_utc TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS inventory_account_revisions (
            account_id INTEGER PRIMARY KEY REFERENCES accounts(id) ON DELETE CASCADE,
            revision INTEGER NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS inventory_owners (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            snapshot_id TEXT NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
            owner_type TEXT NOT NULL,
            owner_name TEXT NOT NULL,
            retainer_id INTEGER NULL,
            last_updated TEXT NULL,
            gil INTEGER NULL,
            requested_sources_json TEXT NOT NULL DEFAULT '[]',
            observed_sources_json TEXT NOT NULL DEFAULT '[]',
            gil_observed_at_utc TEXT NULL,
            listings_observed_at_utc TEXT NULL,
            sort_order INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS inventory_bags (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            owner_id INTEGER NOT NULL REFERENCES inventory_owners(id) ON DELETE CASCADE,
            bag_name TEXT NOT NULL,
            location TEXT NULL,
            observed_at_utc TEXT NULL,
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
            container_key TEXT NULL,
            slot_index INTEGER NULL,
            condition_percent REAL NULL,
            equipped INTEGER NULL,
            sort_order INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS item_metadata_catalog (
            account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            item_id INTEGER NOT NULL,
            item_name TEXT NULL,
            item_type TEXT NULL,
            last_seen_at_utc TEXT NOT NULL,
            PRIMARY KEY (account_id, item_id)
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
            container_key TEXT NULL,
            slot_index INTEGER NULL,
            condition_percent REAL NULL,
            unit_price INTEGER NULL,
            listed_at TEXT NULL,
            sort_order INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS market_owned_listing_versions (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            version_key TEXT NOT NULL,
            listing_key TEXT NOT NULL,
            source_snapshot_id TEXT NOT NULL,
            character_name TEXT NULL,
            world TEXT NOT NULL,
            retainer_id INTEGER NOT NULL,
            retainer_name TEXT NOT NULL,
            item_id INTEGER NOT NULL,
            item_name TEXT NULL,
            quantity INTEGER NOT NULL,
            is_hq INTEGER NOT NULL,
            unit_price INTEGER NOT NULL,
            listed_at_utc TEXT NULL,
            listings_observed_at_utc TEXT NOT NULL,
            first_observed_at_utc TEXT NOT NULL,
            last_observed_at_utc TEXT NOT NULL,
            last_publicly_seen_at_utc TEXT NULL,
            publicly_missing_since_utc TEXT NULL,
            sale_history_checked_at_utc TEXT NULL,
            is_active INTEGER NOT NULL DEFAULT 1,
            closed_at_utc TEXT NULL,
            close_reason TEXT NULL,
            UNIQUE(account_id, version_key)
        );

        CREATE TABLE IF NOT EXISTS market_observations (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            owned_listing_version_id INTEGER NOT NULL REFERENCES market_owned_listing_versions(id) ON DELETE CASCADE,
            observed_at_utc TEXT NOT NULL,
            source_upload_at_utc TEXT NULL,
            source_age_seconds INTEGER NULL,
            source_freshness TEXT NOT NULL,
            classification TEXT NOT NULL,
            own_listing_visible INTEGER NULL,
            own_unit_price INTEGER NOT NULL,
            competitor_listing_id TEXT NULL,
            competitor_retainer_id TEXT NULL,
            competitor_retainer_name TEXT NULL,
            competitor_unit_price INTEGER NULL,
            competitor_quantity INTEGER NULL,
            competitor_reviewed_at_utc TEXT NULL,
            undercut_delta INTEGER NULL
        );

        CREATE TABLE IF NOT EXISTS market_undercut_episodes (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            owned_listing_version_id INTEGER NOT NULL REFERENCES market_owned_listing_versions(id) ON DELETE CASCADE,
            started_at_utc TEXT NOT NULL,
            first_detected_at_utc TEXT NOT NULL,
            last_seen_at_utc TEXT NOT NULL,
            last_clear_observed_at_utc TEXT NULL,
            response_lower_bound_ms INTEGER NOT NULL,
            response_upper_bound_ms INTEGER NOT NULL,
            first_competitor_listing_id TEXT NULL,
            first_competitor_retainer_id TEXT NULL,
            first_competitor_retainer_name TEXT NULL,
            current_competitor_listing_id TEXT NULL,
            current_competitor_retainer_id TEXT NULL,
            current_competitor_retainer_name TEXT NULL,
            own_unit_price INTEGER NOT NULL,
            competitor_unit_price INTEGER NOT NULL,
            undercut_delta INTEGER NOT NULL,
            exact_one_gil INTEGER NOT NULL,
            cleared_at_utc TEXT NULL,
            close_reason TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS retainer_sale_events (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            owned_listing_version_id INTEGER NULL REFERENCES market_owned_listing_versions(id) ON DELETE SET NULL,
            source TEXT NOT NULL,
            confidence TEXT NOT NULL,
            retainer_id INTEGER NULL,
            retainer_name TEXT NULL,
            character_name TEXT NULL,
            world TEXT NULL,
            item_id INTEGER NOT NULL,
            item_name TEXT NULL,
            quantity INTEGER NULL,
            is_hq INTEGER NULL,
            unit_price INTEGER NULL,
            total_gil INTEGER NULL,
            event_at_utc TEXT NULL,
            earliest_event_at_utc TEXT NULL,
            latest_event_at_utc TEXT NULL,
            candidate_count INTEGER NULL,
            observed_at_utc TEXT NOT NULL,
            evidence_hash TEXT NULL,
            raw_evidence_json TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS market_region_observations (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            region TEXT NOT NULL,
            item_id INTEGER NOT NULL,
            item_name TEXT NULL,
            is_hq INTEGER NOT NULL,
            observed_at_utc TEXT NOT NULL,
            min_listing_price INTEGER NULL,
            min_listing_world_id INTEGER NULL,
            average_sale_price REAL NULL,
            daily_sale_velocity REAL NULL,
            recent_purchase_price INTEGER NULL,
            recent_purchase_world_id INTEGER NULL,
            recent_purchase_at_utc TEXT NULL,
            freshest_world_upload_at_utc TEXT NULL,
            source_age_seconds INTEGER NULL
        );

        CREATE TABLE IF NOT EXISTS market_evidence_payloads (
            payload_hash TEXT PRIMARY KEY,
            listing_count INTEGER NOT NULL,
            listings_json TEXT NOT NULL,
            created_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS market_evidence_observations (
            observation_id TEXT PRIMARY KEY,
            account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            idempotency_key TEXT NOT NULL,
            occurrence_id TEXT NOT NULL,
            source_kind TEXT NOT NULL,
            source_version TEXT NOT NULL,
            source_instance_id TEXT NULL,
            source_build TEXT NULL,
            capture_mode TEXT NULL,
            item_id INTEGER NOT NULL,
            item_name TEXT NULL,
            data_center TEXT NOT NULL,
            world_name TEXT NOT NULL,
            observed_at_utc TEXT NOT NULL,
            received_at_utc TEXT NOT NULL,
            coverage TEXT NOT NULL,
            reported_listing_count INTEGER NULL,
            listing_capacity INTEGER NULL,
            is_truncated INTEGER NULL,
            source_freshness TEXT NULL,
            payload_hash TEXT NOT NULL REFERENCES market_evidence_payloads(payload_hash),
            request_hash TEXT NOT NULL,
            aggregate_json TEXT NULL,
            provenance_json TEXT NULL,
            UNIQUE(account_id, idempotency_key),
            UNIQUE(account_id, source_kind, occurrence_id)
        );

        CREATE TABLE IF NOT EXISTS market_intelligence_outbox (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            observation_id TEXT NOT NULL REFERENCES market_evidence_observations(observation_id) ON DELETE CASCADE,
            status TEXT NOT NULL DEFAULT 'Pending',
            attempts INTEGER NOT NULL DEFAULT 0,
            last_error TEXT NULL,
            created_at_utc TEXT NOT NULL,
            completed_at_utc TEXT NULL,
            UNIQUE(observation_id)
        );

        CREATE TABLE IF NOT EXISTS market_intelligence_projection_generations (
            generation_id TEXT PRIMARY KEY,
            account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            classifier_version TEXT NOT NULL,
            revision INTEGER NOT NULL,
            status TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            published_at_utc TEXT NULL,
            error TEXT NULL,
            UNIQUE(account_id, revision)
        );

        CREATE TABLE IF NOT EXISTS market_intelligence_market_rows (
            generation_id TEXT NOT NULL REFERENCES market_intelligence_projection_generations(generation_id) ON DELETE CASCADE,
            account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            item_id INTEGER NOT NULL,
            world_name TEXT NOT NULL,
            row_json TEXT NOT NULL,
            PRIMARY KEY(generation_id, item_id, world_name)
        );

        CREATE TABLE IF NOT EXISTS market_intelligence_current_projection (
            account_id INTEGER PRIMARY KEY REFERENCES accounts(id) ON DELETE CASCADE,
            generation_id TEXT NOT NULL REFERENCES market_intelligence_projection_generations(generation_id) ON DELETE CASCADE,
            revision INTEGER NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS market_intelligence_annotations (
            account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            item_id INTEGER NOT NULL,
            world_name TEXT NOT NULL,
            note TEXT NULL,
            reviewed INTEGER NOT NULL DEFAULT 0,
            updated_at_utc TEXT NOT NULL,
            PRIMARY KEY(account_id, item_id, world_name)
        );

        CREATE TABLE IF NOT EXISTS market_intelligence_import_receipts (
            account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            source_path_hash TEXT NOT NULL,
            source_fingerprint TEXT NOT NULL,
            status TEXT NOT NULL,
            imported_observations INTEGER NOT NULL DEFAULT 0,
            error TEXT NULL,
            updated_at_utc TEXT NOT NULL,
            PRIMARY KEY(account_id, source_path_hash)
        );

        CREATE TABLE IF NOT EXISTS market_actor_key_scopes (
            account_id INTEGER PRIMARY KEY REFERENCES accounts(id) ON DELETE CASCADE,
            key_scheme TEXT NOT NULL,
            key_material BLOB NOT NULL,
            created_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS market_actors (
            account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            actor_key TEXT NOT NULL,
            key_scheme TEXT NOT NULL,
            first_observed_at_utc TEXT NOT NULL,
            last_observed_at_utc TEXT NOT NULL,
            PRIMARY KEY(account_id, actor_key)
        );

        CREATE TABLE IF NOT EXISTS market_actor_listing_evidence (
            account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            observation_id TEXT NOT NULL REFERENCES market_evidence_observations(observation_id) ON DELETE CASCADE,
            listing_id TEXT NOT NULL,
            actor_key TEXT NOT NULL,
            role TEXT NOT NULL,
            item_id INTEGER NOT NULL,
            item_name TEXT NULL,
            world_name TEXT NOT NULL,
            retainer_id TEXT NOT NULL,
            retainer_name TEXT NULL,
            observed_at_utc TEXT NOT NULL,
            is_self_crafted_sale INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(account_id, observation_id, listing_id, actor_key, role),
            FOREIGN KEY(account_id, actor_key) REFERENCES market_actors(account_id, actor_key) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS market_actor_name_observations (
            name_observation_id TEXT PRIMARY KEY,
            account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
            actor_key TEXT NOT NULL,
            idempotency_key TEXT NOT NULL,
            name TEXT NOT NULL,
            resolution_method TEXT NOT NULL,
            observed_at_utc TEXT NOT NULL,
            received_at_utc TEXT NOT NULL,
            source_observation_id TEXT NULL REFERENCES market_evidence_observations(observation_id) ON DELETE SET NULL,
            UNIQUE(account_id, idempotency_key),
            FOREIGN KEY(account_id, actor_key) REFERENCES market_actors(account_id, actor_key) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS idx_snapshots_account_received_at ON snapshots(account_id, received_at_utc DESC);
        CREATE INDEX IF NOT EXISTS idx_snapshots_character_received_at ON snapshots(character_id, received_at_utc DESC);
        CREATE INDEX IF NOT EXISTS idx_inventory_owners_snapshot ON inventory_owners(snapshot_id, sort_order);
        CREATE INDEX IF NOT EXISTS idx_inventory_bags_owner ON inventory_bags(owner_id, sort_order);
        CREATE INDEX IF NOT EXISTS idx_inventory_items_bag ON inventory_items(bag_id, sort_order);
        CREATE INDEX IF NOT EXISTS idx_inventory_items_item ON inventory_items(item_id);
        CREATE INDEX IF NOT EXISTS idx_item_metadata_catalog_type ON item_metadata_catalog(account_id, item_type);
        CREATE INDEX IF NOT EXISTS idx_retainer_market_listings_owner ON retainer_market_listings(owner_id, sort_order);
        CREATE INDEX IF NOT EXISTS idx_market_owned_listing_versions_active
            ON market_owned_listing_versions(account_id, is_active, world, item_id);
        CREATE INDEX IF NOT EXISTS idx_market_observations_listing
            ON market_observations(owned_listing_version_id, observed_at_utc DESC);
        CREATE INDEX IF NOT EXISTS idx_market_observations_account
            ON market_observations(account_id, observed_at_utc DESC);
        CREATE INDEX IF NOT EXISTS idx_market_undercut_episodes_open
            ON market_undercut_episodes(account_id, cleared_at_utc, last_seen_at_utc DESC);
        CREATE INDEX IF NOT EXISTS idx_retainer_sale_events_account
            ON retainer_sale_events(account_id, observed_at_utc DESC);
        CREATE UNIQUE INDEX IF NOT EXISTS idx_retainer_sale_events_evidence
            ON retainer_sale_events(account_id, evidence_hash)
            WHERE evidence_hash IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_market_region_observations_account
            ON market_region_observations(account_id, region, observed_at_utc DESC);
        CREATE INDEX IF NOT EXISTS idx_market_region_observations_item
            ON market_region_observations(account_id, item_id, is_hq, observed_at_utc DESC);
        CREATE INDEX IF NOT EXISTS idx_market_evidence_market_time
            ON market_evidence_observations(account_id, world_name, item_id, observed_at_utc DESC);
        CREATE INDEX IF NOT EXISTS idx_market_evidence_occurrence
            ON market_evidence_observations(account_id, source_kind, occurrence_id);
        CREATE UNIQUE INDEX IF NOT EXISTS idx_market_evidence_source_occurrence_unique
            ON market_evidence_observations(account_id, source_kind, occurrence_id);
        CREATE INDEX IF NOT EXISTS idx_market_intelligence_outbox_pending
            ON market_intelligence_outbox(status, account_id, id);
        CREATE INDEX IF NOT EXISTS idx_market_actor_listing_actor_time
            ON market_actor_listing_evidence(account_id, actor_key, observed_at_utc DESC);
        CREATE INDEX IF NOT EXISTS idx_market_actor_listing_market_time
            ON market_actor_listing_evidence(account_id, world_name, item_id, observed_at_utc DESC);
        CREATE INDEX IF NOT EXISTS idx_market_actor_names_actor_time
            ON market_actor_name_observations(account_id, actor_key, observed_at_utc DESC);
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
