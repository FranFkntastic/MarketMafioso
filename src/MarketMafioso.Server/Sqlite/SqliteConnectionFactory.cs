using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarketMafioso.Server.Sqlite;

public sealed class SqliteConnectionFactory
{
    private readonly string databasePath;
    private readonly int busyTimeoutSeconds;
    private readonly ILogger<SqliteConnectionFactory> log;

    public string DatabasePath => databasePath;

    public SqliteConnectionFactory(IConfiguration configuration, IHostEnvironment environment,
        ILogger<SqliteConnectionFactory>? log = null)
    {
        this.log = log ?? NullLogger<SqliteConnectionFactory>.Instance;
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

    internal WriteLease BeginWrite(SqliteConnection connection,
        [CallerMemberName] string operation = "", [CallerFilePath] string source = "")
    {
        var name = Path.GetFileNameWithoutExtension(source) + "." + operation;
        try
        {
            // Explicit IMMEDIATE avoids deferred read -> write upgrade failures in WAL.
            return new WriteLease(connection.BeginTransaction(deferred: false), name, log);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            log.LogWarning(exception, "SQLite busy waiting for write operation {Operation}", name);
            throw;
        }
    }

    internal static string WaitingOperation(Exception exception) =>
        new StackTrace(exception).GetFrames()
            .Select(frame => frame.GetMethod()?.DeclaringType?.FullName)
            .FirstOrDefault(name => name?.StartsWith("MarketMafioso.Server.", StringComparison.Ordinal) == true)
        ?? "unknown server operation";

    internal sealed class WriteLease(SqliteTransaction transaction, string operation, ILogger log) : IDisposable
    {
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        public SqliteTransaction Transaction => transaction;
        public void Dispose()
        {
            transaction.Dispose();
            if (elapsed.Elapsed >= TimeSpan.FromSeconds(1))
                log.LogWarning("SQLite write operation {Operation} transaction scope lasted {DurationMs} ms",
                    operation, elapsed.Elapsed.TotalMilliseconds);
        }
    }
}
