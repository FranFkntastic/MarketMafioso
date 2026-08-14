using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Franthropy.Observations.V1;
using MarketMafioso.Automation.Items;
using MarketMafioso.Contracts.Inventory;
using MarketMafioso.Quartermaster;

namespace MarketMafioso;

public class HttpReporter : IDisposable
{
    private readonly HttpClient httpClient = new();
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly Configuration config;
    private readonly IPlayerState playerState;
    private readonly IPluginLog log;
    private readonly IChatGui chatGui;
    private readonly InventoryScanner scanner;
    private readonly FranthropyRetainerReportSource retainerReports;
    private readonly QuartermasterIpcClient quartermaster;
    private int disposeStarted;
    private InventoryReport? lastAcknowledgedReport;
    private string? lastAcknowledgedSnapshotId;
    private bool lastCaptureHasRetainerEvidence;
    private bool lastCaptureHasManagementEvidence;

    private static readonly JsonSerializerOptions SerialiserOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public DateTime? LastSentAt { get; private set; }
    public string LastStatus { get; private set; } = "Never sent";
    public string? LastPayload { get; private set; }
    public string? LastDashboardUrl { get; private set; }
    public string? LastDashboardReportUrl { get; private set; }
    public string LastRetainerSourceStatus { get; private set; } = "Franthropy retainer evidence has not been queried.";

    public HttpReporter(
        Configuration config,
        IPlayerState playerState,
        IPluginLog log,
        IChatGui chatGui,
        InventoryScanner scanner,
        FranthropyRetainerReportSource retainerReports,
        QuartermasterIpcClient quartermaster)
    {
        this.config = config;
        this.playerState = playerState;
        this.log = log;
        this.chatGui = chatGui;
        this.scanner = scanner;
        this.retainerReports = retainerReports ?? throw new ArgumentNullException(nameof(retainerReports));
        this.quartermaster = quartermaster ?? throw new ArgumentNullException(nameof(quartermaster));
    }

    public async Task SendReportAsync(bool quiet = false)
    {
        if (Volatile.Read(ref disposeStarted) != 0)
            return;

        await sendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref disposeStarted) != 0)
                return;

            await SendReportCoreAsync(quiet).ConfigureAwait(false);
        }
        finally
        {
            sendGate.Release();
        }
    }

    public async Task SendDeltaReportAsync(bool quiet = false)
    {
        if (Volatile.Read(ref disposeStarted) != 0)
            return;

        await sendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref disposeStarted) != 0)
                return;

            if (!TryValidateEndpoint(quiet, out var endpoint))
                return;

            var capture = BuildReport();
            if (!HasUploadEvidence(capture))
            {
                DeferEvidenceEmptyCapture();
                return;
            }

            var report = PreserveUnavailableEvidence(capture);

            var result = InventoryReportDeltaBuilder.Build(
                lastAcknowledgedSnapshotId,
                lastAcknowledgedReport,
                report);
            if (result.Disposition == InventoryDeltaBuildDisposition.Unchanged)
            {
                LastStatus = "No inventory changes";
                log.Debug("[MarketMafioso] Inventory trigger produced no transport-relevant changes; upload skipped.");
                return;
            }

            if (result.Disposition == InventoryDeltaBuildDisposition.FullSnapshotRequired)
            {
                log.Information(
                    "[MarketMafioso] Inventory delta requires a reconciliation snapshot: {Reason}",
                    result.Reason ?? "unspecified");
                await SendFullReportCoreAsync(report, endpoint, quiet).ConfigureAwait(false);
                return;
            }

            var delta = result.Delta!;
            var deltaUrl = ReceiverEndpointClassifier.BuildInventoryDeltaUrl(config.ServerUrl)
                ?? throw new InvalidOperationException("Could not derive the inventory delta endpoint.");
            LastPayload = JsonSerializer.Serialize(delta, PrettySerialiserOptions);
            using var response = await SendWithBusyRetryAsync(deltaUrl, delta).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            LastSentAt = DateTime.Now;
            LastStatus = $"{(int)response.StatusCode} {response.ReasonPhrase}";

            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                log.Warning(
                    "[MarketMafioso] Receiver rejected inventory delta base {BaseSnapshotId}; sending one reconciliation snapshot.",
                    delta.BaseSnapshotId);
                await SendFullReportCoreAsync(report, endpoint, quiet).ConfigureAwait(false);
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                HandleFailure(endpoint, response.StatusCode, body, quiet);
                return;
            }

            var reportResponse = ParseReportResponse(body);
            if (string.IsNullOrWhiteSpace(reportResponse.ReportId))
            {
                LastStatus = "Receiver omitted snapshot ID";
                log.Warning("[MarketMafioso] Inventory delta was accepted, but the receiver omitted the new snapshot ID; the next change will reconcile in full.");
                lastAcknowledgedReport = null;
                lastAcknowledgedSnapshotId = null;
                return;
            }

            AcceptSnapshot(report, reportResponse);
            var changedBagCount = delta.UpsertedPlayerBags.Count +
                                  delta.RetainerChanges.Sum(change => change.UpsertedBags.Count);
            log.Information(
                "[MarketMafioso] Inventory delta sent - {Status}; {ChangedBags} changed bag(s), {ChangedRetainers} retainer patch(es).",
                LastStatus,
                changedBagCount,
                delta.RetainerChanges.Count);
        }
        catch (Exception ex)
        {
            HandleException(ex, quiet);
        }
        finally
        {
            sendGate.Release();
        }
    }

    private async Task SendReportCoreAsync(bool quiet)
    {
        if (!TryValidateEndpoint(quiet, out var endpoint))
            return;
        try
        {
            var capture = BuildReport();
            if (!HasUploadEvidence(capture))
            {
                DeferEvidenceEmptyCapture();
                return;
            }

            await SendFullReportCoreAsync(PreserveUnavailableEvidence(capture), endpoint, quiet).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            HandleException(ex, quiet);
        }
    }

    private async Task SendFullReportCoreAsync(
        InventoryReport report,
        ReceiverEndpointInfo endpoint,
        bool quiet)
    {
        if (!HasUploadEvidence(report))
        {
            DeferEvidenceEmptyCapture();
            return;
        }

        LastPayload = JsonSerializer.Serialize(report, PrettySerialiserOptions);
        using var response = await SendWithBusyRetryAsync(config.ServerUrl, report).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        LastSentAt = DateTime.Now;
        LastStatus = $"{(int)response.StatusCode} {response.ReasonPhrase}";
        if (!response.IsSuccessStatusCode)
        {
            HandleFailure(endpoint, response.StatusCode, body, quiet);
            return;
        }

        var reportResponse = ParseReportResponse(body);
        if (string.IsNullOrWhiteSpace(reportResponse.ReportId))
        {
            LastStatus = "Receiver omitted snapshot ID";
            lastAcknowledgedReport = null;
            lastAcknowledgedSnapshotId = null;
            log.Warning("[MarketMafioso] Full inventory report was accepted, but the receiver omitted its snapshot ID; deltas remain disabled.");
            return;
        }

        AcceptSnapshot(report, reportResponse);
        var itemCount = report.PlayerInventory.Sum(bag => bag.Items.Count);
        var dashboardSuffix = DashboardSuffix();
        if (!quiet)
        {
            chatGui.Print(
                $"[MarketMafioso] Sent {itemCount} player items + {report.Retainers.Count} retainer(s). " +
                $"Status: {LastStatus}. {LastRetainerSourceStatus}{dashboardSuffix}");
        }
        log.Information($"[MarketMafioso] Reconciliation report sent - {LastStatus}.{dashboardSuffix}");
    }

    internal static bool HasUploadEvidence(InventoryReport report) =>
        InventoryReportEvidence.HasSnapshotEvidence(report);

    internal static InventoryReport PreserveUnavailableEvidence(
        InventoryReport capture,
        InventoryReport? acknowledged,
        bool captureHasRetainerEvidence,
        bool captureHasManagementEvidence = false)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (acknowledged is null)
            return capture;

        if (!InventoryReportEvidence.HasPlayerStorageEvidence(capture))
        {
            capture = capture with
            {
                PlayerGil = acknowledged.PlayerGil,
                PlayerInventory = acknowledged.PlayerInventory,
                PlayerStorage = acknowledged.PlayerStorage,
            };
        }
        else if (capture.PlayerGil is null && acknowledged.PlayerGil is not null)
        {
            capture = capture with { PlayerGil = acknowledged.PlayerGil };
        }

        if (!captureHasRetainerEvidence)
        {
            capture = capture with { Retainers = acknowledged.Retainers };
        }
        if (!captureHasManagementEvidence)
            capture = capture with { RetainerManagement = acknowledged.RetainerManagement };

        return capture;
    }

    private InventoryReport PreserveUnavailableEvidence(InventoryReport capture)
    {
        var playerEvidenceUnavailable = !InventoryReportEvidence.HasPlayerStorageEvidence(capture);
        var retainerEvidenceUnavailable = !lastCaptureHasRetainerEvidence;
        var report = PreserveUnavailableEvidence(
            capture,
            lastAcknowledgedReport,
            lastCaptureHasRetainerEvidence,
            lastCaptureHasManagementEvidence);
        if (lastAcknowledgedReport is not null && playerEvidenceUnavailable)
        {
            log.Debug(
                "[MarketMafioso] Player inventory evidence is unavailable; preserving the last acknowledged player state in the upload baseline.");
        }
        if (lastAcknowledgedReport is not null && retainerEvidenceUnavailable)
        {
            log.Debug(
                "[MarketMafioso] Franthropy retainer evidence is unavailable; preserving the last acknowledged retainer state in the upload baseline.");
        }

        return report;
    }

    private void DeferEvidenceEmptyCapture()
    {
        const string status = "Waiting for inventory evidence";
        if (!string.Equals(LastStatus, status, StringComparison.Ordinal))
        {
            log.Debug(
                "[MarketMafioso] Inventory capture has no observed player or retainer sources; upload deferred until evidence is available.");
        }

        LastStatus = status;
    }

    private InventoryReport BuildReport()
    {
        lastCaptureHasRetainerEvidence = false;
        lastCaptureHasManagementEvidence = false;
        var ownerScope = new QuartermasterOwnerScope(
            playerState.ContentId == 0 ? null : playerState.ContentId,
            playerState.HomeWorld.IsValid ? playerState.HomeWorld.Value.RowId : null,
            playerState.CharacterName,
            playerState.HomeWorld.IsValid ? playerState.HomeWorld.Value.Name.ToString() : null);
        var charName = config.IncludeCharacterInfo ? ownerScope.CharacterName : null;
        var homeWorld = config.IncludeCharacterInfo ? ownerScope.HomeWorldName : null;
        var playerCapture = scanner.CapturePlayerInventory(config);
        var retainers = new List<RetainerReport>();
        QuartermasterSnapshot? quartermasterSnapshotForReport = null;
        if (ownerScope.LocalContentId is > 0 && ownerScope.HomeWorldId is > 0 &&
            retainerReports.TryGetReports(
                new ObservationOwner(ownerScope.LocalContentId.Value, ownerScope.HomeWorldId.Value),
                ownerScope.CharacterName,
                ownerScope.HomeWorldName,
                config.IncludeCharacterInfo,
                config.IncludeItemNames,
                scanner.ResolveItemMetadata,
                out retainers))
        {
            lastCaptureHasRetainerEvidence = true;
            retainers = PreserveMissingRetainerFields(retainers, lastAcknowledgedReport?.Retainers);
            LastRetainerSourceStatus = $"Franthropy supplied {retainers.Count} owner-scoped retainer(s).";
        }
        else
        {
            LastRetainerSourceStatus =
                "Franthropy has no current owner-scoped retainer roster; the last acknowledged retainer state is preserved.";
        }

        if (quartermaster.TryGetSnapshot(out var quartermasterSnapshot, out _) &&
            ownerScope.Matches(quartermasterSnapshot!.Owner))
        {
            quartermasterSnapshotForReport = quartermasterSnapshot;
            lastCaptureHasManagementEvidence = quartermasterSnapshot.HasStowageEvidence;
        }

        var generatedAtUtc = DateTime.UtcNow.ToString("o");
        return new InventoryReport
        {
            Metadata = new InventoryReportMetadata
            {
                SchemaVersion = 5,
                SourcePlugin = "MarketMafioso",
                PluginVersion = PluginBuildInfo.DisplayVersion,
                GeneratedAtUtc = generatedAtUtc,
            },
            CharacterName = charName,
            HomeWorld = homeWorld,
            ServiceAccountNumber = config.IncludeCharacterInfo && config.ServiceAccountNumber is > 0
                ? config.ServiceAccountNumber
                : null,
            PlayerGil = scanner.ScanPlayerGil(),
            Timestamp = generatedAtUtc,
            PlayerInventory = playerCapture.Bags,
            Retainers = retainers,
            PlayerStorage = new StorageSourceEvidence
            {
                RequestedSources = playerCapture.RequestedSources.ToList(),
                ObservedSources = playerCapture.ObservedSources.ToList(),
            },
            RetainerManagement = quartermasterSnapshotForReport is null
                ? null
                : BuildStowageReport(quartermasterSnapshotForReport, config.IncludeItemNames),
        };
    }

    private bool TryValidateEndpoint(bool quiet, out ReceiverEndpointInfo endpoint)
    {
        endpoint = ReceiverEndpointClassifier.Classify(config.ServerUrl);
        if (string.IsNullOrWhiteSpace(config.ServerUrl))
        {
            if (!quiet)
                chatGui.PrintError("[MarketMafioso] No server URL configured. Use /mmf to set one.");
            return false;
        }
        if (endpoint.Kind == ReceiverEndpointKind.Invalid)
        {
            LastStatus = "Invalid server URL";
            if (!quiet)
                chatGui.PrintError("[MarketMafioso] Server URL is not a valid HTTP or HTTPS endpoint.");
            return false;
        }
        if (endpoint.RequiresApiKey && string.IsNullOrWhiteSpace(config.ApiKey))
        {
            LastStatus = "API key required";
            if (!quiet)
                chatGui.PrintError("[MarketMafioso] This hosted receiver requires a MarketMafioso Client Key. Open /mmf and set it under Server Connection.");
            return false;
        }
        if (endpoint.RequiresApiKey && WorkshopHostApiKeyRouting.IsCraftArchitectKey(config.ApiKey))
        {
            LastStatus = "Wrong API key type";
            if (!quiet)
                chatGui.PrintError("[MarketMafioso] A Craft Architect key cannot upload inventory. Move it to the Acquisition Key field and add a MarketMafioso Client Key.");
            return false;
        }
        return true;
    }

    private HttpRequestMessage CreateRequest<T>(string url, T payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload, options: SerialiserOptions),
        };
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
            request.Headers.Add("X-Api-Key", config.ApiKey);
        return request;
    }

    private async Task<HttpResponseMessage> SendWithBusyRetryAsync<T>(string url, T payload)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = CreateRequest(url, payload);
            var response = await httpClient.SendAsync(request).ConfigureAwait(false);
            if (!IsTransientReceiverStatus(response.StatusCode) || attempt >= 1)
                return response;

            var retryDelay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
            retryDelay = TimeSpan.FromMilliseconds(Math.Clamp(retryDelay.TotalMilliseconds, 100, 5_000));
            response.Dispose();
            LastStatus = "Receiver busy; retrying inventory upload";
            log.Debug(
                "[MarketMafioso] Receiver requested a retry; resending the same inventory payload after {RetryDelayMs} ms.",
                retryDelay.TotalMilliseconds);
            await Task.Delay(retryDelay).ConfigureAwait(false);
        }
    }

    private void AcceptSnapshot(InventoryReport report, HttpReportResponse response)
    {
        lastAcknowledgedReport = report;
        lastAcknowledgedSnapshotId = response.ReportId;
        LastDashboardUrl = ResolveDashboardUrlForDisplay(response.DashboardUrl, config.ServerUrl);
        LastDashboardReportUrl = response.ResolveReportUrl(config.ServerUrl);
    }

    private void HandleFailure(
        ReceiverEndpointInfo endpoint,
        System.Net.HttpStatusCode statusCode,
        string body,
        bool quiet)
    {
        if (IsTransientReceiverStatus(statusCode))
        {
            LastStatus = "Receiver busy; upload deferred";
            log.Warning("[MarketMafioso] Receiver remained busy after one retry; the next inventory change will continue from the current acknowledged baseline.");
            return;
        }

        if (endpoint.RequiresApiKey && statusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            if (!quiet)
                chatGui.PrintError("[MarketMafioso] The hosted receiver rejected the MarketMafioso Client Key. Check the saved key for this endpoint.");
            log.Warning($"[MarketMafioso] Hosted receiver rejected API key - {LastStatus}: {body}");
            return;
        }

        if (!quiet)
            chatGui.PrintError($"[MarketMafioso] Server error {LastStatus}: {body[..Math.Min(body.Length, 200)]}");
        log.Warning($"[MarketMafioso] Server returned {LastStatus}: {body}");
    }

    internal static bool IsTransientReceiverStatus(System.Net.HttpStatusCode statusCode) =>
        statusCode == System.Net.HttpStatusCode.ServiceUnavailable;

    private void HandleException(Exception ex, bool quiet)
    {
        if (Volatile.Read(ref disposeStarted) != 0)
        {
            LastStatus = "Stopped";
            return;
        }

        LastStatus = $"Error: {ex.Message}";
        if (!quiet)
            chatGui.PrintError($"[MarketMafioso] Failed to send: {ex.Message}");
        log.Error(ex, "[MarketMafioso] Error sending report");
    }

    private string DashboardSuffix() =>
        string.IsNullOrWhiteSpace(LastDashboardReportUrl)
            ? string.IsNullOrWhiteSpace(LastDashboardUrl)
                ? string.Empty
                : $" Dashboard: {LastDashboardUrl}"
            : $" View: {LastDashboardReportUrl}";

    private static readonly JsonSerializerOptions PrettySerialiserOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
            return;

        httpClient.Dispose();
        // SemaphoreSlim.Dispose is not thread-safe with concurrent members. An upload may
        // still be unwinding after HttpClient.Dispose cancels it, so disposing the gate here
        // would make its guaranteed Release throw on the continuation thread. The gate owns
        // no unmanaged resources unless AvailableWaitHandle is requested (it never is), and
        // becomes collectible with this reporter after admitted sends finish.
    }

    public static List<RetainerReport> BuildRetainerReports(
        QuartermasterSnapshot snapshot,
        QuartermasterOwnerScope ownerScope,
        bool includeOwnerFields,
        Func<uint, AutomationItemMetadata>? resolveItemMetadata = null,
        bool includeItemNames = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(ownerScope);
        if (!ownerScope.Matches(snapshot.Owner))
            return [];

        return snapshot.Retainers
            .Select(r => new RetainerReport
            {
                RetainerName = r.RetainerName,
                RetainerId = r.RetainerId,
                OwnerCharacterName = includeOwnerFields ? ownerScope.CharacterName : null,
                OwnerHomeWorld = includeOwnerFields ? ownerScope.HomeWorldName : null,
                LastUpdated = r.ObservedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                Gil = r.Gil,
                GilObservedAtUtc = r.GilObservedAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ListingsObservedAtUtc = r.ListingsObservedAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                Storage = new StorageSourceEvidence
                {
                    RequestedSources = r.RequestedSources.ToList(),
                    ObservedSources = r.ObservedSources.ToList(),
                },
                Bags = r.Bags
                    .Select(b => new InventoryBag
                    {
                        BagName = b.BagName,
                        Location = b.Location,
                        ObservedAtUtc = b.ObservedAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                        Items = b.Items
                            .Select(i => MapQuartermasterItem(i, resolveItemMetadata, includeItemNames))
                            .ToList(),
                    })
                    .ToList(),
                MarketListings = r.Listings
                    .Select(i => MapQuartermasterListing(i, resolveItemMetadata, includeItemNames))
                    .ToList(),
            })
            .ToList();
    }

    internal static List<RetainerReport> PreserveMissingRetainerFields(
        IReadOnlyList<RetainerReport> current,
        IReadOnlyList<RetainerReport>? acknowledged)
    {
        if (acknowledged is null || acknowledged.Count == 0)
            return current.ToList();
        var previous = acknowledged.ToDictionary(retainer => retainer.RetainerId);
        return current.Select(retainer =>
        {
            if (!previous.TryGetValue(retainer.RetainerId, out var prior))
                return retainer;
            var observedSources = retainer.Storage.ObservedSources.ToHashSet(StringComparer.Ordinal);
            var bags = retainer.Bags.ToDictionary(BagKey, StringComparer.Ordinal);
            foreach (var priorBag in prior.Bags)
            {
                var source = priorBag.Location ?? priorBag.BagName;
                if (!observedSources.Contains(source))
                    bags.TryAdd(BagKey(priorBag), priorBag);
            }
            retainer = retainer with { Bags = bags.Values.OrderBy(bag => bag.BagName, StringComparer.Ordinal).ToList() };
            if (retainer.GilObservedAtUtc is null)
                retainer = retainer with { Gil = prior.Gil, GilObservedAtUtc = prior.GilObservedAtUtc };
            if (retainer.ListingsObservedAtUtc is null)
                retainer = retainer with
                {
                    MarketListings = prior.MarketListings,
                    ListingsObservedAtUtc = prior.ListingsObservedAtUtc,
                };
            return retainer;
        }).ToList();
    }

    private static string BagKey(InventoryBag bag) => $"{bag.BagName}\0{bag.Location}";

    public static QuartermasterStowageReport? BuildStowageReport(
        QuartermasterSnapshot snapshot,
        bool includeItemNames)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.StowagePlans.IsDefaultOrEmpty)
            return null;
        var retainerNames = snapshot.Retainers.ToDictionary(
            retainer => retainer.RetainerId,
            retainer => retainer.RetainerName);
        return new QuartermasterStowageReport
        {
            ProviderInstanceId = snapshot.ProviderInstanceId,
            Revision = snapshot.Revision,
            Owner = new QuartermasterStowageOwner
            {
                LocalContentId = snapshot.Owner.LocalContentId,
                HomeWorldId = snapshot.Owner.HomeWorldId,
            },
            Plans = snapshot.StowagePlans.Select(plan => new QuartermasterStowagePlanReport
            {
                Id = plan.Id,
                Revision = plan.Revision,
                Name = plan.Name,
                Enabled = plan.Enabled,
                Rules = plan.Rules.Select(rule => new QuartermasterStowageRuleReport
                {
                    Id = rule.Id,
                    ItemId = rule.ItemId,
                    ItemName = includeItemNames ? rule.ItemName : null,
                    DesiredPlayerQuantity = rule.DesiredPlayerQuantity,
                    Quality = rule.Quality,
                    Action = rule.Action,
                    Quantity = rule.ActionQuantity,
                    PlayerQuantity = rule.PlayerQuantity,
                    PreferredDestinations = rule.PreferredRetainerIds.Select(retainerId =>
                        new QuartermasterStowageDestinationReport
                        {
                            RetainerId = retainerId,
                            RetainerName = retainerNames.GetValueOrDefault(retainerId),
                        }).ToArray(),
                }).ToArray(),
            }).ToArray(),
        };
    }

    private static ItemSlot MapQuartermasterItem(
        QuartermasterItemSnapshot item,
        Func<uint, AutomationItemMetadata>? resolveItemMetadata,
        bool includeItemNames)
    {
        var metadata = string.IsNullOrWhiteSpace(item.ItemType) || (includeItemNames && string.IsNullOrWhiteSpace(item.ItemName))
            ? resolveItemMetadata?.Invoke(item.ItemId)
            : null;
        return new ItemSlot
        {
            ItemId = item.ItemId,
            ItemName = includeItemNames
                ? (string.IsNullOrWhiteSpace(item.ItemName) ? metadata?.Identity.Name : item.ItemName)
                : null,
            ItemType = string.IsNullOrWhiteSpace(item.ItemType) ? metadata?.ItemType : item.ItemType,
            Quantity = item.Quantity,
            IsHQ = item.IsHq,
            Condition = metadata is { SupportsCondition: false } ? 0 : item.Condition,
            ContainerKey = item.ContainerKey,
            SlotIndex = item.SlotIndex,
            ConditionPercent = metadata is { SupportsCondition: false } ? null : item.ConditionPercent,
            Equipped = item.Equipped,
        };
    }

    private static RetainerMarketListing MapQuartermasterListing(
        QuartermasterListingSnapshot item,
        Func<uint, AutomationItemMetadata>? resolveItemMetadata,
        bool includeItemNames)
    {
        var metadata = string.IsNullOrWhiteSpace(item.ItemType) || (includeItemNames && string.IsNullOrWhiteSpace(item.ItemName))
            ? resolveItemMetadata?.Invoke(item.ItemId)
            : null;
        return new RetainerMarketListing
        {
            ItemId = item.ItemId,
            ItemName = includeItemNames
                ? (string.IsNullOrWhiteSpace(item.ItemName) ? metadata?.Identity.Name : item.ItemName)
                : null,
            ItemType = string.IsNullOrWhiteSpace(item.ItemType) ? metadata?.ItemType : item.ItemType,
            Quantity = item.Quantity,
            IsHQ = item.IsHq,
            Condition = metadata is { SupportsCondition: false } ? 0 : item.Condition,
            ContainerKey = item.ContainerKey,
            SlotIndex = item.SlotIndex,
            ConditionPercent = metadata is { SupportsCondition: false } ? null : item.ConditionPercent,
            UnitPrice = item.UnitPrice,
            ListedAt = item.ListedAt,
        };
    }

    public static HttpReportResponse ParseReportResponse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return new HttpReportResponse(
                TryGetString(root, "id"),
                TryGetString(root, "dashboardUrl"),
                TryGetString(root, "reportUrl"));
        }
        catch (JsonException)
        {
            return new HttpReportResponse(null, null, null);
        }
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    public static string? ResolveDashboardUrlForDisplay(string? dashboardUrl, string? serverUrl) =>
        !string.IsNullOrWhiteSpace(dashboardUrl)
            ? dashboardUrl
            : ReceiverEndpointClassifier.BuildDashboardUrl(serverUrl);
}

public readonly record struct HttpReportResponse(
    string? ReportId,
    string? DashboardUrl,
    string? ReportUrl)
{
    public string? ResolveDashboardUrl(string? serverUrl) =>
        !string.IsNullOrWhiteSpace(DashboardUrl)
            ? DashboardUrl
            : ReceiverEndpointClassifier.BuildDashboardUrl(serverUrl);

    public string? ResolveReportUrl(string? serverUrl) =>
        !string.IsNullOrWhiteSpace(ReportUrl)
            ? ReportUrl
            : ReceiverEndpointClassifier.BuildDashboardReportUrl(serverUrl, ReportId);
}
