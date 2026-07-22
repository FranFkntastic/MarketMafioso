using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace MarketMafioso.Server.Tests;

public sealed class DashboardAccountAuthTests
{
    [Fact]
    public async Task DashboardShell_LoadsWithoutBrowserAuthPrompt()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("MarketMafioso", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DashboardSession_LoginCreatesCookieAndSession()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();

        var anonymous = await client.GetAsync("/auth/session");
        var login = await client.PostAsJsonAsync("/auth/login", new
        {
            username = "admin",
            password = "secret-password",
        });
        var session = await client.GetAsync("/auth/session");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.True(login.Headers.TryGetValues("Set-Cookie", out var cookies));
        Assert.Contains(cookies, cookie => cookie.Contains("mmf_dashboard_session=", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
    }

    [Fact]
    public async Task DashboardSession_WhenAuthenticationIsDisabled_ProvidesLocalSession()
    {
        await using var application = CreateApplication(
            new KeyValuePair<string, string?>("MarketMafioso:RequireDashboardAuth", "false"));
        using var client = application.CreateClient();

        var session = await client.GetAsync("/auth/session");
        var inventory = await client.GetAsync("/api/inventory/characters");

        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.Contains("Local dashboard", await session.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, inventory.StatusCode);
    }

    [Theory]
    [InlineData("/inventory")]
    [InlineData("/api/inventory")]
    public async Task InventoryIngest_WhenApiKeysAreOptionalDoesNotRequireDashboardSession(string path)
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();

        var response = await client.PostAsJsonAsync(path, new
        {
            characterName = "Wei Ning",
            homeWorld = "Maduin",
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
            playerInventory = Array.Empty<object>(),
            retainers = Array.Empty<object>(),
        });

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DashboardSession_RejectsInvalidCredentials()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();

        var response = await client.PostAsJsonAsync("/auth/login", new
        {
            username = "admin",
            password = "wrong-password",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/reports")]
    [InlineData("/api/reports/latest")]
    [InlineData("/reports/latest/json")]
    [InlineData("/reports/missing")]
    public async Task LegacyReportReaders_RequireCookieSession(string path)
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();

        var anonymous = await client.GetAsync(path);
        var login = await client.PostAsJsonAsync("/auth/login", new
        {
            username = "admin",
            password = "secret-password",
        });
        var authenticated = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, authenticated.StatusCode);
    }

    [Fact]
    public async Task AcquisitionRoutes_ApplyCredentialPolicyUnderConfiguredBasePath()
    {
        await using var application = CreateApplication(
            new KeyValuePair<string, string?>("MarketMafioso:RequireApiKey", "true"),
            new KeyValuePair<string, string?>("MarketMafioso:ClientApiKey", "client-secret"),
            new KeyValuePair<string, string?>("MarketMafioso:BasePath", "/marketmafioso"));
        using var dashboardClient = application.CreateClient();
        using var pluginClient = application.CreateClient();

        var authorizationMatrix = new[]
        {
            new AcquisitionAuthorizationCase(
                "/marketmafioso/api/acquisition/requests",
                AcquisitionCredential.DashboardSession,
                HttpStatusCode.OK),
            new AcquisitionAuthorizationCase(
                "/marketmafioso/api/acquisition/batches/pending?characterName=Wei%20Ning&world=Siren",
                AcquisitionCredential.ClientApiKey,
                HttpStatusCode.OK),
            new AcquisitionAuthorizationCase(
                "/marketmafioso/api/acquisition/batches/missing",
                AcquisitionCredential.DashboardSession,
                HttpStatusCode.NotFound),
            new AcquisitionAuthorizationCase(
                "/marketmafioso/api/acquisition/batches/missing",
                AcquisitionCredential.ClientApiKey,
                HttpStatusCode.NotFound),
            new AcquisitionAuthorizationCase(
                "/marketmafioso/api/acquisition/requests/missing/timeline",
                AcquisitionCredential.DashboardSession,
                HttpStatusCode.NotFound),
            new AcquisitionAuthorizationCase(
                "/marketmafioso/api/acquisition/requests/missing/timeline",
                AcquisitionCredential.ClientApiKey,
                HttpStatusCode.NotFound),
        };

        foreach (var testCase in authorizationMatrix)
        {
            using var anonymous = await dashboardClient.GetAsync(testCase.Path);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        var login = await dashboardClient.PostAsJsonAsync("/marketmafioso/auth/login", new
        {
            username = "admin",
            password = "secret-password",
        });
        login.EnsureSuccessStatusCode();
        using var session = await dashboardClient.GetAsync("/marketmafioso/auth/session");
        Assert.Equal(
            HttpStatusCode.OK,
            session.StatusCode);

        foreach (var testCase in authorizationMatrix)
        {
            using var response = testCase.Credential == AcquisitionCredential.DashboardSession
                ? await dashboardClient.GetAsync(testCase.Path)
                : await SendWithClientApiKeyAsync(pluginClient, testCase.Path);
            Assert.Equal(testCase.AuthorizedStatusCode, response.StatusCode);
        }
    }

    [Fact]
    public async Task DashboardSession_StopsWorkingWhenUserIsDisabled()
    {
        var values = CreateApplicationValues();
        await using var application = values.Application;
        using var client = application.CreateClient();

        var login = await client.PostAsJsonAsync("/auth/login", new
        {
            username = "admin",
            password = "secret-password",
        });
        login.EnsureSuccessStatusCode();

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

        var session = await client.GetAsync("/auth/session");

        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
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

    private static async Task<HttpResponseMessage> SendWithClientApiKeyAsync(HttpClient client, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Api-Key", "client-secret");
        return await client.SendAsync(request);
    }

    private enum AcquisitionCredential
    {
        DashboardSession,
        ClientApiKey,
    }

    private sealed record AcquisitionAuthorizationCase(
        string Path,
        AcquisitionCredential Credential,
        HttpStatusCode AuthorizedStatusCode);

    private sealed record ApplicationValues(WebApplicationFactory<Program> Application, string DatabasePath);
}
