using Microsoft.Data.Sqlite;

namespace MarketMafioso.Server.Sqlite;

public sealed class SqliteConnectionFactory
{
    private readonly string databasePath;

    public string DatabasePath => databasePath;

    public SqliteConnectionFactory(IConfiguration configuration, IHostEnvironment environment)
    {
        var configuredPath = configuration["MarketMafioso:DatabasePath"];
        databasePath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(environment.ContentRootPath, "data", "marketmafioso.db")
            : configuredPath;
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);

        return connection;
    }
}
