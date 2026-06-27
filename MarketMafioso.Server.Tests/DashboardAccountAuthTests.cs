using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace MarketMafioso.Server.Tests;

public sealed class DashboardAccountAuthTests
{
    [Fact]
    public async Task DashboardRoutes_RequireBootstrapUserCredentials()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();

        var unauthenticated = await client.GetAsync("/");
        var health = await client.GetAsync("/health");
        using var authenticatedRequest = new HttpRequestMessage(HttpMethod.Get, "/");
        authenticatedRequest.Headers.Authorization = CreateBasicAuth("admin", "secret-password");
        var authenticated = await client.SendAsync(authenticatedRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal("Basic", unauthenticated.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
    }

    [Fact]
    public async Task DashboardRoutes_RejectInvalidBootstrapUserCredentials()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Authorization = CreateBasicAuth("admin", "wrong-password");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DashboardRoutes_RequireCredentialsForBasePathWithoutTrailingSlash()
    {
        await using var application = CreateApplication(
            new KeyValuePair<string, string?>("MarketMafioso:BasePath", "/api/marketmafioso"));
        using var client = application.CreateClient();

        var response = await client.GetAsync("/api/marketmafioso");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/inventory")]
    [InlineData("/acquisition")]
    [InlineData("/diagnostics")]
    public async Task DashboardToolRoutes_RequireBootstrapUserCredentials(string path)
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();

        var unauthenticated = await client.GetAsync(path);
        using var authenticatedRequest = new HttpRequestMessage(HttpMethod.Get, path);
        authenticatedRequest.Headers.Authorization = CreateBasicAuth("admin", "secret-password");
        var authenticated = await client.SendAsync(authenticatedRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
    }

    [Fact]
    public async Task DashboardRoutes_ReuseCachedCredentialsWithinTtl()
    {
        var values = CreateApplicationValues(
            new KeyValuePair<string, string?>("MarketMafioso:DashboardAuthCacheSeconds", "60"));
        await using var application = values.Application;
        using var client = application.CreateClient();

        using var firstRequest = new HttpRequestMessage(HttpMethod.Get, "/");
        firstRequest.Headers.Authorization = CreateBasicAuth("admin", "secret-password");
        var first = await client.SendAsync(firstRequest);
        first.EnsureSuccessStatusCode();

        await using (var connection = new SqliteConnection($"Data Source={values.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE dashboard_users
                SET disabled_at_utc = $disabledAt
                WHERE username = 'admin'
                """;
            command.Parameters.AddWithValue("$disabledAt", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        using var cachedRequest = new HttpRequestMessage(HttpMethod.Get, "/acquisition/requests/recent");
        cachedRequest.Headers.Authorization = CreateBasicAuth("admin", "secret-password");
        var cached = await client.SendAsync(cachedRequest);

        Assert.Equal(HttpStatusCode.OK, cached.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateApplication(params KeyValuePair<string, string?>[] extraConfiguration) =>
        CreateApplicationValues(extraConfiguration).Application;

    private static ApplicationValues CreateApplicationValues(params KeyValuePair<string, string?>[] extraConfiguration)
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "MarketMafioso.Server.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        var databasePath = Path.Combine(contentRoot, "marketmafioso.db");

        var application = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(contentRoot);
                builder.ConfigureAppConfiguration(config =>
                {
                    var values = new Dictionary<string, string?>
                    {
                        ["MarketMafioso:DatabasePath"] = databasePath,
                        ["MarketMafioso:RequireDashboardAuth"] = "true",
                        ["MarketMafioso:DashboardBootstrapUsername"] = "admin",
                        ["MarketMafioso:DashboardBootstrapPassword"] = "secret-password",
                    };
                    foreach (var item in extraConfiguration)
                        values[item.Key] = item.Value;

                    config.AddInMemoryCollection(values);
                });
            });
        return new ApplicationValues(application, databasePath);
    }

    private static AuthenticationHeaderValue CreateBasicAuth(string username, string password)
    {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        return new AuthenticationHeaderValue("Basic", credentials);
    }

    private sealed record ApplicationValues(WebApplicationFactory<Program> Application, string DatabasePath);
}
