using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using MarketMafioso.CraftArchitectCompanion;
using MarketMafioso.MarketAcquisition;
using LuminaItem = Lumina.Excel.Sheets.Item;

namespace MarketMafioso.Windows;

public sealed class CraftArchitectCompanionWindow : Window
{
    private readonly Configuration config;
    private readonly Func<MarketAcquisitionQuickShopScope> getScope;
    private readonly Func<bool> isRouteActive;
    private readonly Func<bool> isBusy;
    private readonly Func<string, uint, int, CancellationToken, Task<IReadOnlyList<MarketAcquisitionListing>>> fetchListings;
    private readonly Func<MarketAcquisitionQuickShopDraft, Task<bool>> createRoute;
    private readonly IPluginLog log;
    private readonly string craftQuoteDiagnosticsDirectory;
    private readonly IReadOnlyList<CompanionItemOption> itemOptions;
    private readonly HttpClient workshopHostQuoteHttpClient = new();
    private readonly WorkshopHostCapabilitiesClient workshopHostCapabilitiesClient;
    private readonly WorkshopHostCraftQuoteProvider workshopHostQuoteProvider;
    private readonly ManualCraftQuoteProvider manualQuoteProvider;
    private readonly CraftArchitectFileQuoteProvider fileQuoteProvider;
    private readonly ICraftQuoteProvider quoteProvider;

    private string itemSearchBuffer = string.Empty;
    private CompanionItemOption? selectedItem;
    private string quantityBuffer = "1";
    private string craftUnitCostBuffer = string.Empty;
    private string quoteFilePathBuffer = string.Empty;
    private string buyThresholdBuffer = string.Empty;
    private string gilCapBuffer = string.Empty;
    private string region = "North America";
    private string worldMode = "Recommended";
    private string sweepScope = "Region";
    private readonly List<string> sweepDataCenters = new();
    private int hqPolicyIndex;
    private bool isRefreshing;
    private bool isFetchingCraftQuote;
    private bool workshopHostCraftQuotesAvailable;
    private bool isCheckingWorkshopHostCapabilities;
    private DateTimeOffset? workshopHostCapabilitiesCheckedAtUtc;
    private string previewStatus = "Market preview has not been fetched.";
    private string workshopHostQuoteStatus = "Workshop Host quote API disabled.";
    private string craftQuoteStatus = "No craft quote yet.";
    private string? lastCraftQuoteDiagnosticFilePath;
    private CraftAppraisalQuote? latestCraftQuote;
    private MarketAppraisalResult? appraisalResult;
    private CancellationTokenSource? refreshCancellation;

    private static readonly Vector4 ColHeader = new(0.38f, 0.73f, 1.00f, 1f);
    private static readonly Vector4 ColSuccess = new(0.45f, 0.90f, 0.55f, 1f);
    private static readonly Vector4 ColError = new(1.00f, 0.40f, 0.40f, 1f);
    private static readonly Vector4 ColMuted = new(0.60f, 0.60f, 0.60f, 1f);
    private static readonly string[] WorldModes = ["Recommended", "AllWorldSweep"];
    private static readonly string[] SweepScopes = ["Region", "CurrentDataCenter", "DataCenters"];
    private static readonly string[] HqPolicies = ["Either", "HqOnly", "NqOnly"];
    private static readonly TimeSpan WorkshopHostCapabilityTtl = TimeSpan.FromMinutes(5);

    public CraftArchitectCompanionWindow(
        Configuration config,
        IDataManager dataManager,
        Func<MarketAcquisitionQuickShopScope> getScope,
        Func<bool> isRouteActive,
        Func<bool> isBusy,
        Func<string, uint, int, CancellationToken, Task<IReadOnlyList<MarketAcquisitionListing>>> fetchListings,
        Func<MarketAcquisitionQuickShopDraft, Task<bool>> createRoute,
        IPluginLog log)
        : base("Craft Architect Companion##CraftArchitectCompanion", ImGuiWindowFlags.None)
    {
        this.config = config;
        this.getScope = getScope;
        this.isRouteActive = isRouteActive;
        this.isBusy = isBusy;
        this.fetchListings = fetchListings;
        this.createRoute = createRoute;
        this.log = log;
        craftQuoteDiagnosticsDirectory = Path.Combine(
            Plugin.PluginInterface.GetPluginConfigDirectory(),
            "craft-architect-quote-logs");
        itemOptions = LoadItemOptions(dataManager);
        workshopHostCapabilitiesClient = new WorkshopHostCapabilitiesClient(workshopHostQuoteHttpClient);
        workshopHostQuoteProvider = new WorkshopHostCraftQuoteProvider(
            workshopHostQuoteHttpClient,
            () => config.EnableWorkshopHostCraftQuotes,
            () => workshopHostCraftQuotesAvailable,
            () => config.ServerUrl,
            () => config.ApiKey);
        if (config.EnableWorkshopHostCraftQuotes)
            workshopHostQuoteStatus = "Workshop Host quote API enabled; check capabilities before use.";

        manualQuoteProvider = new ManualCraftQuoteProvider(_ =>
            TryParseDecimal(craftUnitCostBuffer, out var unitCost) && unitCost > 0
                ? unitCost
                : null);
        quoteFilePathBuffer = config.CraftArchitectQuoteFilePath;
        fileQuoteProvider = new CraftArchitectFileQuoteProvider(() => config.CraftArchitectQuoteFilePath);
        quoteProvider = new LastGoodCraftQuoteProvider(
            new CompositeCraftQuoteProvider([workshopHostQuoteProvider, fileQuoteProvider, manualQuoteProvider]));

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(760, 520),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public MarketAppraisalResult? LastAppraisalResult => appraisalResult;

    public override void Draw()
    {
        var request = TryBuildRequest();
        DrawHeader(request);
        ImGui.Separator();
        DrawBody(request);
    }

    private void DrawHeader(MarketAppraisalRequest? request)
    {
        ImGui.TextColored(ColHeader, "Craft Architect Companion");
        ImGui.TextWrapped("Compare craft-cost evidence with market depth, then hand off your chosen threshold to a monitored quick-shop route.");
        ImGui.Spacing();

        if (!ImGui.BeginTable("CraftArchitectCompanionHeader", 4, ImGuiTableFlags.SizingStretchSame))
            return;

        DrawMetric("Item", selectedItem?.Name ?? "Needs item", selectedItem is not null);
        DrawMetric("Craft", FormatCraftUnitCost(), HasCraftUnitCostForCurrentSelection());
        DrawMetric("Threshold", request is null ? "Needs input" : FormatGil(request.BuyThresholdUnitPrice), request is not null);
        DrawMetric("Stock", appraisalResult is null ? "Not fetched" : appraisalResult.SupportedQuantity.ToString("N0"), appraisalResult?.SupportedQuantity > 0);
        ImGui.EndTable();
    }

    private void DrawBody(MarketAppraisalRequest? request)
    {
        var available = ImGui.GetContentRegionAvail();
        if (available.X < 720)
        {
            DrawPanel("Inputs", DrawInputs, Math.Clamp(available.Y * 0.50f, 270f, 420f));
            ImGui.Spacing();
            DrawPanel("Market Depth", () => DrawMarketDepth(request), MathF.Max(220f, ImGui.GetContentRegionAvail().Y));
            return;
        }

        var leftWidth = Math.Clamp(available.X * 0.42f, 340f, 520f);
        if (!ImGui.BeginTable("CraftArchitectCompanionBody", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable))
            return;

        ImGui.TableSetupColumn("Inputs", ImGuiTableColumnFlags.WidthFixed, leftWidth);
        ImGui.TableSetupColumn("Depth", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        DrawPanel("Inputs", DrawInputs, MathF.Max(360f, available.Y));
        ImGui.TableNextColumn();
        DrawPanel("Market Depth", () => DrawMarketDepth(request), MathF.Max(360f, available.Y));
        ImGui.EndTable();
    }

    private static void DrawPanel(string title, Action drawContent, float height)
    {
        ImGui.BeginChild($"##{title}", new Vector2(0, height), true);
        ImGui.TextColored(ColHeader, title);
        ImGui.Separator();
        drawContent();
        ImGui.EndChild();
    }

    private void DrawInputs()
    {
        DrawItemSearch();
        DrawInput("Quantity", ref quantityBuffer);
        DrawIndexedCombo("HQ", HqPolicies, ref hqPolicyIndex);
        DrawRouteSettings();
        ImGui.Spacing();
        DrawCraftAppraisal();
        ImGui.Spacing();
        ImGui.TextColored(ColHeader, "Acquisition Threshold");
        ImGui.Separator();
        DrawInput("Buy Threshold", ref buyThresholdBuffer);
        DrawInput("Gil Cap", ref gilCapBuffer);
    }

    private void DrawQuoteFileSettings()
    {
        DrawInput("Quote File", ref quoteFilePathBuffer);
        if (ImGuiUi.Button("Save Quote File", true))
        {
            config.CraftArchitectQuoteFilePath = quoteFilePathBuffer.Trim();
            config.Save();
            appraisalResult = null;
            previewStatus = string.IsNullOrWhiteSpace(config.CraftArchitectQuoteFilePath)
                ? "Craft quote file path cleared."
                : "Craft quote file path saved.";
        }

        ImGui.SameLine();
        if (ImGuiUi.Button("Clear Quote File", !string.IsNullOrWhiteSpace(config.CraftArchitectQuoteFilePath) || !string.IsNullOrWhiteSpace(quoteFilePathBuffer)))
        {
            quoteFilePathBuffer = string.Empty;
            config.CraftArchitectQuoteFilePath = string.Empty;
            config.Save();
            appraisalResult = null;
            previewStatus = "Craft quote file path cleared.";
        }

        if (!string.IsNullOrWhiteSpace(config.CraftArchitectQuoteFilePath))
        {
            ImGui.TextColored(ColMuted, $"Quote file: {config.CraftArchitectQuoteFilePath}");
        }
    }

    private void DrawCraftAppraisal()
    {
        var quoteRequest = TryBuildQuoteRequest();
        var quote = quoteRequest is null
            ? latestCraftQuote
            : GetMatchingQuote(quoteRequest);
        var viewState = CraftAppraisalPanelPresenter.Build(new CraftAppraisalPanelState
        {
            WorkshopHostEnabled = config.EnableWorkshopHostCraftQuotes,
            WorkshopHostQuoteAvailable = workshopHostCraftQuotesAvailable,
            ManualFallbackEnabled = config.EnableCraftArchitectManualFallback,
            HasQuoteFilePath = !string.IsNullOrWhiteSpace(config.CraftArchitectQuoteFilePath),
            HasManualCraftCost = TryParseDecimal(craftUnitCostBuffer, out var manualCraftCost) && manualCraftCost > 0,
            LatestQuote = quote,
            NowUtc = DateTimeOffset.UtcNow,
        });

        ImGui.TextColored(ColHeader, "Craft Appraisal");
        ImGui.Separator();
        DrawWorkshopHostQuoteControls(viewState, quoteRequest);
        ImGui.Spacing();
        var quoteColor = quote is null
            ? ColMuted
            : viewState.CanApplyQuoteToThreshold
                ? ColSuccess
                : ColError;
        ImGui.TextColored(quoteColor, viewState.QuoteHeadline);
        ImGui.TextWrapped(viewState.QuoteDetail);
        if (quote is not null)
        {
            foreach (var warning in quote.Warnings.Take(2))
                ImGui.TextColored(ColMuted, warning);
        }

        ImGui.TextColored(isFetchingCraftQuote ? ColHeader : ColMuted, craftQuoteStatus);
        DrawQuoteDiagnostics(viewState);
        DrawQuoteDiagnosticPrintoutPath(quote);

        if (quoteRequest is null)
        {
            ImGui.TextColored(ColMuted, BuildQuoteValidationMessage());
        }
        else if (quote is not null)
        {
            DrawQuoteThresholdActions(quote);
        }

        ImGui.Spacing();
        var fallbackFlags = viewState.ShowFallbackControlsByDefault
            ? ImGuiTreeNodeFlags.DefaultOpen
            : ImGuiTreeNodeFlags.None;
        if (ImGui.CollapsingHeader(viewState.FallbackSectionLabel, fallbackFlags))
        {
            DrawQuoteFileSettings();
            if (viewState.ShowManualFallbackControls)
            {
                ImGui.Spacing();
                DrawManualCraftCostFallback();
            }
            else
            {
                ImGui.TextColored(ColMuted, "Manual craft cost fallback is disabled in Settings.");
            }
        }
    }

    private void DrawWorkshopHostQuoteControls(
        CraftAppraisalPanelViewState viewState,
        MarketAppraisalRequest? quoteRequest)
    {
        var enableWorkshopHostQuotes = config.EnableWorkshopHostCraftQuotes;
        if (ImGui.Checkbox("Use Workshop Host", ref enableWorkshopHostQuotes))
        {
            config.EnableWorkshopHostCraftQuotes = enableWorkshopHostQuotes;
            config.Save();
            appraisalResult = null;
            latestCraftQuote = null;
            lastCraftQuoteDiagnosticFilePath = null;
            workshopHostCraftQuotesAvailable = false;
            workshopHostCapabilitiesCheckedAtUtc = null;
            workshopHostQuoteStatus = enableWorkshopHostQuotes
                ? "Workshop Host quote API enabled; checking capabilities..."
                : "Workshop Host quote API disabled.";
            craftQuoteStatus = "No craft quote yet.";

            if (enableWorkshopHostQuotes)
                _ = RefreshWorkshopHostCapabilitiesAsync();
        }

        ImGui.SameLine();
        if (ImGuiUi.Button("Refresh", config.EnableWorkshopHostCraftQuotes && !isCheckingWorkshopHostCapabilities))
            _ = RefreshWorkshopHostCapabilitiesAsync();

        ImGui.TextColored(workshopHostCraftQuotesAvailable ? ColSuccess : ColMuted, viewState.WorkshopHostStatus);
        ImGui.TextWrapped(viewState.Guidance);
        ImGui.TextColored(workshopHostCraftQuotesAvailable ? ColSuccess : ColMuted, workshopHostQuoteStatus);

        var canQuote = viewState.PrimaryQuoteActionEnabled &&
                       quoteRequest is not null &&
                       !isFetchingCraftQuote &&
                       !isCheckingWorkshopHostCapabilities;
        if (ImGuiUi.Button(viewState.PrimaryQuoteActionLabel, canQuote))
            _ = RefreshCraftQuoteAsync(quoteRequest!);
    }

    private void DrawQuoteThresholdActions(CraftAppraisalQuote quote)
    {
        if (!quote.IsComplete || quote.EstimatedUnitCost <= 0)
            return;

        if (ImGuiUi.Button("Use as threshold", true))
            ApplyQuoteThreshold(quote.EstimatedUnitCost);
        ImGui.SameLine();
        if (ImGuiUi.Button("-10%", true))
            ApplyQuoteThreshold(quote.EstimatedUnitCost * 0.90m);
        ImGui.SameLine();
        if (ImGuiUi.Button("+10%", true))
            ApplyQuoteThreshold(quote.EstimatedUnitCost * 1.10m);
    }

    private void DrawManualCraftCostFallback()
    {
        DrawInput("Craft Unit Cost", ref craftUnitCostBuffer);
        if (!TryParseDecimal(craftUnitCostBuffer, out var craftCost) || craftCost <= 0)
        {
            ImGui.TextColored(ColMuted, "Manual craft cost is empty.");
            return;
        }

        ImGui.TextColored(ColMuted, $"Manual quote: {FormatGilDecimal(craftCost)} / unit");
        if (ImGuiUi.Button("Use Manual Cost", true))
            ApplyQuoteThreshold(craftCost);
        ImGui.SameLine();
        if (ImGuiUi.Button("-10%##ManualCraftCost", true))
            ApplyQuoteThreshold(craftCost * 0.90m);
        ImGui.SameLine();
        if (ImGuiUi.Button("+10%##ManualCraftCost", true))
            ApplyQuoteThreshold(craftCost * 1.10m);
    }

    private void DrawQuoteDiagnostics(CraftAppraisalPanelViewState viewState)
    {
        if (viewState.DiagnosticLines.Count == 0)
            return;

        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Calculation details", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        foreach (var line in viewState.DiagnosticLines.Take(12))
            ImGui.TextWrapped(line);

        if (viewState.DiagnosticLines.Count > 12)
            ImGui.TextColored(ColMuted, $"{viewState.DiagnosticLines.Count - 12:N0} more diagnostic line(s) in the printout file.");
    }

    private void DrawQuoteDiagnosticPrintoutPath(CraftAppraisalQuote? quote)
    {
        if (quote is null)
            return;

        if (string.IsNullOrWhiteSpace(lastCraftQuoteDiagnosticFilePath))
            return;

        ImGui.TextColored(ColMuted, $"Printout: {lastCraftQuoteDiagnosticFilePath}");
    }

    private void DrawRouteSettings()
    {
        DrawFullWidthCombo("Region##caCompanionRegion", MarketAcquisitionWorldCatalog.SupportedRegions.ToArray(), region, value =>
        {
            region = value;
            sweepDataCenters.Clear();
        });

        DrawFullWidthCombo("World Mode##caCompanionWorldMode", WorldModes, worldMode, value =>
        {
            worldMode = value;
            if (worldMode != "AllWorldSweep")
            {
                sweepScope = "Region";
                sweepDataCenters.Clear();
            }
        });

        if (worldMode != "AllWorldSweep")
            return;

        DrawFullWidthCombo("Sweep Scope##caCompanionSweepScope", SweepScopes, sweepScope, value =>
        {
            sweepScope = value;
            if (sweepScope != "DataCenters")
                sweepDataCenters.Clear();
        });

        if (sweepScope == "DataCenters")
            DrawDataCenterSelector();
    }

    private void DrawDataCenterSelector()
    {
        IReadOnlyDictionary<string, string[]> dataCenters;
        try
        {
            dataCenters = MarketAcquisitionWorldCatalog.ResolveDataCenters(region);
        }
        catch (InvalidOperationException ex)
        {
            ImGui.TextColored(ColError, ex.Message);
            return;
        }

        foreach (var dataCenter in dataCenters.Keys)
        {
            var selected = sweepDataCenters.Contains(dataCenter, StringComparer.OrdinalIgnoreCase);
            if (ImGui.Checkbox($"{dataCenter}##caCompanionDc{dataCenter}", ref selected))
            {
                sweepDataCenters.RemoveAll(existing => existing.Equals(dataCenter, StringComparison.OrdinalIgnoreCase));
                if (selected)
                    sweepDataCenters.Add(dataCenter);
            }

            ImGui.SameLine();
        }

        ImGui.NewLine();
    }

    private void DrawMarketDepth(MarketAppraisalRequest? request)
    {
        var scope = getScope();
        if (scope.HasScope)
            ImGui.TextColored(ColMuted, $"Route target: {scope.CharacterName} @ {scope.World}");
        else
            ImGui.TextColored(scope.IsTemporarilyUnavailable ? ColMuted : ColError, "Character scope unavailable.");

        if (request is null)
        {
            ImGui.TextColored(ColError, BuildValidationMessage());
        }
        else if (appraisalResult is not null)
        {
            ImGui.TextColored(appraisalResult.SupportedQuantity > 0 ? ColSuccess : ColMuted,
                $"{appraisalResult.SupportedQuantity:N0} under-threshold unit(s), {appraisalResult.SupportedListingCount:N0} listing(s), {appraisalResult.SupportedWorldCount:N0} world(s), {appraisalResult.SupportedTotalGil:N0} gil.");
        }

        ImGui.TextColored(isRefreshing ? ColHeader : ColMuted, previewStatus);
        ImGui.Spacing();
        if (ImGuiUi.Button("Compare Market Depth", !isRefreshing && request is not null))
            _ = RefreshMarketDepthAsync(request!);
        ImGui.SameLine();
        if (ImGuiUi.Button("Create Quick-Shop Route", !isBusy() && !isRouteActive() && request is not null))
            _ = CreateQuickShopRouteAsync(request!);

        ImGui.Spacing();
        DrawWorldSummaryTable();
    }

    private void DrawWorldSummaryTable()
    {
        if (appraisalResult?.Worlds.Count > 0 != true)
        {
            ImGui.TextColored(ColMuted, "No under-threshold worlds to show.");
            return;
        }

        var tableHeight = MathF.Max(160f, ImGui.GetContentRegionAvail().Y);
        var flags = ImGuiTableFlags.Borders |
                    ImGuiTableFlags.RowBg |
                    ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.ScrollY;
        if (!ImGui.BeginTable("CraftArchitectCompanionWorlds", 5, flags, new Vector2(0, tableHeight)))
            return;

        ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 88);
        ImGui.TableSetupColumn("Listings", ImGuiTableColumnFlags.WidthFixed, 76);
        ImGui.TableSetupColumn("Unit", ImGuiTableColumnFlags.WidthFixed, 112);
        ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthFixed, 112);
        ImGui.TableHeadersRow();

        foreach (var world in appraisalResult.Worlds)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(world.WorldName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(world.Quantity.ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(world.ListingCount.ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{FormatGil(world.LowestUnitPrice)}-{FormatGil(world.HighestUnitPrice)}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{world.TotalGil:N0} gil");
        }

        ImGui.EndTable();
    }

    private async Task RefreshMarketDepthAsync(MarketAppraisalRequest request)
    {
        refreshCancellation?.Cancel();
        refreshCancellation?.Dispose();
        refreshCancellation = new CancellationTokenSource();
        isRefreshing = true;
        previewStatus = "Fetching Universalis listings...";

        try
        {
            await EnsureWorkshopHostCapabilitiesFreshAsync().ConfigureAwait(false);
            var listings = await fetchListings(request.Region, request.ItemId, 100, refreshCancellation.Token)
                .ConfigureAwait(false);
            var quote = await GetCraftQuoteAsync(request, refreshCancellation.Token).ConfigureAwait(false);
            appraisalResult = CraftArchitectMarketAppraisalService.Build(request, listings, quote);
            previewStatus = appraisalResult.SupportedQuantity == 0
                ? "No under-threshold stock found."
                : "Market depth refreshed.";
        }
        catch (OperationCanceledException)
        {
            previewStatus = "Market preview refresh cancelled.";
        }
        catch (Exception ex)
        {
            previewStatus = $"Market preview failed: {ex.Message}";
        }
        finally
        {
            isRefreshing = false;
        }
    }

    private async Task CreateQuickShopRouteAsync(MarketAppraisalRequest request)
    {
        await EnsureWorkshopHostCapabilitiesFreshAsync().ConfigureAwait(false);
        var result = await CraftArchitectQuickShopRouteService.CreateAsync(
            request,
            quoteProvider,
            createRoute).ConfigureAwait(false);
        previewStatus = result.Message;
    }

    private async Task RefreshCraftQuoteAsync(MarketAppraisalRequest request)
    {
        if (isFetchingCraftQuote)
            return;

        isFetchingCraftQuote = true;
        craftQuoteStatus = "Fetching craft quote...";

        try
        {
            await EnsureWorkshopHostCapabilitiesFreshAsync().ConfigureAwait(false);
            var quote = await GetCraftQuoteAsync(request, CancellationToken.None).ConfigureAwait(false);
            latestCraftQuote = quote;
            craftQuoteStatus = quote is null
                ? "No craft quote source returned evidence."
                : "Craft quote refreshed.";
            lastCraftQuoteDiagnosticFilePath = WriteCraftQuoteDiagnosticPrintout(request, quote);
            LogCraftQuoteResult(request, quote);
        }
        catch (Exception ex)
        {
            latestCraftQuote = null;
            lastCraftQuoteDiagnosticFilePath = null;
            craftQuoteStatus = $"Craft quote failed: {ex.Message}";
            log.Warning(ex,
                "[MarketMafioso] Craft Architect quote failed for {ItemName} ({ItemId}) x{Quantity}.",
                request.ItemName,
                request.ItemId,
                request.Quantity);
        }
        finally
        {
            isFetchingCraftQuote = false;
        }
    }

    private async Task RefreshWorkshopHostCapabilitiesAsync()
    {
        if (isCheckingWorkshopHostCapabilities)
            return;

        workshopHostCraftQuotesAvailable = false;
        isCheckingWorkshopHostCapabilities = true;
        workshopHostQuoteStatus = "Checking Workshop Host capabilities...";

        try
        {
            workshopHostCraftQuotesAvailable = await workshopHostCapabilitiesClient.SupportsCraftAppraiseV1Async(
                config.ServerUrl,
                config.ApiKey,
                CancellationToken.None).ConfigureAwait(false);
            workshopHostCapabilitiesCheckedAtUtc = DateTimeOffset.UtcNow;
            workshopHostQuoteStatus = workshopHostCraftQuotesAvailable
                ? "Workshop Host craft quotes available."
                : "Workshop Host does not advertise craft quote support.";
        }
        catch (Exception ex)
        {
            workshopHostCraftQuotesAvailable = false;
            workshopHostQuoteStatus = $"Workshop Host capability check failed: {ex.Message}";
        }
        finally
        {
            isCheckingWorkshopHostCapabilities = false;
        }
    }

    private async Task EnsureWorkshopHostCapabilitiesFreshAsync()
    {
        if (!config.EnableWorkshopHostCraftQuotes)
            return;

        if (workshopHostCapabilitiesCheckedAtUtc is { } checkedAt &&
            DateTimeOffset.UtcNow - checkedAt < WorkshopHostCapabilityTtl)
        {
            return;
        }

        await RefreshWorkshopHostCapabilitiesAsync().ConfigureAwait(false);
    }

    private MarketAppraisalRequest? TryBuildRequest()
    {
        var item = ResolveSelectedItem();
        if (item is null ||
            !TryParseUInt(quantityBuffer, out var quantity) ||
            quantity == 0 ||
            !TryParseUInt(buyThresholdBuffer, out var threshold) ||
            threshold == 0 ||
            !TryParseUIntOptional(gilCapBuffer, out var gilCap))
        {
            return null;
        }

        return new MarketAppraisalRequest
        {
            ItemId = item.ItemId,
            ItemName = item.Name,
            Quantity = quantity,
            HqPolicy = HqPolicies[hqPolicyIndex],
            BuyThresholdUnitPrice = threshold,
            GilCap = gilCap,
            Region = region,
            WorldMode = worldMode,
            SweepScope = sweepScope,
            SweepDataCenters = sweepDataCenters.ToArray(),
        };
    }

    private MarketAppraisalRequest? TryBuildQuoteRequest()
    {
        var item = ResolveSelectedItem();
        if (item is null ||
            !TryParseUInt(quantityBuffer, out var quantity) ||
            quantity == 0)
        {
            return null;
        }

        return new MarketAppraisalRequest
        {
            ItemId = item.ItemId,
            ItemName = item.Name,
            Quantity = quantity,
            HqPolicy = HqPolicies[hqPolicyIndex],
            BuyThresholdUnitPrice = 0,
            GilCap = 0,
            Region = region,
            WorldMode = worldMode,
            SweepScope = sweepScope,
            SweepDataCenters = sweepDataCenters.ToArray(),
        };
    }

    private async Task<CraftAppraisalQuote?> GetCraftQuoteAsync(
        MarketAppraisalRequest request,
        CancellationToken cancellationToken)
    {
        if (GetMatchingQuote(request) is { } matchingQuote)
            return matchingQuote;

        var quote = await quoteProvider.GetQuoteAsync(request, cancellationToken).ConfigureAwait(false);
        latestCraftQuote = quote;
        return quote;
    }

    private string? WriteCraftQuoteDiagnosticPrintout(MarketAppraisalRequest request, CraftAppraisalQuote? quote)
    {
        if (quote is null)
            return null;

        try
        {
            return CraftQuoteDiagnosticPrintout.Write(
                craftQuoteDiagnosticsDirectory,
                request,
                quote,
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[MarketMafioso] Failed to write Craft Architect quote diagnostic printout.");
            return null;
        }
    }

    private void LogCraftQuoteResult(MarketAppraisalRequest request, CraftAppraisalQuote? quote)
    {
        if (quote is null)
        {
            log.Information(
                "[MarketMafioso] Craft Architect quote returned no evidence for {ItemName} ({ItemId}) x{Quantity}.",
                request.ItemName,
                request.ItemId,
                request.Quantity);
            return;
        }

        var summary = CraftAppraisalPanelPresenter.BuildLogSummary(quote);
        if (quote.EstimatedUnitCost <= 0 || quote.Warnings.Count > 0)
            log.Warning("[MarketMafioso] Craft Architect quote diagnostics: {Summary}", summary);
        else
            log.Information("[MarketMafioso] Craft Architect quote diagnostics: {Summary}", summary);
    }

    private CraftAppraisalQuote? GetMatchingQuote(MarketAppraisalRequest? request)
    {
        if (request is null || latestCraftQuote is null)
            return null;

        return latestCraftQuote.ItemId == request.ItemId &&
               latestCraftQuote.RequestedQuantity == request.Quantity
            ? latestCraftQuote
            : null;
    }

    private void ApplyQuoteThreshold(decimal unitCost)
    {
        if (unitCost <= 0)
            return;

        buyThresholdBuffer = ((uint)Math.Ceiling(unitCost)).ToString();
        appraisalResult = null;
        previewStatus = "Threshold updated. Compare market depth to refresh stock.";
    }

    private string BuildValidationMessage()
    {
        if (ResolveSelectedItem() is null)
            return "Select an item.";
        if (!TryParseUInt(quantityBuffer, out var quantity) || quantity == 0)
            return "Enter a quantity greater than zero.";
        if (!TryParseUInt(buyThresholdBuffer, out var threshold) || threshold == 0)
            return latestCraftQuote is null
                ? "Get a craft quote, then apply it as the buy threshold."
                : "Apply the craft quote as the buy threshold or enter one manually.";
        if (!TryParseUIntOptional(gilCapBuffer, out _))
            return "Enter a valid gil cap or leave it blank.";
        return "Needs input.";
    }

    private string BuildQuoteValidationMessage()
    {
        if (ResolveSelectedItem() is null)
            return "Select an item to quote.";
        if (!TryParseUInt(quantityBuffer, out var quantity) || quantity == 0)
            return "Enter a quantity greater than zero to quote.";
        return "Ready to quote.";
    }

    private void DrawItemSearch()
    {
        ImGui.TextColored(ColMuted, "Item");
        ImGui.SetNextItemWidth(-1);
        var previous = itemSearchBuffer;
        if (ImGui.InputText("##caCompanionItemSearch", ref itemSearchBuffer, 160) &&
            !string.Equals(previous, itemSearchBuffer, StringComparison.Ordinal))
        {
            if (selectedItem is not null &&
                !selectedItem.Name.Equals(itemSearchBuffer.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                selectedItem = null;
                latestCraftQuote = null;
                lastCraftQuoteDiagnosticFilePath = null;
                appraisalResult = null;
                craftQuoteStatus = "No craft quote yet.";
            }
        }

        var resolved = ResolveSelectedItem();
        if (resolved is not null)
        {
            selectedItem = resolved;
            ImGui.TextColored(ColSuccess, $"{resolved.Name} ({resolved.ItemId})");
            return;
        }

        if (itemSearchBuffer.Trim().Length < 2)
        {
            ImGui.TextColored(ColMuted, "Type at least 2 characters.");
            return;
        }

        var results = GetItemSearchResults();
        if (results.Count == 0)
        {
            ImGui.TextColored(ColMuted, "No matching items.");
            return;
        }

        var resultHeight = MathF.Min(132f, results.Count * ImGui.GetTextLineHeightWithSpacing() + 10f);
        ImGui.BeginChild("##caCompanionItemResults", new Vector2(0, resultHeight), true);
        foreach (var result in results)
        {
            if (ImGui.Selectable($"{result.Name} ({result.ItemId})##caCompanionItem{result.ItemId}"))
            {
                selectedItem = result;
                itemSearchBuffer = result.Name;
                latestCraftQuote = null;
                lastCraftQuoteDiagnosticFilePath = null;
                appraisalResult = null;
                craftQuoteStatus = "No craft quote yet.";
                previewStatus = "Market preview has not been fetched.";
            }
        }

        ImGui.EndChild();
    }

    private CompanionItemOption? ResolveSelectedItem()
    {
        var search = itemSearchBuffer.Trim();
        if (selectedItem is not null &&
            selectedItem.Name.Equals(search, StringComparison.OrdinalIgnoreCase))
        {
            return selectedItem;
        }

        if (search.Length == 0)
            return null;

        CompanionItemOption? exactMatch = null;
        foreach (var option in itemOptions)
        {
            if (!option.Name.Equals(search, StringComparison.OrdinalIgnoreCase))
                continue;

            if (exactMatch is not null)
                return null;

            exactMatch = option;
        }

        return exactMatch;
    }

    private IReadOnlyList<CompanionItemOption> GetItemSearchResults()
    {
        var search = itemSearchBuffer.Trim();
        if (search.Length < 2)
            return [];

        return itemOptions
            .Where(item => item.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Name.StartsWith(search, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(item => item.Name.Length)
            .ThenBy(item => item.Name)
            .Take(10)
            .ToList();
    }

    private static IReadOnlyList<CompanionItemOption> LoadItemOptions(IDataManager dataManager)
    {
        try
        {
            return dataManager.GetExcelSheet<LuminaItem>()
                .Where(item => item.RowId > 0)
                .Select(item => new CompanionItemOption(item.RowId, item.Name.ToString().Trim()))
                .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                .GroupBy(item => item.ItemId)
                .Select(group => group.First())
                .OrderBy(item => item.Name)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static void DrawMetric(string label, string value, bool positive)
    {
        ImGui.TableNextColumn();
        ImGui.TextColored(ColMuted, label);
        ImGui.TextColored(positive ? ColSuccess : ColMuted, value);
    }

    private static void DrawInput(string label, ref string value)
    {
        ImGui.TextColored(ColMuted, label);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText($"##caCompanion{label}", ref value, 128);
    }

    private static void DrawFullWidthCombo(
        string label,
        IReadOnlyList<string> options,
        string current,
        Action<string> onChanged)
    {
        ImGui.TextColored(ColMuted, label.Split('#')[0]);
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo(label, string.IsNullOrWhiteSpace(current) ? "-" : current))
            return;

        foreach (var option in options)
        {
            var isSelected = option.Equals(current, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable(option, isSelected) && !isSelected)
                onChanged(option);
            if (isSelected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private static void DrawIndexedCombo(string label, IReadOnlyList<string> options, ref int index)
    {
        ImGui.TextColored(ColMuted, label);
        var current = options[Math.Clamp(index, 0, options.Count - 1)];
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo($"##caCompanion{label}", current))
            return;

        for (var optionIndex = 0; optionIndex < options.Count; optionIndex++)
        {
            var isSelected = optionIndex == index;
            if (ImGui.Selectable(options[optionIndex], isSelected))
                index = optionIndex;
            if (isSelected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private bool HasCraftUnitCostForCurrentSelection() =>
        GetDisplayQuote() is not null ||
        TryParseDecimal(craftUnitCostBuffer, out var craftCost) && craftCost > 0;

    private string FormatCraftUnitCost() =>
        GetDisplayQuote() is { } quote
            ? FormatGilDecimal(quote.EstimatedUnitCost)
            : TryParseDecimal(craftUnitCostBuffer, out var craftCost) && craftCost > 0
                ? FormatGilDecimal(craftCost)
                : "No quote";

    private CraftAppraisalQuote? GetDisplayQuote()
    {
        var request = TryBuildQuoteRequest();
        return request is null
            ? null
            : GetMatchingQuote(request) ?? appraisalResult?.CraftQuote;
    }

    private static bool TryParseUInt(string value, out uint parsed) =>
        uint.TryParse(value?.Trim(), out parsed);

    private static bool TryParseUIntOptional(string value, out uint parsed)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            parsed = 0;
            return true;
        }

        return TryParseUInt(value, out parsed);
    }

    private static bool TryParseDecimal(string value, out decimal parsed) =>
        decimal.TryParse(value?.Trim(), out parsed);

    private static string FormatGil(uint gil) => $"{gil:N0} gil";
    private static string FormatGilDecimal(decimal gil) => $"{gil:N0} gil";

    private sealed record CompanionItemOption(uint ItemId, string Name);
}
