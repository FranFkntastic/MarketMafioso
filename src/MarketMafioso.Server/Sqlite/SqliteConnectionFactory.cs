using Microsoft.Data.Sqlite;

namespace MarketMafioso.Server.Sqlite;

public sealed class SqliteConnectionFactory
{
    private readonly string databasePath;
    private readonly int busyTimeoutSeconds;

    public string DatabasePath => databasePath;

    public SqliteConnectionFactory(IConfiguration configuration, IHostEnvironment environment)
    {
        var configuredPath = configuration["MarketMafioso:DatabasePath"];
        databasePath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(environment.ContentRootPath, "data", "marketmafioso.db")
            : configuredPath;
        busyTimeoutSeconds = Math.Clamp(
            configuration.GetValue<int?>("MarketMafioso:SqliteBusyTimeoutSeconds") ?? 5,
            1,
            60);
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            DefaultTimeout = busyTimeoutSeconds,
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);

        return connection;
    }
}
