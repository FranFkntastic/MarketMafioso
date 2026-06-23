using System.Net;
using System.Net.Http.Headers;
using System.Text;
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

    private static WebApplicationFactory<Program> CreateApplication()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "MarketMafioso.Server.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(contentRoot);
                builder.ConfigureAppConfiguration(config =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["MarketMafioso:DatabasePath"] = Path.Combine(contentRoot, "marketmafioso.db"),
                        ["MarketMafioso:RequireDashboardAuth"] = "true",
                        ["MarketMafioso:DashboardBootstrapUsername"] = "admin",
                        ["MarketMafioso:DashboardBootstrapPassword"] = "secret-password",
                    });
                });
            });
    }

    private static AuthenticationHeaderValue CreateBasicAuth(string username, string password)
    {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        return new AuthenticationHeaderValue("Basic", credentials);
    }
}
