using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MarketMafioso.MarketAcquisition;

public sealed record MarketAcquisitionRequestBuilderSyncOutcome(
    MarketAcquisitionRequestDocument Document,
    string StatusMessage);

public sealed record MarketAcquisitionRequestBuilderRefreshOutcome(
    MarketAcquisitionRequestDocument Document,
    MarketAcquisitionRequestView? RemoteRequest,
    string StatusMessage);

public sealed class MarketAcquisitionRequestWorkspace : IDisposable
{
    private readonly Configuration config;
    private readonly MarketAcquisitionRequestClient client;
    private readonly MarketAcquisitionRequestSyncService syncService;
    private readonly MarketAcquisitionPlanPreparationService planPreparationService;
    private readonly Action saveConfig;
    private readonly Action<Exception> logFailure;

    private Action<MarketAcquisitionClaimView>? adoptRequest;
    private Func<MarketAcquisitionClaimView, bool>? adoptRestoredRequest;
    private Func<string>? getCurrentIntentHash;
    private Action<string>? markPlanPrepared;
    private Func<bool>? isRouteActive;
    private Action<string>? resetRoute;
    private CancellationTokenSource? requestCancellation;
    private string? acceptIdempotencyKey;
    private string? rejectIdempotencyKey;

    public MarketAcquisitionRequestWorkspace(
        Configuration config,
        MarketAcquisitionRequestClient client,
        MarketAcquisitionPlanPreparationService planPreparationService,
        Action saveConfig,
        Action<Exception> logFailure)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.planPreparationService = planPreparationService ?? throw new ArgumentNullException(nameof(planPreparationService));
        this.saveConfig = saveConfig ?? throw new ArgumentNullException(nameof(saveConfig));
        this.logFailure = logFailure ?? throw new ArgumentNullException(nameof(logFailure));
        syncService = new MarketAcquisitionRequestSyncService(client);

        var restored = MarketAcquisitionClaimPersistence.Restore(config);
        if (restored is null)
            return;

        ClaimedRequest = restored.Value.Claim;
        acceptIdempotencyKey = restored.Value.AcceptIdempotencyKey;
        rejectIdempotencyKey = restored.Value.RejectIdempotencyKey;
    }

    public IReadOnlyList<MarketAcquisitionRequestView> PendingRequests { get; private set; } = [];

    public MarketAcquisitionClaimView? ClaimedRequest { get; private set; }

    public MarketAcquisitionPlan? PreparedPlan { get; private set; }

    public string? PreparedPlanHash { get; private set; }

    public bool IsBusy { get; private set; }

    public string Status { get; private set; } = "No dashboard request has been fetched this session.";

    public void Connect(
        Action<MarketAcquisitionClaimView> adoptRequest,
        Func<MarketAcquisitionClaimView, bool> adoptRestoredRequest,
        Func<string> getCurrentIntentHash,
        Action<string> markPlanPrepared,
        Func<bool> isRouteActive,
        Action<string> resetRoute)
    {
        this.adoptRequest = adoptRequest ?? throw new ArgumentNullException(nameof(adoptRequest));
        this.adoptRestoredRequest = adoptRestoredRequest ?? throw new ArgumentNullException(nameof(adoptRestoredRequest));
        this.getCurrentIntentHash = getCurrentIntentHash ?? throw new ArgumentNullException(nameof(getCurrentIntentHash));
        this.markPlanPrepared = markPlanPrepared ?? throw new ArgumentNullException(nameof(markPlanPrepared));
        this.isRouteActive = isRouteActive ?? throw new ArgumentNullException(nameof(isRouteActive));
        this.resetRoute = resetRoute ?? throw new ArgumentNullException(nameof(resetRoute));
    }

    public bool RestoreClaimIntoBuilder()
    {
        if (ClaimedRequest is null)
            return false;

        EnsureConnected();
        var adopted = adoptRestoredRequest!(ClaimedRequest);
        Status = adopted
            ? "Restored previously claimed dashboard request into the builder."
            : "Restored previously claimed dashboard request; preserving local builder edits.";
        return true;
    }

    public MarketAcquisitionClaimLifecycleController CreateClaimLifecycleController(Func<string> getRouteStatusMessage) =>
        new(
            config,
            () => ClaimedRequest,
            value => ClaimedRequest = value,
            () => acceptIdempotencyKey,
            () => rejectIdempotencyKey,
            ClearClaimMetadata,
            SetStatus,
            getRouteStatusMessage,
            saveConfig);

    public Task FetchPendingAsync(string characterName, string world) =>
        RunAsync(async token =>
        {
            ValidateScope(characterName, world);
            PendingRequests = await client.FetchPendingAsync(
                config.ServerUrl,
                WorkshopHostApiKeyRouting.ResolveAcquisitionKey(config),
                characterName,
                world,
                token).ConfigureAwait(false);

            Status = PendingRequests.Count == 0
                ? "No matching dashboard requests."
                : $"Loaded {PendingRequests.Count} dashboard batch(es).";
        });

    public async Task<MarketAcquisitionRequestBuilderSyncOutcome> SyncAsync(
        MarketAcquisitionRequestDocument document,
        string characterName,
        string world)
    {
        EnsureConnected();
        if (isRouteActive!())
            throw new InvalidOperationException("Stop the guided route before replacing request intent.");
        ValidateScope(characterName, world);

        MarketAcquisitionRequestSyncResult? result = null;
        await RunAsync(async token =>
        {
            result = await syncService.SyncAsync(
                new MarketAcquisitionRequestSyncRequest(
                    config.ServerUrl,
                    WorkshopHostApiKeyRouting.ResolveAcquisitionKey(config),
                    characterName,
                    world,
                    config.PluginInstanceId,
                    document,
                    ClaimedRequest),
                token).ConfigureAwait(false);

            ClaimedRequest = result.Claim;
            if (!string.IsNullOrWhiteSpace(result.AcceptIdempotencyKey))
                acceptIdempotencyKey = result.AcceptIdempotencyKey;
            rejectIdempotencyKey ??= NewIdempotencyKey();
            PersistClaim();
            ClearPreparedPlan();
            PendingRequests = PendingRequests
                .Where(request => !string.Equals(request.Id, ClaimedRequest.Id, StringComparison.Ordinal))
                .ToList();
            Status = result.WasReplacement
                ? "Request updated. Prepare a fresh advisory plan when ready."
                : "Request synced, claimed, and accepted. Prepare an advisory plan when ready.";
        }).ConfigureAwait(false);

        if (result is null)
            throw new InvalidOperationException("Request sync did not complete.");

        return new MarketAcquisitionRequestBuilderSyncOutcome(result.Document, Status);
    }

    public async Task<MarketAcquisitionRequestBuilderRefreshOutcome> RefreshRemoteAsync(
        MarketAcquisitionRequestDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.RemoteRequestId))
            throw new InvalidOperationException("Sync the request before refreshing remote state.");

        MarketAcquisitionRequestView? remote = null;
        await RunAsync(async token =>
        {
            remote = await client.GetBatchAsync(
                config.ServerUrl,
                WorkshopHostApiKeyRouting.ResolveAcquisitionKey(config),
                document.RemoteRequestId,
                token).ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (remote is null)
            throw new InvalidOperationException("Remote request refresh did not complete.");

        var remoteDocument = MarketAcquisitionRequestDocumentMapper.FromRequestView(remote);
        Status = "Request synchronized from the server.";
        return new MarketAcquisitionRequestBuilderRefreshOutcome(
            remoteDocument,
            RemoteRequest: remote,
            Status);
    }

    public void OnDocumentAdopted(
        MarketAcquisitionRequestDocument document,
        MarketAcquisitionRequestView? remoteRequest)
    {
        _ = document;
        if (remoteRequest is null ||
            ClaimedRequest is null ||
            !string.Equals(remoteRequest.Id, ClaimedRequest.Id, StringComparison.Ordinal))
        {
            return;
        }

        ClaimedRequest = MarketAcquisitionRequestDocumentMapper.MergeClaimWithRequest(ClaimedRequest, remoteRequest);
        PersistClaim();
        ClearPreparedPlan();
    }

    public Task ClaimAsync(string requestId, string characterName, string world) =>
        RunAsync(async token =>
        {
            EnsureConnected();
            ValidateScope(characterName, world);
            ClaimedRequest = await client.ClaimAsync(
                config.ServerUrl,
                WorkshopHostApiKeyRouting.ResolveAcquisitionKey(config),
                requestId,
                characterName,
                world,
                config.PluginInstanceId,
                token).ConfigureAwait(false);

            acceptIdempotencyKey = NewIdempotencyKey();
            rejectIdempotencyKey = NewIdempotencyKey();
            PersistClaim();
            adoptRequest!(ClaimedRequest);
            ClearPreparedPlan();
            PendingRequests = PendingRequests
                .Where(request => !string.Equals(request.Id, requestId, StringComparison.Ordinal))
                .ToList();
            Status = "Dashboard batch claimed. Review it before accepting.";
        });

    public Task AcceptAsync() =>
        RunAsync(async token =>
        {
            EnsureConnected();
            var claimed = RequireClaimedRequest("No dashboard request is claimed.");
            acceptIdempotencyKey ??= NewIdempotencyKey();
            var accepted = await client.AcceptAsync(
                config.ServerUrl,
                WorkshopHostApiKeyRouting.ResolveAcquisitionKey(config),
                claimed.Id,
                claimed.ClaimToken,
                acceptIdempotencyKey,
                token).ConfigureAwait(false);

            ClaimedRequest = MarketAcquisitionRequestDocumentMapper.MergeClaimWithRequest(claimed, accepted);
            PersistClaim();
            adoptRequest!(ClaimedRequest);
            ClearPreparedPlan();
            Status = "Request accepted locally. Prepare an advisory plan when ready.";
        });

    public Task RejectAsync() =>
        RunAsync(async token =>
        {
            var claimed = RequireClaimedRequest("No dashboard request is claimed.");
            rejectIdempotencyKey ??= NewIdempotencyKey();
            await client.RejectAsync(
                config.ServerUrl,
                WorkshopHostApiKeyRouting.ResolveAcquisitionKey(config),
                claimed.Id,
                claimed.ClaimToken,
                rejectIdempotencyKey,
                "Rejected in the MarketMafioso plugin.",
                token).ConfigureAwait(false);

            MarketAcquisitionClaimPersistence.Clear(config);
            saveConfig();
            ClaimedRequest = null;
            ClearClaimMetadata();
            ClearPreparedPlan();
            Status = "Request rejected.";
        });

    public void ForgetLocalClaim()
    {
        MarketAcquisitionClaimPersistence.Clear(config);
        saveConfig();
        ClaimedRequest = null;
        ClearClaimMetadata();
        ClearPreparedPlan();
        Status = "Forgot local acquisition claim. Fetch dashboard requests to pick up a pending request.";
    }

    public Task PreparePlanAsync(
        string currentWorld,
        TimeSpan recentWorldTtl,
        bool ignoreRecentWorldVisitsForSweep) =>
        RunAsync(async token =>
        {
            EnsureConnected();
            var claimed = await EnsureClaimReadyAsync(
                RequireClaimedRequest("No dashboard request is accepted."),
                token).ConfigureAwait(false);
            var result = await planPreparationService.PrepareAsync(
                new MarketAcquisitionPlanPreparationRequest
                {
                    Claim = claimed,
                    CurrentWorld = currentWorld,
                    PreparedAtUtc = DateTimeOffset.UtcNow,
                    RecentWorldTtl = recentWorldTtl,
                    IgnoreRecentWorldVisitsForSweep = ignoreRecentWorldVisitsForSweep,
                },
                token).ConfigureAwait(false);

            PreparedPlan = result.Plan;
            PreparedPlanHash = getCurrentIntentHash!();
            markPlanPrepared!(PreparedPlanHash);
            resetRoute!("No route has started.");
            Status = result.StatusMessage;
        });

    public Task RunWithReportableClaimAsync(
        Func<MarketAcquisitionClaimView, CancellationToken, Task> action) =>
        RunAsync(async token =>
        {
            ArgumentNullException.ThrowIfNull(action);
            var claimed = await EnsureClaimReadyAsync(
                RequireClaimedRequest("No dashboard request is accepted."),
                token).ConfigureAwait(false);
            if (!MarketAcquisitionRouteProgressReporter.CanReportForRequestStatus(claimed.Status))
            {
                throw new InvalidOperationException(
                    $"Request status {claimed.Status} cannot start a route. Fetch or accept a dashboard request first.");
            }

            await action(claimed, token).ConfigureAwait(false);
        });

    public async Task RunAsync(Func<CancellationToken, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsBusy)
            return;

        IsBusy = true;
        requestCancellation?.Dispose();
        requestCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await action(requestCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Status = $"Request failed: {ex.Message}";
            logFailure(ex);
        }
        finally
        {
            requestCancellation?.Dispose();
            requestCancellation = null;
            IsBusy = false;
        }
    }

    public bool IsPreparedPlanStale() =>
        PreparedPlan is not null &&
        !string.IsNullOrWhiteSpace(PreparedPlanHash) &&
        getCurrentIntentHash is not null &&
        !string.Equals(PreparedPlanHash, getCurrentIntentHash(), StringComparison.Ordinal);

    public MarketAcquisitionPlan RequirePreparedPlan(string message) =>
        PreparedPlan ?? throw new InvalidOperationException(message);

    public MarketAcquisitionClaimView RequireClaimedRequest(string message) =>
        ClaimedRequest ?? throw new InvalidOperationException(message);

    public void ReplacePreparedPlan(MarketAcquisitionPlan plan)
    {
        PreparedPlan = plan ?? throw new ArgumentNullException(nameof(plan));
    }

    public void SetStatus(string status) => Status = status;

    public void Dispose()
    {
        requestCancellation?.Cancel();
        requestCancellation?.Dispose();
        requestCancellation = null;
    }

    private async Task<MarketAcquisitionClaimView> EnsureClaimReadyAsync(
        MarketAcquisitionClaimView claimed,
        CancellationToken token)
    {
        if (claimed.WorldMode.Equals("Selected", StringComparison.OrdinalIgnoreCase) &&
            claimed.SelectedWorlds.Count == 0)
        {
            var remote = await client.GetBatchAsync(
                config.ServerUrl,
                WorkshopHostApiKeyRouting.ResolveAcquisitionKey(config),
                claimed.Id,
                token).ConfigureAwait(false);
            claimed = MarketAcquisitionRequestDocumentMapper.MergeClaimWithRequest(claimed, remote);
            ClaimedRequest = claimed;
            PersistClaim();
            Status = "Restored the selected-world scope from Workshop Host.";
        }

        if (!MarketAcquisitionPlanPreparationService.IsFailedStatus(claimed.Status))
            return claimed;

        await client.ResendAsync(config.ServerUrl, WorkshopHostApiKeyRouting.ResolveAcquisitionKey(config), claimed.Id, token).ConfigureAwait(false);
        var reclaimed = await client.ClaimAsync(
            config.ServerUrl,
            WorkshopHostApiKeyRouting.ResolveAcquisitionKey(config),
            claimed.Id,
            claimed.TargetCharacterName,
            claimed.TargetWorld,
            config.PluginInstanceId,
            token).ConfigureAwait(false);

        acceptIdempotencyKey = NewIdempotencyKey();
        rejectIdempotencyKey = NewIdempotencyKey();
        var accepted = await client.AcceptAsync(
            config.ServerUrl,
            WorkshopHostApiKeyRouting.ResolveAcquisitionKey(config),
            reclaimed.Id,
            reclaimed.ClaimToken,
            acceptIdempotencyKey,
            token).ConfigureAwait(false);

        ClaimedRequest = reclaimed with { Status = accepted.Status };
        PersistClaim();
        PendingRequests = PendingRequests
            .Where(request => !string.Equals(request.Id, reclaimed.Id, StringComparison.Ordinal))
            .ToList();
        Status = "Failed request was reopened and accepted locally. Preparing a fresh plan.";
        return ClaimedRequest;
    }

    private void ClearPreparedPlan()
    {
        PreparedPlan = null;
        PreparedPlanHash = null;
        resetRoute?.Invoke("No guided route has started.");
    }

    private void PersistClaim()
    {
        var claimed = ClaimedRequest ?? throw new InvalidOperationException("No acquisition claim is available to persist.");
        MarketAcquisitionClaimPersistence.Save(config, claimed, acceptIdempotencyKey, rejectIdempotencyKey);
        saveConfig();
    }

    private void ClearClaimMetadata()
    {
        acceptIdempotencyKey = null;
        rejectIdempotencyKey = null;
    }

    private void EnsureConnected()
    {
        if (adoptRequest is null ||
            adoptRestoredRequest is null ||
            getCurrentIntentHash is null ||
            markPlanPrepared is null ||
            isRouteActive is null ||
            resetRoute is null)
        {
            throw new InvalidOperationException("The acquisition request workspace is not connected to its UI and route owners.");
        }
    }

    private static void ValidateScope(string characterName, string world)
    {
        if (string.IsNullOrWhiteSpace(characterName) || string.IsNullOrWhiteSpace(world))
            throw new InvalidOperationException("Character scope is unavailable.");
    }

    private static string NewIdempotencyKey() => Guid.NewGuid().ToString("N");
}
