using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace MarketMafioso.Server.Tests;

public sealed class MarketAcquisitionRequestEndpointTests
{
    [Fact]
    public async Task HostedMode_CreatesListsClaimsAndAcceptsAcquisitionRequestWithSeparateKeys()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();

        var created = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/api/marketmafioso/acquisition/requests",
            "read-secret",
            CreateRequest("request-1"));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var requestId = createdJson.RootElement.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(requestId));
        Assert.Equal("PendingPickup", createdJson.RootElement.GetProperty("status").GetString());

        var pending = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            "/api/marketmafioso/acquisition/requests/pending?characterName=Wei%20Ning&world=Gilgamesh",
            "command-secret");

        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        using var pendingJson = JsonDocument.Parse(await pending.Content.ReadAsStringAsync());
        var requests = pendingJson.RootElement.GetProperty("requests");
        Assert.Single(requests.EnumerateArray());
        Assert.Equal(requestId, requests[0].GetProperty("id").GetString());

        var claim = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{requestId}/claim",
            "command-secret",
            new
            {
                characterName = "Wei Ning",
                world = "Gilgamesh",
                pluginInstanceId = "plugin-test-instance",
            });

        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        using var claimJson = JsonDocument.Parse(await claim.Content.ReadAsStringAsync());
        var claimToken = claimJson.RootElement.GetProperty("claimToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(claimToken));
        Assert.Equal("Claimed", claimJson.RootElement.GetProperty("status").GetString());

        var pendingAfterClaim = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            "/api/marketmafioso/acquisition/requests/pending?characterName=Wei%20Ning&world=Gilgamesh",
            "command-secret");

        Assert.Equal(HttpStatusCode.OK, pendingAfterClaim.StatusCode);
        using var pendingAfterClaimJson = JsonDocument.Parse(await pendingAfterClaim.Content.ReadAsStringAsync());
        Assert.Empty(pendingAfterClaimJson.RootElement.GetProperty("requests").EnumerateArray());

        var accept = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{requestId}/accept",
            "command-secret",
            new
            {
                claimToken,
                idempotencyKey = "accept-once",
            });

        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        using var acceptJson = JsonDocument.Parse(await accept.Content.ReadAsStringAsync());
        Assert.Equal("AcceptedInPlugin", acceptJson.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task HostedMode_AcquisitionRoutesRejectWrongKeyPurposes()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();

        var createWithIngestKey = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/api/marketmafioso/acquisition/requests",
            "ingest-secret",
            CreateRequest("wrong-create-key"));
        Assert.Equal(HttpStatusCode.Unauthorized, createWithIngestKey.StatusCode);

        var createWithReadKey = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/api/marketmafioso/acquisition/requests",
            "read-secret",
            CreateRequest("right-create-key"));
        createWithReadKey.EnsureSuccessStatusCode();

        var pendingWithReadKey = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            "/api/marketmafioso/acquisition/requests/pending?characterName=Wei%20Ning&world=Gilgamesh",
            "read-secret");
        Assert.Equal(HttpStatusCode.Unauthorized, pendingWithReadKey.StatusCode);

        var pendingWithIngestKey = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            "/api/marketmafioso/acquisition/requests/pending?characterName=Wei%20Ning&world=Gilgamesh",
            "ingest-secret");
        Assert.Equal(HttpStatusCode.Unauthorized, pendingWithIngestKey.StatusCode);
    }

    [Fact]
    public async Task HostedMode_RequiresCommandPickupKeyAtStartup()
    {
        await using var application = CreateHostedApplication(
            extraConfiguration: new KeyValuePair<string, string?>("MarketMafioso:CommandPickupApiKey", string.Empty));

        var ex = Assert.Throws<InvalidOperationException>(() => application.CreateClient());
        Assert.Contains("MarketMafioso:CommandPickupApiKey", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClaimRequiresMatchingScopeAndClaimToken()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();

        var created = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/api/marketmafioso/acquisition/requests",
            "read-secret",
            CreateRequest("claim-scope"));
        created.EnsureSuccessStatusCode();
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var requestId = createdJson.RootElement.GetProperty("id").GetString();

        var wrongScopeClaim = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{requestId}/claim",
            "command-secret",
            new
            {
                characterName = "Other Character",
                world = "Gilgamesh",
                pluginInstanceId = "plugin-test-instance",
            });
        Assert.Equal(HttpStatusCode.NotFound, wrongScopeClaim.StatusCode);

        var claim = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{requestId}/claim",
            "command-secret",
            new
            {
                characterName = "Wei Ning",
                world = "Gilgamesh",
                pluginInstanceId = "plugin-test-instance",
            });
        claim.EnsureSuccessStatusCode();

        var staleAccept = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{requestId}/accept",
            "command-secret",
            new
            {
                claimToken = "not-the-claim-token",
                idempotencyKey = "stale-accept",
            });

        Assert.Equal(HttpStatusCode.Unauthorized, staleAccept.StatusCode);
    }

    [Fact]
    public async Task AcquisitionRequestsPersistAcrossServerRestart()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "MarketMafioso.Server.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);

        await using (var application = CreateHostedApplication(contentRoot))
        {
            using var client = application.CreateClient();
            var created = await SendWithKeyAsync(
                client,
                HttpMethod.Post,
                "/api/marketmafioso/acquisition/requests",
                "read-secret",
                CreateRequest("persisted-request"));
            created.EnsureSuccessStatusCode();
        }

        await using (var restarted = CreateHostedApplication(contentRoot))
        {
            using var restartedClient = restarted.CreateClient();
            var pending = await SendWithKeyAsync(
                restartedClient,
                HttpMethod.Get,
                "/api/marketmafioso/acquisition/requests/pending?characterName=Wei%20Ning&world=Gilgamesh",
                "command-secret");

            Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
            using var pendingJson = JsonDocument.Parse(await pending.Content.ReadAsStringAsync());
            var requests = pendingJson.RootElement.GetProperty("requests");
            Assert.Single(requests.EnumerateArray());
            Assert.Equal("Fire Shard", requests[0].GetProperty("itemName").GetString());
        }
    }

    [Fact]
    public async Task CreateRequestIsIdempotentForSameKeyAndConflictsForDifferentBody()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();

        var first = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/api/marketmafioso/acquisition/requests",
            "read-secret",
            CreateRequest("same-idempotency-key"));
        first.EnsureSuccessStatusCode();
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var firstId = firstJson.RootElement.GetProperty("id").GetString();

        var replay = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/api/marketmafioso/acquisition/requests",
            "read-secret",
            CreateRequest("same-idempotency-key"));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(firstId, replayJson.RootElement.GetProperty("id").GetString());

        var conflict = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/api/marketmafioso/acquisition/requests",
            "read-secret",
            CreateRequest("same-idempotency-key", itemId: 4, itemName: "Lightning Shard"));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task AcquisitionRoutesDoNotMountUnderApiPlugin()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();

        var response = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/api/plugin/acquisition/requests",
            "read-secret",
            CreateRequest("wrong-base-path"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ClaimedRequestCanBeRejectedAndDuplicateRejectIsIdempotent()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();
        var (requestId, claimToken) = await CreateAndClaimAsync(client, "reject-request");

        var reject = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{requestId}/reject",
            "command-secret",
            new
            {
                claimToken,
                idempotencyKey = "reject-once",
                reason = "User rejected in plugin",
            });

        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        using var rejectJson = JsonDocument.Parse(await reject.Content.ReadAsStringAsync());
        Assert.Equal("Rejected", rejectJson.RootElement.GetProperty("status").GetString());

        var replay = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{requestId}/reject",
            "command-secret",
            new
            {
                claimToken,
                idempotencyKey = "reject-once",
                reason = "User rejected in plugin",
            });

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal("Rejected", replayJson.RootElement.GetProperty("status").GetString());

        var conflict = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{requestId}/reject",
            "command-secret",
            new
            {
                claimToken,
                idempotencyKey = "reject-once",
                reason = "Different body",
            });

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task AcceptedRequestReportsProgressThenCompletesAndRejectsLaterMutation()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();
        var (requestId, claimToken) = await CreateAndClaimAsync(client, "complete-request");
        var accept = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{requestId}/accept",
            "command-secret",
            new
            {
                claimToken,
                idempotencyKey = "complete-accept",
            });
        accept.EnsureSuccessStatusCode();

        var progress = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{requestId}/progress",
            "command-secret",
            new
            {
                claimToken,
                idempotencyKey = "progress-once",
                runnerState = "PreparingWorldBatch",
                message = "Preparing Gilgamesh batch",
            });

        Assert.Equal(HttpStatusCode.OK, progress.StatusCode);
        using var progressJson = JsonDocument.Parse(await progress.Content.ReadAsStringAsync());
        Assert.Equal("Running", progressJson.RootElement.GetProperty("status").GetString());

        var complete = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{requestId}/complete",
            "command-secret",
            new
            {
                claimToken,
                idempotencyKey = "complete-once",
                message = "Done",
            });

        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        using var completeJson = JsonDocument.Parse(await complete.Content.ReadAsStringAsync());
        Assert.Equal("Complete", completeJson.RootElement.GetProperty("status").GetString());

        var failAfterComplete = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{requestId}/fail",
            "command-secret",
            new
            {
                claimToken,
                idempotencyKey = "fail-after-complete",
                reason = "Too late",
            });

        Assert.Equal(HttpStatusCode.Conflict, failAfterComplete.StatusCode);
    }

    [Fact]
    public async Task ExpiredRequestCannotBeListedOrClaimed()
    {
        await using var application = CreateHostedApplication(
            extraConfiguration: new KeyValuePair<string, string?>("MarketMafioso:AcquisitionMinimumExpirySeconds", "1"));
        using var client = application.CreateClient();

        var created = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/api/marketmafioso/acquisition/requests",
            "read-secret",
            CreateRequest("expires-fast", expiresInSeconds: 1));
        created.EnsureSuccessStatusCode();
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var requestId = createdJson.RootElement.GetProperty("id").GetString();

        await Task.Delay(TimeSpan.FromSeconds(2));

        var pending = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            "/api/marketmafioso/acquisition/requests/pending?characterName=Wei%20Ning&world=Gilgamesh",
            "command-secret");
        pending.EnsureSuccessStatusCode();
        using var pendingJson = JsonDocument.Parse(await pending.Content.ReadAsStringAsync());
        Assert.Empty(pendingJson.RootElement.GetProperty("requests").EnumerateArray());

        var claim = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{requestId}/claim",
            "command-secret",
            new
            {
                characterName = "Wei Ning",
                world = "Gilgamesh",
                pluginInstanceId = "plugin-test-instance",
            });
        Assert.Equal(HttpStatusCode.NotFound, claim.StatusCode);
    }

    [Fact]
    public async Task ConcurrentClaimsAllowOnlyOneWinner()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();
        var created = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/api/marketmafioso/acquisition/requests",
            "read-secret",
            CreateRequest("concurrent-claim"));
        created.EnsureSuccessStatusCode();
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var requestId = createdJson.RootElement.GetProperty("id").GetString();

        var claimA = SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{requestId}/claim",
            "command-secret",
            new
            {
                characterName = "Wei Ning",
                world = "Gilgamesh",
                pluginInstanceId = "plugin-a",
            });
        var claimB = SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{requestId}/claim",
            "command-secret",
            new
            {
                characterName = "Wei Ning",
                world = "Gilgamesh",
                pluginInstanceId = "plugin-b",
            });

        var responses = await Task.WhenAll(claimA, claimB);
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task ClaimedRequestExpiresBeforeAcceptance()
    {
        await using var application = CreateHostedApplication(
            extraConfiguration: new KeyValuePair<string, string?>("MarketMafioso:AcquisitionClaimExpirySeconds", "1"));
        using var client = application.CreateClient();
        var (requestId, claimToken) = await CreateAndClaimAsync(client, "claim-expires");

        await Task.Delay(TimeSpan.FromSeconds(2));

        var accept = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{requestId}/accept",
            "command-secret",
            new
            {
                claimToken,
                idempotencyKey = "expired-accept",
            });

        Assert.Equal(HttpStatusCode.Conflict, accept.StatusCode);
    }

    [Fact]
    public async Task LifecycleIdempotencyReplayIsScopedToRequestAndClaimToken()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();
        var (firstRequestId, firstClaimToken) = await CreateAndClaimAsync(client, "first-idempotency-scope");
        var (secondRequestId, _) = await CreateAndClaimAsync(client, "second-idempotency-scope");

        var firstReject = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{firstRequestId}/reject",
            "command-secret",
            new
            {
                claimToken = firstClaimToken,
                idempotencyKey = "shared-lifecycle-key",
                reason = "Rejected",
            });
        firstReject.EnsureSuccessStatusCode();

        var falseReplay = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{secondRequestId}/reject",
            "command-secret",
            new
            {
                claimToken = firstClaimToken,
                idempotencyKey = "shared-lifecycle-key",
                reason = "Rejected",
            });

        Assert.Equal(HttpStatusCode.Unauthorized, falseReplay.StatusCode);
    }

    [Fact]
    public async Task AcceptRequestIsIdempotent()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();
        var (requestId, claimToken) = await CreateAndClaimAsync(client, "accept-idempotent");
        var body = new
        {
            claimToken,
            idempotencyKey = "accept-idempotent-key",
        };

        var accept = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{requestId}/accept",
            "command-secret",
            body);
        accept.EnsureSuccessStatusCode();

        var replay = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{requestId}/accept",
            "command-secret",
            body);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal("AcceptedInPlugin", replayJson.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task DashboardRendersAcquisitionFormAndCreatesRequestWithCsrf()
    {
        await using var application = CreateHostedApplication(
            extraConfiguration: new KeyValuePair<string, string?>("MarketMafioso:TrustExternalDashboardAuth", "true"));
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var dashboard = await client.GetStringAsync("/api/marketmafioso/");
        Assert.Contains("Market Acquisition", dashboard, StringComparison.Ordinal);
        Assert.Contains("/api/marketmafioso/acquisition", dashboard, StringComparison.Ordinal);

        var acquisitionPage = await client.GetStringAsync("/api/marketmafioso/acquisition");
        Assert.Contains("Create Dashboard Request", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("name=\"targetCharacterName\"", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("name=\"worldMode\"", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("/api/marketmafioso/acquisition/requests", acquisitionPage, StringComparison.Ordinal);

        var inventory = await client.GetStringAsync("/api/marketmafioso/inventory");
        Assert.Contains(">Acquisition</a>", inventory, StringComparison.Ordinal);
        var csrf = Regex.Match(acquisitionPage, "name=\"csrf\" value=\"(?<token>[^\"]+)\"").Groups["token"].Value;
        Assert.False(string.IsNullOrWhiteSpace(csrf));

        var missingCsrf = await client.PostAsync(
            "/api/marketmafioso/acquisition/requests",
            new FormUrlEncodedContent(CreateFormFields(csrf: string.Empty, idempotencyKey: "missing-csrf")));
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);

        var created = await client.PostAsync(
            "/api/marketmafioso/acquisition/requests",
            new FormUrlEncodedContent(CreateFormFields(csrf, "dashboard-create")));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);

        var pending = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            "/api/marketmafioso/acquisition/requests/pending?characterName=Wei%20Ning&world=Gilgamesh",
            "command-secret");
        pending.EnsureSuccessStatusCode();
        using var pendingJson = JsonDocument.Parse(await pending.Content.ReadAsStringAsync());
        Assert.Single(pendingJson.RootElement.GetProperty("requests").EnumerateArray());
    }

    [Fact]
    public async Task AcquisitionDashboardCreatesRequestAndRedirectsBackToAcquisitionSurface()
    {
        await using var application = CreateHostedApplication(
            extraConfiguration: new KeyValuePair<string, string?>("MarketMafioso:TrustExternalDashboardAuth", "true"));
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var acquisitionPage = await client.GetStringAsync("/api/marketmafioso/acquisition");
        var csrf = Regex.Match(acquisitionPage, "name=\"csrf\" value=\"(?<token>[^\"]+)\"").Groups["token"].Value;

        var created = await client.PostAsync(
            "/api/marketmafioso/acquisition/requests",
            new FormUrlEncodedContent(CreateFormFields(csrf, "acquisition-page-create")));

        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.StartsWith(
            "/api/marketmafioso/acquisition?acquisition=",
            created.Headers.Location?.OriginalString,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcquisitionDashboardRendersControlSurfaceWithRequestQueue()
    {
        await using var application = CreateHostedApplication(
            extraConfiguration: new KeyValuePair<string, string?>("MarketMafioso:TrustExternalDashboardAuth", "true"));
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var accepted = await CreateAndClaimAsync(client, "accepted-dashboard-row");
        var acceptedResponse = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{accepted.RequestId}/accept",
            "command-secret",
            new
            {
                claimToken = accepted.ClaimToken,
                idempotencyKey = "accepted-dashboard-row-accept",
            });
        acceptedResponse.EnsureSuccessStatusCode();

        var acquisitionPage = await client.GetStringAsync("/api/marketmafioso/acquisition");

        Assert.Contains("New Purchase Request", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("Target", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("Purchase Limits", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("Routing", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("Request Preview", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("Stage Request", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("Request Queue", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("Filter by item, world, status", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("Fire Shard", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("Accepted", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("Plugin status", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("Command pickup uses a separate key", acquisitionPage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcquisitionDashboardShowsEmptyQueueState()
    {
        await using var application = CreateHostedApplication(
            extraConfiguration: new KeyValuePair<string, string?>("MarketMafioso:TrustExternalDashboardAuth", "true"));
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var acquisitionPage = await client.GetStringAsync("/api/marketmafioso/acquisition");

        Assert.Contains("No acquisition requests yet.", acquisitionPage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostedDashboardFormCreateUsesAppManagedDashboardAuth()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var dashboard = await client.GetStringAsync("/api/marketmafioso/");
        var csrf = Regex.Match(dashboard, "name=\"csrf\" value=\"(?<token>[^\"]+)\"").Groups["token"].Value;

        var response = await client.PostAsync(
            "/api/marketmafioso/acquisition/requests",
            new FormUrlEncodedContent(CreateFormFields(csrf, "untrusted-dashboard")));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateHostedApplication(
        string? contentRoot = null,
        params KeyValuePair<string, string?>[] extraConfiguration)
    {
        contentRoot ??= Path.Combine(Path.GetTempPath(), "MarketMafioso.Server.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);

        var values = new Dictionary<string, string?>
        {
            ["MarketMafioso:RequireApiKey"] = "true",
            ["MarketMafioso:IngestApiKey"] = "ingest-secret",
            ["MarketMafioso:ReadApiKey"] = "read-secret",
            ["MarketMafioso:CommandPickupApiKey"] = "command-secret",
            ["MarketMafioso:BasePath"] = "/api/marketmafioso",
        };
        foreach (var item in extraConfiguration)
            values[item.Key] = item.Value;

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseContentRoot(contentRoot);
                builder.ConfigureAppConfiguration(config =>
                {
                    config.AddInMemoryCollection(values);
                });
            });
    }

    private static object CreateRequest(
        string idempotencyKey,
        uint itemId = 2,
        string itemName = "Fire Shard",
        int expiresInSeconds = 90) => new
        {
            schemaVersion = 1,
            idempotencyKey,
            targetCharacterName = "Wei Ning",
            targetWorld = "Gilgamesh",
            region = "North America",
            itemId,
            itemName,
            quantityMode = "Exact",
            quantity = 10,
            hqPolicy = "Either",
            maxUnitPrice = 99,
            maxTotalGil = 990,
            worldMode = "Recommended",
            expiresInSeconds,
        };

    private static Dictionary<string, string> CreateFormFields(string csrf, string idempotencyKey) => new()
    {
        ["csrf"] = csrf,
        ["schemaVersion"] = "1",
        ["idempotencyKey"] = idempotencyKey,
        ["targetCharacterName"] = "Wei Ning",
        ["targetWorld"] = "Gilgamesh",
        ["region"] = "North America",
        ["itemId"] = "2",
        ["itemName"] = "Fire Shard",
        ["quantityMode"] = "Exact",
        ["quantity"] = "10",
        ["hqPolicy"] = "Either",
        ["maxUnitPrice"] = "99",
        ["maxTotalGil"] = "990",
        ["worldMode"] = "Recommended",
        ["expiresInSeconds"] = "90",
    };

    private static async Task<(string RequestId, string ClaimToken)> CreateAndClaimAsync(
        HttpClient client,
        string idempotencyKey)
    {
        var created = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/api/marketmafioso/acquisition/requests",
            "read-secret",
            CreateRequest(idempotencyKey));
        created.EnsureSuccessStatusCode();
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var requestId = createdJson.RootElement.GetProperty("id").GetString()!;

        var claim = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{requestId}/claim",
            "command-secret",
            new
            {
                characterName = "Wei Ning",
                world = "Gilgamesh",
                pluginInstanceId = "plugin-test-instance",
            });
        claim.EnsureSuccessStatusCode();
        using var claimJson = JsonDocument.Parse(await claim.Content.ReadAsStringAsync());
        return (requestId, claimJson.RootElement.GetProperty("claimToken").GetString()!);
    }

    private static Task<HttpResponseMessage> SendWithKeyAsync(
        HttpClient client,
        HttpMethod method,
        string requestUri,
        string apiKey,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Add("X-Api-Key", apiKey);
        if (body != null)
            request.Content = JsonContent.Create(body);

        return client.SendAsync(request);
    }
}
