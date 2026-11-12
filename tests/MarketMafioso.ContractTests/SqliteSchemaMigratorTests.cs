using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MarketMafioso.Server.Sqlite;

namespace MarketMafioso.Server.ContractTests;

public sealed class SqliteSchemaMigratorTests
{

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
        Assert.Equal(1, await ScalarLongAsync(verify, "SELECT is_current FROM snapshots WHERE id = 'legacy'"));
        Assert.Equal(1, await ScalarLongAsync(verify, "SELECT semantic_revision FROM snapshots WHERE id = 'legacy'"));
        Assert.Equal(64, (await ScalarStringAsync(verify, "SELECT semantic_hash FROM snapshots WHERE id = 'legacy'")).Length);
        Assert.Equal(1, await ScalarLongAsync(verify, "SELECT revision FROM inventory_account_revisions WHERE account_id = 1"));
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
