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
    public async Task DashboardApi_RequiresCookieSession()
    {
        await using var application = CreateApplication();
        using var client = application.CreateClient();

        var anonymous = await client.GetAsync("/api/acquisition/requests");
        var login = await client.PostAsJsonAsync("/auth/login", new
        {
            username = "admin",
            password = "secret-password",
        });
        var authenticated = await client.GetAsync("/api/acquisition/requests");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
    }

    [Fact]
    public async Task DashboardSession_WorksUnderConfiguredBasePath()
    {
        await using var application = CreateApplication(
            new KeyValuePair<string, string?>("MarketMafioso:RequireApiKey", "true"),
            new KeyValuePair<string, string?>("MarketMafioso:ClientApiKey", "client-secret"),
            new KeyValuePair<string, string?>("MarketMafioso:BasePath", "/marketmafioso"));
        using var client = application.CreateClient();

        var anonymous = await client.GetAsync("/marketmafioso/auth/session");
        var anonymousApi = await client.GetAsync("/marketmafioso/api/acquisition/requests");
        var anonymousPending = await client.GetAsync("/marketmafioso/api/acquisition/requests/pending?characterName=Smoke&world=Gilgamesh");
        var login = await client.PostAsJsonAsync("/marketmafioso/auth/login", new
        {
            username = "admin",
            password = "secret-password",
        });
        var session = await client.GetAsync("/marketmafioso/auth/session");
        var authenticatedApi = await client.GetAsync("/marketmafioso/api/acquisition/requests");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousApi.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousPending.StatusCode);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authenticatedApi.StatusCode);
    }

    [Fact]
    public async Task DashboardSession_AllowsPluginBatchPendingWithApiKeyUnderBasePath()
    {
        await using var application = CreateApplication(
            new KeyValuePair<string, string?>("MarketMafioso:RequireApiKey", "true"),
            new KeyValuePair<string, string?>("MarketMafioso:ClientApiKey", "client-secret"),
            new KeyValuePair<string, string?>("MarketMafioso:BasePath", "/marketmafioso"));
        using var client = application.CreateClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/marketmafioso/api/acquisition/batches/pending?characterName=Wei%20Ning&world=Siren");
        request.Headers.Add("X-Api-Key", "client-secret");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DashboardSession_AllowsPluginBatchCreateWithApiKeyUnderBasePath()
    {
        await using var application = CreateApplication(
            new KeyValuePair<string, string?>("MarketMafioso:RequireApiKey", "true"),
            new KeyValuePair<string, string?>("MarketMafioso:ClientApiKey", "client-secret"),
            new KeyValuePair<string, string?>("MarketMafioso:BasePath", "/marketmafioso"));
        using var client = application.CreateClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/marketmafioso/api/acquisition/batches")
        {
            Content = JsonContent.Create(MarketAcquisitionTestApp.CreateBatchRequest("dashboard-session-batch-create")),
        };
        request.Headers.Add("X-Api-Key", "client-secret");
        request.Headers.Accept.ParseAdd("application/json");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task DashboardSession_AllowsPluginBatchReplaceWithApiKeyUnderBasePath()
    {
        await using var application = CreateApplication(
            new KeyValuePair<string, string?>("MarketMafioso:RequireApiKey", "true"),
            new KeyValuePair<string, string?>("MarketMafioso:ClientApiKey", "client-secret"),
            new KeyValuePair<string, string?>("MarketMafioso:BasePath", "/marketmafioso"));
        using var client = application.CreateClient();

        using var createRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/marketmafioso/api/acquisition/batches")
        {
            Content = JsonContent.Create(MarketAcquisitionTestApp.CreateBatchRequest("dashboard-session-batch-replace")),
        };
        createRequest.Headers.Add("X-Api-Key", "client-secret");
        createRequest.Headers.Accept.ParseAdd("application/json");
        var created = await client.SendAsync(createRequest);
        created.EnsureSuccessStatusCode();
        var createdBatch = await created.Content.ReadFromJsonAsync<MarketAcquisitionRequestView>()
            ?? throw new InvalidOperationException("Batch create response was empty.");

        using var replaceRequest = new HttpRequestMessage(
            HttpMethod.Put,
            $"/marketmafioso/api/acquisition/batches/{createdBatch.Id}")
        {
            Content = JsonContent.Create(new
            {
                expectedRevision = createdBatch.Revision,
                region = "North America",
                worldMode = "Recommended",
                sweepScope = "Region",
                expiresInSeconds = 300,
                lines = new object[]
                {
                    new
                    {
                        itemId = 19951,
                        itemName = "Koppranickel Ore",
                        itemKind = "Stone",
                        quantityMode = "AllBelowThreshold",
                        targetQuantity = 0,
                        maxQuantity = 25,
                        hqPolicy = "Either",
                        maxUnitPrice = 276,
                        gilCap = 0,
                    },
                },
            }),
        };
        replaceRequest.Headers.Add("X-Api-Key", "client-secret");
        replaceRequest.Headers.Accept.ParseAdd("application/json");

        var response = await client.SendAsync(replaceRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DashboardSession_AllowsPluginBatchReadWithApiKeyUnderBasePath()
    {
        await using var application = CreateApplication(
            new KeyValuePair<string, string?>("MarketMafioso:RequireApiKey", "true"),
            new KeyValuePair<string, string?>("MarketMafioso:ClientApiKey", "client-secret"),
            new KeyValuePair<string, string?>("MarketMafioso:BasePath", "/marketmafioso"));
        using var client = application.CreateClient();
        var accepted = await application.CreateAcceptedBatchAsync(client, "dashboard-session-batch-read");

        var response = await MarketAcquisitionTestApp.SendWithKeyAsync(
            client,
            HttpMethod.Get,
            $"/marketmafioso/api/acquisition/batches/{accepted.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DashboardSession_AllowsPluginBatchLineProgressWithApiKeyUnderBasePath()
    {
        await using var application = CreateApplication(
            new KeyValuePair<string, string?>("MarketMafioso:RequireApiKey", "true"),
            new KeyValuePair<string, string?>("MarketMafioso:ClientApiKey", "client-secret"),
            new KeyValuePair<string, string?>("MarketMafioso:BasePath", "/marketmafioso"));
        using var client = application.CreateClient();
        var accepted = await application.CreateAcceptedBatchAsync(client, "dashboard-session-line-progress");
        var line = Assert.Single(accepted.Lines);

        var response = await MarketAcquisitionTestApp.SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/batches/{accepted.Id}/lines/{line.LineId}/progress",
            new
            {
                claimToken = accepted.ClaimToken,
                idempotencyKey = "dashboard-session-line-progress-1",
                attemptId = "attempt-1",
                sequence = 1,
                status = "Running",
                message = "Line progress reached the server.",
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DashboardSession_AllowsPluginBatchPurchaseAuditWithApiKeyUnderBasePath()
    {
        await using var application = CreateApplication(
            new KeyValuePair<string, string?>("MarketMafioso:RequireApiKey", "true"),
            new KeyValuePair<string, string?>("MarketMafioso:ClientApiKey", "client-secret"),
            new KeyValuePair<string, string?>("MarketMafioso:BasePath", "/marketmafioso"));
        using var client = application.CreateClient();
        var accepted = await application.CreateAcceptedBatchAsync(client, "dashboard-session-purchase-audit");
        var line = Assert.Single(accepted.Lines);

        var response = await MarketAcquisitionTestApp.SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/batches/{accepted.Id}/purchases",
            new
            {
                claimToken = accepted.ClaimToken,
                idempotencyKey = "dashboard-session-purchase-audit-1",
                attemptId = "attempt-1",
                sequence = 1,
                lineId = line.LineId,
                worldName = "Gilgamesh",
                itemId = line.ItemId,
                itemName = line.ItemName,
                listingId = "listing-1",
                retainerName = "Retainer",
                retainerId = "retainer-1",
                quantity = 1,
                unitPrice = 99,
                totalGil = 99,
                isHq = false,
                result = "Purchased",
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DashboardSession_AllowsPluginTimelineReadWithApiKeyUnderBasePath()
    {
        await using var application = CreateApplication(
            new KeyValuePair<string, string?>("MarketMafioso:RequireApiKey", "true"),
            new KeyValuePair<string, string?>("MarketMafioso:ClientApiKey", "client-secret"),
            new KeyValuePair<string, string?>("MarketMafioso:BasePath", "/marketmafioso"));
        using var client = application.CreateClient();
        var accepted = await application.CreateAcceptedBatchAsync(client, "dashboard-session-timeline-read");

        var response = await MarketAcquisitionTestApp.SendWithKeyAsync(
            client,
            HttpMethod.Get,
            $"/marketmafioso/api/acquisition/requests/{accepted.Id}/timeline");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DashboardSession_AllowsTimelineReadWithCookieAndNoApiKeyUnderBasePath()
    {
        await using var application = CreateApplication(
            new KeyValuePair<string, string?>("MarketMafioso:RequireApiKey", "true"),
            new KeyValuePair<string, string?>("MarketMafioso:ClientApiKey", "client-secret"),
            new KeyValuePair<string, string?>("MarketMafioso:BasePath", "/marketmafioso"));
        using var client = application.CreateClient();
        var accepted = await application.CreateAcceptedBatchAsync(client, "dashboard-cookie-timeline-read");
        var login = await client.PostAsJsonAsync("/marketmafioso/auth/login", new
        {
            username = "admin",
            password = "secret-password",
        });
        login.EnsureSuccessStatusCode();

        var response = await client.GetAsync(
            $"/marketmafioso/api/acquisition/requests/{accepted.Id}/timeline");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DashboardSession_AllowsPluginMarketObservationWithApiKeyUnderBasePath()
    {
        await using var application = CreateApplication(
            new KeyValuePair<string, string?>("MarketMafioso:RequireApiKey", "true"),
            new KeyValuePair<string, string?>("MarketMafioso:ClientApiKey", "client-secret"),
            new KeyValuePair<string, string?>("MarketMafioso:BasePath", "/marketmafioso"));
        using var client = application.CreateClient();
        var accepted = await application.CreateAcceptedBatchAsync(client, "dashboard-session-market-observation");
        var line = Assert.Single(accepted.Lines);

        var response = await MarketAcquisitionTestApp.SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/batches/{accepted.Id}/observations",
            new
            {
                claimToken = accepted.ClaimToken,
                idempotencyKey = "dashboard-session-market-observation-1",
                attemptId = "attempt-1",
                sequence = 1,
                lineId = line.LineId,
                itemId = line.ItemId,
                itemName = line.ItemName,
                dataCenter = "Aether",
                worldName = "Gilgamesh",
                readState = "Complete",
                reportedListingCount = 1,
                listingCapacity = 100,
                isTruncated = false,
                observedAtUtc = DateTimeOffset.UtcNow,
                listings = new[]
                {
                    new
                    {
                        listingId = "listing-1",
                        retainerId = "retainer-1",
                        retainerName = "Retainer",
                        quantity = 10,
                        unitPrice = 99,
                        isHq = false,
                    },
                },
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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

    private sealed record ApplicationValues(WebApplicationFactory<Program> Application, string DatabasePath);
}
