using MarketMafioso.MarketAcquisition;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MarketMafioso.Tests.MarketAcquisition;

public sealed class MarketAcquisitionRequestWorkspaceTests
{
    [Fact]
    public void RestoreAndForgetOwnPersistedClaimAndIdempotencyState()
    {
        var config = new Configuration();
        var claim = CreateClaim();
        MarketAcquisitionClaimPersistence.Save(config, claim, "accept-key", "reject-key");
        var saveCount = 0;
        var resetCount = 0;
        MarketAcquisitionClaimView? adopted = null;
        using var workspace = CreateWorkspace(config, () => saveCount++);
        Connect(
            workspace,
            _ => { },
            value =>
            {
                adopted = value;
                return true;
            },
            _ => resetCount++);

        Assert.True(workspace.RestoreClaimIntoBuilder());
        Assert.Equal(claim.Id, adopted?.Id);
        Assert.Contains("Restored", workspace.Status, StringComparison.Ordinal);

        workspace.ForgetLocalClaim();

        Assert.Null(workspace.ClaimedRequest);
        Assert.Null(config.ActiveMarketAcquisitionClaim);
        Assert.Equal(1, saveCount);
        Assert.Equal(1, resetCount);
    }

    [Fact]
    public void AdoptingMatchingRemoteDocumentUpdatesClaimAndClearsPreparedPlan()
    {
        var config = new Configuration();
        MarketAcquisitionClaimPersistence.Save(config, CreateClaim(), "accept-key", "reject-key");
        var saveCount = 0;
        var resetCount = 0;
        using var workspace = CreateWorkspace(config, () => saveCount++);
        Connect(workspace, _ => { }, _ => true, _ => resetCount++);
        workspace.ReplacePreparedPlan(new MarketAcquisitionPlan { RequestId = "request-1", Status = "Ready" });
        var remote = CreateRequest() with
        {
            Revision = 7,
            Status = "AcceptedInPlugin",
            ItemName = "Updated Darksteel Ore",
        };

        workspace.OnDocumentAdopted(MarketAcquisitionRequestDocumentMapper.FromRequestView(remote), remote);

        Assert.Equal(7, workspace.ClaimedRequest?.Revision);
        Assert.Equal("Updated Darksteel Ore", workspace.ClaimedRequest?.ItemName);
        Assert.Null(workspace.PreparedPlan);
        Assert.Equal(7, config.ActiveMarketAcquisitionClaim?.Revision);
        Assert.Equal("accept-key", config.ActiveMarketAcquisitionClaim?.AcceptIdempotencyKey);
        Assert.Equal("reject-key", config.ActiveMarketAcquisitionClaim?.RejectIdempotencyKey);
        Assert.Equal(1, saveCount);
        Assert.Equal(1, resetCount);
    }

    [Fact]
    public void AdoptingRemoteStatusRefreshPreservesPreparedPlanWhenIntentIsUnchanged()
    {
        var config = new Configuration();
        MarketAcquisitionClaimPersistence.Save(config, CreateClaim(), "accept-key", "reject-key");
        var resetCount = 0;
        using var workspace = CreateWorkspace(config, () => { });
        Connect(workspace, _ => { }, _ => true, _ => resetCount++);
        var plan = new MarketAcquisitionPlan { RequestId = "request-1", Status = "Ready" };
        workspace.ReplacePreparedPlan(plan);
        var remote = CreateRequest() with
        {
            Revision = 7,
            Status = "AcceptedInPlugin",
        };

        workspace.OnDocumentAdopted(MarketAcquisitionRequestDocumentMapper.FromRequestView(remote), remote);

        Assert.Same(plan, workspace.PreparedPlan);
        Assert.Equal(7, workspace.ClaimedRequest?.Revision);
        Assert.Equal("AcceptedInPlugin", workspace.ClaimedRequest?.Status);
        Assert.Equal(0, resetCount);
    }

    [Fact]
    public void RouteLifecycleControllerMutatesWorkspaceClaimState()
    {
        var config = new Configuration();
        var claim = CreateClaim() with { Status = "Running" };
        MarketAcquisitionClaimPersistence.Save(config, claim, "accept-key", "reject-key");
        var saveCount = 0;
        using var workspace = CreateWorkspace(config, () => saveCount++);
        var lifecycle = workspace.CreateClaimLifecycleController(() => "Route running.");

        lifecycle.ApplySuccessfulRouteProgressReport(
            new MarketAcquisitionRouteProgressReportOutcome(
                MarketAcquisitionRouteProgressReporter.CompleteAction,
                CreateRequest() with { Status = "Complete" }),
            claim,
            reportSessionVersion: 3,
            currentSessionVersion: 3,
            "All requested work is complete.");

        Assert.Null(workspace.ClaimedRequest);
        Assert.Null(config.ActiveMarketAcquisitionClaim);
        Assert.Equal("Route complete: All requested work is complete.", workspace.Status);
        Assert.Equal(1, saveCount);
    }

    [Fact]
    public async Task BusyWorkspaceIgnoresConcurrentRequestAction()
    {
        using var workspace = CreateWorkspace(new Configuration(), () => { });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRan = false;
        var first = workspace.RunAsync(async _ =>
        {
            entered.SetResult();
            await release.Task;
        });
        await entered.Task;

        await workspace.RunAsync(_ =>
        {
            secondRan = true;
            return Task.CompletedTask;
        });

        Assert.True(workspace.IsBusy);
        Assert.False(secondRan);
        release.SetResult();
        await first;
        Assert.False(workspace.IsBusy);
    }

    [Fact]
    public async Task FailedClaimIsReopenedBeforeRouteActionReceivesIt()
    {
        var config = new Configuration
        {
            ServerUrl = "https://example.test/inventory",
            ApiKey = "client-key",
            PluginInstanceId = "plugin-instance",
        };
        var failed = CreateClaim() with { Status = "Failed", ClaimToken = "stale-token" };
        MarketAcquisitionClaimPersistence.Save(config, failed, "old-accept-key", "old-reject-key");
        var reclaimed = CreateClaim() with { Status = "Claimed", ClaimToken = "fresh-token" };
        var accepted = CreateRequest() with { Status = "AcceptedInPlugin" };
        using var httpClient = new HttpClient(new SequenceHttpMessageHandler(
            CreateRequest() with { Status = "PendingPickup" },
            reclaimed,
            accepted));
        using var workspace = CreateWorkspace(config, () => { }, httpClient);
        MarketAcquisitionClaimView? routeClaim = null;

        await workspace.RunWithReportableClaimAsync((claim, _) =>
        {
            routeClaim = claim;
            return Task.CompletedTask;
        });

        Assert.Equal("AcceptedInPlugin", routeClaim?.Status);
        Assert.Equal("fresh-token", routeClaim?.ClaimToken);
        Assert.Equal("AcceptedInPlugin", workspace.ClaimedRequest?.Status);
        Assert.Equal("fresh-token", config.ActiveMarketAcquisitionClaim?.ClaimToken);
        Assert.NotEqual("old-accept-key", config.ActiveMarketAcquisitionClaim?.AcceptIdempotencyKey);
        Assert.NotEqual("old-reject-key", config.ActiveMarketAcquisitionClaim?.RejectIdempotencyKey);
    }

    [Fact]
    public async Task RestoredSelectedClaimRecoversMissingWorldScopeBeforeRouteAction()
    {
        var config = new Configuration
        {
            ServerUrl = "https://example.test/inventory",
            ApiKey = "client-key",
        };
        var restored = CreateClaim() with
        {
            Status = "AcceptedInPlugin",
            WorldMode = "Selected",
            SelectedWorlds = [],
        };
        MarketAcquisitionClaimPersistence.Save(config, restored, "accept-key", "reject-key");
        var remote = CreateRequest() with
        {
            Status = "AcceptedInPlugin",
            WorldMode = "Selected",
            SelectedWorlds = ["Siren"],
        };
        using var httpClient = new HttpClient(new SequenceHttpMessageHandler(remote));
        using var workspace = CreateWorkspace(config, () => { }, httpClient);
        MarketAcquisitionClaimView? routeClaim = null;

        await workspace.RunWithReportableClaimAsync((claim, _) =>
        {
            routeClaim = claim;
            return Task.CompletedTask;
        });

        Assert.Equal(["Siren"], routeClaim?.SelectedWorlds);
        Assert.Equal(["Siren"], config.ActiveMarketAcquisitionClaim?.SelectedWorlds);
        Assert.Contains("selected-world scope", workspace.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExplicitLeaseLossSignalsRouteStop()
    {
        var config = new Configuration
        {
            ServerUrl = "https://example.test/inventory",
            ApiKey = "client-key",
            PluginInstanceId = "plugin-instance",
        };
        MarketAcquisitionClaimPersistence.Save(config, CreateClaim() with { Status = "Running" }, "accept-key", "reject-key");
        using var httpClient = new HttpClient(new LeaseHttpMessageHandler(HttpStatusCode.Unauthorized));
        using var workspace = CreateWorkspace(config, () => { }, httpClient);

        await workspace.RenewLeaseIfDueAsync();

        Assert.True(workspace.ConsumeLeaseLossSignal());
        Assert.False(workspace.ConsumeLeaseLossSignal());
        Assert.Contains("route will stop", workspace.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SuccessfulLeaseRenewalIsNotRepeatedEveryFrameworkTick()
    {
        var config = new Configuration
        {
            ServerUrl = "https://example.test/inventory",
            ApiKey = "client-key",
            PluginInstanceId = "plugin-instance",
        };
        MarketAcquisitionClaimPersistence.Save(config, CreateClaim() with { Status = "Running" }, "accept-key", "reject-key");
        var handler = new LeaseHttpMessageHandler(HttpStatusCode.OK);
        using var httpClient = new HttpClient(handler);
        using var workspace = CreateWorkspace(config, () => { }, httpClient);

        await workspace.RenewLeaseIfDueAsync();
        await workspace.RenewLeaseIfDueAsync();

        Assert.Equal(1, handler.RequestCount);
        Assert.False(workspace.ConsumeLeaseLossSignal());
    }

    private static MarketAcquisitionRequestWorkspace CreateWorkspace(
        Configuration config,
        Action saveConfig,
        HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient();
        var client = new MarketAcquisitionRequestClient(httpClient);
        var planPreparation = new MarketAcquisitionPlanPreparationService(
            new UniversalisMarketAcquisitionPlanSource(httpClient),
            new MarketAcquisitionWorldVisitCatalog(config));
        return new MarketAcquisitionRequestWorkspace(config, client, planPreparation, saveConfig, _ => { });
    }

    private static void Connect(
        MarketAcquisitionRequestWorkspace workspace,
        Action<MarketAcquisitionClaimView> adoptRequest,
        Func<MarketAcquisitionClaimView, bool> adoptRestoredRequest,
        Action<string> resetRoute) =>
        workspace.Connect(
            adoptRequest,
            adoptRestoredRequest,
            () => "intent-hash",
            _ => { },
            () => false,
            resetRoute);

    private static MarketAcquisitionClaimView CreateClaim() =>
        new()
        {
            Id = "request-1",
            Revision = 2,
            Status = "Claimed",
            TargetCharacterName = "Eriana Ning",
            TargetWorld = "Siren",
            Region = "North America",
            ItemId = 5060,
            ItemName = "Darksteel Ore",
            QuantityMode = "AllBelowThreshold",
            Quantity = 20,
            HqPolicy = "Either",
            MaxUnitPrice = 600,
            WorldMode = "AllWorldSweep",
            ClaimToken = "claim-token",
        };

    private static MarketAcquisitionRequestView CreateRequest() =>
        new()
        {
            Id = "request-1",
            Revision = 2,
            Status = "Claimed",
            TargetCharacterName = "Eriana Ning",
            TargetWorld = "Siren",
            Region = "North America",
            ItemId = 5060,
            ItemName = "Darksteel Ore",
            QuantityMode = "AllBelowThreshold",
            Quantity = 20,
            HqPolicy = "Either",
            MaxUnitPrice = 600,
            WorldMode = "AllWorldSweep",
        };

    private sealed class SequenceHttpMessageHandler(params MarketAcquisitionRequestView[] responses) : HttpMessageHandler
    {
        private readonly Queue<MarketAcquisitionRequestView> remaining = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!remaining.TryDequeue(out var response))
                throw new InvalidOperationException($"No response remains for {request.RequestUri}.");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(response, response.GetType())),
            });
        }
    }

    private sealed class LeaseHttpMessageHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var response = new HttpResponseMessage(statusCode);
            if (statusCode == HttpStatusCode.OK)
            {
                response.Content = JsonContent.Create(new MarketAcquisitionExecutionLeaseView
                {
                    WorkOrderId = "request-1",
                    PluginInstanceId = "plugin-instance",
                    RenewedAtUtc = DateTimeOffset.UtcNow,
                    ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(2),
                });
            }

            return Task.FromResult(response);
        }
    }
}
