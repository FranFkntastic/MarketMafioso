using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace MarketMafioso.Server.ContractTests;

public sealed class DashboardAccountAuthTests
{
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

    [Fact]
    public async Task ReportReaderRequiresCookieSession()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();

        var anonymous = await client.GetAsync("/api/reports");
        var login = await client.PostAsJsonAsync("/auth/login", new
        {
            username = "admin",
            password = "secret-password",
        });
        var authenticated = await client.GetAsync("/api/reports");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, authenticated.StatusCode);
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
        var host = ServerTestHost.CreateConfiguration();
        host.Configuration["MarketMafioso:RequireDashboardAuth"] = "true";
        host.Configuration["MarketMafioso:DashboardBootstrapUsername"] = "admin";
        host.Configuration["MarketMafioso:DashboardBootstrapPassword"] = "secret-password";
        foreach (var item in extraConfiguration)
            host.Configuration[item.Key] = item.Value;

        return new ApplicationValues(ServerTestHost.Create(host), host.DatabasePath);
    }

    private sealed record ApplicationValues(WebApplicationFactory<Program> Application, string DatabasePath);
}
