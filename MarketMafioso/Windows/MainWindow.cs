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
    private readonly IPlayerState playerState;
    private readonly IPluginLog log;
    private readonly HttpClient acquisitionHttpClient = new();
    private readonly MarketAcquisitionRequestClient acquisitionClient;
    private readonly UniversalisMarketAcquisitionPlanSource acquisitionPlanSource;
    private readonly MarketBoardListingReader marketBoardListingReader;

    private string urlBuffer = string.Empty;
    private string apiKeyBuffer = string.Empty;
    private string commandPickupApiKeyBuffer = string.Empty;
    private bool showApiKey = false;
    private bool showCommandPickupApiKey = false;
    private bool showPreview = false;
    private readonly WorkshopProjectSelectionState workshopProjectSelection = new();
    private IReadOnlyList<MarketAcquisitionRequestView> pendingAcquisitionRequests = [];
    private MarketAcquisitionClaimView? claimedAcquisitionRequest;
    private string? claimedAcceptIdempotencyKey;
    private string? claimedRejectIdempotencyKey;
    private MarketAcquisitionPlan? acquisitionPlan;
    private MarketBoardReadResult? marketBoardReadResult;
    private MarketBoardListingReconciliation? marketBoardReconciliation;
    private bool acquisitionRequestBusy = false;
    private string acquisitionStatus = "No dashboard request has been fetched this session.";
    private CancellationTokenSource? acquisitionRequestCancellation;
    private bool confirmViwiClear = false;
    private string workshopStatus = "Workshop prep queue is idle.";

    private const string ProductSummary = "Small, practical FFXIV improvements under one roof.";
    private const string InventoryModuleSummary = "Inventory Reporter exports character and retainer inventory snapshots as JSON.";
    private const string WorkshopPrepModuleSummary = "Workshop Prep tracks company workshop projects and their direct material needs.";
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
        IPlayerState playerState,
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
        this.playerState = playerState;
        this.log = log;
        acquisitionClient = new MarketAcquisitionRequestClient(acquisitionHttpClient);
        acquisitionPlanSource = new UniversalisMarketAcquisitionPlanSource(acquisitionHttpClient);
        marketBoardListingReader = new MarketBoardListingReader(Plugin.GameGui);

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 520),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        urlBuffer = config.ServerUrl;
        apiKeyBuffer = config.ApiKey;
        commandPickupApiKeyBuffer = config.CommandPickupApiKey;
        ProjectBrowser = new WorkshopProjectBrowserWindow(
            config,
            workshopCatalog,
            workshopProjectSelection,
            AddWorkshopProject);
    }

    public WorkshopProjectBrowserWindow ProjectBrowser { get; }

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

            if (ImGui.BeginTabItem("Workshop Prep"))
            {
                DrawWorkshopPrepTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Market Acquisition"))
            {
                DrawMarketAcquisitionTab();
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
        ImGui.TextColored(ColMuted, "Current modules: Inventory Reporter, Workshop Prep, Market Acquisition");
    }

    private void DrawOverviewTab()
    {
        ImGui.Spacing();
        ImGui.TextColored(ColHeader, "Modules");
        ImGui.Separator();

        DrawModuleSummary("Inventory Reporter", "Enabled", InventoryModuleSummary);
        DrawModuleSummary("Workshop Prep", "Enabled", WorkshopPrepModuleSummary);
        DrawModuleSummary("Market Acquisition", "Foundation", MarketAcquisitionModuleSummary);
        DrawModuleSummary("General Improvements", "Planned", "Small quality-of-life tools that are useful, but too narrow for their own plugin.");
    }

    private void DrawInventoryReporterTab()
    {
        ImGui.Spacing();
        ImGui.TextColored(ColHeader, "Inventory Reporter");
        ImGui.TextWrapped(InventoryModuleSummary);
        ImGui.Spacing();

        DrawServerSection();
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
        ImGui.TextColored(ColHeader, "Workshop Prep");
        ImGui.TextWrapped(WorkshopPrepModuleSummary);
        ImGui.Spacing();

        var projects = workshopCatalog.GetProjects();

        DrawWorkshopPrepQueue(projects);
        ImGui.Spacing();
        DrawWorkshopMaterialSummary();
        ImGui.Spacing();
        DrawWorkshopPrepActions();
    }

    private void DrawMarketAcquisitionTab()
    {
        ImGui.Spacing();
        ImGui.TextColored(ColHeader, "Market Acquisition");
        ImGui.TextWrapped(MarketAcquisitionModuleSummary);
        ImGui.Spacing();

        DrawMarketAcquisitionEndpointSection();
        ImGui.Spacing();
        DrawMarketAcquisitionPickupSection();
        ImGui.Spacing();
        DrawClaimedAcquisitionRequest();
        ImGui.Spacing();
        DrawMarketAcquisitionPlan();
        ImGui.Spacing();
        DrawMarketBoardProbe();
    }

    private void DrawMarketAcquisitionEndpointSection()
    {
        ImGuiUi.SectionHeader("Dashboard Pickup", ColHeader);

        var dashboardUrl = ReceiverEndpointClassifier.BuildDashboardBaseUrl(config.ServerUrl) ?? string.Empty;
        ImGui.Text("Dashboard URL:");
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 140);
        ImGui.InputText("##marketAcquisitionDashboardUrl", ref dashboardUrl, 512, ImGuiInputTextFlags.ReadOnly);
        ImGui.SameLine();
        if (ImGuiUi.Button("Open Dashboard", new Vector2(130, 0), !string.IsNullOrWhiteSpace(dashboardUrl)))
            OpenExternalUrl(dashboardUrl);

        ImGui.Text("Command Pickup Key:");
        var keyWidth = ImGui.GetContentRegionAvail().X - 70;
        ImGui.SetNextItemWidth(keyWidth);
        var flags = showCommandPickupApiKey ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password;
        if (ImGui.InputText("##commandPickupApiKey", ref commandPickupApiKeyBuffer, 256, flags))
        {
            config.CommandPickupApiKey = commandPickupApiKeyBuffer;
            config.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button(showCommandPickupApiKey ? "Hide##commandPickupKey" : "Show##commandPickupKey", new Vector2(60, 0)))
            showCommandPickupApiKey = !showCommandPickupApiKey;

        if (string.IsNullOrWhiteSpace(commandPickupApiKeyBuffer))
            ImGui.TextColored(ColError, "Command pickup key is required to fetch dashboard requests.");
    }

    private void DrawMarketAcquisitionPickupSection()
    {
        ImGuiUi.SectionHeader("Request Pickup", ColHeader);

        if (TryGetAcquisitionScope(out var characterName, out var world))
            ImGui.TextColored(ColMuted, $"Character scope: {characterName} @ {world}");
        else
            ImGui.TextColored(ColError, "Character scope unavailable. Log into a character before fetching requests.");

        var canFetch = !acquisitionRequestBusy &&
                       !string.IsNullOrWhiteSpace(commandPickupApiKeyBuffer) &&
                       TryGetAcquisitionScope(out _, out _);
        if (ImGuiUi.Button("Fetch Dashboard Requests", canFetch))
            _ = FetchDashboardRequestsAsync();

        ImGui.SameLine();
        ImGui.TextColored(GetAcquisitionStatusColor(), acquisitionStatus);

        if (pendingAcquisitionRequests.Count == 0)
        {
            ImGui.TextColored(ColMuted, "No pending dashboard requests are loaded.");
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
            DrawClaimedRequestRow("Gil Cap", FormatGil(claimedAcquisitionRequest.MaxTotalGil));
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

            var tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable;
            if (ImGui.BeginTable("MarketBoardProbeReconciliation", 6, tableFlags))
            {
                ImGui.TableSetupColumn("Status");
                ImGui.TableSetupColumn("Retainer");
                ImGui.TableSetupColumn("Qty");
                ImGui.TableSetupColumn("Unit");
                ImGui.TableSetupColumn("HQ");
                ImGui.TableSetupColumn("Message");
                ImGui.TableHeadersRow();

                foreach (var row in marketBoardReconciliation.Listings)
                {
                    var listing = row.LiveListing;
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextColored(row.Status == "Matched" ? ColSuccess : ColError, row.Status);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(string.IsNullOrWhiteSpace(listing?.RetainerName)
                        ? row.PlannedListing.RetainerName
                        : listing.RetainerName);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted((listing?.Quantity ?? row.PlannedListing.Quantity).ToString("N0"));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(FormatGil(listing?.UnitPrice ?? row.PlannedListing.UnitPrice));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted((listing?.IsHq ?? row.PlannedListing.IsHq) ? "HQ" : "NQ");
                    ImGui.TableNextColumn();
                    ImGui.TextWrapped(row.Message);
                }

                ImGui.EndTable();
            }
        }
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
                config.CommandPickupApiKey,
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
                config.CommandPickupApiKey,
                requestId,
                characterName,
                world,
                config.PluginInstanceId,
                token).ConfigureAwait(false);

            claimedAcceptIdempotencyKey = Guid.NewGuid().ToString("N");
            claimedRejectIdempotencyKey = Guid.NewGuid().ToString("N");
            acquisitionPlan = null;
            marketBoardReadResult = null;
            marketBoardReconciliation = null;
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
                config.CommandPickupApiKey,
                claimed.Id,
                claimed.ClaimToken,
                claimedAcceptIdempotencyKey,
                token).ConfigureAwait(false);

            claimedAcquisitionRequest = claimed with { Status = accepted.Status };
            acquisitionPlan = null;
            marketBoardReadResult = null;
            marketBoardReconciliation = null;
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
                config.CommandPickupApiKey,
                claimed.Id,
                claimed.ClaimToken,
                claimedRejectIdempotencyKey,
                "Rejected in the MarketMafioso plugin.",
                token).ConfigureAwait(false);

            claimedAcquisitionRequest = claimed with { Status = rejected.Status };
            acquisitionPlan = null;
            marketBoardReadResult = null;
            marketBoardReconciliation = null;
            acquisitionStatus = "Request rejected.";
        }).ConfigureAwait(false);
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
            var currentWorld = GetCurrentWorldName();
            marketBoardReconciliation = null;
            marketBoardReadResult = marketBoardListingReader.ReadCurrentListings(currentWorld);

            marketBoardReconciliation = marketBoardReadResult.Status == "Ready"
                ? MarketBoardListingReconciler.Reconcile(
                    plan,
                    currentWorld,
                    marketBoardReadResult.ItemId,
                    marketBoardReadResult.Listings)
                : null;
            acquisitionStatus = marketBoardReconciliation == null
                ? marketBoardReadResult.Message
                : $"Live listing reconciliation {marketBoardReconciliation.Status}.";

            return Task.CompletedTask;
        });
    }

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
        ImGuiUi.SectionHeader("Prep Queue", ColHeader);

        if (projects.Count == 0)
        {
            ImGui.TextColored(ColMuted, "No company workshop projects were found.");
            return;
        }

        var selectedProject = projects.FirstOrDefault(x => x.WorkshopItemId == workshopProjectSelection.SelectedWorkshopItemId);
        ImGui.TextColored(ColMuted, selectedProject == null
            ? "No workshop project selected."
            : $"Selected: {selectedProject.Name}");

        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Quantity##workshopQuantity", ref workshopProjectSelection.Quantity))
        {
            if (workshopProjectSelection.Quantity < 1)
                workshopProjectSelection.Quantity = 1;
        }

        ImGui.SameLine();
        if (ImGui.Button("Browse Projects..."))
            ProjectBrowser.IsOpen = true;

        ImGui.SameLine();
        if (ImGuiUi.Button("Add Selected", selectedProject != null))
            AddWorkshopProject(workshopProjectSelection.SelectedWorkshopItemId);

        ImGui.Spacing();
        DrawWorkshopQueueTable(projects);
    }

    private void AddWorkshopProject(uint workshopItemId)
    {
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

        config.Save();
        workshopStatus = "Added project to workshop prep queue.";
    }

    private void DrawWorkshopQueueTable(IReadOnlyList<WorkshopProjectDefinition> projects)
    {
        if (config.WorkshopPrepQueue.Count == 0)
        {
            ImGui.TextColored(ColMuted, "No workshop projects queued.");
            return;
        }

        var projectNames = projects.ToDictionary(x => x.WorkshopItemId, x => x.Name);
        if (ImGui.BeginTable("WorkshopPrepQueue", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Project");
            ImGui.TableSetupColumn("Qty");
            ImGui.TableSetupColumn("");
            ImGui.TableHeadersRow();

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
                if (ImGui.InputInt($"##workshopQueueQty{index}", ref quantity))
                {
                    item.Quantity = Math.Max(1, quantity);
                    config.Save();
                }

                ImGui.TableNextColumn();
                if (ImGui.Button($"Remove##workshopQueueRemove{index}"))
                {
                    config.WorkshopPrepQueue.RemoveAt(index);
                    config.Save();
                    workshopStatus = "Removed project from workshop prep queue.";
                    index--;
                }
            }

            ImGui.EndTable();
        }
    }

    private void DrawWorkshopMaterialSummary()
    {
        ImGuiUi.SectionHeader("Materials", ColHeader);

        var availability = GetWorkshopAvailability();
        if (availability.Count == 0)
        {
            ImGui.TextColored(ColMuted, "No workshop materials yet. Add projects to the prep queue.");
            return;
        }

        if (ImGui.BeginTable("WorkshopPrepMaterials", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Required");
            ImGui.TableSetupColumn("Player");
            ImGui.TableSetupColumn("Retainers");
            ImGui.TableSetupColumn("Shortage");
            ImGui.TableSetupColumn("Candidates");
            ImGui.TableHeadersRow();

            foreach (var item in availability)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.ItemName);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.Required.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.PlayerInventory.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(item.RetainerCache.ToString());
                ImGui.TableNextColumn();
                ImGui.TextColored(item.Shortage > 0 ? ColError : ColSuccess, item.Shortage.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.Join(", ", item.CandidateRetainers.Select(x => x.RetainerName)));
            }

            ImGui.EndTable();
        }
    }

    private IReadOnlyList<WorkshopMaterialAvailability> GetWorkshopAvailability()
    {
        if (config.WorkshopPrepQueue.Count == 0)
            return [];

        var requirements = workshopCatalog.BuildRequirements(config.WorkshopPrepQueue);
        var playerInventory = scanner.CountPlayerInventory(config);
        return WorkshopMaterialAvailabilityService.BuildAvailability(requirements, playerInventory, config);
    }

    private void DrawWorkshopPrepActions()
    {
        ImGuiUi.SectionHeader("Actions", ColHeader);

        if (config.WorkshopPrepQueue.Count == 0)
            confirmViwiClear = false;

        var canRefreshRetainers = autoRetainerRefresh.CanStartRefresh &&
                                  !autoRetainerRefresh.IsRefreshing &&
                                  !autoRetainerRefresh.IsStartQueued;
        if (ImGuiUi.Button("Refresh Retainer Cache", canRefreshRetainers))
            autoRetainerRefresh.StartFullRefresh();

        ImGui.SameLine();
        if (ImGuiUi.Button("Restock Materials From Retainers", !workshopRetainerRestock.IsRunning))
            _ = workshopRetainerRestock.StartAsync(GetWorkshopAvailability());

        ImGui.SameLine();
        var hasPrepQueue = config.WorkshopPrepQueue.Count > 0;
        if (ImGuiUi.Button("Send Queue To VIWI", hasPrepQueue))
            confirmViwiClear = true;

        if (confirmViwiClear)
        {
            ImGui.TextColored(ColMuted, "This will clear VIWI Workshoppa's queue and send the MarketMafioso prep queue.");

            if (ImGuiUi.Button("Confirm VIWI Queue Sync", hasPrepQueue))
            {
                var result = viwiWorkshoppaIpc.SendQueue(config.WorkshopPrepQueue, clearExisting: true);
                workshopStatus = result.Message;
                confirmViwiClear = false;
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel VIWI Queue Sync"))
                confirmViwiClear = false;
        }

        if (ImGuiUi.Button("Clear Prep Queue", hasPrepQueue))
        {
            config.WorkshopPrepQueue.Clear();
            config.Save();
            workshopStatus = "Cleared prep queue.";
        }

        ImGui.Spacing();
        ImGui.TextColored(GetWorkshopStatusColor(), workshopStatus);
        ImGui.TextColored(workshopRetainerRestock.IsRunning ? ColHeader : ColMuted, workshopRetainerRestock.LastStatus);
    }

    private Vector4 GetWorkshopStatusColor()
    {
        if (workshopStatus.Contains("unable", StringComparison.OrdinalIgnoreCase) ||
            workshopStatus.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            workshopStatus.Contains("not available", StringComparison.OrdinalIgnoreCase))
            return ColError;

        if (workshopStatus.Contains("sent", StringComparison.OrdinalIgnoreCase) ||
            workshopStatus.Contains("added", StringComparison.OrdinalIgnoreCase) ||
            workshopStatus.Contains("cleared", StringComparison.OrdinalIgnoreCase) ||
            workshopStatus.Contains("removed", StringComparison.OrdinalIgnoreCase))
            return ColSuccess;

        return ColMuted;
    }

    private void DrawStatusTab()
    {
        ImGui.Spacing();
        DrawStatusSection();
        ImGui.Spacing();
        DrawRetainerCacheSection();
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
        ImGui.TextColored(ColHeader, "Export Endpoint");
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
            ? "API Key (required for this endpoint):"
            : "API Key (optional - sent as X-Api-Key header):");
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
            ImGui.TextColored(ColError, "This endpoint requires an API key before reports can be sent.");
        else if (endpoint.Kind == ReceiverEndpointKind.CustomRemote)
            ImGui.TextColored(ColMuted, "Custom remote endpoint. API key is required by default.");
    }

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
        acquisitionRequestCancellation?.Cancel();
        acquisitionRequestCancellation?.Dispose();
        acquisitionHttpClient.Dispose();
    }
}
