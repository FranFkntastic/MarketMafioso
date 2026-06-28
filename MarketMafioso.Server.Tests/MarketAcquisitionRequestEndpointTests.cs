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
                "/marketmafioso/api/acquisition/requests",
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
                "/marketmafioso/api/acquisition/requests/pending?characterName=Wei%20Ning&world=Gilgamesh",
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
            "/marketmafioso/api/acquisition/requests",
            "client-secret",
            CreateRequest("same-idempotency-key"));
        first.EnsureSuccessStatusCode();
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var firstId = firstJson.RootElement.GetProperty("id").GetString();

        var replay = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/marketmafioso/api/acquisition/requests",
            "client-secret",
            CreateRequest("same-idempotency-key"));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(firstId, replayJson.RootElement.GetProperty("id").GetString());

        var conflict = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/marketmafioso/api/acquisition/requests",
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
    public async Task ClaimedRequestCanBeRejectedAndDuplicateRejectIsIdempotent()
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

        var replay = await SendWithKeyAsync(
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

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal("Rejected", replayJson.RootElement.GetProperty("status").GetString());

        var conflict = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/requests/{requestId}/reject",
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
            $"/marketmafioso/api/acquisition/requests/{requestId}/accept",
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
            $"/marketmafioso/api/acquisition/requests/{requestId}/progress",
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
            $"/marketmafioso/api/acquisition/requests/{requestId}/complete",
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
            $"/marketmafioso/api/acquisition/requests/{requestId}/fail",
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
    public async Task ProgressAttemptEventIsIdempotentForSameBody()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();
        var claimed = await CreateAcceptedRequestAsync(client, "attempt-idempotent-same");
        var body = CreateAttemptProgress(
            claimed.ClaimToken,
            "req-attempt-001-1-progress",
            "attempt-001",
            1,
            "Traveling",
            "Traveling.",
            worldName: "Brynhildr",
            routeStopId: "stop-brynhildr");

        var first = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/progress",
            "client-secret",
            body);
        var second = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/progress",
            "client-secret",
            body);

        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();
        using var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal("replayed", secondJson.RootElement.GetProperty("result").GetString());
    }

    [Fact]
    public async Task ProgressAttemptEventRejectsSameIdempotencyKeyWithDifferentBody()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();
        var claimed = await CreateAcceptedRequestAsync(client, "attempt-idempotent-different");

        var first = CreateAttemptProgress(
            claimed.ClaimToken,
            "req-attempt-001-1-progress",
            "attempt-001",
            1,
            "Traveling",
            "Traveling.");
        var second = CreateAttemptProgress(
            claimed.ClaimToken,
            "req-attempt-001-1-progress",
            "attempt-001",
            1,
            "Traveling",
            "Different payload.");

        var accepted = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/progress",
            "client-secret",
            first);
        accepted.EnsureSuccessStatusCode();
        var conflict = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/progress",
            "client-secret",
            second);

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var text = await conflict.Content.ReadAsStringAsync();
        Assert.Contains("Idempotency key was already used with a different request body", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProgressAttemptEventRejectsSameAttemptSequenceWithDifferentIdempotencyKey()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient();
        var claimed = await CreateAcceptedRequestAsync(client, "attempt-sequence-conflict");

        var first = CreateAttemptProgress(
            claimed.ClaimToken,
            "attempt-sequence-key-a",
            "attempt-001",
            1,
            "Traveling",
            "First sequence payload.");
        var second = CreateAttemptProgress(
            claimed.ClaimToken,
            "attempt-sequence-key-b",
            "attempt-001",
            1,
            "Traveling",
            "Second sequence payload.");

        var accepted = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/progress",
            "client-secret",
            first);
        accepted.EnsureSuccessStatusCode();
        var conflict = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/progress",
            "client-secret",
            second);

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var text = await conflict.Content.ReadAsStringAsync();
        Assert.Contains("Attempt event sequence was already used", text, StringComparison.Ordinal);
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
    public async Task ExpiredRequestCannotBeListedOrClaimed()
    {
        await using var application = CreateHostedApplication(
            extraConfiguration: new KeyValuePair<string, string?>("MarketMafioso:AcquisitionMinimumExpirySeconds", "1"));
        using var client = application.CreateClient();

        var created = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            "/marketmafioso/api/acquisition/requests",
            "client-secret",
            CreateRequest("expires-fast", expiresInSeconds: 1));
        created.EnsureSuccessStatusCode();
        using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var requestId = createdJson.RootElement.GetProperty("id").GetString();

        await Task.Delay(TimeSpan.FromSeconds(2));

        var pending = await SendWithKeyAsync(
            client,
            HttpMethod.Get,
            "/marketmafioso/api/acquisition/requests/pending?characterName=Wei%20Ning&world=Gilgamesh",
            "client-secret");
        pending.EnsureSuccessStatusCode();
        using var pendingJson = JsonDocument.Parse(await pending.Content.ReadAsStringAsync());
        Assert.Empty(pendingJson.RootElement.GetProperty("requests").EnumerateArray());

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
            $"/marketmafioso/api/acquisition/requests/{requestId}/accept",
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
            $"/marketmafioso/api/acquisition/requests/{firstRequestId}/reject",
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
            $"/marketmafioso/api/acquisition/requests/{secondRequestId}/reject",
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
            $"/marketmafioso/api/acquisition/requests/{requestId}/accept",
            "client-secret",
            body);
        accept.EnsureSuccessStatusCode();

        var replay = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/requests/{requestId}/accept",
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
    public async Task AcquisitionDashboardCreatesRequestAndRedirectsBackToAcquisitionSurface()
    {
        await using var application = CreateHostedApplication(
            extraConfiguration: new KeyValuePair<string, string?>("MarketMafioso:TrustExternalDashboardAuth", "true"));
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var created = await client.PostAsync(
            "/marketmafioso/acquisition/requests",
            new FormUrlEncodedContent(CreateFormFields("acquisition-page-create")));

        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        Assert.StartsWith(
            "/marketmafioso/acquisition?acquisition=",
            created.Headers.Location?.OriginalString,
            StringComparison.Ordinal);
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
            $"/marketmafioso/api/acquisition/requests/{accepted.RequestId}/accept",
            "client-secret",
            new
            {
                claimToken = accepted.ClaimToken,
                idempotencyKey = "accepted-dashboard-row-accept",
            });
        acceptedResponse.EnsureSuccessStatusCode();

        var acquisitionPage = await client.GetStringAsync("/marketmafioso/acquisition");
        AssertDashboardShell(acquisitionPage);
        Assert.DoesNotContain("name=\"csrf\"", acquisitionPage, StringComparison.Ordinal);
        Assert.DoesNotContain("mmf_csrf", acquisitionPage, StringComparison.Ordinal);

        var recent = await client.GetAsync("/marketmafioso/api/acquisition/requests/recent");
        recent.EnsureSuccessStatusCode();
        using var recentJson = JsonDocument.Parse(await recent.Content.ReadAsStringAsync());
        var root = recentJson.RootElement;
        Assert.Equal("1 active / 1 recent", root.GetProperty("activeSummary").GetString());
        Assert.Contains("Fire Shard", root.GetProperty("queueRows").GetString(), StringComparison.Ordinal);
        Assert.Contains("Accepted", root.GetProperty("queueRows").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("invalid_csrf", root.GetProperty("queueRows").GetString(), StringComparison.Ordinal);
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
            $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/accept",
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
            $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/progress",
            "client-secret",
            new
            {
                claimToken = claimed.ClaimToken,
                idempotencyKey = "dashboard-live-queue-progress",
                runnerState = "Running",
                message = "Arrived on Maduin; reading live listings.",
            });
        progress.EnsureSuccessStatusCode();

        var recent = await client.GetAsync("/marketmafioso/api/acquisition/requests/recent");

        recent.EnsureSuccessStatusCode();
        using var recentJson = JsonDocument.Parse(await recent.Content.ReadAsStringAsync());
        var root = recentJson.RootElement;
        Assert.Contains("Running", root.GetProperty("latestRequestStatus").GetString(), StringComparison.Ordinal);
        Assert.Contains("Arrived on Maduin", root.GetProperty("latestRequestEvent").GetString(), StringComparison.Ordinal);
        Assert.Contains("data-resize-col=\"status\"", root.GetProperty("queueRows").GetString(), StringComparison.Ordinal);
        Assert.Contains("Arrived on Maduin", root.GetProperty("queueRows").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcquisitionDashboardQueueRefreshDoesNotReturnCsrfToken()
    {
        await using var application = CreateHostedApplication(
            extraConfiguration: new KeyValuePair<string, string?>("MarketMafioso:TrustExternalDashboardAuth", "true"));
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var recent = await client.GetAsync("/marketmafioso/api/acquisition/requests/recent");

        recent.EnsureSuccessStatusCode();
        using var recentJson = JsonDocument.Parse(await recent.Content.ReadAsStringAsync());
        Assert.False(recentJson.RootElement.TryGetProperty("csrfToken", out _));

        var postWithoutCsrf = await client.PostAsync(
            "/marketmafioso/acquisition/requests",
            new FormUrlEncodedContent(CreateFormFields("without-csrf")));
        Assert.Equal(HttpStatusCode.Redirect, postWithoutCsrf.StatusCode);
    }

    [Fact]
    public async Task AcquisitionDashboardRecentQueueEndpointClearsCancelledRequests()
    {
        await using var application = CreateHostedApplication(
            extraConfiguration: new KeyValuePair<string, string?>("MarketMafioso:TrustExternalDashboardAuth", "true"));
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var claimed = await CreateAndClaimAsync(client, "dashboard-live-cancelled");

        var cancelled = await client.PostAsync(
            $"/marketmafioso/acquisition/requests/{claimed.RequestId}/cancel",
            new FormUrlEncodedContent([]));

        Assert.Equal(HttpStatusCode.Redirect, cancelled.StatusCode);
        var recent = await client.GetAsync("/marketmafioso/api/acquisition/requests/recent");

        recent.EnsureSuccessStatusCode();
        using var recentJson = JsonDocument.Parse(await recent.Content.ReadAsStringAsync());
        var root = recentJson.RootElement;
        Assert.Equal("0 active / 0 recent", root.GetProperty("activeSummary").GetString());
        Assert.Equal("Idle", root.GetProperty("latestRequestStatus").GetString());
        Assert.Contains("No acquisition requests yet.", root.GetProperty("queueRows").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Cancelled", root.GetProperty("queueRows").GetString(), StringComparison.Ordinal);
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
        var recent = await client.GetAsync("/marketmafioso/api/acquisition/requests/recent");
        recent.EnsureSuccessStatusCode();
        using var recentJson = JsonDocument.Parse(await recent.Content.ReadAsStringAsync());
        Assert.Contains("No acquisition requests yet.", recentJson.RootElement.GetProperty("queueRows").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Cancelled", recentJson.RootElement.GetProperty("queueRows").GetString(), StringComparison.Ordinal);

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
    public async Task AcquisitionDashboardCancelIsIdempotentForCancelledAndCompleteRequests()
    {
        await using var application = CreateHostedApplication(
            extraConfiguration: new KeyValuePair<string, string?>("MarketMafioso:TrustExternalDashboardAuth", "true"));
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var claimed = await CreateAndClaimAsync(client, "dashboard-cancel-terminal");

        var accepted = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/accept",
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
            $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/complete",
            "client-secret",
            new
            {
                claimToken = claimed.ClaimToken,
                idempotencyKey = "terminal-cancel-complete",
                message = "Route complete.",
            });
        complete.EnsureSuccessStatusCode();

        var cancelComplete = await client.PostAsync(
            $"/marketmafioso/acquisition/requests/{claimed.RequestId}/cancel",
            new FormUrlEncodedContent([]));
        var cancelCompleteAgain = await client.PostAsync(
            $"/marketmafioso/acquisition/requests/{claimed.RequestId}/cancel",
            new FormUrlEncodedContent([]));

        Assert.Equal(HttpStatusCode.Redirect, cancelComplete.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, cancelCompleteAgain.StatusCode);
        var recent = await client.GetAsync("/marketmafioso/api/acquisition/requests/recent");
        recent.EnsureSuccessStatusCode();
        using var recentJson = JsonDocument.Parse(await recent.Content.ReadAsStringAsync());
        Assert.Contains("Complete", recentJson.RootElement.GetProperty("queueRows").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Cancelled", recentJson.RootElement.GetProperty("queueRows").GetString(), StringComparison.Ordinal);
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
    public async Task AcquisitionClientCanResendFailedRequest()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var claimed = await CreateAndClaimAsync(client, "client-resend-failed");

        var accepted = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/accept",
            "client-secret",
            new
            {
                claimToken = claimed.ClaimToken,
                idempotencyKey = "accept-client-resend-failed",
            });
        accepted.EnsureSuccessStatusCode();

        var failed = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/fail",
            "client-secret",
            new
            {
                claimToken = claimed.ClaimToken,
                idempotencyKey = "fail-client-resend-failed",
                reason = "Synthetic route failure.",
            });
        failed.EnsureSuccessStatusCode();

        var resent = await SendWithKeyAsync(
            client,
            HttpMethod.Post,
            $"/marketmafioso/api/acquisition/requests/{claimed.RequestId}/resend",
            "client-secret",
            new { });

        resent.EnsureSuccessStatusCode();
        using var resentJson = JsonDocument.Parse(await resent.Content.ReadAsStringAsync());
        Assert.Equal(claimed.RequestId, resentJson.RootElement.GetProperty("id").GetString());
        Assert.Equal("PendingPickup", resentJson.RootElement.GetProperty("status").GetString());

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
    public async Task AcquisitionDashboardServesShellWithConfiguredXivDataBaseUrl()
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

        var acquisitionPage = await client.GetStringAsync("/marketmafioso/acquisition");

        AssertDashboardShell(acquisitionPage);
    }

    [Fact]
    public async Task AcquisitionDashboardServesShellWithPublicOriginXivDataDefault()
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

        var acquisitionPage = await client.GetStringAsync("/marketmafioso/acquisition");

        AssertDashboardShell(acquisitionPage);
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
    public async Task AcquisitionDashboardShowsEmptyQueueState()
    {
        await using var application = CreateHostedApplication(
            extraConfiguration: new KeyValuePair<string, string?>("MarketMafioso:TrustExternalDashboardAuth", "true"));
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var recent = await client.GetAsync("/marketmafioso/api/acquisition/requests/recent");

        recent.EnsureSuccessStatusCode();
        using var recentJson = JsonDocument.Parse(await recent.Content.ReadAsStringAsync());
        Assert.Contains("No acquisition requests yet.", recentJson.RootElement.GetProperty("queueRows").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostedDashboardFormCreateUsesAppManagedDashboardAuth()
    {
        await using var application = CreateHostedApplication();
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var response = await client.PostAsync(
            "/marketmafioso/acquisition/requests",
            new FormUrlEncodedContent(CreateFormFields("untrusted-dashboard")));

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
            ["MarketMafioso:BasePath"] = "/marketmafioso",
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

