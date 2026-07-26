using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MarketMafioso.Server.Tests;

internal sealed class ServerTestHostConfiguration
{
    internal ServerTestHostConfiguration(string? contentRoot)
    {
        DeleteContentRootOnDispose = contentRoot is null;
        ContentRoot = contentRoot ?? Path.Combine(
            Path.GetTempPath(),
            "MarketMafioso.Server.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ContentRoot);
        DatabasePath = Path.Combine(ContentRoot, "marketmafioso.db");
        Configuration["MarketMafioso:DatabasePath"] = DatabasePath;
    }

    public string ContentRoot { get; }
    public string DatabasePath { get; }
    public Dictionary<string, string?> Configuration { get; } = [];
    internal bool DeleteContentRootOnDispose { get; }
}

internal static class ServerTestHost
{
    public static ServerTestHostConfiguration CreateConfiguration(string? contentRoot = null) =>
        new(contentRoot);

    public static WebApplicationFactory<Program> Create(
        Action<ServerTestHostConfiguration>? configure = null,
        Action<IServiceCollection>? configureServices = null,
        string? contentRoot = null)
    {
        var host = CreateConfiguration(contentRoot);
        configure?.Invoke(host);
        return Create(host, configureServices);
    }

    public static WebApplicationFactory<Program> Create(
        ServerTestHostConfiguration host,
        Action<IServiceCollection>? configureServices = null) =>
        new TemporaryServerTestFactory(host, configureServices);

    private sealed class TemporaryServerTestFactory(
        ServerTestHostConfiguration host,
        Action<IServiceCollection>? configureServices)
        : WebApplicationFactory<Program>
    {
        private int contentRootDeleted;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(host.ContentRoot);
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(host.Configuration));
            if (configureServices is not null)
                builder.ConfigureServices(configureServices);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
                DeleteContentRoot();
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync().ConfigureAwait(false);
            DeleteContentRoot();
            GC.SuppressFinalize(this);
        }

        private void DeleteContentRoot()
        {
            if (!host.DeleteContentRootOnDispose ||
                Interlocked.Exchange(ref contentRootDeleted, 1) != 0)
                return;

            SqliteConnection.ClearAllPools();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Directory.Delete(host.ContentRoot, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Sleep(25);
                }
                catch (UnauthorizedAccessException) when (attempt < 2)
                {
                    Thread.Sleep(25);
                }
                catch (IOException)
                {
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                    return;
                }
            }
        }
    }
}
