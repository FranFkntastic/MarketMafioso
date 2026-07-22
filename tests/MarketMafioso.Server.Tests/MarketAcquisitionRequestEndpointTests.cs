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
    public async Task NewHostCanClaimBatchCreatedByPreviousHost()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "MarketMafioso.Server.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        string requestId;

        await using (var application = await MarketAcquisitionTestApp.CreateAsync(contentRoot))
        {
            using var client = application.CreateAuthenticatedClient();
            var created = await MarketAcquisitionTestApp.SendWithKeyAsync(
                client,
                HttpMethod.Post,
                "/marketmafioso/api/acquisition/batches",
                MarketAcquisitionTestApp.CreateBatchRequest("restart-claim"));
            created.EnsureSuccessStatusCode();
            var batch = await created.Content.ReadFromJsonAsync<MarketAcquisitionRequestView>();
            requestId = batch?.Id ?? throw new InvalidOperationException("Batch creation returned no request ID.");
        }

        await using (var restarted = await MarketAcquisitionTestApp.CreateAsync(contentRoot))
        {
            using var client = restarted.CreateAuthenticatedClient();
            var claim = await MarketAcquisitionTestApp.SendWithKeyAsync(
                client,
                HttpMethod.Post,
                $"/marketmafioso/api/acquisition/requests/{requestId}/claim",
                new MarketAcquisitionClaimRequest
                {
                    CharacterName = MarketAcquisitionTestApp.CharacterName,
                    World = MarketAcquisitionTestApp.WorldName,
                    PluginInstanceId = "restarted-plugin",
                });

            claim.EnsureSuccessStatusCode();
            var claimed = await claim.Content.ReadFromJsonAsync<MarketAcquisitionClaimView>();
            Assert.Equal(requestId, claimed?.Id);
            Assert.Equal(MarketAcquisitionStatuses.Claimed, claimed?.Status);
            Assert.False(string.IsNullOrWhiteSpace(claimed?.ClaimToken));
        }
    }

    [Fact]
    public async Task HostedMode_CreatesListsClaimsAndAcceptsAcquisitionRequestWithClientKey()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();

        var created = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/marketmafioso/api/acquisition/requests",
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
            "/marketmafioso/api/acquisition/requests/pending?characterName=Wei%20Ning&world=Gilgamesh",
            "client-secret");

        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        using var pendingJson = JsonDocument.Parse(await pending.Content.ReadAsStringAsync());
        var requests = pendingJson.RootElement.GetProperty("requests");
        Assert.Single(requests.EnumerateArray());
        Assert.Equal(requestId, requests[0].GetProperty("id").GetString());

        var claim = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/requests/{requestId}/claim",
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
            "/marketmafioso/api/acquisition/requests/pending?characterName=Wei%20Ning&world=Gilgamesh",
            "client-secret");

        Assert.Equal(HttpStatusCode.OK, pendingAfterClaim.StatusCode);
        using var pendingAfterClaimJson = JsonDocument.Parse(await pendingAfterClaim.Content.ReadAsStringAsync());
        Assert.Empty(pendingAfterClaimJson.RootElement.GetProperty("requests").EnumerateArray());

        var accept = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/requests/{requestId}/accept",
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
    public async Task HostedMode_CreatesListsAndClaimsAcquisitionBatchWithLines()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();

        var created = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/marketmafioso/api/acquisition/batches",
            "client-secret",
            CreateBatchRequest("batch-1", "Selected", ["Faerie"]));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var requestId = createdJson.RootElement.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(requestId));
        var createdLines = createdJson.RootElement.GetProperty("lines");
        Assert.Equal(2, createdLines.GetArrayLength());
        Assert.Equal(2u, createdLines[0].GetProperty("itemId").GetUInt32());
        Assert.Equal(4u, createdLines[1].GetProperty("itemId").GetUInt32());
        Assert.Equal("AllBelowThreshold", createdLines[1].GetProperty("quantityMode").GetString());
        Assert.Equal(999u, createdLines[1].GetProperty("maxQuantity").GetUInt32());
        Assert.Equal("Selected", createdJson.RootElement.GetProperty("worldMode").GetString());
        Assert.Equal("Faerie", createdJson.RootElement.GetProperty("selectedWorlds")[0].GetString());

        var pending = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            "/marketmafioso/api/acquisition/batches/pending?characterName=Wei%20Ning&world=Gilgamesh",
            "client-secret");

        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);
        using var pendingJson = JsonDocument.Parse(await pending.Content.ReadAsStringAsync());
        var batches = pendingJson.RootElement.GetProperty("batches");
        Assert.Single(batches.EnumerateArray());
        Assert.Equal(requestId, batches[0].GetProperty("id").GetString());
        Assert.Equal(2, batches[0].GetProperty("lines").GetArrayLength());

        var claim = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/requests/{requestId}/claim",
            "client-secret",
            new
            {
                characterName = "Wei Ning",
                world = "Gilgamesh",
                pluginInstanceId = "plugin-test-instance",
            });

        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        using var claimJson = JsonDocument.Parse(await claim.Content.ReadAsStringAsync());
        Assert.Equal(2, claimJson.RootElement.GetProperty("lines").GetArrayLength());
        Assert.False(string.IsNullOrWhiteSpace(claimJson.RootElement.GetProperty("claimToken").GetString()));
    }

    [Fact]
    public async Task HostedMode_AppendsPendingBatchLines()
    {
        await using var application = CreateHostedApplication(
            extraConfiguration: new KeyValuePair<string, string?>("MarketMafioso:AcquisitionMaximumExpirySeconds", "86400"));
        using var client = application.CreateClient();

        var created = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/marketmafioso/api/acquisition/batches",
            "client-secret",
            CreateBatchRequest("append-endpoint-batch"));
        created.EnsureSuccessStatusCode();
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var requestId = createdJson.RootElement.GetProperty("id").GetString();
        var revision = createdJson.RootElement.GetProperty("revision").GetInt32();

        var appended = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/batches/{requestId}/lines",
            "client-secret",
            new
            {
                expectedRevision = revision,
                expiresInSeconds = 3600,
                lines = new object[]
                {
                    new
                    {
                        itemId = 6,
                        itemName = "Ice Shard",
                        itemKind = "Crystal",
                        quantityMode = "AllBelowThreshold",
                        targetQuantity = 0,
                        maxQuantity = 250,
                        hqPolicy = "Either",
                        maxUnitPrice = 40,
                        gilCap = 0,
                    },
                },
            });

        appended.EnsureSuccessStatusCode();
        using var appendedJson = JsonDocument.Parse(await appended.Content.ReadAsStringAsync());
        Assert.Equal(revision + 1, appendedJson.RootElement.GetProperty("revision").GetInt32());
        Assert.Equal(3, appendedJson.RootElement.GetProperty("lines").GetArrayLength());
        Assert.Contains(
            appendedJson.RootElement.GetProperty("lines").EnumerateArray(),
            line => line.GetProperty("itemId").GetUInt32() == 6);
    }

    [Fact]
    public async Task LineProgressRejectsUnknownLineId()
    {
        using var app = await MarketAcquisitionTestApp.CreateAsync();
        var client = app.CreateAuthenticatedClient();
        var claimed = await app.CreateClaimedBatchAsync(client, "line-progress-unknown");

        var response = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/batches/{claimed.Id}/lines/not-a-line/progress",
            "client-secret",
            new
            {
                claimToken = claimed.ClaimToken,
                idempotencyKey = "line-progress-unknown-key",
                attemptId = "attempt-1",
                sequence = 1,
                status = "Running",
                message = "Testing wrong line."
            });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task LineProgressRequiresApiKey()
    {
        using var app = await MarketAcquisitionTestApp.CreateAsync();
        var client = app.CreateAuthenticatedClient();
        var claimed = await app.CreateClaimedBatchAsync(client, "line-progress-api-key");
        var line = Assert.Single(claimed.Lines);

        var response = await client.PostAsJsonAsync(
            $"/marketmafioso/api/acquisition/batches/{claimed.Id}/lines/{line.LineId}/progress",
            new
            {
                claimToken = claimed.ClaimToken,
                idempotencyKey = "line-progress-api-key-missing",
                attemptId = "attempt-1",
                sequence = 1,
                status = "Running",
                message = "Missing the plugin API key.",
            });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task OldApiNamespaceDoesNotExposeLineProgress()
    {
        using var app = await MarketAcquisitionTestApp.CreateAsync();
        var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync(
            "/api/marketmafioso/acquisition/batches/request-1/lines/line-1/progress",
            new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task HostedMode_RejectsAcquisitionBatchWithoutLines()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();

        var created = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/marketmafioso/api/acquisition/batches",
            "client-secret",
            new
            {
                schemaVersion = 1,
                idempotencyKey = "empty-batch",
                targetCharacterName = "Wei Ning",
                targetWorld = "Gilgamesh",
                region = "North America",
                worldMode = "Recommended",
                expiresInSeconds = 90,
                lines = Array.Empty<object>(),
            });

        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
    }

    [Fact]
    public async Task HostedMode_SingleRequestExposesCompatibilityLine()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();

        var created = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/marketmafioso/api/acquisition/requests",
            "client-secret",
            CreateRequest("single-line-view"));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var lines = createdJson.RootElement.GetProperty("lines");
        Assert.Single(lines.EnumerateArray());
        Assert.Equal(createdJson.RootElement.GetProperty("id").GetString(), lines[0].GetProperty("batchId").GetString());
        Assert.Equal(createdJson.RootElement.GetProperty("itemId").GetUInt32(), lines[0].GetProperty("itemId").GetUInt32());
    }

    [Fact]
    public async Task HostedMode_AcquisitionRoutesUseSameClientKeyAsInventory()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();

        var createWithClientKey = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/marketmafioso/api/acquisition/requests",
            "client-secret",
            CreateRequest("client-create-key"));
        createWithClientKey.EnsureSuccessStatusCode();

        var pendingWithClientKey = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            "/marketmafioso/api/acquisition/requests/pending?characterName=Wei%20Ning&world=Gilgamesh",
            "client-secret");
        Assert.Equal(HttpStatusCode.OK, pendingWithClientKey.StatusCode);

        var pendingWithWrongKey = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            "/marketmafioso/api/acquisition/requests/pending?characterName=Wei%20Ning&world=Gilgamesh",
            "wrong-secret");
        Assert.Equal(HttpStatusCode.Unauthorized, pendingWithWrongKey.StatusCode);
    }

    [Fact]
    public async Task HostedMode_CanStartLockedWithoutBootstrapClientApiKey()
    {
        await using var application = CreateHostedApplication(
            extraConfiguration: [
                new KeyValuePair<string, string?>("MarketMafioso:ClientApiKey", string.Empty),
                new KeyValuePair<string, string?>("MarketMafioso:ApiKey", string.Empty),
                new KeyValuePair<string, string?>("MarketMafioso:IngestApiKey", string.Empty),
            ]);

        using var client = application.CreateClient();
        var response = await client.GetAsync("/marketmafioso/api/capabilities");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ClaimRequiresMatchingScopeAndClaimToken()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();

        var created = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/marketmafioso/api/acquisition/requests",
            "client-secret",
            CreateRequest("claim-scope"));
        created.EnsureSuccessStatusCode();
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var requestId = createdJson.RootElement.GetProperty("id").GetString();

        var wrongScopeClaim = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/requests/{requestId}/claim",
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
            $"/marketmafioso/api/acquisition/requests/{requestId}/claim",
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
            $"/marketmafioso/api/acquisition/requests/{requestId}/accept",
            "client-secret",
            new
            {
                claimToken = "not-the-claim-token",
                idempotencyKey = "stale-accept",
            });

        Assert.Equal(HttpStatusCode.Unauthorized, staleAccept.StatusCode);
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
    public async Task HostedMode_RetiredApiMarketMafiosoRoutesDoNotServeDashboardOrPluginApi()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();

        var dashboard = await client.GetAsync("/api/marketmafioso/");
        var pending = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            "/api/marketmafioso/acquisition/requests/pending?characterName=Wei%20Ning&world=Gilgamesh",
            "client-secret");
        var create = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/api/marketmafioso/acquisition/requests",
            "client-secret",
            CreateRequest("retired-route-create"));

        Assert.Equal(HttpStatusCode.NotFound, dashboard.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, pending.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
    }

    [Fact]
    public async Task ClaimedRequestCanBeRejected()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();
        var (requestId, claimToken) = await CreateAndClaimAsync(client, "reject-request");

        var reject = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/requests/{requestId}/reject",
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

    }

    [Fact]
    public async Task RecentQueueIncludesLatestAttemptProjection()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();
        var claimed = await CreateAcceptedRequestAsync(client, "attempt-projection");

        var progress = await SendAttemptProgressAsync(
            client,
            claimed.RequestId,
            claimed.ClaimToken,
            "attempt-projection-progress-1",
            "attempt-001",
            1,
            "Traveling",
            "Traveling to Brynhildr.",
            worldName: "Brynhildr",
            routeStopId: "stop-brynhildr");
        progress.EnsureSuccessStatusCode();

        var recentResponse = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            "/marketmafioso/api/acquisition/requests",
            "client-secret");
        recentResponse.EnsureSuccessStatusCode();
        var recent = await recentResponse.Content.ReadFromJsonAsync<JsonElement>();

        var payload = recent[0];
        Assert.Equal("attempt-001", payload.GetProperty("latestAttemptId").GetString());
        Assert.Equal(1, payload.GetProperty("latestAttemptSequence").GetInt64());
        Assert.Equal("Traveling", payload.GetProperty("latestAttemptPhase").GetString());
        Assert.Equal("Brynhildr", payload.GetProperty("latestAttemptWorld").GetString());
    }

    [Fact]
    public async Task AcquisitionRequestTimelineReturnsLifecycleAndAttemptEvents()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();
        var claimed = await CreateAcceptedRequestAsync(client, "attempt-timeline");

        var firstProgress = await SendAttemptProgressAsync(
            client,
            claimed.RequestId,
            claimed.ClaimToken,
            "attempt-timeline-progress-1",
            "attempt-001",
            1,
            "Traveling",
            "Traveling to Brynhildr.",
            worldName: "Brynhildr",
            routeStopId: "stop-brynhildr");
        var secondProgress = await SendAttemptProgressAsync(
            client,
            claimed.RequestId,
            claimed.ClaimToken,
            "attempt-timeline-progress-2",
            "attempt-001",
            2,
            "Buying",
            "Bought safe listing.",
            worldName: "Brynhildr",
            routeStopId: "stop-brynhildr");
        firstProgress.EnsureSuccessStatusCode();
        secondProgress.EnsureSuccessStatusCode();

        var timelineResponse = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/timeline",
            "client-secret");

        timelineResponse.EnsureSuccessStatusCode();
        using var timelineJson = JsonDocument.Parse(await timelineResponse.Content.ReadAsStringAsync());
        var root = timelineJson.RootElement;
        Assert.Equal(claimed.RequestId, root.GetProperty("request").GetProperty("id").GetString());

        var lifecycleEvents = root.GetProperty("lifecycleEvents").EnumerateArray().ToArray();
        Assert.Contains(lifecycleEvents, entry => entry.GetProperty("eventType").GetString() == "accept");

        var attemptEvents = root.GetProperty("attemptEvents").EnumerateArray().ToArray();
        Assert.Equal(2, attemptEvents.Length);
        Assert.Equal("attempt-001", attemptEvents[0].GetProperty("attemptId").GetString());
        Assert.Equal(1, attemptEvents[0].GetProperty("sequence").GetInt64());
        Assert.Equal("Traveling", attemptEvents[0].GetProperty("phase").GetString());
        Assert.Equal("Brynhildr", attemptEvents[0].GetProperty("worldName").GetString());
        Assert.Equal("Traveling to Brynhildr.", attemptEvents[0].GetProperty("message").GetString());
        Assert.Equal("Buying", attemptEvents[1].GetProperty("phase").GetString());
        Assert.Equal("Bought safe listing.", attemptEvents[1].GetProperty("message").GetString());
    }

    [Fact]
    public async Task LateOldAttemptProgressAfterNewAttemptIsClassifiedStale()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();
        var claimed = await CreateAcceptedRequestAsync(client, "attempt-stale");

        var first = await SendAttemptProgressAsync(
            client,
            claimed.RequestId,
            claimed.ClaimToken,
            "attempt-a-1",
            "attempt-a",
            1,
            "Traveling",
            "Attempt A.");
        first.EnsureSuccessStatusCode();

        var second = await SendAttemptProgressAsync(
            client,
            claimed.RequestId,
            claimed.ClaimToken,
            "attempt-b-1",
            "attempt-b",
            1,
            "Traveling",
            "Attempt B.");
        second.EnsureSuccessStatusCode();

        var stale = await SendAttemptProgressAsync(
            client,
            claimed.RequestId,
            claimed.ClaimToken,
            "attempt-a-2",
            "attempt-a",
            2,
            "SearchingItem",
            "Old attempt woke up.");

        stale.EnsureSuccessStatusCode();
        using var staleJson = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
        Assert.Equal("stale_attempt", staleJson.RootElement.GetProperty("result").GetString());
    }

    [Fact]
    public async Task LegacyProgressWithoutAttemptIdStillWorksDuringMigration()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();
        var claimed = await CreateAcceptedRequestAsync(client, "legacy-progress");

        var progress = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/progress",
            "client-secret",
            new
            {
                claimToken = claimed.ClaimToken,
                idempotencyKey = "legacy-progress-1",
                runnerState = "Running",
                message = "Legacy plugin progress.",
            });

        progress.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await progress.Content.ReadAsStringAsync());
        Assert.Equal("Running", json.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task WorkOrderEndpointsShelfRestoreAndCloneDurableIntent()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();
        var created = await SendWithKeyAsync(client, HttpMethod.Post, "/marketmafioso/api/acquisition/requests", "client-secret", CreateRequest("work-order-commands"));
        created.EnsureSuccessStatusCode();
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = createdJson.RootElement.GetProperty("id").GetString()!;

        var shelf = await SendWithKeyAsync(client, HttpMethod.Post, $"/marketmafioso/api/acquisition/work-orders/{id}/shelf", "client-secret", new { expectedRevision = 1 });
        shelf.EnsureSuccessStatusCode();
        using var shelfJson = JsonDocument.Parse(await shelf.Content.ReadAsStringAsync());
        Assert.Equal("Shelved", shelfJson.RootElement.GetProperty("state").GetString());

        var restore = await SendWithKeyAsync(client, HttpMethod.Post, $"/marketmafioso/api/acquisition/work-orders/{id}/restore", "client-secret", new { expectedRevision = 2 });
        restore.EnsureSuccessStatusCode();

        var clone = await SendWithKeyAsync(client, HttpMethod.Post, $"/marketmafioso/api/acquisition/work-orders/{id}/clone", "client-secret", new
        {
            expectedRevision = 3,
            idempotencyKey = "work-order-clone-command",
            title = "Endpoint clone",
        });
        clone.EnsureSuccessStatusCode();
        using var cloneJson = JsonDocument.Parse(await clone.Content.ReadAsStringAsync());
        Assert.Equal(id, cloneJson.RootElement.GetProperty("parentWorkOrderId").GetString());
        Assert.Equal("Endpoint clone", cloneJson.RootElement.GetProperty("title").GetString());

        var list = await SendWithKeyAsync(client, HttpMethod.Get, "/marketmafioso/api/acquisition/work-orders?characterName=Wei%20Ning&world=Gilgamesh", "client-secret");
        list.EnsureSuccessStatusCode();
        using var listJson = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.Equal(2, listJson.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task ConcurrentClaimsAllowOnlyOneWinner()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();
        var created = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/marketmafioso/api/acquisition/requests",
            "client-secret",
            CreateRequest("concurrent-claim"));
        created.EnsureSuccessStatusCode();
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var requestId = createdJson.RootElement.GetProperty("id").GetString();

        var claimA = SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/requests/{requestId}/claim",
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
            $"/marketmafioso/api/acquisition/requests/{requestId}/claim",
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
    public async Task DashboardRendersAcquisitionFormAndCreatesRequestWithCsrf()
    {
        await using var application = CreateHostedApplication(
            extraConfiguration: new KeyValuePair<string, string?>("MarketMafioso:TrustExternalDashboardAuth", "true"));
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var dashboard = await client.GetStringAsync("/marketmafioso/");
        AssertDashboardShell(dashboard);

        var acquisitionPage = await client.GetStringAsync("/marketmafioso/acquisition");
        AssertDashboardShell(acquisitionPage);
        Assert.DoesNotContain("name=\"csrf\"", acquisitionPage, StringComparison.Ordinal);
        Assert.DoesNotContain("mmf_csrf", acquisitionPage, StringComparison.Ordinal);

        var inventory = await client.GetStringAsync("/marketmafioso/inventory");
        AssertDashboardShell(inventory);

        var created = await client.PostAsync(
            "/marketmafioso/acquisition/requests",
            new FormUrlEncodedContent(CreateFormFields("dashboard-create")));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.StartsWith(
            "/marketmafioso/acquisition?acquisition=",
            created.Headers.Location?.OriginalString,
            StringComparison.Ordinal);

        var pending = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            "/marketmafioso/api/acquisition/requests/pending?characterName=Wei%20Ning&world=Gilgamesh",
            "client-secret");
        pending.EnsureSuccessStatusCode();
        using var pendingJson = JsonDocument.Parse(await pending.Content.ReadAsStringAsync());
        Assert.Single(pendingJson.RootElement.GetProperty("requests").EnumerateArray());
    }

    [Fact]
    public async Task AcquisitionDashboardAjaxCreateReturnsJsonInsteadOfRedirect()
    {
        await using var application = CreateHostedApplication(
            extraConfiguration: new KeyValuePair<string, string?>("MarketMafioso:TrustExternalDashboardAuth", "true"));
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/marketmafioso/api/acquisition/requests")
        {
            Content = new FormUrlEncodedContent(CreateFormFields("acquisition-ajax-create")),
        };
        createRequest.Headers.Accept.ParseAdd("application/json");

        var created = await client.SendAsync(createRequest);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("application/json", created.Content.Headers.ContentType?.MediaType);
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Assert.Equal("Fire Shard", createdJson.RootElement.GetProperty("itemName").GetString());
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

        var cancelled = await client.PostAsync(
            $"/marketmafioso/acquisition/requests/{claimed.RequestId}/cancel",
            new FormUrlEncodedContent([]));

        Assert.Equal(HttpStatusCode.Redirect, cancelled.StatusCode);
        var pending = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            "/marketmafioso/api/acquisition/requests/pending?characterName=Wei%20Ning&world=Gilgamesh",
            "client-secret");
        pending.EnsureSuccessStatusCode();
        using var pendingJson = JsonDocument.Parse(await pending.Content.ReadAsStringAsync());
        Assert.Empty(pendingJson.RootElement.GetProperty("requests").EnumerateArray());
    }

    [Fact]
    public async Task AcquisitionDashboardRequestListIncludesTerminalRequestsOnlyWhenRequested()
    {
        await using var application = CreateHostedApplication(
            extraConfiguration: new KeyValuePair<string, string?>("MarketMafioso:TrustExternalDashboardAuth", "true"));
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var claimed = await CreateAndClaimAsync(client, "dashboard-terminal-filter");

        var accepted = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/accept",
            "client-secret",
            new
            {
                claimToken = claimed.ClaimToken,
                idempotencyKey = "dashboard-terminal-filter-accept",
            });
        accepted.EnsureSuccessStatusCode();
        var complete = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/complete",
            "client-secret",
            new
            {
                claimToken = claimed.ClaimToken,
                idempotencyKey = "dashboard-terminal-filter-complete",
                message = "Route complete.",
            });
        complete.EnsureSuccessStatusCode();

        var active = await client.GetFromJsonAsync<JsonElement[]>("/marketmafioso/api/acquisition/requests");
        var archived = await client.GetFromJsonAsync<JsonElement[]>("/marketmafioso/api/acquisition/requests?includeTerminal=true");

        Assert.Empty(active!);
        var request = Assert.Single(archived!);
        Assert.Equal(claimed.RequestId, request.GetProperty("id").GetString());
        Assert.Equal("Complete", request.GetProperty("status").GetString());
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

        var resent = await client.PostAsync(
            $"/marketmafioso/acquisition/requests/{claimed.RequestId}/resend",
            new FormUrlEncodedContent([]));

        Assert.Equal(HttpStatusCode.Redirect, resent.StatusCode);

        var pending = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            "/marketmafioso/api/acquisition/requests/pending?characterName=Wei%20Ning&world=Gilgamesh",
            "client-secret");
        pending.EnsureSuccessStatusCode();
        using var pendingJson = JsonDocument.Parse(await pending.Content.ReadAsStringAsync());
        var request = Assert.Single(pendingJson.RootElement.GetProperty("requests").EnumerateArray());
        Assert.Equal(claimed.RequestId, request.GetProperty("id").GetString());
        Assert.Equal("PendingPickup", request.GetProperty("status").GetString());
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
        var fields = CreateFormFields("unresolved-item");
        fields["itemId"] = string.Empty;
        fields["itemName"] = "Darksteel Nugget";

        var response = await client.PostAsync(
            "/marketmafioso/acquisition/requests",
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
        var fields = CreateFormFields("item-id-fallback");
        fields["itemId"] = "5057";
        fields["itemName"] = string.Empty;

        var response = await client.PostAsync(
            "/marketmafioso/acquisition/requests",
            new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var pending = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            "/marketmafioso/api/acquisition/requests/pending?characterName=Wei%20Ning&world=Gilgamesh",
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
        var fields = CreateFormFields("blank-gil-cap");
        fields["quantityMode"] = "AllBelowThreshold";
        fields["maxTotalGil"] = string.Empty;

        var response = await client.PostAsync(
            "/marketmafioso/acquisition/requests",
            new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var pending = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            "/marketmafioso/api/acquisition/requests/pending?characterName=Wei%20Ning&world=Gilgamesh",
            "client-secret");
        pending.EnsureSuccessStatusCode();
        using var pendingJson = JsonDocument.Parse(await pending.Content.ReadAsStringAsync());
        var request = Assert.Single(pendingJson.RootElement.GetProperty("requests").EnumerateArray());
        Assert.Equal(0u, request.GetProperty("maxTotalGil").GetUInt32());
    }

    [Theory]
    [InlineData("Exact")]
    [InlineData("UpTo")]
    public async Task AcquisitionRequestRejectsRemovedQuantityModes(string quantityMode)
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();

        var response = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/marketmafioso/api/acquisition/requests",
            "client-secret",
            CreateRequest($"removed-mode-{quantityMode}", quantityMode: quantityMode));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AcquisitionDashboardAllowsBlankAllBelowThresholdQuantity()
    {
        await using var application = CreateHostedApplication(
            extraConfiguration: new KeyValuePair<string, string?>("MarketMafioso:TrustExternalDashboardAuth", "true"));
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var fields = CreateFormFields("blank-all-below-quantity");
        fields["quantityMode"] = "AllBelowThreshold";
        fields["quantity"] = string.Empty;

        var response = await client.PostAsync(
            "/marketmafioso/acquisition/requests",
            new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var pending = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            "/marketmafioso/api/acquisition/requests/pending?characterName=Wei%20Ning&world=Gilgamesh",
            "client-secret");
        pending.EnsureSuccessStatusCode();
        using var pendingJson = JsonDocument.Parse(await pending.Content.ReadAsStringAsync());
        var request = Assert.Single(pendingJson.RootElement.GetProperty("requests").EnumerateArray());
        Assert.Equal(0u, request.GetProperty("quantity").GetUInt32());
    }

    [Fact]
    public async Task AcquisitionDashboardLegacyRecentQueueEndpointIsRetired()
    {
        await using var application = CreateHostedApplication(
            extraConfiguration: new KeyValuePair<string, string?>("MarketMafioso:TrustExternalDashboardAuth", "true"));
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/marketmafioso/api/acquisition/requests/recent");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
            ["MarketMafioso:BasePath"] = "/marketmafioso",
            ["MarketMafioso:EnableMarketAcquisition"] = "true",
            ["MarketMafioso:DatabasePath"] = Path.Combine(contentRoot, "marketmafioso.db"),
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
        int expiresInSeconds = 90,
        string quantityMode = "TargetQuantity",
        uint quantity = 10) => new
        {
            schemaVersion = 1,
            idempotencyKey,
            targetCharacterName = "Wei Ning",
            targetWorld = "Gilgamesh",
            region = "North America",
            itemId,
            itemName,
            quantityMode,
            quantity,
            hqPolicy = "Either",
            maxUnitPrice = 99,
            maxTotalGil = 990,
            worldMode = "Recommended",
            expiresInSeconds,
        };

    private static object CreateBatchRequest(
        string idempotencyKey,
        string worldMode = "Recommended",
        IReadOnlyList<string>? selectedWorlds = null) => new
        {
            schemaVersion = 1,
            idempotencyKey,
            targetCharacterName = "Wei Ning",
            targetWorld = "Gilgamesh",
            region = "North America",
            worldMode,
            selectedWorlds = selectedWorlds ?? [],
            expiresInSeconds = 90,
            lines = new object[]
        {
            new
            {
                itemId = 2,
                itemName = "Fire Shard",
                itemKind = "Crystal",
                quantityMode = "TargetQuantity",
                targetQuantity = 10,
                maxQuantity = 0,
                hqPolicy = "Either",
                maxUnitPrice = 99,
                gilCap = 990,
            },
            new
            {
                itemId = 4,
                itemName = "Lightning Shard",
                itemKind = "Crystal",
                quantityMode = "AllBelowThreshold",
                targetQuantity = 0,
                maxQuantity = 999,
                hqPolicy = "Either",
                maxUnitPrice = 120,
                gilCap = 0,
            },
        },
        };

    private static Dictionary<string, string> CreateFormFields(string idempotencyKey) => new()
    {
        ["schemaVersion"] = "1",
        ["idempotencyKey"] = idempotencyKey,
        ["targetCharacterName"] = "Wei Ning",
        ["targetWorld"] = "Gilgamesh",
        ["region"] = "North America",
        ["itemId"] = "2",
        ["itemName"] = "Fire Shard",
        ["quantityMode"] = "TargetQuantity",
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
            "/marketmafioso/api/acquisition/requests",
            "client-secret",
            CreateRequest(idempotencyKey));
        created.EnsureSuccessStatusCode();
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var requestId = createdJson.RootElement.GetProperty("id").GetString()!;

        var claim = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/requests/{requestId}/claim",
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

    private static void AssertDashboardShell(string html)
    {
        Assert.Contains("MarketMafioso", html, StringComparison.Ordinal);
        Assert.Contains("_framework/blazor", html, StringComparison.Ordinal);
        Assert.DoesNotContain("[.{fingerprint}]", html, StringComparison.Ordinal);
    }

    private static async Task<(string RequestId, string ClaimToken)> CreateAcceptedRequestAsync(
        HttpClient client,
        string idempotencyKey)
    {
        var claimed = await CreateAndClaimAsync(client, idempotencyKey);
        var accept = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/accept",
            "client-secret",
            new
            {
                claimToken = claimed.ClaimToken,
                idempotencyKey = $"{idempotencyKey}-accept",
            });
        accept.EnsureSuccessStatusCode();
        return claimed;
    }

    private static Task<HttpResponseMessage> SendAttemptProgressAsync(
        HttpClient client,
        string requestId,
        string claimToken,
        string idempotencyKey,
        string attemptId,
        long eventSequence,
        string phase,
        string message,
        string? worldName = null,
        string? routeStopId = null) =>
        SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/requests/{requestId}/progress",
            "client-secret",
            CreateAttemptProgress(
                claimToken,
                idempotencyKey,
                attemptId,
                eventSequence,
                phase,
                message,
                worldName,
                routeStopId));

    private static object CreateAttemptProgress(
        string claimToken,
        string idempotencyKey,
        string attemptId,
        long eventSequence,
        string phase,
        string message,
        string? worldName = null,
        string? routeStopId = null) => new
        {
            claimToken,
            idempotencyKey,
            pluginInstanceId = "plugin-test-instance",
            attemptId,
            eventSequence,
            eventType = "progress",
            phase,
            routeStopId,
            runnerState = "Running",
            message,
            worldName,
            pluginVersion = "1.0.159.53063",
            clientTimestampUtc = DateTimeOffset.UtcNow,
        };

    private static Task<HttpResponseMessage> SendWithKeyAsync(
        HttpClient client,
        HttpMethod method,
        string requestUri,
        string apiKey,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Add("X-Api-Key", apiKey);
        request.Headers.Accept.ParseAdd("application/json");
        if (body != null)
            request.Content = JsonContent.Create(body);

        return client.SendAsync(request);
    }
}

