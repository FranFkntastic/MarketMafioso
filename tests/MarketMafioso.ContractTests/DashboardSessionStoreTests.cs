using MarketMafioso.Server.Auth;
using MarketMafioso.Server.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarketMafioso.Server.ContractTests;

public sealed class DashboardSessionStoreTests
{
    [Fact]
    public async Task GetAsync_RemainsReadOnlyWhileAnotherWriterOwnsTheDatabase()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            "MarketMafioso.Server.Tests",
            Guid.NewGuid().ToString("N"),
            "marketmafioso.db");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketMafioso:DatabasePath"] = databasePath,
                ["MarketMafioso:SqliteBusyTimeoutSeconds"] = "1",
            })
            .Build();
        var connectionFactory = new SqliteConnectionFactory(
            configuration,
            new TestHostEnvironment(Path.GetDirectoryName(databasePath)!));
        await new SqliteSchemaMigrator(connectionFactory, NullLogger<SqliteSchemaMigrator>.Instance)
            .MigrateAsync(CancellationToken.None);

        var passwordHasher = new DashboardPasswordHasher();
        await using (var connection = await connectionFactory.OpenConnectionAsync(CancellationToken.None))
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO dashboard_users (username, password_hash, created_at_utc)
                VALUES ('viewer', $passwordHash, $createdAt)
                """;
            insert.Parameters.AddWithValue("$passwordHash", passwordHasher.HashPassword("correct horse battery staple"));
            insert.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var store = new DashboardSessionStore(connectionFactory, passwordHasher, configuration);
        var created = Assert.IsType<DashboardSessionCreateResult>(
            await store.CreateAsync("viewer", "correct horse battery staple", CancellationToken.None));

        await using var writer = await connectionFactory.OpenConnectionAsync(CancellationToken.None);
        await using var transaction = (SqliteTransaction)await writer.BeginTransactionAsync(CancellationToken.None);
        await using (var update = writer.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE dashboard_users SET last_login_at_utc = $now WHERE username = 'viewer'";
            update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            Assert.Equal(1, await update.ExecuteNonQueryAsync(CancellationToken.None));
        }

        var read = store.GetAsync(created.Token, CancellationToken.None);
        var completedWhileLocked = await Task.WhenAny(read, Task.Delay(TimeSpan.FromMilliseconds(750))) == read;
        await transaction.RollbackAsync(CancellationToken.None);
        var session = await read;

        Assert.True(completedWhileLocked);
        Assert.NotNull(session);
        Assert.Equal(created.Session.SessionId, session.SessionId);
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "MarketMafioso.ContractTests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
