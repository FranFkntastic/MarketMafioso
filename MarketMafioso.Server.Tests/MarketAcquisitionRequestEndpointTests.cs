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
    public async Task HostedMode_CreatesListsClaimsAndAcceptsAcquisitionRequestWithClientKey()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();

        var created = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/api/marketmafioso/acquisition/requests",
            "client-secret",
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
            "client-secret");

        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        using var pendingJson = JsonDocument.Parse(await pending.Content.ReadAsStringAsync());
        var requests = pendingJson.RootElement.GetProperty("requests");
        Assert.Single(requests.EnumerateArray());
        Assert.Equal(requestId, requests[0].GetProperty("id").GetString());

        var claim = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{requestId}/claim",
            "client-secret",
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
            "client-secret");

        Assert.Equal(HttpStatusCode.OK, pendingAfterClaim.StatusCode);
        using var pendingAfterClaimJson = JsonDocument.Parse(await pendingAfterClaim.Content.ReadAsStringAsync());
        Assert.Empty(pendingAfterClaimJson.RootElement.GetProperty("requests").EnumerateArray());

        var accept = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{requestId}/accept",
            "client-secret",
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
    public async Task HostedMode_AcquisitionRoutesUseSameClientKeyAsInventory()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();

        var createWithClientKey = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/api/marketmafioso/acquisition/requests",
            "client-secret",
            CreateRequest("client-create-key"));
        createWithClientKey.EnsureSuccessStatusCode();

        var pendingWithClientKey = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            "/api/marketmafioso/acquisition/requests/pending?characterName=Wei%20Ning&world=Gilgamesh",
            "client-secret");
        Assert.Equal(HttpStatusCode.OK, pendingWithClientKey.StatusCode);

        var pendingWithWrongKey = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            "/api/marketmafioso/acquisition/requests/pending?characterName=Wei%20Ning&world=Gilgamesh",
            "wrong-secret");
        Assert.Equal(HttpStatusCode.Unauthorized, pendingWithWrongKey.StatusCode);
    }

    [Fact]
    public async Task HostedMode_RequiresClientApiKeyAtStartup()
    {
        await using var application = CreateHostedApplication(
            extraConfiguration: [
                new KeyValuePair<string, string?>("MarketMafioso:ClientApiKey", string.Empty),
                new KeyValuePair<string, string?>("MarketMafioso:ApiKey", string.Empty),
                new KeyValuePair<string, string?>("MarketMafioso:IngestApiKey", string.Empty),
            ]);

        var ex = Assert.Throws<InvalidOperationException>(() => application.CreateClient());
        Assert.Contains("MarketMafioso:ClientApiKey", ex.Message, StringComparison.Ordinal);
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
            "client-secret",
            CreateRequest("claim-scope"));
        created.EnsureSuccessStatusCode();
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var requestId = createdJson.RootElement.GetProperty("id").GetString();

        var wrongScopeClaim = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{requestId}/claim",
            "client-secret",
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
            "client-secret",
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
            "client-secret",
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
                "client-secret",
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
                "client-secret");

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
            "client-secret",
            CreateRequest("same-idempotency-key"));
        first.EnsureSuccessStatusCode();
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var firstId = firstJson.RootElement.GetProperty("id").GetString();

        var replay = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/api/marketmafioso/acquisition/requests",
            "client-secret",
            CreateRequest("same-idempotency-key"));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(firstId, replayJson.RootElement.GetProperty("id").GetString());

        var conflict = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/api/marketmafioso/acquisition/requests",
            "client-secret",
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
            "client-secret",
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
            "client-secret",
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
            "client-secret",
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
            "client-secret",
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
            "client-secret",
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
            "client-secret",
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
        Assert.Equal("progress", progressJson.RootElement.GetProperty("latestEventType").GetString());
        Assert.Equal("PreparingWorldBatch", progressJson.RootElement.GetProperty("latestRunnerState").GetString());
        Assert.Equal("Preparing Gilgamesh batch", progressJson.RootElement.GetProperty("latestMessage").GetString());

        var complete = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{requestId}/complete",
            "client-secret",
            new
            {
                claimToken,
                idempotencyKey = "complete-once",
                message = "Done",
            });

        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        using var completeJson = JsonDocument.Parse(await complete.Content.ReadAsStringAsync());
        Assert.Equal("Complete", completeJson.RootElement.GetProperty("status").GetString());
        Assert.Equal("complete", completeJson.RootElement.GetProperty("latestEventType").GetString());
        Assert.Equal("Done", completeJson.RootElement.GetProperty("latestMessage").GetString());

        var failAfterComplete = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{requestId}/fail",
            "client-secret",
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
            "client-secret",
            CreateRequest("expires-fast", expiresInSeconds: 1));
        created.EnsureSuccessStatusCode();
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var requestId = createdJson.RootElement.GetProperty("id").GetString();

        await Task.Delay(TimeSpan.FromSeconds(2));

        var pending = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            "/api/marketmafioso/acquisition/requests/pending?characterName=Wei%20Ning&world=Gilgamesh",
            "client-secret");
        pending.EnsureSuccessStatusCode();
        using var pendingJson = JsonDocument.Parse(await pending.Content.ReadAsStringAsync());
        Assert.Empty(pendingJson.RootElement.GetProperty("requests").EnumerateArray());

        var claim = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{requestId}/claim",
            "client-secret",
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
            "client-secret",
            CreateRequest("concurrent-claim"));
        created.EnsureSuccessStatusCode();
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var requestId = createdJson.RootElement.GetProperty("id").GetString();

        var claimA = SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{requestId}/claim",
            "client-secret",
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
            "client-secret",
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
            "client-secret",
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
            "client-secret",
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
            "client-secret",
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
            "client-secret",
            body);
        accept.EnsureSuccessStatusCode();

        var replay = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{requestId}/accept",
            "client-secret",
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
            "client-secret");
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
            "client-secret",
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
        Assert.Contains("Add to Queue", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("Stage Queue", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("Request Queue", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("Filter by item, world, status", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("class=\"acquisition-main\"", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("class=\"pane request-pane\"", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("class=\"pane queue-pane\"", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("class=\"section-title\">Item</", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("id=\"acquisitionItemSearch\"", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("id=\"acquisitionItemSuggestions\"", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("name=\"itemId\"", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("name=\"itemName\"", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("id=\"acquisitionQueueRows\"", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("id=\"acquisitionStageStatus\"", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("searchAcquisitionItems", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("validateAcquisitionQueueRow", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("readAcquisitionStageError", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("addAcquisitionQueueRow", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("stageAcquisitionQueue", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("id=\"acquisitionQueueTable\"", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("id=\"acquisitionQueueBody\"", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("data-resize=\"status\"", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("refreshAcquisitionQueue", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("setInterval(refreshAcquisitionQueue, 3000)", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("data-xiv-data-base-url=\"https://dev.xivcraftarchitect.com/api/xivdata\"", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("Plugin pickup required", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("No background polling", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("All statuses", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("Refresh", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("class=\"detail\"", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("class=\"statusbar\"", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("Fire Shard", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("Accepted", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("Plugin status", acquisitionPage, StringComparison.Ordinal);
        Assert.Contains("Plugin pickup uses the same client API key", acquisitionPage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcquisitionDashboardRecentQueueEndpointReturnsLiveStatusMarkup()
    {
        await using var application = CreateHostedApplication(
            extraConfiguration: new KeyValuePair<string, string?>("MarketMafioso:TrustExternalDashboardAuth", "true"));
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var claimed = await CreateAndClaimAsync(client, "dashboard-live-queue");
        var accept = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{claimed.RequestId}/accept",
            "client-secret",
            new
            {
                claimToken = claimed.ClaimToken,
                idempotencyKey = "dashboard-live-queue-accept",
            });
        accept.EnsureSuccessStatusCode();
        var progress = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{claimed.RequestId}/progress",
            "client-secret",
            new
            {
                claimToken = claimed.ClaimToken,
                idempotencyKey = "dashboard-live-queue-progress",
                runnerState = "Running",
                message = "Arrived on Maduin; reading live listings.",
            });
        progress.EnsureSuccessStatusCode();

        var recent = await client.GetAsync("/api/marketmafioso/acquisition/requests/recent");

        recent.EnsureSuccessStatusCode();
        using var recentJson = JsonDocument.Parse(await recent.Content.ReadAsStringAsync());
        var root = recentJson.RootElement;
        Assert.Contains("Running", root.GetProperty("latestRequestStatus").GetString(), StringComparison.Ordinal);
        Assert.Contains("Arrived on Maduin", root.GetProperty("latestRequestEvent").GetString(), StringComparison.Ordinal);
        Assert.Contains("data-resize-col=\"status\"", root.GetProperty("queueRows").GetString(), StringComparison.Ordinal);
        Assert.Contains("Arrived on Maduin", root.GetProperty("queueRows").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcquisitionDashboardCanCancelClaimedRequest()
    {
        await using var application = CreateHostedApplication(
            extraConfiguration: new KeyValuePair<string, string?>("MarketMafioso:TrustExternalDashboardAuth", "true"));
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var claimed = await CreateAndClaimAsync(client, "dashboard-cancel-claimed");
        var acquisitionPage = await client.GetStringAsync("/api/marketmafioso/acquisition");
        var csrf = Regex.Match(acquisitionPage, "name=\"csrf\" value=\"(?<token>[^\"]+)\"").Groups["token"].Value;

        var cancelled = await client.PostAsync(
            $"/api/marketmafioso/acquisition/requests/{claimed.RequestId}/cancel",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["csrf"] = csrf,
            }));

        Assert.Equal(HttpStatusCode.Redirect, cancelled.StatusCode);
        var refreshedPage = await client.GetStringAsync("/api/marketmafioso/acquisition");
        Assert.Contains("Cancelled", refreshedPage, StringComparison.Ordinal);

        var pending = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            "/api/marketmafioso/acquisition/requests/pending?characterName=Wei%20Ning&world=Gilgamesh",
            "client-secret");
        pending.EnsureSuccessStatusCode();
        using var pendingJson = JsonDocument.Parse(await pending.Content.ReadAsStringAsync());
        Assert.Empty(pendingJson.RootElement.GetProperty("requests").EnumerateArray());
    }

    [Fact]
    public async Task AcquisitionDashboardCancelIsIdempotentForCancelledAndCompleteRequests()
    {
        await using var application = CreateHostedApplication(
            extraConfiguration: new KeyValuePair<string, string?>("MarketMafioso:TrustExternalDashboardAuth", "true"));
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var claimed = await CreateAndClaimAsync(client, "dashboard-cancel-terminal");
        var acquisitionPage = await client.GetStringAsync("/api/marketmafioso/acquisition");
        var csrf = Regex.Match(acquisitionPage, "name=\"csrf\" value=\"(?<token>[^\"]+)\"").Groups["token"].Value;

        var accepted = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{claimed.RequestId}/accept",
            "client-secret",
            new
            {
                claimToken = claimed.ClaimToken,
                idempotencyKey = "terminal-cancel-accept",
            });
        accepted.EnsureSuccessStatusCode();
        var complete = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{claimed.RequestId}/complete",
            "client-secret",
            new
            {
                claimToken = claimed.ClaimToken,
                idempotencyKey = "terminal-cancel-complete",
                message = "Dry-run route complete.",
            });
        complete.EnsureSuccessStatusCode();

        var cancelComplete = await client.PostAsync(
            $"/api/marketmafioso/acquisition/requests/{claimed.RequestId}/cancel",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["csrf"] = csrf,
            }));
        var cancelCompleteAgain = await client.PostAsync(
            $"/api/marketmafioso/acquisition/requests/{claimed.RequestId}/cancel",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["csrf"] = csrf,
            }));

        Assert.Equal(HttpStatusCode.Redirect, cancelComplete.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, cancelCompleteAgain.StatusCode);
        var refreshedPage = await client.GetStringAsync("/api/marketmafioso/acquisition");
        Assert.Contains("Complete", refreshedPage, StringComparison.Ordinal);
        Assert.DoesNotContain("Cancelled", refreshedPage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcquisitionDashboardCanResendClaimedRequest()
    {
        await using var application = CreateHostedApplication(
            extraConfiguration: new KeyValuePair<string, string?>("MarketMafioso:TrustExternalDashboardAuth", "true"));
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var claimed = await CreateAndClaimAsync(client, "dashboard-resend-claimed");
        var acquisitionPage = await client.GetStringAsync("/api/marketmafioso/acquisition");
        var csrf = Regex.Match(acquisitionPage, "name=\"csrf\" value=\"(?<token>[^\"]+)\"").Groups["token"].Value;

        var resent = await client.PostAsync(
            $"/api/marketmafioso/acquisition/requests/{claimed.RequestId}/resend",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["csrf"] = csrf,
            }));

        Assert.Equal(HttpStatusCode.Redirect, resent.StatusCode);

        var pending = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            "/api/marketmafioso/acquisition/requests/pending?characterName=Wei%20Ning&world=Gilgamesh",
            "client-secret");
        pending.EnsureSuccessStatusCode();
        using var pendingJson = JsonDocument.Parse(await pending.Content.ReadAsStringAsync());
        var request = Assert.Single(pendingJson.RootElement.GetProperty("requests").EnumerateArray());
        Assert.Equal(claimed.RequestId, request.GetProperty("id").GetString());
        Assert.Equal("PendingPickup", request.GetProperty("status").GetString());
    }

    [Fact]
    public async Task AcquisitionDashboardUsesConfiguredXivDataBaseUrl()
    {
        await using var application = CreateHostedApplication(
            extraConfiguration:
            [
                new KeyValuePair<string, string?>("MarketMafioso:TrustExternalDashboardAuth", "true"),
                new KeyValuePair<string, string?>("MarketMafioso:XivDataBaseUrl", "https://example.test/xivdata"),
            ]);
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var acquisitionPage = await client.GetStringAsync("/api/marketmafioso/acquisition");

        Assert.Contains("data-xiv-data-base-url=\"https://example.test/xivdata\"", acquisitionPage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcquisitionDashboardDefaultsXivDataBaseUrlFromPublicOrigin()
    {
        await using var application = CreateHostedApplication(
            extraConfiguration:
            [
                new KeyValuePair<string, string?>("MarketMafioso:TrustExternalDashboardAuth", "true"),
                new KeyValuePair<string, string?>("MarketMafioso:PublicOrigin", "https://staging.example.test/"),
            ]);
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var acquisitionPage = await client.GetStringAsync("/api/marketmafioso/acquisition");

        Assert.Contains("data-xiv-data-base-url=\"https://staging.example.test/api/xivdata\"", acquisitionPage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcquisitionDashboardRejectsUnresolvedItemName()
    {
        await using var application = CreateHostedApplication(
            extraConfiguration: new KeyValuePair<string, string?>("MarketMafioso:TrustExternalDashboardAuth", "true"));
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var acquisitionPage = await client.GetStringAsync("/api/marketmafioso/acquisition");
        var csrf = Regex.Match(acquisitionPage, "name=\"csrf\" value=\"(?<token>[^\"]+)\"").Groups["token"].Value;
        var fields = CreateFormFields(csrf, "unresolved-item");
        fields["itemId"] = string.Empty;
        fields["itemName"] = "Darksteel Nugget";

        var response = await client.PostAsync(
            "/api/marketmafioso/acquisition/requests",
            new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AcquisitionDashboardUsesItemIdFallbackNameWhenNameIsBlank()
    {
        await using var application = CreateHostedApplication(
            extraConfiguration: new KeyValuePair<string, string?>("MarketMafioso:TrustExternalDashboardAuth", "true"));
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var acquisitionPage = await client.GetStringAsync("/api/marketmafioso/acquisition");
        var csrf = Regex.Match(acquisitionPage, "name=\"csrf\" value=\"(?<token>[^\"]+)\"").Groups["token"].Value;
        var fields = CreateFormFields(csrf, "item-id-fallback");
        fields["itemId"] = "5057";
        fields["itemName"] = string.Empty;

        var response = await client.PostAsync(
            "/api/marketmafioso/acquisition/requests",
            new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var pending = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            "/api/marketmafioso/acquisition/requests/pending?characterName=Wei%20Ning&world=Gilgamesh",
            "client-secret");
        pending.EnsureSuccessStatusCode();
        using var pendingJson = JsonDocument.Parse(await pending.Content.ReadAsStringAsync());
        var request = Assert.Single(pendingJson.RootElement.GetProperty("requests").EnumerateArray());
        Assert.Equal("Item 5057", request.GetProperty("itemName").GetString());
    }

    [Fact]
    public async Task AcquisitionDashboardAllowsBlankGilCap()
    {
        await using var application = CreateHostedApplication(
            extraConfiguration: new KeyValuePair<string, string?>("MarketMafioso:TrustExternalDashboardAuth", "true"));
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var acquisitionPage = await client.GetStringAsync("/api/marketmafioso/acquisition");
        var csrf = Regex.Match(acquisitionPage, "name=\"csrf\" value=\"(?<token>[^\"]+)\"").Groups["token"].Value;
        var fields = CreateFormFields(csrf, "blank-gil-cap");
        fields["quantityMode"] = "AllBelowThreshold";
        fields["maxTotalGil"] = string.Empty;

        var response = await client.PostAsync(
            "/api/marketmafioso/acquisition/requests",
            new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var pending = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            "/api/marketmafioso/acquisition/requests/pending?characterName=Wei%20Ning&world=Gilgamesh",
            "client-secret");
        pending.EnsureSuccessStatusCode();
        using var pendingJson = JsonDocument.Parse(await pending.Content.ReadAsStringAsync());
        var request = Assert.Single(pendingJson.RootElement.GetProperty("requests").EnumerateArray());
        Assert.Equal(0u, request.GetProperty("maxTotalGil").GetUInt32());
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
            ["MarketMafioso:ClientApiKey"] = "client-secret",
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
            "client-secret",
            CreateRequest(idempotencyKey));
        created.EnsureSuccessStatusCode();
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var requestId = createdJson.RootElement.GetProperty("id").GetString()!;

        var claim = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/api/marketmafioso/acquisition/requests/{requestId}/claim",
            "client-secret",
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

