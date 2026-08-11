using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MarketMafioso.Server.Auth;
using MarketMafioso.Server.Sqlite;

namespace MarketMafioso.Server.ContractTests;

public sealed class SqliteSchemaMigratorTests
{
    [Fact]
    public async Task MigrateAsync_CreatesReceiverTables()
    {
        var databasePath = CreateDatabasePath();
        var factory = CreateFactory(databasePath);
        var migrator = new SqliteSchemaMigrator(factory, NullLogger<SqliteSchemaMigrator>.Instance);

        await migrator.MigrateAsync(CancellationToken.None);

        await using var connection = await factory.OpenConnectionAsync(CancellationToken.None);
        Assert.True(await TableExistsAsync(connection, "schema_migrations"));
        Assert.True(await TableExistsAsync(connection, "accounts"));
        Assert.True(await TableExistsAsync(connection, "dashboard_users"));
        Assert.True(await TableExistsAsync(connection, "dashboard_user_accounts"));
        Assert.True(await TableExistsAsync(connection, "dashboard_preferences"));
        Assert.True(await TableExistsAsync(connection, "ingest_keys"));
        Assert.True(await TableExistsAsync(connection, "characters"));
        Assert.True(await TableExistsAsync(connection, "snapshots"));
        Assert.True(await TableExistsAsync(connection, "inventory_owners"));
        Assert.True(await TableExistsAsync(connection, "inventory_bags"));
        Assert.True(await TableExistsAsync(connection, "inventory_items"));
        Assert.True(await TableExistsAsync(connection, "retainer_market_listings"));
        Assert.True(await TableExistsAsync(connection, "market_owned_listing_versions"));
        Assert.True(await TableExistsAsync(connection, "market_observations"));
        Assert.True(await TableExistsAsync(connection, "market_undercut_episodes"));
        Assert.True(await TableExistsAsync(connection, "retainer_sale_events"));
        Assert.True(await TableExistsAsync(connection, "market_region_observations"));
        Assert.True(await ColumnExistsAsync(connection, "inventory_owners", "gil"));
        Assert.True(await ColumnExistsAsync(connection, "inventory_items", "item_type"));
        Assert.True(await ColumnExistsAsync(connection, "inventory_bags", "location"));
        Assert.True(await ColumnExistsAsync(connection, "inventory_items", "container_key"));
        Assert.True(await ColumnExistsAsync(connection, "inventory_items", "slot_index"));
        Assert.True(await ColumnExistsAsync(connection, "inventory_items", "condition_percent"));
        Assert.True(await ColumnExistsAsync(connection, "inventory_items", "equipped"));
        Assert.True(await ColumnExistsAsync(connection, "retainer_market_listings", "container_key"));
        Assert.True(await ColumnExistsAsync(connection, "retainer_market_listings", "slot_index"));
        Assert.True(await ColumnExistsAsync(connection, "retainer_market_listings", "condition_percent"));
        Assert.True(await ColumnExistsAsync(connection, "ingest_keys", "purpose"));
        Assert.True(await ColumnExistsAsync(connection, "ingest_keys", "key_prefix"));
        Assert.True(await ColumnExistsAsync(connection, "ingest_keys", "last_used_at_utc"));
        Assert.True(await ColumnExistsAsync(connection, "characters", "service_account_number"));
        Assert.True(await ColumnExistsAsync(connection, "snapshots", "service_account_number"));
    }

    [Fact]
    public async Task MigrateAsync_CanRunTwiceWithAddedColumns()
    {
        var databasePath = CreateDatabasePath();
        var factory = CreateFactory(databasePath);
        var migrator = new SqliteSchemaMigrator(factory, NullLogger<SqliteSchemaMigrator>.Instance);

        await migrator.MigrateAsync(CancellationToken.None);
        await migrator.MigrateAsync(CancellationToken.None);

        await using var connection = await factory.OpenConnectionAsync(CancellationToken.None);
        Assert.True(await ColumnExistsAsync(connection, "inventory_owners", "gil"));
        Assert.True(await ColumnExistsAsync(connection, "inventory_items", "item_type"));
        Assert.True(await ColumnExistsAsync(connection, "inventory_bags", "location"));
        Assert.True(await ColumnExistsAsync(connection, "inventory_items", "condition_percent"));
        Assert.True(await ColumnExistsAsync(connection, "ingest_keys", "purpose"));
        Assert.True(await ColumnExistsAsync(connection, "ingest_keys", "key_prefix"));
        Assert.True(await ColumnExistsAsync(connection, "ingest_keys", "last_used_at_utc"));
        Assert.True(await TableExistsAsync(connection, "retainer_market_listings"));
        Assert.True(await TableExistsAsync(connection, "market_owned_listing_versions"));
        Assert.True(await TableExistsAsync(connection, "market_observations"));
        Assert.True(await TableExistsAsync(connection, "market_undercut_episodes"));
        Assert.True(await TableExistsAsync(connection, "retainer_sale_events"));
        Assert.True(await TableExistsAsync(connection, "market_region_observations"));
    }

    [Fact]
    public async Task MigrateAsync_ReconcilesUniqueIncompleteIdentityAndPreservesReferences()
    {
        var databasePath = CreateDatabasePath();
        var factory = CreateFactory(databasePath);
        var migrator = new SqliteSchemaMigrator(factory, NullLogger<SqliteSchemaMigrator>.Instance);
        await migrator.MigrateAsync(CancellationToken.None);

        await using (var connection = await factory.OpenConnectionAsync(CancellationToken.None))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO accounts (id, display_name, created_at_utc) VALUES (1, 'Default', '2026-08-11T00:00:00Z');
                INSERT INTO characters (id, account_id, character_name, home_world, service_account_number, first_seen_at_utc, last_seen_at_utc)
                VALUES
                    (10, 1, 'Wei Ning', 'Siren', 1, '2026-08-11T01:00:00Z', '2026-08-11T02:00:00Z'),
                    (11, 1, 'Wei Ning', NULL, NULL, '2026-08-10T01:00:00Z', '2026-08-11T03:00:00Z');
                INSERT INTO snapshots (
                    id, account_id, character_id, received_at_utc, character_name, home_world,
                    report_timestamp, schema_version, source_plugin, plugin_version, generated_at_utc)
                VALUES ('legacy', 1, 11, '2026-08-11T03:00:00Z', 'Wei Ning', NULL,
                    '2026-08-11T03:00:00Z', 4, 'MarketMafioso', 'legacy', '2026-08-11T03:00:00Z');
                INSERT INTO dashboard_preferences (owner_kind, owner_key, scope, preferences_json, updated_at_utc)
                VALUES ('user', '1', 'dashboard', '{"defaultCharacterId":11}', '2026-08-11T03:00:00Z');
                """;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await migrator.MigrateAsync(CancellationToken.None);

        await using var verify = await factory.OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(1, await ScalarLongAsync(verify, "SELECT count(*) FROM characters"));
        Assert.Equal(10, await ScalarLongAsync(verify, "SELECT character_id FROM snapshots WHERE id = 'legacy'"));
        Assert.Equal(10, await ScalarLongAsync(verify, "SELECT json_extract(preferences_json, '$.defaultCharacterId') FROM dashboard_preferences"));
        Assert.Equal("2026-08-10T01:00:00Z", await ScalarStringAsync(verify, "SELECT first_seen_at_utc FROM characters WHERE id = 10"));
        Assert.Equal("2026-08-11T03:00:00Z", await ScalarStringAsync(verify, "SELECT last_seen_at_utc FROM characters WHERE id = 10"));
    }

    [Fact]
    public async Task MigrateAsync_PreservesAmbiguousIncompleteIdentityForDiagnostics()
    {
        var databasePath = CreateDatabasePath();
        var factory = CreateFactory(databasePath);
        var migrator = new SqliteSchemaMigrator(factory, NullLogger<SqliteSchemaMigrator>.Instance);
        await migrator.MigrateAsync(CancellationToken.None);

        await using (var connection = await factory.OpenConnectionAsync(CancellationToken.None))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO accounts (id, display_name, created_at_utc) VALUES (1, 'Default', '2026-08-11T00:00:00Z');
                INSERT INTO characters (account_id, character_name, home_world, first_seen_at_utc, last_seen_at_utc)
                VALUES
                    (1, 'Same Name', 'Siren', '2026-08-11T01:00:00Z', '2026-08-11T01:00:00Z'),
                    (1, 'Same Name', 'Cactuar', '2026-08-11T01:00:00Z', '2026-08-11T01:00:00Z'),
                    (1, 'Same Name', NULL, '2026-08-11T01:00:00Z', '2026-08-11T01:00:00Z');
                INSERT INTO dashboard_preferences (owner_kind, owner_key, scope, preferences_json, updated_at_utc)
                VALUES ('user', '1', 'dashboard', json_object('defaultCharacterId', last_insert_rowid()), '2026-08-11T03:00:00Z');
                """;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await migrator.MigrateAsync(CancellationToken.None);

        await using var verify = await factory.OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(3, await ScalarLongAsync(verify, "SELECT count(*) FROM characters"));
        Assert.Null(await ScalarNullableLongAsync(verify, "SELECT json_extract(preferences_json, '$.defaultCharacterId') FROM dashboard_preferences"));
    }

    [Fact]
    public async Task MigrateAsync_EnablesWriteAheadLogging()
    {
        var databasePath = CreateDatabasePath();
        var factory = CreateFactory(databasePath);
        var migrator = new SqliteSchemaMigrator(factory, NullLogger<SqliteSchemaMigrator>.Instance);

        await migrator.MigrateAsync(CancellationToken.None);

        await using var connection = await factory.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        var journalMode = (string?)await command.ExecuteScalarAsync(CancellationToken.None);
        Assert.Equal("wal", journalMode, ignoreCase: true);
    }

    [Fact]
    public async Task BootstrapAsync_CreatesDefaultAccountAdminUserAndIngestKey()
    {
        var databasePath = CreateDatabasePath();
        var configuration = CreateConfiguration(
            databasePath,
            new KeyValuePair<string, string?>("MarketMafioso:RequireDashboardAuth", "true"),
            new KeyValuePair<string, string?>("MarketMafioso:DashboardBootstrapUsername", "admin"),
            new KeyValuePair<string, string?>("MarketMafioso:DashboardBootstrapPassword", "secret-password"),
            new KeyValuePair<string, string?>("MarketMafioso:IngestApiKey", "ingest-secret"));
        var factory = CreateFactory(databasePath, configuration);
        var migrator = new SqliteSchemaMigrator(factory, NullLogger<SqliteSchemaMigrator>.Instance);
        await migrator.MigrateAsync(CancellationToken.None);
        var bootstrapper = new ReceiverBootstrapper(
            factory,
            configuration,
            new DashboardPasswordHasher(),
            NullLogger<ReceiverBootstrapper>.Instance);

        await bootstrapper.BootstrapAsync(CancellationToken.None);

        await using var connection = await factory.OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(1, await CountAsync(connection, "accounts"));
        Assert.Equal(1, await CountAsync(connection, "dashboard_users"));
        Assert.Equal(1, await CountAsync(connection, "dashboard_user_accounts"));
        Assert.Equal(1, await CountAsync(connection, "ingest_keys"));
        Assert.Equal("Default", await ScalarStringAsync(connection, "SELECT display_name FROM accounts"));
        Assert.Equal("admin", await ScalarStringAsync(connection, "SELECT username FROM dashboard_users"));
    }

    [Fact]
    public async Task ResolveAccountIdAsync_ReturnsBootstrapAccountForIngestKey()
    {
        var databasePath = CreateDatabasePath();
        var configuration = CreateConfiguration(
            databasePath,
            new KeyValuePair<string, string?>("MarketMafioso:RequireDashboardAuth", "true"),
            new KeyValuePair<string, string?>("MarketMafioso:DashboardBootstrapUsername", "admin"),
            new KeyValuePair<string, string?>("MarketMafioso:DashboardBootstrapPassword", "secret-password"),
            new KeyValuePair<string, string?>("MarketMafioso:IngestApiKey", "ingest-secret"));
        var factory = CreateFactory(databasePath, configuration);
        var migrator = new SqliteSchemaMigrator(factory, NullLogger<SqliteSchemaMigrator>.Instance);
        await migrator.MigrateAsync(CancellationToken.None);
        var bootstrapper = new ReceiverBootstrapper(
            factory,
            configuration,
            new DashboardPasswordHasher(),
            NullLogger<ReceiverBootstrapper>.Instance);
        await bootstrapper.BootstrapAsync(CancellationToken.None);
        var resolver = new IngestKeyAccountResolver(factory);

        var accountId = await resolver.ResolveAccountIdAsync("ingest-secret", CancellationToken.None);
        var missing = await resolver.ResolveAccountIdAsync("missing-secret", CancellationToken.None);

        Assert.Equal(1, accountId);
        Assert.Null(missing);
    }

    private static SqliteConnectionFactory CreateFactory(string databasePath, IConfiguration? configuration = null)
    {
        configuration ??= CreateConfiguration(databasePath);
        return new SqliteConnectionFactory(configuration, new TestHostEnvironment(Path.GetDirectoryName(databasePath)!));
    }

    private static IConfiguration CreateConfiguration(string databasePath, params KeyValuePair<string, string?>[] values)
    {
        var configurationValues = new Dictionary<string, string?>
        {
            ["MarketMafioso:DatabasePath"] = databasePath,
        };

        foreach (var value in values)
            configurationValues[value.Key] = value.Value;

        return new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MarketMafioso.Server.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "marketmafioso.db");
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", tableName);
        var result = (long)(await command.ExecuteScalarAsync(CancellationToken.None))!;
        return result == 1;
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string tableName, string columnName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName})";
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
        {
            if (reader.GetString(1).Equals(columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static async Task<int> CountAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName}";
        var result = (long)(await command.ExecuteScalarAsync(CancellationToken.None))!;
        return checked((int)result);
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }

    private static async Task<long?> ScalarNullableLongAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(CancellationToken.None);
        return value is null or DBNull ? null : (long)value;
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "MarketMafioso.Server.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
