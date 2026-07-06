using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Windows.AcquisitionWorkbench;

namespace MarketMafioso.Windows;

public sealed class AcquisitionWorkbenchWindow : Window
{
    private readonly Configuration config;
    private readonly Func<MarketAcquisitionQuickShopScope> getScope;
    private readonly Func<bool> isRouteActive;
    private readonly Func<bool> isBusy;
    private readonly Func<string> getStatus;
    private readonly Func<string, uint, int, CancellationToken, Task<IReadOnlyList<MarketAcquisitionListing>>> fetchListings;
    private readonly Func<MarketAcquisitionQuickShopDraft, Task<bool>> createRoute;
    private readonly Func<AcquisitionWorkbenchRouteSnapshot> getRouteSnapshot;
    private readonly Func<Task> prepareRoute;
    private readonly Func<bool, Task> startRoute;
    private readonly Func<Task> pauseRoute;
    private readonly Func<Task> resumeRoute;
    private readonly Func<Task> stopRoute;
    private readonly Func<Task> restartRoute;
    private readonly Func<Task> reprepareRoute;
    private readonly IReadOnlyList<AcquisitionItemOption> itemOptions;
    private readonly ObservedMarketSnapshotCache observedMarketSnapshots = new(64, TimeSpan.FromMinutes(15));
    private readonly Dictionary<string, WorkbenchStockState> stockStates = new(StringComparer.Ordinal);
    private readonly object stockStateGate = new();

    private MarketAcquisitionQuickShopDraft draft = MarketAcquisitionQuickShopDraft.CreateDefault();
    private readonly ItemAutocompleteState itemAutocomplete = new();
    private string targetQuantityBuffer = string.Empty;
    private string maxQuantityBuffer = string.Empty;
    private string maxUnitPriceBuffer = string.Empty;
    private string gilCapBuffer = string.Empty;
    private int quantityModeIndex = 1;
    private int hqPolicyIndex;
    private int selectedLineIndex;
    private WorkbenchPane activePane = WorkbenchPane.Build;

    private static readonly Vector4 ColHeader = new(0.38f, 0.73f, 1.00f, 1f);
    private static readonly Vector4 ColSuccess = new(0.45f, 0.90f, 0.55f, 1f);
    private static readonly Vector4 ColError = new(1.00f, 0.40f, 0.40f, 1f);
    private static readonly Vector4 ColMuted = new(0.60f, 0.60f, 0.60f, 1f);
    private static readonly string[] QuantityModes = ["TargetQuantity", "AllBelowThreshold"];
    private static readonly string[] HqPolicies = ["Either", "HqOnly", "NqOnly"];

    public AcquisitionWorkbenchWindow(
        Configuration config,
        IDataManager dataManager,
        Func<MarketAcquisitionQuickShopScope> getScope,
        Func<bool> isRouteActive,
        Func<bool> isBusy,
        Func<string> getStatus,
        Func<string, uint, int, CancellationToken, Task<IReadOnlyList<MarketAcquisitionListing>>> fetchListings,
        Func<MarketAcquisitionQuickShopDraft, Task<bool>> createRoute,
        Func<AcquisitionWorkbenchRouteSnapshot> getRouteSnapshot,
        Func<Task> prepareRoute,
        Func<bool, Task> startRoute,
        Func<Task> pauseRoute,
        Func<Task> resumeRoute,
        Func<Task> stopRoute,
        Func<Task> restartRoute,
        Func<Task> reprepareRoute)
        : base("Acquisition Workbench##MarketAcquisitionWorkbench", ImGuiWindowFlags.None)
    {
        this.config = config;
        this.getScope = getScope;
        this.isRouteActive = isRouteActive;
        this.isBusy = isBusy;
        this.getStatus = getStatus;
        this.fetchListings = fetchListings;
        this.createRoute = createRoute;
        this.getRouteSnapshot = getRouteSnapshot;
        this.prepareRoute = prepareRoute;
        this.startRoute = startRoute;
        this.pauseRoute = pauseRoute;
        this.resumeRoute = resumeRoute;
        this.stopRoute = stopRoute;
        this.restartRoute = restartRoute;
        this.reprepareRoute = reprepareRoute;
        itemOptions = ItemAutocompleteControl.LoadItemOptions(dataManager);

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(900, 560),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public int DraftLineCount => draft.Lines.Count;
    public bool HasDraftInput => DraftLineCount > 0 || HasLineInput();

    public override void Draw()
    {
        var scope = getScope();
        var validation = MarketAcquisitionQuickShopDraftValidator.Validate(
            draft,
            config.ApiKey,
            scope.CharacterName,
            scope.World);

        DrawHeader(scope, validation);
        ImGui.Separator();
        DrawBody(scope, validation);
        ImGui.Separator();
        DrawPhaseStrip();
    }

    private void DrawHeader(
        MarketAcquisitionQuickShopScope scope,
        MarketAcquisitionQuickShopValidationResult validation)
    {
        ImGui.TextColored(ColHeader, "Acquisition Workbench");
        ImGui.TextWrapped("Build, sync, and monitor client-created acquisition routes from one popout.");
        ImGui.Spacing();

        if (!ImGui.BeginTable("AcquisitionWorkbenchHeader", 6, ImGuiTableFlags.SizingStretchSame))
            return;

        DrawMetric("Target", scope.HasScope ? $"{scope.CharacterName} @ {scope.World}" : "Unavailable", scope.HasScope);
        DrawMetric("Route", FormatRouteMode(draft), true);
        DrawMetric("Lines", draft.Lines.Count.ToString("N0"), draft.Lines.Count > 0);
        DrawMetric("Stock", FormatStockMetric(), HasCurrentStockResult());
        DrawMetric("Ready", validation.IsValid ? "Yes" : "Needs input", validation.IsValid);
        DrawMetric("Sync", isBusy() ? "Working" : "Idle", !isBusy());
        ImGui.EndTable();

        var status = getStatus();
        if (!string.IsNullOrWhiteSpace(status))
            ImGui.TextColored(ColMuted, status);
    }

    private void DrawBody(
        MarketAcquisitionQuickShopScope scope,
        MarketAcquisitionQuickShopValidationResult validation)
    {
        var available = ImGui.GetContentRegionAvail();
        var phaseStripHeight = ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().FramePadding.Y * 2f + ImGui.GetStyle().ItemSpacing.Y * 2f;
        var bodyHeight = MathF.Max(300f, available.Y - phaseStripHeight);
        var stack = available.X < 760f;

        if (stack)
        {
            DrawPanel("Draft", DrawDraftBuilder, Math.Clamp(bodyHeight * 0.50f, 260f, 420f));
            ImGui.Spacing();
            DrawPanel("Route", () => DrawMainPane(scope, validation), Math.Clamp(bodyHeight * 0.32f, 190f, 320f));
            ImGui.Spacing();
            DrawPanel("Details", () => DrawSidePane(validation), MathF.Max(160f, ImGui.GetContentRegionAvail().Y));
            return;
        }

        if (!ImGui.BeginTable("AcquisitionWorkbenchBody", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable))
            return;

        ImGui.TableSetupColumn("Draft", ImGuiTableColumnFlags.WidthFixed, Math.Clamp(available.X * 0.32f, 320f, 460f));
        ImGui.TableSetupColumn("Route", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Details", ImGuiTableColumnFlags.WidthFixed, Math.Clamp(available.X * 0.24f, 260f, 380f));
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        DrawPanel("Draft", DrawDraftBuilder, bodyHeight);
        ImGui.TableNextColumn();
        DrawPanel("Route", () => DrawMainPane(scope, validation), bodyHeight);
        ImGui.TableNextColumn();
        DrawPanel("Details", () => DrawSidePane(validation), bodyHeight);
        ImGui.EndTable();
    }

    private static void DrawPanel(string title, Action drawContent, float height)
    {
        ImGui.BeginChild($"##AcquisitionWorkbench{title}", new Vector2(0, height), true);
        ImGui.TextColored(ColHeader, title);
        ImGui.Separator();
        drawContent();
        ImGui.EndChild();
    }

    private void DrawDraftBuilder()
    {
        RouteScopeSelector.Draw(
            "workbench",
            AcquisitionRouteScope.FromDraft(draft),
            ApplyRouteScope,
            ColMuted,
            ColError);

        ImGui.Spacing();
        ImGui.TextColored(ColHeader, "Line");
        ImGui.Separator();
        ItemAutocompleteControl.Draw(
            "workbench",
            itemOptions,
            itemAutocomplete,
            null,
            ColMuted,
            ColSuccess,
            ColError);
        DrawIndexedCombo("Quantity Mode", QuantityModes, ref quantityModeIndex);

        if (QuantityModes[quantityModeIndex] == "TargetQuantity")
            DrawInput("Target Quantity", ref targetQuantityBuffer);
        else
            DrawInput("Max Quantity", ref maxQuantityBuffer);

        DrawIndexedCombo("HQ", HqPolicies, ref hqPolicyIndex);
        DrawInput("Max Unit Price", ref maxUnitPriceBuffer);
        DrawInput("Gil Cap", ref gilCapBuffer);

        ImGui.Spacing();
        if (ImGuiUi.Button("Add Line", CanAddLine()))
            AddLineFromBuffers();
        ImGui.SameLine();
        if (ImGuiUi.Button("Clear Fields", HasLineInput()))
            ClearLineBuffers();
    }

    private void DrawMainPane(
        MarketAcquisitionQuickShopScope scope,
        MarketAcquisitionQuickShopValidationResult validation)
    {
        switch (activePane)
        {
            case WorkbenchPane.Build:
                DrawQueuedLines();
                break;
            case WorkbenchPane.Appraise:
                DrawAppraisePane();
                break;
            case WorkbenchPane.Run:
                DrawRunPane();
                break;
            case WorkbenchPane.Recover:
                DrawRecoverPane();
                break;
        }

        ImGui.Spacing();
        DrawSubmit(scope, validation);
    }

    private void DrawSidePane(MarketAcquisitionQuickShopValidationResult validation)
    {
        ImGui.TextColored(ColHeader, "Sync");
        ImGui.Separator();
        if (validation.IsValid)
        {
            ImGui.TextColored(ColSuccess, "Draft can be synced as a monitored route.");
        }
        else
        {
            foreach (var error in validation.Errors.Take(5))
                ImGui.TextColored(ColError, error);
            if (validation.Errors.Count > 5)
                ImGui.TextColored(ColError, $"{validation.Errors.Count - 5:N0} more issue(s).");
        }

        ImGui.Spacing();
        ImGui.TextColored(ColHeader, "Selected Line");
        ImGui.Separator();
        DrawSelectedLineSummary();
    }

    private void DrawSelectedLineSummary()
    {
        var selected = ResolveSelectedLine();
        var state = selected is null ? null : GetStockState(selected);
        var view = StockAvailabilityPanelPresenter.BuildSideSummary(new StockAvailabilityPanelState
        {
            SelectedLine = selected,
            Result = state?.Result,
            Source = state?.Source ?? StockAvailabilityPanelSource.None,
            SnapshotFetchedAtUtc = state?.SnapshotFetchedAtUtc,
            NowUtc = DateTimeOffset.UtcNow,
            IsFetching = state?.IsFetching == true,
            ErrorMessage = state?.ErrorMessage,
        });

        ImGui.TextColored(ColMuted, view.Title);
        ImGui.TextColored(ToColor(view.Severity), view.Headline);
        ImGui.TextWrapped(view.Detail);
        if (!string.IsNullOrWhiteSpace(view.Footer))
            ImGui.TextColored(ColMuted, view.Footer);

        if (selected is null)
            return;

        ImGui.Spacing();
        ImGui.TextColored(ColMuted, $"Mode: {MarketAcquisitionQuantityModePresenter.FormatMode(selected.QuantityMode)}");
        var quantity = selected.QuantityMode == "TargetQuantity" ? selected.TargetQuantity : selected.MaxQuantity;
        ImGui.TextColored(ColMuted, $"Quantity: {MarketAcquisitionQuantityModePresenter.FormatQuantity(selected.QuantityMode, quantity)}");
        ImGui.TextColored(ColMuted, $"Max unit: {FormatGil(selected.MaxUnitPrice)}");
    }

    private void DrawQueuedLines()
    {
        if (draft.Lines.Count == 0)
        {
            ImGui.TextColored(ColMuted, "No acquisition lines queued.");
            return;
        }

        var tableHeight = MathF.Max(160f, ImGui.GetContentRegionAvail().Y - 92f);
        var flags = ImGuiTableFlags.Borders |
                    ImGuiTableFlags.RowBg |
                    ImGuiTableFlags.Resizable |
                    ImGuiTableFlags.ScrollY;
        if (!ImGui.BeginTable("AcquisitionWorkbenchQueuedLines", 6, flags, new Vector2(0, tableHeight)))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Mode", ImGuiTableColumnFlags.WidthFixed, 112);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 108);
        ImGui.TableSetupColumn("Max Unit", ImGuiTableColumnFlags.WidthFixed, 88);
        ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, 64);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 138);
        ImGui.TableHeadersRow();

        for (var index = 0; index < draft.Lines.Count; index++)
        {
            var line = draft.Lines[index];
            var selected = index == selectedLineIndex;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(selected ? ColSuccess : ColMuted, selected ? ">" : " ");
            ImGui.SameLine();
            ImGui.TextUnformatted(FormatQueuedItem(line));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(MarketAcquisitionQuantityModePresenter.FormatMode(line.QuantityMode));
            ImGui.TableNextColumn();
            var quantity = line.QuantityMode == "TargetQuantity" ? line.TargetQuantity : line.MaxQuantity;
            ImGui.TextUnformatted(MarketAcquisitionQuantityModePresenter.FormatQuantity(line.QuantityMode, quantity));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatGil(line.MaxUnitPrice));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.HqPolicy);
            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"Select##workbenchSelect{index}"))
            {
                selectedLineIndex = index;
                activePane = WorkbenchPane.Appraise;
            }

            ImGui.SameLine();
            if (ImGui.SmallButton($"Duplicate##workbenchDuplicate{index}"))
                DuplicateLine(index);
            ImGui.SameLine();
            if (ImGui.SmallButton($"Remove##workbenchRemove{index}"))
                RemoveLine(index);
        }

        ImGui.EndTable();
    }

    private void DrawRunPane()
    {
        var snapshot = getRouteSnapshot();
        ImGui.TextColored(GetRouteStateColor(snapshot.State), snapshot.StatusMessage);

        if (snapshot.HasPreparedPlan)
        {
            ImGui.TextColored(
                ColMuted,
                $"Prepared {snapshot.PreparedWorldCount:N0} world(s), {snapshot.PreparedQuantity:N0} item(s), {FormatGil(snapshot.PreparedGil)}.");
        }
        else
        {
            ImGui.TextColored(ColMuted, "No market plan prepared.");
        }

        if (!string.IsNullOrWhiteSpace(snapshot.ActiveWorld))
        {
            ImGui.TextColored(
                ColHeader,
                $"Active world: {snapshot.ActiveWorld} - planned {snapshot.ActiveWorldPlannedQuantity:N0} item(s), {FormatGil(snapshot.ActiveWorldPlannedGil)}");
        }

        DrawRunActions(snapshot);
        ImGui.Spacing();
        DrawRouteRows(snapshot);
    }

    private void DrawRecoverPane()
    {
        var snapshot = getRouteSnapshot();
        ImGui.TextColored(GetRouteStateColor(snapshot.State), snapshot.RecoverySummary);
        ImGui.TextWrapped(snapshot.RecoveryDetail);

        ImGui.Spacing();
        DrawRecoveryStatusTable(snapshot);
        ImGui.Spacing();
        DrawRunActions(snapshot);
        ImGui.Spacing();
        DrawRouteRows(snapshot);
    }

    private void DrawRunActions(AcquisitionWorkbenchRouteSnapshot snapshot)
    {
        if (ImGuiUi.Button("Prepare Plan", snapshot.CanPrepare))
            _ = prepareRoute();
        ImGui.SameLine();
        if (ImGuiUi.Button("Start Route", snapshot.CanStart))
            _ = startRoute(false);
        ImGui.SameLine();
        if (ImGuiUi.Button("Start Diagnostics", snapshot.CanStartWithDiagnostics))
            _ = startRoute(true);

        if (snapshot.CanResume)
        {
            if (ImGuiUi.Button("Resume", true))
                _ = resumeRoute();
        }
        else
        {
            if (ImGuiUi.Button("Pause", snapshot.CanPause))
                _ = pauseRoute();
        }

        ImGui.SameLine();
        if (ImGuiUi.Button("Stop", snapshot.CanStop))
            _ = stopRoute();
        ImGui.SameLine();
        if (ImGuiUi.Button("Restart", snapshot.CanRestart))
            _ = restartRoute();
        ImGui.SameLine();
        if (ImGuiUi.Button("Re-prepare Route", snapshot.CanReprepare))
            _ = reprepareRoute();
    }

    private static void DrawRouteRows(AcquisitionWorkbenchRouteSnapshot snapshot)
    {
        if (snapshot.RouteRows.Count == 0)
        {
            ImGui.TextColored(ColMuted, "Start after preparing a plan. Routes travel, validate live listings, and purchase safe rows automatically.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.LastDiagnosticFilePath))
            ImGui.TextColored(ColMuted, $"Diagnostics: {snapshot.LastDiagnosticFilePath}");

        var tableHeight = MathF.Max(170f, ImGui.GetContentRegionAvail().Y - 24f);
        var flags = ImGuiUi.InteractiveTableFlags | ImGuiTableFlags.ScrollX;
        if (!ImGui.BeginTable("AcquisitionWorkbenchRouteRows", 6, flags, new Vector2(0, tableHeight)))
            return;

        ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, 130);
        ImGui.TableSetupColumn("Data Center", ImGuiTableColumnFlags.WidthFixed, 96);
        ImGui.TableSetupColumn("Lines", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 94);
        ImGui.TableSetupColumn("Result", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("Notes", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        foreach (var row in snapshot.RouteRows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.WorldName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.DataCenter);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.RouteLines);
            if (!string.IsNullOrWhiteSpace(row.LineMix))
            {
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(row.LineMix);
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.State);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Result);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Notes);

            foreach (var line in row.Lines)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(ColMuted, "  Item");
                ImGui.TableNextColumn();
                ImGui.TextColored(ColMuted, line.Source);
                ImGui.TableNextColumn();
                ImGui.TextColored(ColMuted, line.Item);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(line.State);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{line.Planned} / {line.Discovered} / {line.Bought}");
                ImGui.TableNextColumn();
                ImGui.TextColored(line.State.Equals("Blocked", StringComparison.OrdinalIgnoreCase) ? ColError : ColMuted, line.Notes);
            }
        }

        ImGui.EndTable();
    }

    private static void DrawRecoveryStatusTable(AcquisitionWorkbenchRouteSnapshot snapshot)
    {
        if (!ImGui.BeginTable("AcquisitionWorkbenchRecoveryStatus", 4, ImGuiTableFlags.SizingStretchSame))
            return;

        DrawRecoveryMetric("Route State", snapshot.State);
        DrawRecoveryMetric(
            "Prepared Plan",
            snapshot.HasPreparedPlan
                ? $"{snapshot.PreparedWorldCount:N0} world(s), {snapshot.PreparedQuantity:N0} item(s)"
                : "None");
        DrawRecoveryMetric(
            "Completed / Probed",
            snapshot.CompletedOrProbedWorldCount > 0
                ? snapshot.CompletedOrProbedWorldCount.ToString("N0")
                : "None");
        DrawRecoveryMetric(
            "Active World",
            string.IsNullOrWhiteSpace(snapshot.ActiveWorld)
                ? "None"
                : $"{snapshot.ActiveWorld}, {snapshot.ActiveWorldPlannedQuantity:N0} item(s)");

        ImGui.EndTable();

        if (snapshot.LatestWorldCompletionSummary is { } latestWorld)
        {
            ImGui.TextColored(
                ColMuted,
                $"Last world: {latestWorld.WorldName} - bought {latestWorld.PurchasedQuantity:N0}, spent {FormatGil(latestWorld.SpentGil)}.");
        }

        if (snapshot.LastRunSummary is { } runSummary)
        {
            ImGui.TextColored(
                ColMuted,
                $"Last run: bought {runSummary.PurchasedQuantity:N0}, spent {FormatGil(runSummary.SpentGil)}, completed {runSummary.CompletedWorldCount:N0} world(s).");
        }
    }

    private static void DrawRecoveryMetric(string label, string value)
    {
        ImGui.TableNextColumn();
        ImGui.TextColored(ColMuted, label);
        ImGui.TextUnformatted(value);
    }

    private void DrawAppraisePane()
    {
        var selected = ResolveSelectedLine();
        DrawLineSelector(selected);

        var state = selected is null ? null : GetStockState(selected);
        var view = StockAvailabilityPanelPresenter.Build(new StockAvailabilityPanelState
        {
            SelectedLine = selected,
            Result = state?.Result,
            Source = state?.Source ?? StockAvailabilityPanelSource.None,
            SnapshotFetchedAtUtc = state?.SnapshotFetchedAtUtc,
            NowUtc = DateTimeOffset.UtcNow,
            IsFetching = state?.IsFetching == true,
            ErrorMessage = state?.ErrorMessage,
        });

        ImGui.TextColored(ToColor(view.Severity), view.Headline);
        ImGui.TextWrapped(view.Detail);
        if (!string.IsNullOrWhiteSpace(view.SourceLine))
            ImGui.TextColored(ColMuted, view.SourceLine);

        ImGui.Spacing();
        var routeScopeError = ResolveStockRouteScopeError();
        if (!string.IsNullOrWhiteSpace(routeScopeError))
            ImGui.TextColored(ColError, routeScopeError);

        var canCheck = selected is not null &&
                       state?.IsFetching != true &&
                       string.IsNullOrWhiteSpace(routeScopeError);
        if (ImGuiUi.Button("Check Stock", canCheck))
            _ = CheckStockAsync(forceRefresh: false);
        ImGui.SameLine();
        if (ImGuiUi.Button("Refresh Stock", canCheck))
            _ = CheckStockAsync(forceRefresh: true);

        ImGui.Spacing();
        DrawEligibleListingPreview(state?.Result);
    }

    private void DrawLineSelector(MarketAcquisitionQuickShopLineDraft? selected)
    {
        if (draft.Lines.Count == 0)
        {
            ImGui.TextColored(ColMuted, "No queued lines.");
            return;
        }

        selectedLineIndex = Math.Clamp(selectedLineIndex, 0, draft.Lines.Count - 1);
        var preview = selected is null ? "Select a line" : FormatQueuedItem(selected);
        ImGui.TextColored(ColMuted, "Selected line");
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##workbenchStockLine", preview))
            return;

        for (var index = 0; index < draft.Lines.Count; index++)
        {
            var line = draft.Lines[index];
            var isSelected = index == selectedLineIndex;
            if (ImGui.Selectable($"{FormatQueuedItem(line)}##workbenchStockLine{index}", isSelected))
                selectedLineIndex = index;
            if (isSelected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private static void DrawEligibleListingPreview(StockAvailabilityResult? result)
    {
        if (result?.EligibleListings.Count > 0 != true)
            return;

        ImGui.TextColored(ColHeader, "Eligible Listings");
        var tableHeight = MathF.Min(220f, MathF.Max(120f, ImGui.GetContentRegionAvail().Y));
        if (!ImGui.BeginTable("AcquisitionWorkbenchEligibleListings", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(0, tableHeight)))
            return;

        ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Unit", ImGuiTableColumnFlags.WidthFixed, 92);
        ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, 42);
        ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        foreach (var listing in result.EligibleListings.Take(20))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(listing.WorldName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(listing.Quantity.ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatGil(listing.UnitPrice));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(listing.IsHq ? "HQ" : "NQ");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(listing.RetainerName);
        }

        ImGui.EndTable();
    }

    private void DrawSubmit(
        MarketAcquisitionQuickShopScope scope,
        MarketAcquisitionQuickShopValidationResult validation)
    {
        if (isRouteActive())
        {
            ImGui.TextColored(ColMuted, "A guided route is active. Finish or stop it before syncing a new draft.");
        }
        else if (!scope.HasScope)
        {
            var message = scope.IsTemporarilyUnavailable
                ? "Character scope is temporarily unavailable during route travel."
                : "Log into a character before syncing a route.";
            ImGui.TextColored(ColError, message);
        }
        else if (validation.IsValid)
        {
            ImGui.TextColored(ColSuccess, "Ready to sync, claim, and accept automatically.");
        }

        if (ImGuiUi.Button("Sync Route", !isBusy() && !isRouteActive() && validation.IsValid))
            _ = SubmitAsync();
        ImGui.SameLine();
        if (ImGuiUi.Button("Clear Draft", HasDraftInput))
            ClearDraft();
    }

    private void DrawPhaseStrip()
    {
        DrawPhaseButton(WorkbenchPane.Build, "Build");
        ImGui.SameLine();
        DrawPhaseButton(WorkbenchPane.Appraise, "Appraise");
        ImGui.SameLine();
        DrawPhaseButton(WorkbenchPane.Run, "Run");
        ImGui.SameLine();
        DrawPhaseButton(WorkbenchPane.Recover, "Recover");
    }

    private void DrawPhaseButton(WorkbenchPane pane, string label)
    {
        var active = activePane == pane;
        if (active)
            ImGui.PushStyleColor(ImGuiCol.Button, ColHeader);
        if (ImGui.Button($"{label}##workbenchPane{pane}"))
            activePane = pane;
        if (active)
            ImGui.PopStyleColor();
    }

    private async Task SubmitAsync()
    {
        var submittedDraft = draft;
        if (await createRoute(submittedDraft).ConfigureAwait(false))
            ClearDraft();
    }

    private void ApplyRouteScope(AcquisitionRouteScope scope)
    {
        draft = draft.WithNextRevision() with
        {
            Region = scope.Region,
            WorldMode = scope.WorldMode,
            SweepScope = scope.SweepScope,
            SweepDataCenters = scope.SweepDataCenters.ToList(),
        };
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
        ImGui.InputText($"##workbench{label}", ref value, 128);
    }

    private static void DrawIndexedCombo(string label, IReadOnlyList<string> options, ref int index)
    {
        ImGui.TextColored(ColMuted, label);
        var current = options[Math.Clamp(index, 0, options.Count - 1)];
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo($"##workbench{label}", current))
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

    private bool CanAddLine() =>
        ResolveSelectedItem() is not null &&
        TryParseUInt(maxUnitPriceBuffer, out var maxUnitPrice) &&
        maxUnitPrice > 0 &&
        (QuantityModes[quantityModeIndex] != "TargetQuantity" ||
         TryParseUInt(targetQuantityBuffer, out var targetQuantity) && targetQuantity > 0) &&
        (string.IsNullOrWhiteSpace(gilCapBuffer) || TryParseUInt(gilCapBuffer, out _)) &&
        (string.IsNullOrWhiteSpace(maxQuantityBuffer) || TryParseUInt(maxQuantityBuffer, out _));

    private void AddLineFromBuffers()
    {
        var item = ResolveSelectedItem();
        if (item is null)
            return;

        _ = TryParseUInt(targetQuantityBuffer, out var targetQuantity);
        _ = TryParseUInt(maxQuantityBuffer, out var maxQuantity);
        _ = TryParseUInt(maxUnitPriceBuffer, out var maxUnitPrice);
        _ = TryParseUInt(gilCapBuffer, out var gilCap);

        var quantityMode = QuantityModes[quantityModeIndex];
        var lines = draft.Lines.ToList();
        lines.Add(new MarketAcquisitionQuickShopLineDraft
        {
            ItemId = item.ItemId,
            ItemName = item.Name,
            QuantityMode = quantityMode,
            TargetQuantity = quantityMode == "TargetQuantity" ? targetQuantity : 0,
            MaxQuantity = quantityMode == "AllBelowThreshold" ? maxQuantity : 0,
            HqPolicy = HqPolicies[hqPolicyIndex],
            MaxUnitPrice = maxUnitPrice,
            GilCap = gilCap,
        });

        draft = draft.WithNextRevision() with { Lines = lines };
        selectedLineIndex = lines.Count - 1;
        ClearLineBuffers();
    }

    private void DuplicateLine(int index)
    {
        if (index < 0 || index >= draft.Lines.Count)
            return;

        var lines = draft.Lines.ToList();
        lines.Insert(index + 1, lines[index]);
        draft = draft.WithNextRevision() with { Lines = lines };
        selectedLineIndex = index + 1;
    }

    private void RemoveLine(int index)
    {
        if (index < 0 || index >= draft.Lines.Count)
            return;

        var lines = draft.Lines.ToList();
        lines.RemoveAt(index);
        draft = draft.WithNextRevision() with { Lines = lines };
        selectedLineIndex = Math.Clamp(selectedLineIndex, 0, Math.Max(0, lines.Count - 1));
    }

    private void ClearDraft()
    {
        draft = MarketAcquisitionQuickShopDraft.CreateDefault();
        selectedLineIndex = 0;
        lock (stockStateGate)
            stockStates.Clear();
        ClearLineBuffers();
    }

    private bool HasLineInput() =>
        !string.IsNullOrWhiteSpace(itemAutocomplete.SearchBuffer) ||
        !string.IsNullOrWhiteSpace(targetQuantityBuffer) ||
        !string.IsNullOrWhiteSpace(maxQuantityBuffer) ||
        !string.IsNullOrWhiteSpace(maxUnitPriceBuffer) ||
        !string.IsNullOrWhiteSpace(gilCapBuffer);

    private void ClearLineBuffers()
    {
        itemAutocomplete.SearchBuffer = string.Empty;
        itemAutocomplete.SelectedItem = null;
        targetQuantityBuffer = string.Empty;
        maxQuantityBuffer = string.Empty;
        maxUnitPriceBuffer = string.Empty;
        gilCapBuffer = string.Empty;
    }

    private static bool TryParseUInt(string value, out uint parsed) =>
        uint.TryParse(value?.Trim(), out parsed);

    private AcquisitionItemOption? ResolveSelectedItem() =>
        ItemAutocompletePresenter.ResolveSelectedItem(
            itemOptions,
            itemAutocomplete.SearchBuffer,
            itemAutocomplete.SelectedItem);

    private string FormatQueuedItem(MarketAcquisitionQuickShopLineDraft line)
    {
        if (string.IsNullOrWhiteSpace(line.ItemName))
            return $"Item {line.ItemId}";

        var option = itemOptions.FirstOrDefault(item => item.ItemId == line.ItemId);
        return option is null
            ? line.ItemName
            : ItemAutocompletePresenter.FormatDisplayName(itemOptions, option);
    }

    private static string FormatRouteMode(MarketAcquisitionQuickShopDraft draft)
    {
        if (draft.WorldMode != "AllWorldSweep")
            return "Recommended";

        return draft.SweepScope switch
        {
            "DataCenters" when draft.SweepDataCenters.Count > 0 => $"Sweep: {string.Join(", ", draft.SweepDataCenters)}",
            "CurrentDataCenter" => "Sweep: current DC",
            _ => "Sweep: region",
        };
    }

    private static string FormatGil(uint gil) => $"{gil:N0} gil";

    private MarketAcquisitionQuickShopLineDraft? ResolveSelectedLine()
    {
        if (draft.Lines.Count == 0)
            return null;

        selectedLineIndex = Math.Clamp(selectedLineIndex, 0, draft.Lines.Count - 1);
        return draft.Lines[selectedLineIndex];
    }

    private WorkbenchStockState? GetStockState(MarketAcquisitionQuickShopLineDraft line)
    {
        lock (stockStateGate)
        {
            stockStates.TryGetValue(BuildStockStateKey(line), out var state);
            return state;
        }
    }

    private async Task CheckStockAsync(bool forceRefresh)
    {
        var line = ResolveSelectedLine();
        if (line is null)
            return;

        var scope = getScope();
        if (!scope.HasScope)
        {
            SetStockState(BuildStockStateKey(line, string.Empty), new WorkbenchStockState
            {
                ErrorMessage = "Current character world is required before checking stock.",
            });
            return;
        }

        AcquisitionWorkbenchStockCheckContext check;
        try
        {
            check = AcquisitionWorkbenchStockRequestBuilder.BuildCheckContext(draft, line, scope.World);
        }
        catch (Exception ex)
        {
            SetStockState(BuildStockStateKey(line, scope.World), new WorkbenchStockState
            {
                ErrorMessage = ex.Message,
            });
            return;
        }

        var stateKey = check.StateKey;
        SetStockState(stateKey, new WorkbenchStockState { IsFetching = true });

        try
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var lookup = forceRefresh
                ? new ObservedMarketSnapshotLookup { Status = ObservedMarketSnapshotLookupStatus.Miss }
                : observedMarketSnapshots.TryGet(check.SnapshotKey, nowUtc);
            ObservedMarketSnapshot snapshot;
            StockAvailabilityPanelSource source;

            if (lookup.Found && lookup.Snapshot is not null)
            {
                snapshot = lookup.Snapshot;
                source = StockAvailabilityPanelSource.Cache;
            }
            else
            {
                var listings = await fetchListings(check.Region, check.ItemId, 100, CancellationToken.None)
                    .ConfigureAwait(false);
                nowUtc = DateTimeOffset.UtcNow;
                observedMarketSnapshots.Replace(
                    check.SnapshotKey,
                    listings,
                    nowUtc,
                    "Universalis listings fetched on demand.",
                    forceRefresh ? "ManualRefresh" : "Fetched",
                    $"{listings.Count:N0} listing(s) fetched for {check.ItemName}.");
                snapshot = observedMarketSnapshots.TryGet(check.SnapshotKey, nowUtc).Snapshot
                    ?? throw new InvalidOperationException("Fresh stock snapshot was not available after fetch.");
                source = StockAvailabilityPanelSource.FreshFetch;
            }

            var result = StockAvailabilityService.Analyze(
                check.AnalyzeRequest,
                snapshot.Listings);
            SetStockState(stateKey, new WorkbenchStockState
            {
                Result = result,
                Source = source,
                SnapshotFetchedAtUtc = snapshot.FetchedAtUtc,
            });
        }
        catch (Exception ex)
        {
            SetStockState(stateKey, new WorkbenchStockState
            {
                ErrorMessage = ex.Message,
            });
        }
    }

    private void SetStockState(string stateKey, WorkbenchStockState state)
    {
        lock (stockStateGate)
            stockStates[stateKey] = state;
    }

    private string BuildStockStateKey(MarketAcquisitionQuickShopLineDraft line)
    {
        var scope = getScope();
        return BuildStockStateKey(line, scope.World);
    }

    private string BuildStockStateKey(MarketAcquisitionQuickShopLineDraft line, string currentWorld)
    {
        try
        {
            return AcquisitionWorkbenchStockRequestBuilder.BuildCheckContext(draft, line, currentWorld).StateKey;
        }
        catch
        {
            return $"{line.ItemId}|invalid-scope|{line.QuantityMode}|{line.HqPolicy}|{line.TargetQuantity}|{line.MaxQuantity}|{line.MaxUnitPrice}";
        }
    }

    private bool HasCurrentStockResult() =>
        ResolveSelectedLine() is { } line && GetStockState(line)?.Result is not null;

    private string? ResolveStockRouteScopeError()
    {
        var scope = getScope();
        var validation = MarketAcquisitionQuickShopDraftValidator.Validate(
            draft,
            config.ApiKey,
            scope.CharacterName,
            scope.World);
        return validation.Errors.FirstOrDefault(error =>
            error.Contains("data center", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("Sweep scope", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("World mode", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("Region", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("Current world", StringComparison.OrdinalIgnoreCase));
    }

    private string FormatStockMetric()
    {
        var line = ResolveSelectedLine();
        if (line is null)
            return "No line";

        var state = GetStockState(line);
        if (state?.Result is null)
            return state?.IsFetching == true ? "Checking" : "Not checked";

        return state.Result.Status == StockAvailabilityStatus.Depth
            ? $"{state.Result.EligibleQuantity:N0} depth"
            : $"{state.Result.EligibleQuantity:N0}/{state.Result.RequiredQuantity.GetValueOrDefault():N0}";
    }

    private Vector4 ToColor(StockAvailabilityPanelSeverity severity) =>
        severity switch
        {
            StockAvailabilityPanelSeverity.Success => ColSuccess,
            StockAvailabilityPanelSeverity.Warning => new Vector4(1.00f, 0.76f, 0.32f, 1f),
            StockAvailabilityPanelSeverity.Error => ColError,
            _ => ColMuted,
        };

    private static Vector4 GetRouteStateColor(string state) =>
        state switch
        {
            "Running" => ColSuccess,
            "Paused" => ColHeader,
            "Stopped" or "Failed" => ColError,
            "Completed" => ColSuccess,
            _ => ColMuted,
        };

    private enum WorkbenchPane
    {
        Build,
        Appraise,
        Run,
        Recover,
    }

    private sealed record WorkbenchStockState
    {
        public StockAvailabilityResult? Result { get; init; }
        public StockAvailabilityPanelSource Source { get; init; }
        public DateTimeOffset? SnapshotFetchedAtUtc { get; init; }
        public bool IsFetching { get; init; }
        public string? ErrorMessage { get; init; }
    }
}
