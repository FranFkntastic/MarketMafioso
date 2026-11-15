using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using MarketMafioso.Contracts.Workshop;

namespace MarketMafioso.WorkshopPrep;

/// <summary>Plugin-owned host delivery. No network work runs on the drawing thread.</summary>
public sealed class HostWorkshopHandoffService : IWorkshopHandoffService, IDisposable
{
    private readonly Configuration config;
    private readonly IPlayerState player;
    private readonly IClientState client;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly CancellationTokenSource shutdown = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Task worker;
    private int disposed;
    private int epoch;
    private Cache cache = Cache.Empty;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Context(string Url, string Key, WorkshopCharacter Actor, ulong ContentId, int Epoch);
    private sealed record Cache(Context? Owner, WorkshopAssembler[] Known, WorkshopHandoffView[] Outgoing,
        WorkshopHandoffView[] Incoming, WorkshopHandoffView[] Received, string? Error)
    {
        public static Cache Empty { get; } = new(null, [], [], [], [], "Workshop Host has not been contacted yet.");
    }

    public HostWorkshopHandoffService(Configuration config, IPlayerState playerState, IClientState clientState,
        IFramework framework, IPluginLog log)
    {
        this.config = config;
        player = playerState;
        client = clientState;
        this.framework = framework;
        this.log = log;
        client.Login += OnLogin;
        client.Logout += OnLogout;
        worker = Task.Run(RefreshLoopAsync);
    }

    private string? ConfigurationReason => string.IsNullOrWhiteSpace(config.ApiKey) || BaseUrl() is null
        ? "Workshop Host is not configured: set the receiver URL and ingest API key." : null;
    public bool IsAvailable => UnavailableReason is null;
    public string? UnavailableReason => Volatile.Read(ref disposed) != 0 ? "Workshop handoff service is disposed."
        : !client.IsLoggedIn ? "Not logged in to a character."
        : ConfigurationReason ?? (Current.Owner is null ? "Verifying Workshop Host availability..." : Current.Error);
    private Cache Current
    {
        get
        {
            var value = Volatile.Read(ref cache);
            return client.IsLoggedIn && value.Owner is { } owner && owner.Epoch == Volatile.Read(ref epoch) &&
                   owner.Key == config.ApiKey && owner.Url == BaseUrl() ? value : Cache.Empty;
        }
    }
    public IReadOnlyList<WorkshopAssembler> KnownAssemblers => Array.AsReadOnly(Current.Known);
    public IReadOnlyList<WorkshopHandoffView> Outgoing => Array.AsReadOnly(Current.Outgoing);
    public IReadOnlyList<WorkshopHandoffView> Incoming => Array.AsReadOnly(Current.Incoming);

    private string? BaseUrl()
    {
        var acquisition = ReceiverEndpointClassifier.BuildAcquisitionBaseUrl(config.ServerUrl);
        return acquisition is null ? null : acquisition[..^"/acquisition".Length] + "/workshop/handoffs";
    }

    private Context Capture()
    {
        if (!client.IsLoggedIn || player.ContentId == 0) throw new InvalidOperationException("Not logged in to a character.");
        if (ConfigurationReason is { } reason) throw new InvalidOperationException(reason);
        // Same authoritative character/home-world fields used by HttpReporter.BuildReport.
        if (!player.HomeWorld.IsValid || string.IsNullOrWhiteSpace(player.CharacterName))
            throw new InvalidOperationException("Logged-in character identity or home world is not available yet.");
        return new(BaseUrl()!, config.ApiKey, new(player.CharacterName, player.HomeWorld.Value.Name.ToString()), player.ContentId, Volatile.Read(ref epoch));
    }

    private async Task<bool> StillCurrentAsync(Context owner) =>
        await framework.RunOnFrameworkThread(() =>
        {
            try { return Volatile.Read(ref disposed) == 0 && Capture() == owner; }
            catch (InvalidOperationException) { return false; }
        }).ConfigureAwait(false);

    public IReadOnlyList<WorkshopHandoffView> Received => Array.AsReadOnly(Current.Received);

    public Task<WorkshopHoldingsResult> GetHoldingsAsync(WorkshopAssembler character, IReadOnlyCollection<uint> itemIds, CancellationToken cancellationToken)
    {
        var ids = itemIds.ToArray();
        return Task.Run(async () =>
        {
            if (Volatile.Read(ref disposed) != 0) return WorkshopHoldingsResult.Fail("Workshop handoff service is disposed.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
            var entered = false;
            try
            {
                await gate.WaitAsync(linked.Token).ConfigureAwait(false);
                entered = true;
                var owner = await framework.RunOnFrameworkThread(Capture).ConfigureAwait(false);
                var path = $"?characterName={Uri.EscapeDataString(character.CharacterName)}&world={Uri.EscapeDataString(character.World)}&itemIds={string.Join(",", ids)}";
                var result = await SendAsync<WorkshopCharacterHoldings>(owner, HttpMethod.Get, path, null, linked.Token, holdings: true).ConfigureAwait(false);
                if (!await StillCurrentAsync(owner).ConfigureAwait(false))
                    throw new InvalidOperationException("Character or host configuration changed while reading recipient holdings.");
                return new WorkshopHoldingsResult(true, result.CapturedAt is null ? "No inventory report for this character." : "Recipient holdings loaded.",
                    result.CapturedAt, result.Items.ToDictionary(x => x.ItemId, x => x.Quantity));
            }
            catch (Exception ex)
            {
                var reason = Failure(ex, linked.IsCancellationRequested);
                log.Warning(ex, "Workshop recipient holdings failed for {Character}: {Reason}", character.Key, reason);
                return WorkshopHoldingsResult.Fail(reason);
            }
            finally { if (entered) gate.Release(); }
        });
    }

    public Task<WorkshopHandoffResult> ReportHandoverAsync(Guid handoffId, IReadOnlyList<WorkshopMaterialCount> handedOver, int handoverTotal, CancellationToken cancellationToken)
    {
        var copied = handedOver.Select(x => new WorkshopMaterialQuantity(x.ItemId, x.Quantity)).ToArray();
        return MutateAsync("handover", handoffId, owner => new WorkshopHandoverReport(owner.Actor, copied, handoverTotal), cancellationToken);
    }

    public void RequestRefresh()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        try { wake.Release(); } catch (SemaphoreFullException) { }
    }
    private void OnLogin() { Interlocked.Increment(ref epoch); RequestRefresh(); }
    private void OnLogout(int type, int code)
    {
        Interlocked.Increment(ref epoch);
        Volatile.Write(ref cache, Cache.Empty);
        RequestRefresh();
    }

    private async Task RefreshLoopAsync()
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                // Right after login the character identity is not readable yet; retry soon instead of
                // leaving incoming shares hidden for a full refresh interval.
                var identityPending = await RefreshAsync(shutdown.Token).ConfigureAwait(false);
                await wake.WaitAsync(TimeSpan.FromSeconds(identityPending ? 2 : 30), shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        finally { http.Dispose(); }
    }

    /// <returns>True when logged in but the character identity is not readable yet.</returns>
    private async Task<bool> RefreshAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        Context? owner = null;
        try
        {
            // Do not contact the host while logged out or without host configuration.
            var identityPending = false;
            owner = await framework.RunOnFrameworkThread(() =>
            {
                if (!client.IsLoggedIn || ConfigurationReason is not null) return null;
                try { return Capture(); }
                catch (InvalidOperationException) { identityPending = true; return null; }
            }).ConfigureAwait(false);
            if (owner is null) { Volatile.Write(ref cache, Cache.Empty); return identityPending; }
            var path = $"?characterName={Uri.EscapeDataString(owner.Actor.CharacterName)}&world={Uri.EscapeDataString(owner.Actor.World)}";
            var result = await SendAsync<WorkshopHandoffList>(owner, HttpMethod.Get, path, null, token).ConfigureAwait(false);
            var next = new Cache(owner, result.KnownAssemblers.Select(Map).ToArray(), result.Outgoing.Select(Map).ToArray(), result.Incoming.Select(Map).ToArray(),
                (result.Received ?? []).Select(Map).ToArray(), null);
            if (await StillCurrentAsync(owner).ConfigureAwait(false)) Volatile.Write(ref cache, next);
            return false;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var message = Failure(ex);
            log.Warning(ex, "Workshop handoff refresh failed for {Character}: {Reason}", owner?.Actor.ToString() ?? "unknown", message);
            if (owner is not null && await StillCurrentAsync(owner).ConfigureAwait(false))
            {
                var old = Volatile.Read(ref cache);
                Volatile.Write(ref cache, old.Owner == owner ? old with { Error = message } : new(owner, [], [], [], [], message));
            }
            return false;
        }
        finally { gate.Release(); }
    }

    public Task<WorkshopHandoffResult> OfferAsync(Guid sourceJobId, string jobName, WorkshopAssembler target,
        IReadOnlyList<WorkshopPrepQueueItem> items, CancellationToken cancellationToken)
    {
        // Snapshot mutable UI-owned inputs before scheduling the request.
        var copied = items.Select(x => new WorkshopHandoffItem(x.WorkshopItemId, x.Quantity)).ToArray();
        return MutateAsync("offer", null, owner => new WorkshopHandoffOffer(sourceJobId, jobName,
            owner.Actor, new(target.CharacterName, target.World), copied), cancellationToken);
    }
    public Task<WorkshopHandoffResult> WithdrawAsync(Guid id, DateTimeOffset? expected, CancellationToken token) => CommandAsync(id, "withdraw", expected, token);
    public Task<WorkshopHandoffResult> AcceptAsync(Guid id, DateTimeOffset? expected, CancellationToken token) => CommandAsync(id, "accept", expected, token);
    public Task<WorkshopHandoffResult> DeclineAsync(Guid id, DateTimeOffset? expected, CancellationToken token) => CommandAsync(id, "decline", expected, token);
    private Task<WorkshopHandoffResult> CommandAsync(Guid id, string action, DateTimeOffset? expected, CancellationToken token) =>
        MutateAsync(action, id, owner => new WorkshopHandoffCommand(owner.Actor, expected), token);

    private Task<WorkshopHandoffResult> MutateAsync(string action, Guid? id, Func<Context, object> body, CancellationToken token) => Task.Run(async () =>
    {
        if (Volatile.Read(ref disposed) != 0) return WorkshopHandoffResult.Fail("Workshop handoff service is disposed.");
        Context? owner = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
        var entered = false;
        try
        {
            await gate.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            owner = await framework.RunOnFrameworkThread(Capture).ConfigureAwait(false);
            var result = await SendAsync<WorkshopHandoffDto>(owner, HttpMethod.Post,
                id is null ? "" : $"/{id}/{action}", body(owner), linked.Token).ConfigureAwait(false);
            if (!await StillCurrentAsync(owner).ConfigureAwait(false))
                throw new InvalidOperationException($"Host recorded {action} for {owner.Actor}, but the logged-in character or host configuration changed; local import was not performed.");
            return new WorkshopHandoffResult(true, action == "handover" ? "Material handover recorded." : $"Handoff {result.State.ToLowerInvariant()}.", Map(result));
        }
        catch (Exception ex)
        {
            var reason = Failure(ex, linked.IsCancellationRequested);
            log.Warning(ex, "Workshop handoff {Action} {HandoffId} as {Character} failed: {Reason}", action, id?.ToString() ?? "new", owner?.Actor.ToString() ?? "unknown", reason);
            if (owner is not null && (ex is OperationCanceledException ||
                ex is HttpRequestException transport && (transport.StatusCode is null || (int)transport.StatusCode >= 500)) &&
                await StillCurrentAsync(owner).ConfigureAwait(false))
            {
                var old = Volatile.Read(ref cache);
                Volatile.Write(ref cache, old.Owner == owner ? old with { Error = reason } : new(owner, [], [], [], [], reason));
            }
            return WorkshopHandoffResult.Fail(reason);
        }
        finally
        {
            if (entered) gate.Release();
            RequestRefresh();
        }
    });

    private async Task<T> SendAsync<T>(Context owner, HttpMethod method, string path, object? body, CancellationToken token, bool holdings = false)
    {
        if (!await StillCurrentAsync(owner).ConfigureAwait(false)) throw new InvalidOperationException("Character or host configuration changed before the request was sent.");
        using var request = new HttpRequestMessage(method, (holdings ? owner.Url[..^"/handoffs".Length] + "/holdings" : owner.Url) + path);
        request.Headers.Add("X-Api-Key", owner.Key);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        using var response = await http.SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var raw = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            string reason = raw;
            try { using var doc = JsonDocument.Parse(raw); reason = doc.RootElement.GetProperty("error").GetString() ?? raw; }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
            throw new HttpRequestException($"Workshop Host HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {reason}", null, response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<T>(Json, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Workshop Host returned an empty response.");
    }

    private static string Failure(Exception ex, bool cancelled = false) => ex is OperationCanceledException
        ? $"Workshop Host request {(cancelled ? "was cancelled" : "timed out after 15 seconds")} at {DateTimeOffset.UtcNow:O}; its delivery outcome may be unknown. Background refresh will reconcile host state."
        : $"Workshop Host request failed at {DateTimeOffset.UtcNow:O}: {ex.Message}";
    private static WorkshopAssembler Map(WorkshopCharacter value) => new(value.CharacterName, value.World);
    private static WorkshopHandoffView Map(WorkshopHandoffDto value) => new(value.HandoffId, value.SourceJobId,
        value.JobName, Map(value.Sender), Map(value.Target), value.Items.Select(x => new WorkshopPrepQueueItem
        { WorkshopItemId = x.WorkshopItemId, Quantity = x.Quantity }).ToArray(),
        Enum.Parse<WorkshopHandoffState>(value.State), value.UpdatedAt,
        value.HandedOver?.Select(x => new WorkshopMaterialCount(x.ItemId, x.Quantity)).ToArray(), value.HandoverTotal, value.HandoverUpdatedAt);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        client.Login -= OnLogin;
        client.Logout -= OnLogout;
        shutdown.Cancel();
        Volatile.Write(ref cache, Cache.Empty);
        // Never block the framework thread while the worker is awaiting a framework snapshot.
        // The worker disposes HTTP after cancellation. Leave managed synchronization objects
        // for GC: already queued mutations may still be unwinding their finally blocks.
    }
}
