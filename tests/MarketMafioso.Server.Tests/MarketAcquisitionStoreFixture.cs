using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using MarketMafioso.Server;
using MarketMafioso.Server.Sqlite;

namespace MarketMafioso.Server.Tests;

internal sealed class MarketAcquisitionStoreFixture : IDisposable
{
    private readonly string contentRoot;

    private MarketAcquisitionStoreFixture(
        string contentRoot,
        string databasePath,
        MarketAcquisitionRequestStore store)
    {
        this.contentRoot = contentRoot;
        DatabasePath = databasePath;
        Store = store;
    }

    public MarketAcquisitionRequestStore Store { get; }
    public string DatabasePath { get; }
    public string ReleaseLocalDatabasePath => Path.Combine(contentRoot, "data", "marketmafioso.db");

    public static Task<MarketAcquisitionStoreFixture> CreateAsync(
        params KeyValuePair<string, string?>[] extraConfiguration)
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "MarketMafioso.Server.StoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        var databasePath = Path.Combine(contentRoot, "persistent-data", "marketmafioso.db");

        var values = new Dictionary<string, string?>
        {
            ["MarketMafioso:AcquisitionMinimumExpirySeconds"] = "1",
            ["MarketMafioso:AcquisitionClaimExpirySeconds"] = "300",
            ["MarketMafioso:DatabasePath"] = databasePath,
        };
        foreach (var item in extraConfiguration)
            values[item.Key] = item.Value;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var environment = new TestHostEnvironment(contentRoot);
        var connectionFactory = new SqliteConnectionFactory(configuration, environment);
        var store = new MarketAcquisitionRequestStore(connectionFactory, configuration);

        return Task.FromResult(new MarketAcquisitionStoreFixture(contentRoot, databasePath, store));
    }

    public async Task<MarketAcquisitionTestBatch> CreateClaimedBatchAsync(
        string idempotencyKey,
        int lineCount = 1,
        CancellationToken cancellationToken = default)
    {
        var created = await Store.CreateBatchAsync(
            (MarketAcquisitionBatchCreateRequest)CreateBatchRequest(idempotencyKey, lineCount),
            cancellationToken).ConfigureAwait(false);
        var claimed = await Store.ClaimAsync(
            created.Request.Id,
            new MarketAcquisitionClaimRequest
            {
                CharacterName = MarketAcquisitionTestApp.CharacterName,
                World = MarketAcquisitionTestApp.WorldName,
                PluginInstanceId = MarketAcquisitionTestApp.PluginInstanceId,
            },
            cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Test batch claim failed.");

        return MarketAcquisitionTestBatch.FromClaim(claimed);
    }

    public async Task<MarketAcquisitionTestBatch> CreateAcceptedBatchAsync(
        string idempotencyKey,
        int lineCount = 1,
        CancellationToken cancellationToken = default)
    {
        var claimed = await CreateClaimedBatchAsync(idempotencyKey, lineCount, cancellationToken)
            .ConfigureAwait(false);
        var accepted = await Store.AcceptAsync(
            claimed.Id,
            new MarketAcquisitionClaimTokenRequest
            {
                ClaimToken = claimed.ClaimToken,
                IdempotencyKey = $"{idempotencyKey}-accept",
            },
            cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Test batch accept failed.");

        return claimed with
        {
            Status = accepted.Status,
            Lines = accepted.Lines,
        };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(contentRoot))
                Directory.Delete(contentRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static object CreateBatchRequest(string idempotencyKey, int lineCount)
    {
        var raw = MarketAcquisitionTestApp.CreateBatchRequest(idempotencyKey, lineCount);
        var json = System.Text.Json.JsonSerializer.Serialize(raw, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        return System.Text.Json.JsonSerializer.Deserialize<MarketAcquisitionBatchCreateRequest>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Failed to create typed test batch request.");
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "MarketMafioso.Server.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
    }
}
