using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MarketMafioso.Server.Auth;
using MarketMafioso.Server.Sqlite;

namespace MarketMafioso.Server.Tests;

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
        Assert.True(await TableExistsAsync(connection, "ingest_keys"));
        Assert.True(await TableExistsAsync(connection, "characters"));
        Assert.True(await TableExistsAsync(connection, "snapshots"));
        Assert.True(await TableExistsAsync(connection, "inventory_owners"));
        Assert.True(await TableExistsAsync(connection, "inventory_bags"));
        Assert.True(await TableExistsAsync(connection, "inventory_items"));
    }

    [Fact]
    public async Task MigrateAsync_AddsItemTypeColumnToExistingInventoryItemsTable()
    {
        var databasePath = CreateDatabasePath();
        var factory = CreateFactory(databasePath);
        await using (var connection = await factory.OpenConnectionAsync(CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE inventory_items (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    bag_id INTEGER NOT NULL,
                    item_id INTEGER NOT NULL,
                    item_name TEXT NULL,
                    quantity INTEGER NOT NULL,
                    is_hq INTEGER NOT NULL,
                    condition REAL NOT NULL,
                    sort_order INTEGER NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var migrator = new SqliteSchemaMigrator(factory, NullLogger<SqliteSchemaMigrator>.Instance);

        await migrator.MigrateAsync(CancellationToken.None);

        await using (var connection = await factory.OpenConnectionAsync(CancellationToken.None))
            Assert.True(await ColumnExistsAsync(connection, "inventory_items", "item_type"));
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
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
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

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "MarketMafioso.Server.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
