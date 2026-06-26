using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Configuration config;
    private readonly HttpReporter reporter;
    private readonly InventoryScanner scanner;
    private readonly AutoRetainerRefreshService autoRetainerRefresh;
    private readonly WorkshopProjectCatalog workshopCatalog;
    private readonly VIWIWorkshoppaIpc viwiWorkshoppaIpc;
    private readonly WorkshopRetainerRestockService workshopRetainerRestock;
    private readonly WorkshopAssemblyRunner workshopAssemblyRunner;
    private readonly WorkshopMaterialManifestExportService workshopMaterialManifestExport;
    private readonly IPlayerState playerState;
    private readonly IPluginLog log;
    private readonly HttpClient acquisitionHttpClient = new();
    private readonly MarketAcquisitionRequestClient acquisitionClient;
    private readonly UniversalisMarketAcquisitionPlanSource acquisitionPlanSource;
    private readonly MarketBoardListingReader marketBoardListingReader;
    private readonly MarketBoardItemSearchDriver marketBoardItemSearchDriver;
    private readonly MarketBoardApproachService marketBoardApproachService;
    private readonly MarketAcquisitionRouteRunner marketAcquisitionRouteRunner;

    private string urlBuffer = string.Empty;
    private string apiKeyBuffer = string.Empty;
    private string dashboardUrlBuffer = string.Empty;
    private string dashboardOpenStatus = "Dashboard link appears after a successful send.";
    private bool showApiKey = false;
    private bool showPreview = false;
    private readonly WorkshopProjectSelectionState workshopProjectSelection = new();
    private IReadOnlyList<MarketAcquisitionRequestView> pendingAcquisitionRequests = [];
    private MarketAcquisitionClaimView? claimedAcquisitionRequest;
    private string? claimedAcceptIdempotencyKey;
    private string? claimedRejectIdempotencyKey;
    private MarketAcquisitionPlan? acquisitionPlan;
    private MarketBoardReadResult? marketBoardReadResult;
    private MarketBoardListingReconciliation? marketBoardReconciliation;
    private MarketAcquisitionLiveDryRun? marketAcquisitionLiveDryRun;
    private DateTimeOffset nextGuidedRouteMonitorUtc = DateTimeOffset.MinValue;
    private bool guidedRouteProbeRunning = false;
    private bool acquisitionRequestBusy = false;
    private string acquisitionStatus = "No dashboard request has been fetched this session.";
    private CancellationTokenSource? acquisitionRequestCancellation;
    private bool confirmViwiClear = false;
    private bool confirmNewWorkshopQueue = false;
    private bool confirmLoadFrozenQueue = false;
    private Guid? selectedFrozenQueueId;
    private string frozenQueueNameInput = string.Empty;
    private string workshopStatus = "Workshop prep queue is idle.";

    private const string ProductSummary = "Small, practical FFXIV improvements under one roof.";
    private const string InventoryModuleSummary = "Inventory Reporter exports character and retainer inventory snapshots as JSON.";
    private const string WorkshopLogisticsModuleSummary = "Workshop Logistics tracks company workshop jobs, materials, retainer restock, handoff, and assembly.";
    private const string MarketAcquisitionModuleSummary = "Market Acquisition picks up dashboard-created purchase requests for local review.";
    private const string LocalReceiverUrl = "http://localhost:8080/inventory";
    private const string DevReceiverUrl = "https://dev.xivcraftarchitect.com/api/marketmafioso/inventory";
    private const string ProductionReceiverUrl = "https://xivcraftarchitect.com/api/marketmafioso/inventory";

    private static readonly Vector4 ColHeader = new(0.38f, 0.73f, 1.00f, 1f);
    private static readonly Vector4 ColSuccess = new(0.45f, 0.90f, 0.55f, 1f);
    private static readonly Vector4 ColError = new(1.00f, 0.40f, 0.40f, 1f);
    private static readonly Vector4 ColMuted = new(0.60f, 0.60f, 0.60f, 1f);

    public MainWindow(
        Configuration config,
        HttpReporter reporter,
        InventoryScanner scanner,
        AutoRetainerRefreshService autoRetainerRefresh,
        WorkshopProjectCatalog workshopCatalog,
        VIWIWorkshoppaIpc viwiWorkshoppaIpc,
        WorkshopRetainerRestockService workshopRetainerRestock,
        WorkshopAssemblyRunner workshopAssemblyRunner,
        WorkshopMaterialManifestExportService workshopMaterialManifestExport,
        IPlayerState playerState,
        MarketBoardApproachService marketBoardApproachService,
        string marketAcquisitionRouteDiagnosticsDirectory,
        IPluginLog log)
        : base("MarketMafioso##MarketMafiosoMainWindow",
               ImGuiWindowFlags.None)
    {
        this.config = config;
        this.reporter = reporter;
        this.scanner = scanner;
        this.autoRetainerRefresh = autoRetainerRefresh;
        this.workshopCatalog = workshopCatalog;
        this.viwiWorkshoppaIpc = viwiWorkshoppaIpc;
        this.workshopRetainerRestock = workshopRetainerRestock;
        this.workshopAssemblyRunner = workshopAssemblyRunner;
        this.workshopMaterialManifestExport = workshopMaterialManifestExport;
        this.playerState = playerState;
        this.log = log;
        acquisitionClient = new MarketAcquisitionRequestClient(acquisitionHttpClient);
        acquisitionPlanSource = new UniversalisMarketAcquisitionPlanSource(acquisitionHttpClient);
        marketBoardListingReader = new MarketBoardListingReader(Plugin.GameGui);
        marketBoardItemSearchDriver = new MarketBoardItemSearchDriver(Plugin.GameGui);
        this.marketBoardApproachService = marketBoardApproachService;
        marketAcquisitionRouteRunner = new MarketAcquisitionRouteRunner(marketAcquisitionRouteDiagnosticsDirectory);

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(980, 560),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        if (string.IsNullOrWhiteSpace(config.ApiKey) &&
            !string.IsNullOrWhiteSpace(config.CommandPickupApiKey))
        {
            config.ApiKey = config.CommandPickupApiKey;
            config.Save();
        }

        urlBuffer = config.ServerUrl;
        apiKeyBuffer = config.ApiKey;
        ProjectBrowser = new WorkshopProjectBrowserWindow(
            config,
            workshopCatalog,
            workshopProjectSelection,
            AddWorkshopProject);
        FrozenQueueBrowser = new WorkshopFrozenQueueBrowserWindow(
            config,
            workshopCatalog,
            new WorkshopFrozenQueueBrowserActions(
                () => !workshopAssemblyRunner.HasActiveRun,
                LoadFrozenQueue,
                OverwriteFrozenQueueWithCurrent,
                RenameFrozenQueue,
                DuplicateFrozenQueue,
                DeleteFrozenQueue,
                SaveCurrentQueueAsNew));
        AcquisitionDiagnostics = new MarketAcquisitionDiagnosticsWindow(
            () => marketBoardReadResult,
            () => marketBoardReconciliation,
            () => marketAcquisitionLiveDryRun);

        var restoredAcquisitionClaim = MarketAcquisitionClaimPersistence.Restore(config);
        if (restoredAcquisitionClaim != null)
        {
            claimedAcquisitionRequest = restoredAcquisitionClaim.Value.Claim;
            claimedAcceptIdempotencyKey = restoredAcquisitionClaim.Value.AcceptIdempotencyKey;
            claimedRejectIdempotencyKey = restoredAcquisitionClaim.Value.RejectIdempotencyKey;
            acquisitionStatus = "Restored previously claimed dashboard request from plugin settings.";
        }
    }

    public WorkshopProjectBrowserWindow ProjectBrowser { get; }
    public WorkshopFrozenQueueBrowserWindow FrozenQueueBrowser { get; }
    public MarketAcquisitionDiagnosticsWindow AcquisitionDiagnostics { get; }

    public void OnFrameworkUpdate(IFramework _) => MonitorGuidedRoute();

    public override void Draw()
    {
        DrawHeader();
        ImGui.Spacing();

        if (ImGui.BeginTabBar("##MarketMafiosoTabs"))
        {
            if (ImGui.BeginTabItem("Overview"))
            {
                DrawOverviewTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Inventory Reporter"))
            {
                DrawInventoryReporterTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Workshop Logistics"))
            {
                DrawWorkshopPrepTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Market Acquisition"))
            {
                DrawMarketAcquisitionTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Settings"))
            {
                DrawSettingsTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Status"))
            {
                DrawStatusTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawHeader()
    {
        ImGui.TextColored(ColHeader, "MarketMafioso");
        ImGui.TextWrapped(ProductSummary);
        ImGui.TextColored(ColMuted, "Current modules: Inventory Reporter, Workshop Logistics, Market Acquisition");
    }

    private void DrawOverviewTab()
    {
        ImGui.Spacing();
        ImGui.TextColored(ColHeader, "Modules");
        ImGui.Separator();

        DrawModuleSummary("Inventory Reporter", "Enabled", InventoryModuleSummary);
        DrawModuleSummary("Workshop Logistics", "Enabled", WorkshopLogisticsModuleSummary);
        DrawModuleSummary("Market Acquisition", "Foundation", MarketAcquisitionModuleSummary);
        DrawModuleSummary("General Improvements", "Planned", "Small quality-of-life tools that are useful, but too narrow for their own plugin.");
    }

    private void DrawInventoryReporterTab()
    {
        ImGui.Spacing();
        ImGui.TextColored(ColHeader, "Inventory Reporter");
        ImGui.TextWrapped(InventoryModuleSummary);
        ImGui.Spacing();

        DrawInventoryOptionsSection();
        ImGui.Spacing();
        DrawBehaviourSection();
        ImGui.Spacing();
        DrawActionsSection();

        if (showPreview)
        {
            ImGui.Separator();
            DrawJsonPreview();
        }
    }

    private void DrawWorkshopPrepTab()
    {
        ImGui.Spacing();
        ImGui.TextColored(ColHeader, "Workshop Logistics");
        ImGui.TextWrapped(WorkshopLogisticsModuleSummary);
        ImGui.Spacing();

        var projects = workshopCatalog.GetProjects();

        DrawWorkshopPrepQueue(projects);
        ImGui.Spacing();
        DrawWorkshopMaterialSummary();
        ImGui.Spacing();
        DrawWorkshopAssemblyWorkflow();
    }

    private void DrawMarketAcquisitionTab()
    {
        ImGui.Spacing();
        ImGui.TextColored(ColHeader, "Market Acquisition");
        ImGui.TextWrapped(MarketAcquisitionModuleSummary);
        ImGui.Spacing();

        DrawMarketAcquisitionPickupSection();
        ImGui.Spacing();
        DrawClaimedAcquisitionRequest();
        ImGui.Spacing();
        DrawMarketAcquisitionPlan();
        ImGui.Spacing();
        DrawMarketAcquisitionGuidedRoute();
        ImGui.Spacing();
        DrawMarketBoardProbe();
    }

    private void DrawMarketAcquisitionPickupSection()
    {
        ImGuiUi.SectionHeader("Request Pickup", ColHeader);

        if (TryGetAcquisitionScope(out var characterName, out var world))
            ImGui.TextColored(ColMuted, $"Character scope: {characterName} @ {world}");
        else
            ImGui.TextColored(ColError, "Character scope unavailable. Log into a character before fetching requests.");

        var canFetch = !acquisitionRequestBusy &&
                       !string.IsNullOrWhiteSpace(apiKeyBuffer) &&
                       TryGetAcquisitionScope(out _, out _);
        if (ImGuiUi.Button("Fetch Dashboard Requests", canFetch))
            _ = FetchDashboardRequestsAsync();

        ImGui.SameLine();
        ImGui.TextColored(GetAcquisitionStatusColor(), acquisitionStatus);

        if (pendingAcquisitionRequests.Count == 0)
        {
            ImGui.TextColored(
                string.IsNullOrWhiteSpace(apiKeyBuffer) ? ColError : ColMuted,
                string.IsNullOrWhiteSpace(apiKeyBuffer)
                    ? "Set the client API key in Settings before fetching dashboard requests."
                    : "No pending dashboard requests are loaded.");
            return;
        }

        var tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable;
        if (ImGui.BeginTable("MarketAcquisitionPendingRequests", 6, tableFlags))
        {
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Qty");
            ImGui.TableSetupColumn("HQ");
            ImGui.TableSetupColumn("Max Unit");
            ImGui.TableSetupColumn("Mode");
            ImGui.TableSetupColumn("");
            ImGui.TableHeadersRow();

            foreach (var request in pendingAcquisitionRequests)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatAcquisitionItem(request));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(request.Quantity.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(request.HqPolicy);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatGil(request.MaxUnitPrice));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(request.WorldMode);
                ImGui.TableNextColumn();
                if (ImGuiUi.Button($"Claim##marketAcquisitionClaim{request.Id}", !acquisitionRequestBusy))
                    _ = ClaimAcquisitionRequestAsync(request.Id);
            }

            ImGui.EndTable();
        }
    }

    private void DrawClaimedAcquisitionRequest()
    {
        ImGuiUi.SectionHeader("Claimed Request", ColHeader);

        if (claimedAcquisitionRequest == null)
        {
            ImGui.TextColored(ColMuted, "No request is claimed by this plugin session.");
            return;
        }

        if (ImGui.BeginTable("MarketAcquisitionClaimedRequest", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            DrawClaimedRequestRow("Status", claimedAcquisitionRequest.Status);
            DrawClaimedRequestRow("Item", FormatAcquisitionItem(claimedAcquisitionRequest));
            DrawClaimedRequestRow("Quantity", $"{claimedAcquisitionRequest.QuantityMode} {claimedAcquisitionRequest.Quantity}");
            DrawClaimedRequestRow("HQ Policy", claimedAcquisitionRequest.HqPolicy);
            DrawClaimedRequestRow("Max Unit", FormatGil(claimedAcquisitionRequest.MaxUnitPrice));
            DrawClaimedRequestRow("Gil Cap", FormatGilCap(claimedAcquisitionRequest.MaxTotalGil));
            DrawClaimedRequestRow("World Mode", claimedAcquisitionRequest.WorldMode);
            ImGui.EndTable();
        }

        var canMutateClaim = !acquisitionRequestBusy &&
                             string.Equals(claimedAcquisitionRequest.Status, "Claimed", StringComparison.OrdinalIgnoreCase);
        if (ImGuiUi.Button("Accept Locally", canMutateClaim))
            _ = AcceptClaimedAcquisitionRequestAsync();

        ImGui.SameLine();
        if (ImGuiUi.Button("Reject", canMutateClaim))
            _ = RejectClaimedAcquisitionRequestAsync();

        ImGui.SameLine();
        if (ImGuiUi.Button("Forget Local", !acquisitionRequestBusy))
            ForgetLocalAcquisitionRequest();

        ImGui.SameLine();
        var canPrepare = !acquisitionRequestBusy &&
                         string.Equals(claimedAcquisitionRequest.Status, "AcceptedInPlugin", StringComparison.OrdinalIgnoreCase);
        if (ImGuiUi.Button("Prepare Plan", canPrepare))
            _ = PrepareMarketAcquisitionPlanAsync();

        ImGui.TextColored(ColMuted, "Preparing a plan only reads remote market data. Game UI automation and purchases are not implemented.");
    }

    private void DrawMarketAcquisitionPlan()
    {
        ImGuiUi.SectionHeader("Dry-Run Plan", ColHeader);

        if (acquisitionPlan == null)
        {
            ImGui.TextColored(ColMuted, "No market plan prepared.");
            return;
        }

        ImGui.TextColored(
            acquisitionPlan.Status == "Ready" ? ColSuccess : ColMuted,
            $"Status: {acquisitionPlan.Status}  -  Mode: {FormatWorldMode(acquisitionPlan.WorldMode)}  -  Planned {acquisitionPlan.PlannedQuantity:N0}/{acquisitionPlan.RequestedQuantity:N0} item(s), {FormatGil(acquisitionPlan.PlannedGil)}");

        if (acquisitionPlan.WorldBatches.Count == 0)
            return;

        var tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable;
        if (ImGui.BeginTable("MarketAcquisitionPlanBatches", 6, tableFlags))
        {
            ImGui.TableSetupColumn("World");
            ImGui.TableSetupColumn("Qty");
            ImGui.TableSetupColumn("Gil");
            ImGui.TableSetupColumn("Unit");
            ImGui.TableSetupColumn("HQ");
            ImGui.TableSetupColumn("Listing");
            ImGui.TableHeadersRow();

            foreach (var batch in acquisitionPlan.WorldBatches)
            {
                foreach (var listing in batch.Listings)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(batch.WorldName);
                    if (batch.ExceedsRequestedQuantity)
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(ColMuted, "(over)");
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(listing.Quantity.ToString("N0"));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(FormatGil(listing.TotalGil));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(FormatGil(listing.UnitPrice));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(listing.IsHq ? "HQ" : "NQ");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{listing.RetainerName} / {listing.ListingId}");
                }
            }

            ImGui.EndTable();
        }
    }

    private void DrawMarketBoardProbe()
    {
        ImGuiUi.SectionHeader("Live Market Board Probe", ColHeader);

        ImGui.TextColored(ColMuted, "Open market board search results for a planned item/world, then run the read-only probe.");

        var canProbe = !acquisitionRequestBusy &&
                       acquisitionPlan is { Status: "Ready" } &&
                       acquisitionPlan.WorldBatches.Count > 0;
        if (ImGuiUi.Button("Read Live Listings", canProbe))
            _ = ProbeLiveMarketBoardAsync();

        if (marketBoardReadResult == null)
        {
            ImGui.TextColored(ColMuted, "No live market board probe has run.");
            return;
        }

        ImGui.TextColored(
            marketBoardReadResult.Status == "Ready" ? ColSuccess : ColMuted,
            $"Read status: {marketBoardReadResult.Status}  -  {marketBoardReadResult.Message}");

        if (marketBoardReconciliation != null)
        {
            ImGui.TextColored(
                marketBoardReconciliation.Status == "Ready" ? ColSuccess : ColError,
                $"Reconciliation: {marketBoardReconciliation.Status}");
        }

        DrawLiveDryRunResult();

        if (ImGuiUi.Button("Open Diagnostics", marketBoardReadResult != null))
            AcquisitionDiagnostics.IsOpen = true;
    }

    private void MonitorGuidedRoute()
    {
        if (acquisitionRequestBusy || guidedRouteProbeRunning)
            return;

        var route = marketAcquisitionRouteRunner;
        if (!route.IsRunning)
            return;

        var now = DateTimeOffset.UtcNow;
        if (now < nextGuidedRouteMonitorUtc)
            return;

        nextGuidedRouteMonitorUtc = now.AddSeconds(2);

        try
        {
            var activeStop = route.ActiveStop;
            if (activeStop == null)
                return;

            if (string.Equals(activeStop.Status, "Pending", StringComparison.OrdinalIgnoreCase))
            {
                route.PreparePendingStopForCurrentWorld(
                    playerState.CurrentWorld.IsValid,
                    playerState.CurrentWorld.IsValid ? GetCurrentWorldName() : null,
                    Plugin.CommandManager.ProcessCommand);
                nextGuidedRouteMonitorUtc = DateTimeOffset.UtcNow.AddSeconds(2);
                return;
            }

            if (!playerState.CurrentWorld.IsValid)
            {
                route.RecordCurrentWorldUnavailable();
                return;
            }

            var currentWorld = GetCurrentWorldName();
            if (!activeStop.WorldName.Equals(currentWorld, StringComparison.OrdinalIgnoreCase))
            {
                route.RecordCurrentWorld(currentWorld);
                return;
            }

            if (string.Equals(activeStop.Status, "TravelCommandSent", StringComparison.OrdinalIgnoreCase))
            {
                route.RecordCurrentWorld(currentWorld);
            }

            if (route.ActiveStop?.Status == "Arrived")
            {
                var claimed = claimedAcquisitionRequest ??
                              throw new InvalidOperationException("No dashboard request is accepted.");
                if (!route.SearchSubmitted)
                {
                    var approachResult = marketBoardApproachService.OpenOrApproach();
                    route.RecordMarketBoardApproach(approachResult);
                    if (approachResult.MarketBoardTravelNeeded)
                    {
                        route.ExecuteMarketBoardTravelCommand(Plugin.CommandManager.ProcessCommand);
                        nextGuidedRouteMonitorUtc = DateTimeOffset.UtcNow.AddSeconds(2);
                        return;
                    }

                    if (!approachResult.ReadyToSearch)
                    {
                        nextGuidedRouteMonitorUtc = DateTimeOffset.UtcNow.AddSeconds(1);
                        return;
                    }

                    var searchResult = marketBoardItemSearchDriver.Search(claimed.ItemId, claimed.ItemName);
                    route.RecordSearchResult(searchResult);
                    if (!searchResult.SearchSent)
                        return;

                    nextGuidedRouteMonitorUtc = DateTimeOffset.UtcNow.AddSeconds(1);
                    return;
                }

                route.BeginProbe($"Arrived on {currentWorld}. Reading live listings for {FormatAcquisitionItem(claimed)}.");
                guidedRouteProbeRunning = true;
                _ = ProbeGuidedRouteMarketBoardAsync();
            }
        }
        catch (Exception ex)
        {
            route.FailRoute($"Unable to monitor guided route. {ex.Message}", ex);
            log.Warning(ex, "[MarketMafioso] Unable to monitor guided market acquisition route.");
        }
    }

    private async Task ProbeGuidedRouteMarketBoardAsync()
    {
        try
        {
            await ProbeLiveMarketBoardAsync().ConfigureAwait(false);

            var activeStop = marketAcquisitionRouteRunner.ActiveStop;
            if (activeStop is { Status: "Arrived" } &&
                !string.Equals(marketBoardReadResult?.Status, "Ready", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(marketBoardReadResult?.Status, "NoSearchItem", StringComparison.OrdinalIgnoreCase))
                    marketAcquisitionRouteRunner.ClearSearchSubmission("Market board results did not expose a searched item id.");

                marketAcquisitionRouteRunner.BeginProbe(
                    $"Arrived on {activeStop.WorldName}; waiting for live listings. {marketBoardReadResult?.Message ?? "Market board read has not completed."}");
            }
        }
        finally
        {
            if (marketAcquisitionRouteRunner.ActiveStop?.Status != "Arrived")
                marketAcquisitionRouteRunner.ClearSearchSubmission("Route advanced or stopped before the next live listing read.");

            guidedRouteProbeRunning = false;
            nextGuidedRouteMonitorUtc = DateTimeOffset.UtcNow.AddSeconds(2);
        }
    }

    private void DrawLiveDryRunResult()
    {
        if (marketAcquisitionLiveDryRun == null)
            return;

        ImGui.Spacing();
        ImGui.TextColored(
            marketAcquisitionLiveDryRun.Status == "Ready" ? ColSuccess : ColHeader,
            $"Live dry-run: {marketAcquisitionLiveDryRun.Status}  -  Would buy {marketAcquisitionLiveDryRun.WouldBuyQuantity:N0}/{marketAcquisitionLiveDryRun.RequestedQuantity:N0}, spend {FormatGil(marketAcquisitionLiveDryRun.WouldSpendGil)}");
        ImGui.TextWrapped(marketAcquisitionLiveDryRun.Message);

        var summary = MarketAcquisitionLiveDryRunPresenter.BuildSummary(marketAcquisitionLiveDryRun);
        ImGui.TextColored(ColMuted, $"{summary.WouldBuyRows:N0} buy row(s), {summary.SkippedRows:N0} skipped row(s).");
    }

    private static void DrawClaimedRequestRow(string label, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextColored(ColMuted, label);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(value);
    }

    private async Task FetchDashboardRequestsAsync()
    {
        await RunAcquisitionRequestAsync(async token =>
        {
            if (!TryGetAcquisitionScope(out var characterName, out var world))
                throw new InvalidOperationException("Character scope is unavailable.");

            pendingAcquisitionRequests = await acquisitionClient.FetchPendingAsync(
                config.ServerUrl,
                config.ApiKey,
                characterName,
                world,
                token).ConfigureAwait(false);

            acquisitionStatus = pendingAcquisitionRequests.Count == 0
                ? "No matching dashboard requests."
                : $"Loaded {pendingAcquisitionRequests.Count} dashboard request(s).";
        }).ConfigureAwait(false);
    }

    private async Task ClaimAcquisitionRequestAsync(string requestId)
    {
        await RunAcquisitionRequestAsync(async token =>
        {
            if (!TryGetAcquisitionScope(out var characterName, out var world))
                throw new InvalidOperationException("Character scope is unavailable.");

            claimedAcquisitionRequest = await acquisitionClient.ClaimAsync(
                config.ServerUrl,
                config.ApiKey,
                requestId,
                characterName,
                world,
                config.PluginInstanceId,
                token).ConfigureAwait(false);

            claimedAcceptIdempotencyKey = Guid.NewGuid().ToString("N");
            claimedRejectIdempotencyKey = Guid.NewGuid().ToString("N");
            MarketAcquisitionClaimPersistence.Save(
                config,
                claimedAcquisitionRequest,
                claimedAcceptIdempotencyKey,
                claimedRejectIdempotencyKey);
            config.Save();
            acquisitionPlan = null;
            marketBoardReadResult = null;
            marketBoardReconciliation = null;
            marketAcquisitionLiveDryRun = null;
            ResetGuidedRoute("No guided route has started.");
            pendingAcquisitionRequests = pendingAcquisitionRequests
                .Where(request => !string.Equals(request.Id, requestId, StringComparison.Ordinal))
                .ToList();
            acquisitionStatus = "Dashboard request claimed. Review it before accepting.";
        }).ConfigureAwait(false);
    }

    private async Task AcceptClaimedAcquisitionRequestAsync()
    {
        await RunAcquisitionRequestAsync(async token =>
        {
            var claimed = claimedAcquisitionRequest ??
                          throw new InvalidOperationException("No dashboard request is claimed.");
            claimedAcceptIdempotencyKey ??= Guid.NewGuid().ToString("N");

            var accepted = await acquisitionClient.AcceptAsync(
                config.ServerUrl,
                config.ApiKey,
                claimed.Id,
                claimed.ClaimToken,
                claimedAcceptIdempotencyKey,
                token).ConfigureAwait(false);

            claimedAcquisitionRequest = claimed with { Status = accepted.Status };
            MarketAcquisitionClaimPersistence.Save(
                config,
                claimedAcquisitionRequest,
                claimedAcceptIdempotencyKey,
                claimedRejectIdempotencyKey);
            config.Save();
            acquisitionPlan = null;
            marketBoardReadResult = null;
            marketBoardReconciliation = null;
            marketAcquisitionLiveDryRun = null;
            ResetGuidedRoute("No guided route has started.");
            acquisitionStatus = "Request accepted locally. Prepare a dry-run plan when ready.";
        }).ConfigureAwait(false);
    }

    private async Task RejectClaimedAcquisitionRequestAsync()
    {
        await RunAcquisitionRequestAsync(async token =>
        {
            var claimed = claimedAcquisitionRequest ??
                          throw new InvalidOperationException("No dashboard request is claimed.");
            claimedRejectIdempotencyKey ??= Guid.NewGuid().ToString("N");

            var rejected = await acquisitionClient.RejectAsync(
                config.ServerUrl,
                config.ApiKey,
                claimed.Id,
                claimed.ClaimToken,
                claimedRejectIdempotencyKey,
                "Rejected in the MarketMafioso plugin.",
                token).ConfigureAwait(false);

            claimedAcquisitionRequest = claimed with { Status = rejected.Status };
            MarketAcquisitionClaimPersistence.Clear(config);
            config.Save();
            claimedAcquisitionRequest = null;
            acquisitionPlan = null;
            marketBoardReadResult = null;
            marketBoardReconciliation = null;
            marketAcquisitionLiveDryRun = null;
            ResetGuidedRoute("No guided route has started.");
            acquisitionStatus = "Request rejected.";
        }).ConfigureAwait(false);
    }

    private void ForgetLocalAcquisitionRequest()
    {
        MarketAcquisitionClaimPersistence.Clear(config);
        config.Save();
        claimedAcquisitionRequest = null;
        claimedAcceptIdempotencyKey = null;
        claimedRejectIdempotencyKey = null;
        acquisitionPlan = null;
        marketBoardReadResult = null;
        marketBoardReconciliation = null;
        marketAcquisitionLiveDryRun = null;
        ResetGuidedRoute("No route has started.");
        acquisitionStatus = "Forgot local acquisition claim. Fetch dashboard requests to pick up a pending request.";
    }

    private async Task PrepareMarketAcquisitionPlanAsync()
    {
        await RunAcquisitionRequestAsync(async token =>
        {
            var claimed = claimedAcquisitionRequest ??
                          throw new InvalidOperationException("No dashboard request is accepted.");
            if (string.Equals(claimed.WorldMode, "Selected", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Selected world mode cannot be planned until selected worlds are carried in the dashboard request payload.");

            var listings = await acquisitionPlanSource.FetchListingsAsync(
                claimed.Region,
                claimed.ItemId,
                100,
                token).ConfigureAwait(false);
            acquisitionPlan = MarketAcquisitionPlanner.BuildPlan(claimed, listings, DateTimeOffset.UtcNow);
            marketBoardReadResult = null;
            marketBoardReconciliation = null;
            marketAcquisitionLiveDryRun = null;
            ResetGuidedRoute("No route has started.");
            acquisitionStatus = acquisitionPlan.Status == "Ready"
                ? $"Prepared {acquisitionPlan.WorldBatches.Count} world batch(es)."
                : "No supported listings found under the configured thresholds.";
        }).ConfigureAwait(false);
    }

    private Task ProbeLiveMarketBoardAsync()
    {
        return RunAcquisitionRequestAsync(_ =>
        {
            var plan = acquisitionPlan ??
                       throw new InvalidOperationException("Prepare a dry-run plan before probing live market board listings.");
            var claimed = claimedAcquisitionRequest ??
                          throw new InvalidOperationException("No dashboard request is accepted.");
            var currentWorld = GetCurrentWorldName();
            marketBoardReconciliation = null;
            marketAcquisitionLiveDryRun = null;
            marketBoardReadResult = marketBoardListingReader.ReadCurrentListings(currentWorld);

            marketBoardReconciliation = marketBoardReadResult.Status == "Ready"
                ? MarketBoardListingReconciler.Reconcile(
                    plan,
                    currentWorld,
                    marketBoardReadResult.ItemId,
                    marketBoardReadResult.Listings)
                : null;
            marketAcquisitionLiveDryRun = marketBoardReadResult.Status == "Ready"
                ? MarketAcquisitionLiveDryRunPlanner.BuildDryRun(
                    claimed,
                    plan,
                    currentWorld,
                    marketBoardReadResult.ItemId,
                    marketBoardReadResult.Listings)
                : null;
            var guidedRouteResult = marketAcquisitionRouteRunner.IsRunning &&
                                    marketAcquisitionRouteRunner.ActiveStop is { Status: "Arrived" } &&
                                    marketAcquisitionLiveDryRun != null
                ? marketAcquisitionRouteRunner.RecordProbe(currentWorld, marketAcquisitionLiveDryRun)
                : null;
            acquisitionStatus = marketBoardReconciliation == null
                ? marketBoardReadResult.Message
                : $"Live listing reconciliation {marketBoardReconciliation.Status}; dry-run {marketAcquisitionLiveDryRun?.Status ?? "Unavailable"}.";
            if (guidedRouteResult != null)
            {
                acquisitionStatus = $"{acquisitionStatus} Route: {guidedRouteResult.Message}";
            }

            return Task.CompletedTask;
        });
    }

    private void DrawMarketAcquisitionGuidedRoute()
    {
        ImGuiUi.SectionHeader("Guided World Route", ColHeader);

        var canStart = acquisitionPlan is { Status: "Ready" } &&
                       acquisitionPlan.WorldBatches.Count > 0 &&
                       !marketAcquisitionRouteRunner.IsRunning &&
                       !marketAcquisitionRouteRunner.IsPaused;
        if (ImGuiUi.Button("Start Route", canStart))
            StartGuidedRoute(enableDiagnostics: false);

        ImGui.SameLine();
        if (ImGuiUi.Button("Start With Diagnostics", canStart))
            StartGuidedRoute(enableDiagnostics: true);

        ImGui.SameLine();
        if (marketAcquisitionRouteRunner.IsPaused)
        {
            if (ImGuiUi.Button("Resume", true))
                marketAcquisitionRouteRunner.Resume();
        }
        else
        {
            if (ImGuiUi.Button("Pause", marketAcquisitionRouteRunner.IsRunning))
            {
                marketBoardApproachService.StopNavigation();
                marketAcquisitionRouteRunner.Pause();
            }
        }

        ImGui.SameLine();
        if (ImGuiUi.Button("Stop", marketAcquisitionRouteRunner.IsRunning || marketAcquisitionRouteRunner.IsPaused))
        {
            marketBoardApproachService.StopNavigation();
            marketAcquisitionRouteRunner.Stop();
        }

        ImGui.SameLine();
        if (ImGuiUi.Button("Restart", canStart && marketAcquisitionRouteRunner.CanRestart))
            RestartGuidedRoute();

        ImGui.TextColored(GetGuidedRouteStatusColor(), marketAcquisitionRouteRunner.StatusMessage);

        if (marketAcquisitionRouteRunner.Stops.Count == 0)
        {
            ImGui.TextColored(ColMuted, "Start after preparing a plan. Routes guide travel and read-only probes; they do not purchase yet.");
            return;
        }

        if (marketAcquisitionRouteRunner.LastDiagnosticFilePath != null)
            ImGui.TextColored(ColMuted, $"Diagnostics: {marketAcquisitionRouteRunner.LastDiagnosticFilePath}");

        var activeStop = marketAcquisitionRouteRunner.ActiveStop;
        if (activeStop == null)
        {
            ImGui.TextColored(ColSuccess, "Route is not actively executing.");
        }
        else
        {
            ImGui.TextColored(ColHeader, $"Next stop: {activeStop.WorldName}");
            ImGui.SameLine();
            ImGui.TextColored(ColMuted, $"Planned {activeStop.PlannedQuantity:N0} item(s), {FormatGil(activeStop.PlannedGil)}");
        }

        DrawGuidedRouteStops(marketAcquisitionRouteRunner.Stops);
    }

    private void DrawGuidedRouteStops(IReadOnlyList<MarketAcquisitionGuidedRouteStop> stops)
    {
        if (ImGui.BeginTable("MarketAcquisitionGuidedRouteStops", 5, ImGuiUi.InteractiveTableFlags))
        {
            ImGui.TableSetupColumn("World");
            ImGui.TableSetupColumn("Status");
            ImGui.TableSetupColumn("Planned");
            ImGui.TableSetupColumn("Live");
            ImGui.TableSetupColumn("Dry-run");
            ImGui.TableHeadersRow();

            foreach (var stop in stops)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(stop.WorldName);
                ImGui.TableNextColumn();
                ImGui.TextColored(GetGuidedRouteStopColor(stop), stop.Status);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{stop.PlannedQuantity:N0} / {FormatGil(stop.PlannedGil)}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(stop.WouldBuyQuantity == 0
                    ? "-"
                    : $"{stop.WouldBuyQuantity:N0} / {FormatGil(stop.WouldSpendGil)}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(stop.DryRunStatus ?? "-");
            }

            ImGui.EndTable();
        }
    }

    private void StartGuidedRoute(bool enableDiagnostics)
    {
        try
        {
            var plan = acquisitionPlan ??
                       throw new InvalidOperationException("Prepare a plan before starting a guided route.");
            marketAcquisitionRouteRunner.Start(plan, enableDiagnostics);
            marketBoardReadResult = null;
            marketBoardReconciliation = null;
            marketAcquisitionLiveDryRun = null;
            guidedRouteProbeRunning = false;
            nextGuidedRouteMonitorUtc = DateTimeOffset.MinValue;
        }
        catch (Exception ex)
        {
            marketAcquisitionRouteRunner.FailRoute($"Unable to start guided route. {ex.Message}", ex);
            log.Warning(ex, "[MarketMafioso] Unable to start guided market acquisition route.");
        }
    }

    private void RestartGuidedRoute()
    {
        try
        {
            var plan = acquisitionPlan ??
                       throw new InvalidOperationException("Prepare a plan before restarting a guided route.");
            marketBoardApproachService.StopNavigation();
            marketAcquisitionRouteRunner.Restart(plan);
            marketBoardReadResult = null;
            marketBoardReconciliation = null;
            marketAcquisitionLiveDryRun = null;
            guidedRouteProbeRunning = false;
            nextGuidedRouteMonitorUtc = DateTimeOffset.MinValue;
        }
        catch (Exception ex)
        {
            marketAcquisitionRouteRunner.FailRoute($"Unable to restart guided route. {ex.Message}", ex);
            log.Warning(ex, "[MarketMafioso] Unable to restart guided market acquisition route.");
        }
    }

    private void ResetGuidedRoute(string status)
    {
        marketBoardApproachService.StopNavigation();
        marketAcquisitionRouteRunner.Reset(status);
        guidedRouteProbeRunning = false;
        nextGuidedRouteMonitorUtc = DateTimeOffset.MinValue;
    }

    private Vector4 GetGuidedRouteStatusColor()
    {
        var status = marketAcquisitionRouteRunner.StatusMessage;
        if (status.StartsWith("Unable", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("Cannot", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("Failed", StringComparison.OrdinalIgnoreCase))
            return ColError;

        if (status.Contains("Waiting", StringComparison.OrdinalIgnoreCase) ||
            marketAcquisitionRouteRunner.IsPaused)
            return ColHeader;

        if (status.Contains("Arrived", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("Recorded", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("complete", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("started", StringComparison.OrdinalIgnoreCase))
            return ColSuccess;

        return ColMuted;
    }

    private static Vector4 GetGuidedRouteStopColor(MarketAcquisitionGuidedRouteStop stop) =>
        stop.Status switch
        {
            "Complete" => ColSuccess,
            "Arrived" => ColHeader,
            _ => ColMuted,
        };

    private async Task RunAcquisitionRequestAsync(Func<CancellationToken, Task> action)
    {
        if (acquisitionRequestBusy)
            return;

        acquisitionRequestBusy = true;
        acquisitionRequestCancellation?.Dispose();
        acquisitionRequestCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            await action(acquisitionRequestCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            acquisitionStatus = $"Request failed: {ex.Message}";
            log.Warning(ex, "[MarketMafioso] Market acquisition request action failed.");
        }
        finally
        {
            acquisitionRequestCancellation?.Dispose();
            acquisitionRequestCancellation = null;
            acquisitionRequestBusy = false;
        }
    }

    private bool TryGetAcquisitionScope(out string characterName, out string world)
    {
        characterName = playerState.CharacterName ?? string.Empty;
        world = playerState.HomeWorld.IsValid ? playerState.HomeWorld.Value.Name.ToString() : string.Empty;
        return !string.IsNullOrWhiteSpace(characterName) && !string.IsNullOrWhiteSpace(world);
    }

    private string GetCurrentWorldName()
    {
        if (!playerState.CurrentWorld.IsValid)
            throw new InvalidOperationException("Current world is unavailable.");

        return playerState.CurrentWorld.Value.Name.ToString();
    }

    private static string FormatAcquisitionItem(MarketAcquisitionRequestView request)
    {
        var name = string.IsNullOrWhiteSpace(request.ItemName)
            ? $"Item {request.ItemId}"
            : request.ItemName;
        return $"{name} ({request.ItemId})";
    }

    private static string FormatGil(uint gil) => $"{gil:N0} gil";

    private static string FormatGilCap(uint gil) => gil == 0 ? "No cap" : FormatGil(gil);

    private static string FormatWorldMode(string worldMode) =>
        worldMode switch
        {
            "AllWorldSweep" => "All-world sweep",
            "CurrentWorldOnly" => "Current world only",
            _ => worldMode,
        };

    private Vector4 GetAcquisitionStatusColor()
    {
        if (acquisitionRequestBusy)
            return ColHeader;

        if (acquisitionStatus.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            acquisitionStatus.Contains("required", StringComparison.OrdinalIgnoreCase) ||
            acquisitionStatus.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
            return ColError;

        if (acquisitionStatus.Contains("Loaded", StringComparison.OrdinalIgnoreCase) ||
            acquisitionStatus.Contains("claimed", StringComparison.OrdinalIgnoreCase) ||
            acquisitionStatus.Contains("accepted", StringComparison.OrdinalIgnoreCase))
            return ColSuccess;

        return ColMuted;
    }

    private void OpenExternalUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            acquisitionStatus = $"Unable to open dashboard: {ex.Message}";
            log.Warning(ex, "[MarketMafioso] Unable to open market acquisition dashboard.");
        }
    }

    private void DrawWorkshopPrepQueue(IReadOnlyList<WorkshopProjectDefinition> projects)
    {
        ImGuiUi.SectionHeaderWithActions("Prep Queue", ColHeader, DrawWorkshopQueueHeaderActions, 180);
        DrawFrozenQueueToolbar();
        ImGui.Spacing();

        if (projects.Count == 0)
        {
            ImGui.TextColored(ColMuted, "No company workshop projects were found.");
            return;
        }

        DrawWorkshopQueueTable(projects);
    }

    private void AddWorkshopProject(uint workshopItemId)
    {
        if (workshopAssemblyRunner.HasActiveRun)
        {
            workshopStatus = "Cannot edit prep queue while workshop assembly is active.";
            return;
        }

        var existing = config.WorkshopPrepQueue.FirstOrDefault(x => x.WorkshopItemId == workshopItemId);
        var quantity = Math.Max(1, workshopProjectSelection.Quantity);
        if (existing != null)
        {
            existing.Quantity += quantity;
        }
        else
        {
            config.WorkshopPrepQueue.Add(new WorkshopPrepQueueItem
            {
                WorkshopItemId = workshopItemId,
                Quantity = quantity,
            });
        }

        SaveActiveQueueEdit();
        workshopStatus = "Added project to workshop prep queue.";
    }

    private void DrawWorkshopQueueHeaderActions()
    {
        var hasPrepQueue = config.WorkshopPrepQueue.Count > 0;
        var canEditQueue = !workshopAssemblyRunner.HasActiveRun;

        if (ImGuiUi.MenuButton("Handoff"))
            ImGui.OpenPopup("WorkshopQueueHandoffMenu");

        if (ImGui.BeginPopup("WorkshopQueueHandoffMenu"))
        {
            if (ImGuiUi.MenuItem("Send to VIWI", hasPrepQueue && canEditQueue))
                confirmViwiClear = true;

            ImGui.EndPopup();
        }

        ImGui.SameLine();
        if (ImGuiUi.MenuButton("Export"))
            ImGui.OpenPopup("WorkshopQueueExportMenu");

        if (ImGui.BeginPopup("WorkshopQueueExportMenu"))
        {
            if (ImGuiUi.MenuItem("Copy Artisan Manifest", hasPrepQueue))
                CopyWorkshopArtisanManifest();

            if (ImGuiUi.MenuItem("Copy Craft Architect Plan", hasPrepQueue))
                CopyWorkshopCraftArchitectPlan();

            ImGui.EndPopup();
        }
    }

    private void DrawFrozenQueueToolbar()
    {
        var canEditQueue = !workshopAssemblyRunner.HasActiveRun;
        var activeFrozenQueue = config.ActiveFrozenWorkshopQueueId == null
            ? null
            : config.FrozenWorkshopQueues.FirstOrDefault(x => x.Id == config.ActiveFrozenWorkshopQueueId.Value);

        var activeFrozenQueueLabel = activeFrozenQueue == null
            ? "Active queue: unsaved"
            : WorkshopQueueService.ActiveQueueMatchesFrozenQueue(config)
                ? $"Active saved job: {activeFrozenQueue.Name}"
                : $"Active saved job: {activeFrozenQueue.Name} (modified)";
        ImGui.TextColored(ColMuted, activeFrozenQueueLabel);

        var commandWidth = 720f;
        var nameWidth = Math.Max(220f, ImGui.GetContentRegionAvail().X - commandWidth);
        ImGui.SetNextItemWidth(nameWidth);
        ImGui.InputText("##workshopFrozenQueueName", ref frozenQueueNameInput, 128);

        ImGui.SameLine();
        if (ImGuiUi.Button("Save Queue", canEditQueue && config.WorkshopPrepQueue.Count > 0))
        {
            var createsFrozenQueue = config.ActiveFrozenWorkshopQueueId == null;
            ApplyFrozenQueueResult(
                WorkshopQueueService.SaveActiveQueue(config, frozenQueueNameInput, DateTime.UtcNow),
                clearName: createsFrozenQueue);
        }

        ImGui.SameLine();
        if (ImGuiUi.Button("Save As...", canEditQueue && config.WorkshopPrepQueue.Count > 0))
            ApplyFrozenQueueResult(WorkshopQueueService.FreezeCurrentQueue(config, frozenQueueNameInput, DateTime.UtcNow), clearName: true);

        ImGui.SameLine();
        if (ImGuiUi.Button("New Queue", canEditQueue))
        {
            if (config.WorkshopPrepQueue.Count > 0)
                confirmNewWorkshopQueue = true;
            else
                StartNewWorkshopQueue();
        }

        ImGui.SameLine();
        if (ImGuiUi.Button("Add Project...", canEditQueue))
            ProjectBrowser.IsOpen = true;

        ImGui.SameLine();
        DrawFrozenQueueLoadCombo(canEditQueue);

        ImGui.SameLine();
        if (ImGui.Button("Manage Saved Jobs"))
            FrozenQueueBrowser.IsOpen = true;

        ImGui.TextColored(ColMuted, "Handoff contains VIWI and future queue targets. Export contains Artisan JSON and Craft Architect .craftplan JSON.");

        DrawFrozenQueueConfirmations(canEditQueue);
    }

    private void DrawFrozenQueueLoadCombo(bool canEditQueue)
    {
        var canLoad = canEditQueue && config.FrozenWorkshopQueues.Count > 0;
        if (!canLoad)
            ImGui.BeginDisabled();

        var preview = selectedFrozenQueueId is { } id
            ? config.FrozenWorkshopQueues.FirstOrDefault(x => x.Id == id)?.Name ?? "Load saved job..."
            : "Load saved job...";
        ImGui.SetNextItemWidth(220);
        if (ImGui.BeginCombo("##workshopFrozenQueueLoad", preview))
        {
            foreach (var frozenQueue in config.FrozenWorkshopQueues.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                var isSelected = selectedFrozenQueueId == frozenQueue.Id;
                if (ImGui.Selectable($"{frozenQueue.Name} ({frozenQueue.Items.Sum(x => x.Quantity)})##load{frozenQueue.Id}", isSelected))
                {
                    selectedFrozenQueueId = frozenQueue.Id;
                    RequestLoadFrozenQueue(frozenQueue.Id);
                }
            }

            ImGui.EndCombo();
        }

        if (!canLoad)
            ImGui.EndDisabled();
    }

    private void DrawFrozenQueueConfirmations(bool canEditQueue)
    {
        if (confirmNewWorkshopQueue)
        {
            ImGui.TextColored(ColMuted, "Start a new queue? Unsaved active queue changes will be discarded.");
            if (ImGuiUi.Button("Confirm New Queue", canEditQueue))
                StartNewWorkshopQueue();

            ImGui.SameLine();
            if (ImGui.Button("Cancel New Queue"))
                confirmNewWorkshopQueue = false;
        }

        if (confirmLoadFrozenQueue)
        {
            ImGui.TextColored(ColMuted, "Load saved job? Unsaved active queue changes will be discarded.");
            if (ImGuiUi.Button("Confirm Load Saved Job", canEditQueue && selectedFrozenQueueId != null))
                LoadSelectedFrozenQueue();

            ImGui.SameLine();
            if (ImGui.Button("Cancel Load Saved Job"))
                confirmLoadFrozenQueue = false;
        }

    }

    private void RequestLoadFrozenQueue(Guid queueId)
    {
        selectedFrozenQueueId = queueId;
        if (config.WorkshopPrepQueue.Count > 0 && config.ActiveFrozenWorkshopQueueId != queueId)
        {
            confirmLoadFrozenQueue = true;
            return;
        }

        LoadSelectedFrozenQueue();
    }

    private void LoadSelectedFrozenQueue()
    {
        if (selectedFrozenQueueId == null)
            return;

        LoadFrozenQueue(selectedFrozenQueueId.Value);
        confirmLoadFrozenQueue = false;
    }

    private void LoadFrozenQueue(Guid queueId)
    {
        selectedFrozenQueueId = queueId;
        ApplyFrozenQueueResult(WorkshopQueueService.LoadFrozenQueue(config, queueId));
    }

    private void DeleteFrozenQueue(Guid queueId)
    {
        var result = WorkshopQueueService.DeleteFrozenQueue(config, queueId);
        if (result.Success)
            selectedFrozenQueueId = config.FrozenWorkshopQueues.FirstOrDefault()?.Id;

        ApplyFrozenQueueResult(result);
    }

    private void OverwriteFrozenQueueWithCurrent(Guid queueId)
    {
        selectedFrozenQueueId = queueId;
        ApplyFrozenQueueResult(WorkshopQueueService.OverwriteFrozenQueue(config, queueId, DateTime.UtcNow));
    }

    private void RenameFrozenQueue(Guid queueId, string name)
    {
        selectedFrozenQueueId = queueId;
        ApplyFrozenQueueResult(WorkshopQueueService.RenameFrozenQueue(config, queueId, name, DateTime.UtcNow));
    }

    private void DuplicateFrozenQueue(Guid queueId, string name)
    {
        selectedFrozenQueueId = queueId;
        ApplyFrozenQueueResult(WorkshopQueueService.DuplicateFrozenQueue(config, queueId, name, DateTime.UtcNow));
    }

    private void SaveCurrentQueueAsNew(string name)
    {
        ApplyFrozenQueueResult(WorkshopQueueService.FreezeCurrentQueue(config, name, DateTime.UtcNow), clearName: false);
    }

    private void StartNewWorkshopQueue()
    {
        WorkshopQueueService.NewActiveQueue(config);
        config.Save();
        confirmNewWorkshopQueue = false;
        workshopStatus = "Started a new workshop prep queue.";
    }

    private void ApplyFrozenQueueResult(WorkshopQueueOperationResult result, bool clearName = false)
    {
        workshopStatus = result.Message;
        if (!result.Success)
            return;

        if (result.QueueId != null)
            selectedFrozenQueueId = result.QueueId;

        if (clearName)
            frozenQueueNameInput = string.Empty;

        config.Save();
    }

    private void DrawWorkshopQueueTable(IReadOnlyList<WorkshopProjectDefinition> projects)
    {
        var projectNames = projects.ToDictionary(x => x.WorkshopItemId, x => x.Name);
        var canEditQueue = !workshopAssemblyRunner.HasActiveRun;
        if (ImGui.BeginTable("WorkshopPrepQueue", 3, ImGuiUi.InteractiveTableFlags))
        {
            ImGui.TableSetupColumn("Project", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 96);
            ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 104);
            ImGui.TableHeadersRow();

            if (config.WorkshopPrepQueue.Count == 0)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(ColMuted, "No workshop projects queued.");
                ImGui.TableNextColumn();
                ImGui.TextColored(ColMuted, "-");
                ImGui.TableNextColumn();
                if (ImGuiUi.Button("Add##workshopQueueEmptyAdd", canEditQueue))
                    ProjectBrowser.IsOpen = true;
            }

            for (var index = 0; index < config.WorkshopPrepQueue.Count; index++)
            {
                var item = config.WorkshopPrepQueue[index];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(projectNames.TryGetValue(item.WorkshopItemId, out var name)
                    ? name
                    : $"Unknown project {item.WorkshopItemId}");

                ImGui.TableNextColumn();
                var quantity = item.Quantity;
                ImGui.SetNextItemWidth(80);
                if (!canEditQueue)
                    ImGui.BeginDisabled();

                if (ImGui.InputInt($"##workshopQueueQty{index}", ref quantity))
                {
                    item.Quantity = Math.Max(1, quantity);
                    SaveActiveQueueEdit();
                }

                if (!canEditQueue)
                    ImGui.EndDisabled();

                ImGui.TableNextColumn();
                if (ImGuiUi.Button($"Remove##workshopQueueRemove{index}", canEditQueue))
                {
                    config.WorkshopPrepQueue.RemoveAt(index);
                    SaveActiveQueueEdit();
                    workshopStatus = "Removed project from workshop prep queue.";
                    index--;
                }
            }

            ImGui.EndTable();
        }
    }

    private void DrawWorkshopMaterialSummary()
    {
        ImGuiUi.SectionHeaderWithActions("Materials", ColHeader, DrawWorkshopMaterialHeaderActions, 420);

        var availability = GetWorkshopAvailability();
        if (ImGui.BeginTable("WorkshopPrepMaterials", 7, ImGuiUi.InteractiveTableFlags))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Required", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Stock Differential", ImGuiTableColumnFlags.WidthFixed, 128);
            ImGui.TableSetupColumn("Inventory Missing", ImGuiTableColumnFlags.WidthFixed, 128);
            ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthFixed, 72);
            ImGui.TableSetupColumn("Retainers", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultHide, 88);
            ImGui.TableSetupColumn("Candidates", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultHide);
            ImGui.TableHeadersRow();

            if (availability.Count == 0)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(ColMuted, "No workshop materials yet. Add projects to the prep queue.");
                ImGui.TableNextColumn();
                ImGui.TextColored(ColMuted, "-");
                ImGui.TableNextColumn();
                ImGui.TextColored(ColMuted, "-");
                ImGui.TableNextColumn();
                ImGui.TextColored(ColMuted, "-");
                ImGui.TableNextColumn();
                ImGui.TextColored(ColMuted, "-");
                ImGui.TableNextColumn();
                ImGui.TextColored(ColMuted, "-");
                ImGui.TableNextColumn();
                ImGui.TextColored(ColMuted, "-");
            }

            foreach (var item in availability)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.ItemName);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.Required.ToString());
                ImGui.TableNextColumn();
                ImGui.TextColored(item.StockDifferential < 0 ? ColError : ColSuccess, FormatSignedQuantity(item.StockDifferential));
                ImGui.TableNextColumn();
                ImGui.TextColored(item.Shortage > 0 ? ColError : ColSuccess, item.Shortage.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.PlayerInventory.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.RetainerCache.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.Join(", ", item.CandidateRetainers.Select(x => x.RetainerName)));
            }

            ImGui.EndTable();
        }
    }

    private void DrawWorkshopMaterialHeaderActions()
    {
        var canRefreshRetainers = autoRetainerRefresh.CanStartRefresh &&
                                  !autoRetainerRefresh.IsRefreshing &&
                                  !autoRetainerRefresh.IsStartQueued;

        if (ImGuiUi.Button("Refresh Retainer Cache", canRefreshRetainers))
            autoRetainerRefresh.StartFullRefresh();

        ImGui.SameLine();
        if (ImGuiUi.Button("Restock From Retainers", !workshopRetainerRestock.IsRunning))
            _ = workshopRetainerRestock.StartAsync(GetWorkshopAvailability());

        ImGui.SameLine();
        if (ImGuiUi.MenuButton("Columns"))
            ImGui.OpenPopup("WorkshopMaterialColumnsMenu");

        if (ImGui.BeginPopup("WorkshopMaterialColumnsMenu"))
        {
            ImGui.TextColored(ColMuted, "Use table header context menu to hide columns.");
            ImGui.EndPopup();
        }
    }

    private void SaveActiveQueueEdit()
    {
        WorkshopQueueService.MarkActiveQueueEdited(config);
        config.Save();
    }

    private static string FormatSignedQuantity(int value)
    {
        return value > 0
            ? $"+{value}"
            : value.ToString();
    }

    private IReadOnlyList<WorkshopMaterialAvailability> GetWorkshopAvailability()
    {
        if (config.WorkshopPrepQueue.Count == 0)
            return [];

        var requirements = workshopCatalog.BuildRequirements(config.WorkshopPrepQueue);
        var playerInventory = scanner.CountPlayerInventory(config);
        return WorkshopMaterialAvailabilityService.BuildAvailability(requirements, playerInventory, config);
    }

    private void DrawWorkshopAssemblyWorkflow()
    {
        var hasPrepQueue = config.WorkshopPrepQueue.Count > 0;
        var actionWidth = workshopAssemblyRunner.HasActiveRun ? 280f : 140f;
        ImGuiUi.SectionHeaderWithActions(
            "Assembly Workflow",
            ColHeader,
            () => DrawWorkshopAssemblyActions(hasPrepQueue),
            actionWidth);

        ImGui.TextColored(GetWorkshopStatusColor(), workshopStatus);
        ImGui.TextColored(workshopRetainerRestock.IsRunning ? ColHeader : ColMuted, workshopRetainerRestock.LastStatus);

        var progress = workshopAssemblyRunner.Progress;
        ImGui.TextColored(workshopAssemblyRunner.HasActiveRun ? ColHeader : ColMuted, progress.Message);
        if (progress.TotalProjects > 0)
        {
            var completed = Math.Clamp(progress.CompletedProjects, 0, progress.TotalProjects);
            var fraction = completed / (float)progress.TotalProjects;
            ImGui.TextColored(ColMuted, $"Assembly progress: {completed}/{progress.TotalProjects}");
            ImGui.SameLine();
            ImGui.ProgressBar(fraction, new Vector2(210, 0), string.Empty);
        }

        DrawWorkshopQueueConfirmations();
    }

    private void DrawWorkshopAssemblyActions(bool hasPrepQueue)
    {
        if (workshopAssemblyRunner.IsPaused)
        {
            if (ImGui.Button("Resume"))
                workshopStatus = workshopAssemblyRunner.Resume().Message;

            ImGui.SameLine();
            if (ImGui.Button("Stop"))
            {
                workshopAssemblyRunner.Stop();
                workshopStatus = "Workshop assembly stopped.";
            }
        }
        else if (workshopAssemblyRunner.IsRunning)
        {
            if (ImGui.Button("Pause"))
                workshopStatus = workshopAssemblyRunner.Pause().Message;

            ImGui.SameLine();
            if (ImGui.Button("Stop"))
            {
                workshopAssemblyRunner.Stop();
                workshopStatus = "Workshop assembly stopped.";
            }
        }

        if (workshopAssemblyRunner.HasActiveRun)
            ImGui.SameLine();

        if (ImGuiUi.MenuButton("Start Options", !workshopAssemblyRunner.HasActiveRun && hasPrepQueue))
            ImGui.OpenPopup("WorkshopAssemblyStartMenu");

        if (ImGui.BeginPopup("WorkshopAssemblyStartMenu"))
        {
            if (ImGuiUi.MenuItem("Start Assembly", hasPrepQueue))
                StartWorkshopAssembly(enableDiagnostics: false);

            if (ImGuiUi.MenuItem("Start With Diagnostics", hasPrepQueue))
                StartWorkshopAssembly(enableDiagnostics: true);

            ImGui.EndPopup();
        }
    }

    private void DrawWorkshopQueueConfirmations()
    {
        var hasPrepQueue = config.WorkshopPrepQueue.Count > 0;
        var canEditQueue = !workshopAssemblyRunner.HasActiveRun;

        if (config.WorkshopPrepQueue.Count == 0)
            confirmViwiClear = false;

        if (!confirmViwiClear)
            return;

        ImGui.TextColored(ColMuted, "This will clear VIWI Workshoppa's queue and send the MarketMafioso prep queue.");

        if (ImGuiUi.Button("Confirm VIWI Queue Sync", hasPrepQueue && canEditQueue))
        {
            var result = viwiWorkshoppaIpc.SendQueue(config.WorkshopPrepQueue, clearExisting: true);
            workshopStatus = result.Message;
            confirmViwiClear = false;
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel VIWI Queue Sync"))
            confirmViwiClear = false;
    }

    private void StartWorkshopAssembly(bool enableDiagnostics)
    {
        try
        {
            var preflight = WorkshopAssemblyPreflightService.Check(
                config.WorkshopPrepQueue,
                workshopCatalog.GetProjects(),
                scanner.CountPlayerInventory(config));
            if (!preflight.CanStart || preflight.Plan == null)
            {
                workshopStatus = preflight.Message;
                return;
            }

            var result = workshopAssemblyRunner.Start(preflight.Plan, enableDiagnostics);
            workshopStatus = result.Message;
            if (enableDiagnostics && workshopAssemblyRunner.LastDiagnosticFilePath != null)
                workshopStatus = $"{workshopStatus} Diagnostics: {workshopAssemblyRunner.LastDiagnosticFilePath}";
        }
        catch (Exception ex)
        {
            workshopStatus = $"Unable to start workshop assembly. {ex.Message}";
            log.Warning(ex, "[MarketMafioso] Native workshop assembly preflight failed.");
        }
    }

    private Vector4 GetWorkshopStatusColor()
    {
        if (workshopStatus.Contains("unable", StringComparison.OrdinalIgnoreCase) ||
            workshopStatus.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            workshopStatus.Contains("not available", StringComparison.OrdinalIgnoreCase))
            return ColError;

        if (workshopStatus.Contains("copied", StringComparison.OrdinalIgnoreCase) ||
            workshopStatus.Contains("sent", StringComparison.OrdinalIgnoreCase) ||
            workshopStatus.Contains("added", StringComparison.OrdinalIgnoreCase) ||
            workshopStatus.Contains("cleared", StringComparison.OrdinalIgnoreCase) ||
            workshopStatus.Contains("removed", StringComparison.OrdinalIgnoreCase))
            return ColSuccess;

        return ColMuted;
    }

    private void CopyWorkshopArtisanManifest()
    {
        CopyWorkshopManifest(workshopMaterialManifestExport.ExportArtisanManifest(
            config.WorkshopPrepQueue,
            workshopCatalog.GetProjects(),
            GetWorkshopAvailability(),
            WorkshopMaterialManifestQuantityMode.InventoryMissing,
            DateTime.UtcNow));
    }

    private void CopyWorkshopCraftArchitectPlan()
    {
        CopyWorkshopManifest(WorkshopMaterialManifestExportService.ExportCraftArchitectPlan(
            config.WorkshopPrepQueue,
            workshopCatalog.GetProjects(),
            GetWorkshopAvailability(),
            WorkshopMaterialManifestQuantityMode.InventoryMissing,
            DateTime.UtcNow));
    }

    private void CopyWorkshopManifest(WorkshopMaterialManifestExportResult result)
    {
        if (result.Success && !string.IsNullOrWhiteSpace(result.Content))
            ImGui.SetClipboardText(result.Content);

        workshopStatus = result.Message;
        if (result.Severity is WorkshopMaterialManifestExportSeverity.Error or WorkshopMaterialManifestExportSeverity.Warning)
            log.Warning($"[MarketMafioso] {result.Message}");
    }

    private void DrawStatusTab()
    {
        ImGui.Spacing();
        DrawStatusSection();
        ImGui.Spacing();
        DrawRetainerCacheSection();
    }

    private void DrawSettingsTab()
    {
        ImGui.Spacing();
        ImGui.TextColored(ColHeader, "Plugin Settings");
        ImGui.TextWrapped("Shared MarketMafioso client/server settings used by Inventory Reporter and Market Acquisition.");
        ImGui.Spacing();

        DrawServerSection();
    }

    private void DrawModuleSummary(string name, string state, string description)
    {
        ImGui.BulletText(name);
        ImGui.SameLine();
        ImGui.TextColored(state == "Enabled" ? ColSuccess : ColMuted, $"({state})");
        ImGui.TextWrapped(description);
        ImGui.Spacing();
    }

    private void DrawServerSection()
    {
        ImGui.TextColored(ColHeader, "Server Connection");
        ImGui.Separator();

        ImGui.Text("Server URL:");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##url", ref urlBuffer, 512))
        {
            config.ServerUrl = urlBuffer;
            config.Save();
        }

        if (ImGui.Button("Local Receiver"))
            ApplyServerUrlPreset(LocalReceiverUrl);
        ImGui.SameLine();
        if (ImGui.Button("Dev VPS"))
            ApplyServerUrlPreset(DevReceiverUrl);
        ImGui.SameLine();
        ImGui.BeginDisabled();
        ImGui.Button("Production VPS (future)");
        ImGui.EndDisabled();

        var endpoint = ReceiverEndpointClassifier.Classify(urlBuffer);
        var requiresApiKey = endpoint.RequiresApiKey;
        ImGui.Text(requiresApiKey
            ? "Client API Key (required for this endpoint):"
            : "Client API Key (optional - sent as X-Api-Key header):");
        var keyWidth = ImGui.GetContentRegionAvail().X - 70;
        ImGui.SetNextItemWidth(keyWidth);
        var flags = showApiKey ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password;
        if (ImGui.InputText("##apikey", ref apiKeyBuffer, 256, flags))
        {
            config.ApiKey = apiKeyBuffer;
            config.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button(showApiKey ? "Hide##k" : "Show##k", new Vector2(60, 0)))
            showApiKey = !showApiKey;

        if (endpoint.Kind == ReceiverEndpointKind.Invalid)
            ImGui.TextColored(ColError, "Enter a valid HTTP or HTTPS receiver URL.");
        else if (requiresApiKey && string.IsNullOrWhiteSpace(apiKeyBuffer))
            ImGui.TextColored(ColError, "This endpoint requires a client API key before plugin requests can be sent.");
        else if (endpoint.Kind == ReceiverEndpointKind.CustomRemote)
            ImGui.TextColored(ColMuted, "Custom remote endpoint. Client API key is required by default.");

        ImGui.Spacing();
        DrawDashboardOpenSection();
    }

    private void DrawDashboardOpenSection()
    {
        var dashboardUrl = HttpReporter.ResolveDashboardUrlForDisplay(reporter.LastDashboardUrl, urlBuffer) ?? string.Empty;
        if (!string.Equals(dashboardUrlBuffer, dashboardUrl, StringComparison.Ordinal))
            dashboardUrlBuffer = dashboardUrl;

        ImGui.Text("Dashboard URL:");
        var buttonWidth = 128f;
        var inputWidth = Math.Max(120f, ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X);
        ImGui.SetNextItemWidth(inputWidth);
        ImGui.InputText("##dashboardUrl", ref dashboardUrlBuffer, 1024, ImGuiInputTextFlags.ReadOnly);
        ImGui.SameLine();
        if (ImGuiUi.Button("Open Dashboard", new Vector2(buttonWidth, 0), !string.IsNullOrWhiteSpace(dashboardUrl)))
            OpenDashboardUrl(dashboardUrl);

        var status = string.IsNullOrWhiteSpace(dashboardUrl)
            ? dashboardOpenStatus
            : string.IsNullOrWhiteSpace(reporter.LastDashboardUrl)
                ? "Dashboard link derived from endpoint."
                : dashboardOpenStatus;
        ImGui.TextColored(GetDashboardOpenStatusColor(status), status);
    }

    private void OpenDashboardUrl(string dashboardUrl)
    {
        if (!Uri.TryCreate(dashboardUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            dashboardOpenStatus = "Dashboard URL is not a valid HTTP or HTTPS link.";
            log.Warning($"[MarketMafioso] Refusing to open invalid dashboard URL: {dashboardUrl}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.ToString())
            {
                UseShellExecute = true,
            });
            dashboardOpenStatus = "Opened dashboard in external browser.";
        }
        catch (Exception ex)
        {
            dashboardOpenStatus = $"Unable to open dashboard. {ex.Message}";
            log.Error(ex, "[MarketMafioso] Unable to open dashboard URL.");
        }
    }

    private static Vector4 GetDashboardOpenStatusColor(string status) =>
        status.StartsWith("Unable", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("not a valid", StringComparison.OrdinalIgnoreCase)
            ? ColError
            : ColMuted;

    private void ApplyServerUrlPreset(string serverUrl)
    {
        urlBuffer = serverUrl;
        config.ServerUrl = serverUrl;
        config.Save();
    }

    private void DrawInventoryOptionsSection()
    {
        ImGui.TextColored(ColHeader, "Included Data");
        ImGui.Separator();

        ImGui.TextColored(ColMuted, "Player inventory (4 bags) is always included.");
        ImGui.Spacing();

        DrawCheckbox("Armoury Chest", v => config.IncludeArmoury = v, config.IncludeArmoury);
        DrawCheckbox("Crystal bag", v => config.IncludeCrystals = v, config.IncludeCrystals);
        DrawCheckbox("Equipped gear", v => config.IncludeEquipped = v, config.IncludeEquipped);
        DrawCheckbox("Saddlebag (if subscribed)", v => config.IncludeSaddlebag = v, config.IncludeSaddlebag);
        ImGui.Spacing();
        DrawCheckbox("Resolve item names via Lumina", v => config.IncludeItemNames = v, config.IncludeItemNames);
        DrawCheckbox("Include character name & world", v => config.IncludeCharacterInfo = v, config.IncludeCharacterInfo);
    }

    private void DrawBehaviourSection()
    {
        ImGui.TextColored(ColHeader, "Automation");
        ImGui.Separator();

        DrawCheckbox("Auto-send on retainer window close", v => config.AutoSendOnRetainerClose = v, config.AutoSendOnRetainerClose);
        ImGui.TextColored(ColMuted,
            "  Retainer data is cached each time you close a retainer window.\n" +
            "  Visit each retainer once per session to populate the cache.");

        ImGui.Spacing();

        DrawCheckbox("Enable automatic periodic sending", v =>
        {
            config.EnableAutoSendTimer = v;
            Plugin.Instance.RestartTimer();
        }, config.EnableAutoSendTimer);

        if (config.EnableAutoSendTimer)
        {
            var interval = config.AutoSendIntervalMinutes;
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputInt("Send Interval (minutes)##interval", ref interval, 1, 5))
            {
                if (interval < 1) interval = 1;
                if (interval != config.AutoSendIntervalMinutes)
                {
                    config.AutoSendIntervalMinutes = interval;
                    config.Save();
                    Plugin.Instance.RestartTimer();
                }
            }
        }
    }

    private void DrawRetainerCacheSection()
    {
        ImGui.TextColored(ColHeader, "Retainer Cache");
        ImGui.Separator();

        if (config.RetainerCache.Count == 0)
        {
            ImGui.TextColored(ColMuted, "No retainers cached. Open a retainer inventory to populate.");
        }
        else
        {
            foreach (var (_, cached) in config.RetainerCache)
            {
                var total = cached.Bags.Sum(b => b.Items.Count);
                ImGui.BulletText(
                    $"{cached.RetainerName}  -  {total} items  (last seen {cached.LastUpdated:HH:mm:ss UTC})");
            }

            ImGui.Spacing();
            if (ImGui.Button("Clear Retainer Cache"))
            {
                config.RetainerCache.Clear();
                config.Save();
            }
        }
    }

    private void DrawActionsSection()
    {
        ImGui.TextColored(ColHeader, "Inventory Reporter Actions");
        ImGui.Separator();

        var third = (ImGui.GetContentRegionAvail().X - 2 * ImGui.GetStyle().ItemSpacing.X) / 3f;

        if (ImGui.Button("Send Report Now", new Vector2(third, 0)))
            _ = reporter.SendReportAsync();

        ImGui.SameLine();

        var canRefreshRetainers = autoRetainerRefresh.CanStartRefresh &&
                                  !autoRetainerRefresh.IsRefreshing &&
                                  !autoRetainerRefresh.IsStartQueued;
        if (!canRefreshRetainers)
            ImGui.BeginDisabled();

        if (ImGui.Button("Refresh Retainer Cache", new Vector2(third, 0)))
            autoRetainerRefresh.StartFullRefresh();

        if (!canRefreshRetainers)
            ImGui.EndDisabled();

        ImGui.SameLine();

        var previewLabel = showPreview ? "Hide JSON Preview" : "Show JSON Preview";
        if (ImGui.Button(previewLabel, new Vector2(third, 0)))
            showPreview = !showPreview;

        ImGui.Spacing();
        ImGui.TextColored(GetRefreshStatusColor(), autoRetainerRefresh.LastStatus);
    }

    private Vector4 GetRefreshStatusColor()
    {
        if (autoRetainerRefresh.IsRefreshing)
            return ColHeader;

        if (autoRetainerRefresh.LastStatus.Contains("complete", StringComparison.OrdinalIgnoreCase))
            return ColSuccess;

        if (autoRetainerRefresh.LastStatus.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            autoRetainerRefresh.LastStatus.Contains("unable", StringComparison.OrdinalIgnoreCase) ||
            autoRetainerRefresh.LastStatus.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return ColError;

        return ColMuted;
    }

    private void DrawStatusSection()
    {
        ImGui.TextColored(ColHeader, "Module Status");
        ImGui.Separator();
        ImGui.TextColored(ColMuted, $"Build: {PluginBuildInfo.DisplayVersion}");
        ImGui.Spacing();

        if (reporter.LastSentAt.HasValue)
        {
            var statusOk = reporter.LastStatus.StartsWith("2");
            ImGui.TextColored(
                statusOk ? ColSuccess : ColError,
                $"Last sent: {reporter.LastSentAt:HH:mm:ss}  -  Status: {reporter.LastStatus}");
        }
        else
        {
            ImGui.TextColored(ColMuted, $"Status: {reporter.LastStatus}");
        }
    }

    private void DrawJsonPreview()
    {
        ImGui.TextColored(ColHeader, "JSON Preview (last payload)");
        ImGui.Separator();

        var json = reporter.LastPayload ?? "(No payload yet - press 'Send Report Now' first)";
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextMultiline(
            "##jsonPreview",
            ref json,
            Math.Max(json.Length + 1, 8192),
            new Vector2(-1, 240),
            ImGuiInputTextFlags.ReadOnly,
            (ImGui.ImGuiInputTextCallbackDelegate?)null);
    }


    private void DrawCheckbox(string label, Action<bool> setter, bool currentValue)
    {
        var v = currentValue;
        if (ImGui.Checkbox(label, ref v))
        {
            setter(v);
            config.Save();
        }
    }

    public void Dispose()
    {
        acquisitionRequestCancellation?.Dispose();
        marketAcquisitionRouteRunner.Dispose();
        acquisitionHttpClient.Dispose();
    }
}
