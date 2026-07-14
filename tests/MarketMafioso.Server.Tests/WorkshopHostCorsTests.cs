using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MarketMafioso.Server.Tests;

public sealed class WorkshopHostCorsTests
{
    [Fact]
    public async Task ConfiguredOriginCanPreflightWorkshopHostApi()
    {
        await using var application = await MarketAcquisitionTestApp.CreateAsync(
            extraConfiguration: new KeyValuePair<string, string?>(
                "MarketMafioso:AllowedOrigins:0",
                "https://ca.example.test/"));
        using var client = application.CreateClient();
        using var request = CreatePreflight("https://ca.example.test");

        Assert.Equal(
            "https://ca.example.test/",
            application.Services.GetRequiredService<IConfiguration>()["MarketMafioso:AllowedOrigins:0"]);

        using var response = await client.SendAsync(request);

        Assert.Equal(
            "https://ca.example.test",
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains("X-Api-Key", response.Headers.GetValues("Access-Control-Allow-Headers").Single());
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task UnconfiguredOriginReceivesNoCorsGrant()
    {
        await using var application = await MarketAcquisitionTestApp.CreateAsync(
            extraConfiguration: new KeyValuePair<string, string?>(
                "MarketMafioso:AllowedOrigins:0",
                "https://ca.example.test"));
        using var client = application.CreateClient();
        using var request = CreatePreflight("https://untrusted.example.test");

        using var response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    private static HttpRequestMessage CreatePreflight(string origin)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Options,
            "/marketmafioso/api/capabilities");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "X-Api-Key");
        return request;
    }
}
