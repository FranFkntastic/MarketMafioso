using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Tables;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Windows.Main;
using MarketMafioso.MarketAcquisition.ExactAuthority;

namespace MarketMafioso.Windows.MarketAcquisitionPanels;

internal sealed class MarketAcquisitionGuidedRoutePanel
{
    private readonly Func<MarketAcquisitionRouteEngineSnapshot> getRouteSnapshot;
    private readonly Action startRoute;
    private readonly Func<MarketAcquisitionPlan?, Action?> createEvidenceRefreshConfirmation;
    private readonly Func<bool> canObserveCurrentBoard;
    private readonly Action observeCurrentBoard;
    private readonly Action pauseRoute;
    private readonly Action resumeRoute;
    private readonly Action recoverRoute;
    private readonly Action stopRoute;
    private readonly Action restartRoute;
    private readonly Action reprepareRoute;
    private readonly Func<bool, MarketPurchaseTerminalResolutionResult> reconcileTerminalPurchase;
    private readonly Action retryExactAcquisitionRecovery;
    private readonly Action<MarketAcquisitionRouteEngineSnapshot> drawPostRunDiagnosticSummary;
    private readonly Action<MarketAcquisitionRouteEngineSnapshot> drawLatestWorldCompletionSummary;
    private readonly Action<MarketAcquisitionRouteEngineSnapshot> drawMarketBoardProbeStatus;
    private readonly Action openLogsFolder;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly Action? forgetShardLedger;
    private readonly Action? discardRecoveryBlock;
    private readonly Action? forgetTravelLease;
    private readonly DalamudExpandableRows expandedStops = new();
    private Action? confirmedEvidenceRefresh;
    private string evidenceRefreshWorlds = string.Empty;
    private bool openEvidenceRefreshConfirmation;
    private bool closeEvidenceRefreshConfirmation;

    public MarketAcquisitionGuidedRoutePanel(
        Func<MarketAcquisitionRouteEngineSnapshot> getRouteSnapshot,
        Action startRoute,
        Func<MarketAcquisitionPlan?, Action?> createEvidenceRefreshConfirmation,
        Func<bool> canObserveCurrentBoard,
        Action observeCurrentBoard,
        Action pauseRoute,
        Action resumeRoute,
        Action recoverRoute,
        Action stopRoute,
        Action restartRoute,
        Action reprepareRoute,
        Func<bool, MarketPurchaseTerminalResolutionResult> reconcileTerminalPurchase,
        Action retryExactAcquisitionRecovery,
        Action<MarketAcquisitionRouteEngineSnapshot> drawPostRunDiagnosticSummary,
        Action<MarketAcquisitionRouteEngineSnapshot> drawLatestWorldCompletionSummary,
        Action<MarketAcquisitionRouteEngineSnapshot> drawMarketBoardProbeStatus,
        Action openLogsFolder,
        AgentBridgeUiReviewRegistry reviewRegistry,
        Action? forgetShardLedger = null,
        Action? discardRecoveryBlock = null,
        Action? forgetTravelLease = null)
    {
        this.forgetShardLedger = forgetShardLedger;
        this.discardRecoveryBlock = discardRecoveryBlock;
        this.forgetTravelLease = forgetTravelLease;
        this.getRouteSnapshot = getRouteSnapshot ?? throw new ArgumentNullException(nameof(getRouteSnapshot));
        this.startRoute = startRoute ?? throw new ArgumentNullException(nameof(startRoute));
        this.createEvidenceRefreshConfirmation = createEvidenceRefreshConfirmation ?? throw new ArgumentNullException(nameof(createEvidenceRefreshConfirmation));
        this.canObserveCurrentBoard = canObserveCurrentBoard ?? throw new ArgumentNullException(nameof(canObserveCurrentBoard));
        this.observeCurrentBoard = observeCurrentBoard ?? throw new ArgumentNullException(nameof(observeCurrentBoard));
        this.pauseRoute = pauseRoute ?? throw new ArgumentNullException(nameof(pauseRoute));
        this.resumeRoute = resumeRoute ?? throw new ArgumentNullException(nameof(resumeRoute));
        this.recoverRoute = recoverRoute ?? throw new ArgumentNullException(nameof(recoverRoute));
        this.stopRoute = stopRoute ?? throw new ArgumentNullException(nameof(stopRoute));
        this.restartRoute = restartRoute ?? throw new ArgumentNullException(nameof(restartRoute));
        this.reprepareRoute = reprepareRoute ?? throw new ArgumentNullException(nameof(reprepareRoute));
        this.reconcileTerminalPurchase = reconcileTerminalPurchase ?? throw new ArgumentNullException(nameof(reconcileTerminalPurchase));
        this.retryExactAcquisitionRecovery = retryExactAcquisitionRecovery ?? throw new ArgumentNullException(nameof(retryExactAcquisitionRecovery));
        this.drawPostRunDiagnosticSummary = drawPostRunDiagnosticSummary ?? throw new ArgumentNullException(nameof(drawPostRunDiagnosticSummary));
        this.drawLatestWorldCompletionSummary = drawLatestWorldCompletionSummary ?? throw new ArgumentNullException(nameof(drawLatestWorldCompletionSummary));
        this.drawMarketBoardProbeStatus = drawMarketBoardProbeStatus ?? throw new ArgumentNullException(nameof(drawMarketBoardProbeStatus));
        this.openLogsFolder = openLogsFolder ?? throw new ArgumentNullException(nameof(openLogsFolder));
        this.reviewRegistry = reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry));
    }

    /// <param name="drawPipelineActions">When set, replaces the ordinary action row (the cross-regional pipeline owns Start/Stop).</param>
    public void Draw(
        MarketAcquisitionPlan? plan,
        bool isPlanStale,
        bool canRefreshEvidence,
        Action? drawPipelineActions = null,
        bool suppressIdleRouteSummary = false)
    {
        var snapshot = getRouteSnapshot();
        // The cross-regional pipeline owns start blockers and status while no local route exists.
        var pipelineOwnsIdleView = suppressIdleRouteSummary && !snapshot.IsRunning && !snapshot.IsPaused && snapshot.Stops.Count == 0;
        ImGuiUi.SectionHeader("Route", MarketMafiosoUiTheme.Header);
        if (ImGuiUi.Button("Open Logs Folder##MarketAcquisitionOpenLogsFolder", true))
            openLogsFolder();
        reviewRegistry.RegisterLastButton(
            "acquisition.route.open-logs-folder",
            "Open the date-grouped Market Acquisition logs folder",
            true,
            openLogsFolder);

        var canStart = plan is { Status: "Ready" } &&
                       !isPlanStale &&
                       plan.WorldBatches.Count > 0 &&
                       !snapshot.IsRunning &&
                       !snapshot.IsPaused &&
                       snapshot.RecoveryBlockedReason == null;
        var canReprepare = canStart &&
                            snapshot.CanRestart &&
                            snapshot.CompletedOrProbedStopCount > 0;
        DrawExactAcquisitionExecution(snapshot, inlineRetry: drawPipelineActions is not null);
        DrawShardStorageCheckpoint(snapshot);
        if (!DrawTerminalPurchaseRecovery(snapshot))
        {
            if (drawPipelineActions is not null)
                drawPipelineActions();
            else
                DrawGuidedRouteActionRow(snapshot, canStart, canReprepare, canRefreshEvidence, plan);
        }

        DrawEvidenceRefreshConfirmation(canRefreshEvidence);

        if (!pipelineOwnsIdleView)
            ImGui.TextColored(GetGuidedRouteStatusColor(snapshot), snapshot.StatusMessage);
        if (snapshot.RecoveryBlockedReason is { } recoveryBlockedReason)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Warning, recoveryBlockedReason);
            // Shown only when no purchase evidence is unresolved; pending evidence keeps its own block.
            if (discardRecoveryBlock is not null && snapshot.PurchaseEvidenceState is null)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Discard \u2014 I'll handle it##MarketAcquisitionDiscardRecoveryBlock"))
                    discardRecoveryBlock();
                RegisterLastControl(
                    "acquisition.route.discard-recovery-block",
                    "Discard the route recovery block and handle it in game",
                    true,
                    discardRecoveryBlock);
            }
        }
        if (snapshot.UnresolvedTravelTarget is { } travelTarget && forgetTravelLease is not null)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Warning, $"Lifestream travel to {travelTarget} unresolved");
            ImGui.SameLine();
            if (ImGui.SmallButton("Forget travel lease##MarketAcquisitionForgetTravelLease"))
                forgetTravelLease();
            RegisterLastControl(
                "acquisition.route.forget-travel-lease",
                "Forget the unresolved Lifestream travel lease",
                true,
                forgetTravelLease);
        }
        if (isPlanStale && !pipelineOwnsIdleView)
            ImGui.TextColored(MarketMafiosoUiTheme.Error, "Request changed after this plan was prepared. Prepare a fresh plan before starting.");
        drawPostRunDiagnosticSummary(snapshot);
        drawLatestWorldCompletionSummary(snapshot);

        if (snapshot.Stops.Count == 0)
        {
            if (!pipelineOwnsIdleView)
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Start after preparing a plan. Routes travel, validate live listings, and purchase safe rows automatically.");
            return;
        }

        if (snapshot.LastDiagnosticFilePath != null)
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"Diagnostics: {snapshot.LastDiagnosticFilePath}");

        var activeStop = snapshot.ActiveStop;
        if (activeStop == null)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Success, "Route is not actively executing.");
        }
        else
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Header, $"Next stop: {activeStop.WorldName}");
            ImGui.SameLine();
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"Planned {activeStop.PlannedQuantity:N0} item(s), {FormatGil(activeStop.PlannedGil)}");
        }

        DrawGuidedRouteStops(snapshot.Stops);
        drawMarketBoardProbeStatus(snapshot);
    }

    private bool DrawTerminalPurchaseRecovery(MarketAcquisitionRouteEngineSnapshot snapshot)
    {
        if (snapshot.RecoveryBlockedReason == null ||
            snapshot.PurchaseEvidenceState is not { } terminal ||
            terminal is PendingMarketPurchase)
        {
            return false;
        }

        var intent = terminal.Intent;
        ImGui.TextColored(
            MarketMafiosoUiTheme.Warning,
            $"Resolve listing {intent.ListingId}: {intent.Quantity:N0} item(s), {intent.TotalGil:N0} gil on {intent.WorldName}.");
        if (terminal is ConfirmedMarketPurchase)
        {
            if (ImGuiUi.PrimaryButton("Apply Confirmed Purchase##MarketAcquisitionApplyConfirmedPurchase", true))
                _ = reconcileTerminalPurchase(true);
            RegisterLastReconciliationControl(
                "acquisition.route.purchase-recovery.apply-confirmed",
                "Apply the server-confirmed purchase and unlock retained route recovery",
                purchaseOccurred: true);
            return true;
        }

        if (ImGuiUi.PrimaryButton("Purchase Happened##MarketAcquisitionReconcilePurchased", true))
            _ = reconcileTerminalPurchase(true);
        RegisterLastReconciliationControl(
            "acquisition.route.purchase-recovery.purchased",
            "Record the indeterminate purchase and unlock retained route recovery",
            purchaseOccurred: true);
        ImGui.SameLine();
        if (ImGuiUi.Button("Purchase Failed##MarketAcquisitionReconcileFailed", true))
            _ = reconcileTerminalPurchase(false);
        RegisterLastReconciliationControl(
            "acquisition.route.purchase-recovery.failed",
            "Record that the indeterminate purchase did not occur and unlock retained route recovery",
            purchaseOccurred: false);
        return true;
    }

    private void DrawGuidedRouteActionRow(
        MarketAcquisitionRouteEngineSnapshot snapshot,
        bool canStart,
        bool canReprepare,
        bool canRefreshEvidence,
        MarketAcquisitionPlan? plan)
    {
        var primaryAction = MarketAcquisitionGuidedRouteActionPresenter.Resolve(snapshot);
        if (primaryAction == MarketAcquisitionGuidedRoutePrimaryAction.RetryExactAcquisitionRecovery)
        {
            if (ImGuiUi.PrimaryButton("Retry Exact-Acquisition Recovery##ExactAcquisitionRecovery", true))
                retryExactAcquisitionRecovery();
            RegisterLastControl(
                "acquisition.route.exactAcquisition.retry-recovery",
                "Refresh and optimize the remaining exact-quality External plan route",
                true,
                retryExactAcquisitionRecovery);
            return;
        }
        if (primaryAction == MarketAcquisitionGuidedRoutePrimaryAction.ResumeManualPause)
        {
            if (ImGuiUi.PrimaryButton("Resume Route##MarketAcquisitionResumeRoute", true))
                resumeRoute();
            RegisterLastControl("acquisition.route.resume", "Resume the Market Acquisition route", true, resumeRoute);
            ImGui.SameLine();
            if (ImGuiUi.Button("Stop##MarketAcquisitionStopRoute", true))
                stopRoute();
            RegisterLastControl("acquisition.route.stop", "Stop the Market Acquisition route", true, stopRoute);
            return;
        }
        if (primaryAction == MarketAcquisitionGuidedRoutePrimaryAction.RecoverStoppedRoute)
        {
            if (ImGuiUi.PrimaryButton("Recover Route##MarketAcquisitionRecoverRoute", true))
                recoverRoute();
            RegisterLastControl(
                "acquisition.route.recover",
                "Recover the stopped Market Acquisition route from its retained stop and line progress",
                true,
                recoverRoute);
            return;
        }

        if (primaryAction == MarketAcquisitionGuidedRoutePrimaryAction.PauseActiveRoute)
        {
            if (ImGuiUi.PrimaryButton("Pause Route##MarketAcquisitionPauseRoute", true))
                pauseRoute();
            RegisterLastControl("acquisition.route.pause", "Pause the Market Acquisition route", true, pauseRoute);
            ImGui.SameLine();
            if (ImGuiUi.Button("Stop##MarketAcquisitionStopRoute", true))
                stopRoute();
            RegisterLastControl("acquisition.route.stop", "Stop the Market Acquisition route", true, stopRoute);
            return;
        }

        if (!canStart && !canRefreshEvidence)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Prepare a current plan to enable route actions.");
        }
        else
        {
            if (canStart)
            {
                if (ImGuiUi.PrimaryButton("Start Route##MarketAcquisitionStartRoute", true))
                    startRoute();
                RegisterLastControl("acquisition.route.start", "Start the Market Acquisition route", true, startRoute);

                ImGui.SameLine();
            }

            if (ImGuiUi.Button("Refresh Evidence (scan only, no buying)...##MarketAcquisitionRefreshEvidence", canRefreshEvidence))
                OpenEvidenceRefreshConfirmation(plan);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Travel to the selected/prepared worlds and scan market listings. Buys nothing and does not change your purchase request or limits. Confirmation required.");
            RegisterLastControl(
                "acquisition.route.refresh-evidence",
                "Visit the selected worlds and publish fresh market evidence without purchasing",
                canRefreshEvidence,
                () => OpenEvidenceRefreshConfirmation(plan));

            var canObserve = canObserveCurrentBoard();
            ImGui.SameLine();
            if (ImGuiUi.Button("Observe Current Board##MarketAcquisitionObserveCurrentBoard", canObserve))
                observeCurrentBoard();
            RegisterLastControl(
                "acquisition.observe-current-board",
                "Read and publish the current market-board item without purchasing",
                canObserve,
                observeCurrentBoard);
        }

        if (!snapshot.CanRestart && !canReprepare)
            return;

        if (!ImGui.TreeNode("Previous route actions##MarketAcquisitionPreviousRouteActions"))
            return;

        if (ImGuiUi.Button("Restart##MarketAcquisitionRestartRoute", snapshot.CanRestart))
            restartRoute();
        RegisterLastControl("acquisition.route.restart", "Restart the Market Acquisition route", snapshot.CanRestart, restartRoute);
        ImGui.SameLine();
        if (ImGuiUi.Button("Refresh Plan##MarketAcquisitionReprepareRoute", canReprepare))
            reprepareRoute();
        RegisterLastControl("acquisition.route.refresh-plan", "Refresh the Market Acquisition plan", canReprepare, reprepareRoute);
        ImGui.TreePop();
    }

    private void OpenEvidenceRefreshConfirmation(MarketAcquisitionPlan? plan)
    {
        confirmedEvidenceRefresh = createEvidenceRefreshConfirmation(plan);
        if (confirmedEvidenceRefresh is null || plan is null)
            return;
        evidenceRefreshWorlds = string.Join(", ", plan.WorldBatches.Select(batch => batch.WorldName));
        closeEvidenceRefreshConfirmation = false;
        openEvidenceRefreshConfirmation = true;
    }

    private void DrawEvidenceRefreshConfirmation(bool canRefreshEvidence)
    {
        const string popup = "Travel and scan only?##AcquisitionEvidenceRefreshConfirmation";
        if (openEvidenceRefreshConfirmation)
        {
            ImGui.OpenPopup(popup);
            openEvidenceRefreshConfirmation = false;
        }
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos + viewport.WorkSize * 0.5f,
            ImGuiCond.Always, new System.Numerics.Vector2(0.5f, 0.5f));
        if (!ImGui.BeginPopupModal(popup, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        if (closeEvidenceRefreshConfirmation)
        {
            closeEvidenceRefreshConfirmation = false;
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }
        ImGui.TextUnformatted("Evidence-only scan: travel, no purchasing.");
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 45f);
        ImGui.TextWrapped($"Travel to these selected/prepared worlds: {evidenceRefreshWorlds}");
        ImGui.PopTextWrapPos();
        ImGui.TextUnformatted("Buys nothing. Your purchase request and limits stay unchanged.");
        if (ImGuiUi.Button("Cancel##AcquisitionEvidenceRefreshCancel", true))
            CloseEvidenceRefreshConfirmation();
        RegisterLastControl("acquisition.route.refresh-evidence.cancel", "Cancel evidence-only travel without starting a route", true,
            CloseEvidenceRefreshConfirmation);
        ImGui.SameLine();
        var canConfirm = canRefreshEvidence && confirmedEvidenceRefresh is not null;
        if (ImGuiUi.Button("Travel and scan - buy nothing##AcquisitionEvidenceRefreshConfirm", canConfirm))
            ConfirmEvidenceRefresh();
        RegisterLastControl("acquisition.route.refresh-evidence.confirm", "Confirm travel and scanning only; buy nothing", canConfirm,
            ConfirmEvidenceRefresh);
        ImGui.EndPopup();
    }

    private void CloseEvidenceRefreshConfirmation()
    {
        confirmedEvidenceRefresh = null;
        closeEvidenceRefreshConfirmation = true;
    }

    private void ConfirmEvidenceRefresh()
    {
        var confirmedAction = confirmedEvidenceRefresh;
        CloseEvidenceRefreshConfirmation();
        confirmedAction?.Invoke();
    }

    private void DrawExactAcquisitionExecution(MarketAcquisitionRouteEngineSnapshot snapshot, bool inlineRetry)
    {
        if (snapshot.ExactAcquisitionExecution is not { } execution)
            return;
        var remaining = execution.Lines.Aggregate(0u, (sum, line) =>
            checked(sum + (line.RequiredQuantity - line.PurchasedQuantity)));
        ImGui.TextColored(
            execution.Phase is ExactAcquisitionRouteAuthorityPhase.Paused ? MarketMafiosoUiTheme.Warning : MarketMafiosoUiTheme.Muted,
            $"External plan {execution.Phase} · {remaining:N0} remaining · {execution.TotalSpentGil:N0} gil spent");
        // The ordinary action row carries the retry; a pipeline-owned row does not, so offer it here.
        if (inlineRetry &&
            MarketAcquisitionGuidedRouteActionPresenter.Resolve(snapshot) == MarketAcquisitionGuidedRoutePrimaryAction.RetryExactAcquisitionRecovery)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Retry recovery##ExactAcquisitionRetryRecoveryInline"))
                retryExactAcquisitionRecovery();
            RegisterLastControl(
                "acquisition.route.exactAcquisition.retry-recovery-inline",
                "Refresh and optimize the remaining exact-quality External plan route",
                true,
                retryExactAcquisitionRecovery);
        }
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, execution.Message);
    }

    private void DrawShardStorageCheckpoint(MarketAcquisitionRouteEngineSnapshot snapshot)
    {
        if (snapshot.ShardCheckpoint is not { IsEnabled: true } checkpoint)
            return;
        var outstanding = checkpoint.OutstandingByItem.Values.Sum();
        var failed = checkpoint.Phase == ShardAcquisitionCheckpointPhase.Failed;
        if (!checkpoint.IsActive && !failed && outstanding == 0)
            return;
        var color = failed
            ? MarketMafiosoUiTheme.Error
            : checkpoint.IsActive
                ? MarketMafiosoUiTheme.Warning
                : MarketMafiosoUiTheme.Muted;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(color, checkpoint.IsActive || failed
            ? $"Storing {outstanding:N0} purchased shards: {checkpoint.Message}"
            : $"{outstanding:N0} purchased shards awaiting the final storage run.");
        if (forgetShardLedger is null)
            return;
        ImGui.SameLine();
        if (ImGui.SmallButton("Forget shard ledger##MarketAcquisitionForgetShardLedger"))
            forgetShardLedger();
        RegisterLastControl(
            "acquisition.route.forget-shard-ledger",
            "Forget the purchased-shard storage ledger",
            true,
            forgetShardLedger);
    }

    private void DrawGuidedRouteStops(IReadOnlyList<MarketAcquisitionGuidedRouteStop> stops)
    {
        var rows = MarketAcquisitionRouteTablePresenter.BuildRows(stops);
        if (ImGui.BeginTable("MarketAcquisitionGuidedRouteStops", 7,
                DalamudNestedTableFlags.Parent(resizable: true) | ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable))
        {
            ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, 140);
            ImGui.TableSetupColumn("Data Center");
            ImGui.TableSetupColumn("Contribution (gil)");
            ImGui.TableSetupColumn("State");
            ImGui.TableSetupColumn("Planned");
            ImGui.TableSetupColumn("Bought");
            ImGui.TableSetupColumn("Notes");
            ImGui.TableHeadersRow();

            foreach (var row in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                expandedStops.DrawToggle(GetGuidedRouteStopKey(row));
                ImGui.SameLine();
                ImGui.TextUnformatted(row.WorldName);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatRouteDataCenter(row.DataCenter));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.RouteLines);
                if (!string.IsNullOrWhiteSpace(row.LineMix) && !string.Equals(row.LineMix, "No route lines", StringComparison.Ordinal))
                {
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(string.Join("\n", row.Lines.Select(line => $"{line.Item}: {line.Bought} · {line.State}")));
                }

                ImGui.TableNextColumn();
                ImGui.TextColored(GetGuidedRouteStopColor(row.State), row.State);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Intent);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Result);
                ImGui.TableNextColumn();
                ImGui.TextColored(row.Aggregate.FailedLineCount > 0 ? MarketMafiosoUiTheme.Error : MarketMafiosoUiTheme.Muted, row.Notes);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(row.Notes);

                expandedStops.DrawDetail(GetGuidedRouteStopKey(row), () => DrawGuidedRouteStopLineRows(row));
            }

            ImGui.EndTable();
        }
    }

    private static void DrawGuidedRouteStopLineRows(MarketAcquisitionRouteStopRow stop)
    {
        if (!ImGui.BeginTable("ItemDetails", 7, DalamudNestedTableFlags.Child)) return;
        foreach (var header in new[] { "Item", "Source", "Outcome", "Planned", "Seen", "Bought", "Notes" })
            ImGui.TableSetupColumn(header);
        ImGui.TableHeadersRow();

        foreach (var line in stop.Lines)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"  {line.Item}");
            ImGui.TableNextColumn();
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, line.Source);
            ImGui.TableNextColumn();
            ImGui.TextColored(GetRouteLineStateColor(line.State), line.State);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(line.State);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.Planned);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.Discovered);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.Bought);
            ImGui.TableNextColumn();
            ImGui.TextColored(line.State.Equals("Blocked", StringComparison.OrdinalIgnoreCase) ? MarketMafiosoUiTheme.Error : MarketMafiosoUiTheme.Muted, line.Notes);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(line.Notes);
        }
        ImGui.EndTable();
    }

    private static System.Numerics.Vector4 GetGuidedRouteStatusColor(MarketAcquisitionRouteEngineSnapshot snapshot)
    {
        var status = snapshot.StatusMessage;
        if (status.StartsWith("Unable", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("Cannot", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("Failed", StringComparison.OrdinalIgnoreCase))
            return MarketMafiosoUiTheme.Error;

        if (status.Contains("Waiting", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("Approve", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("Purchasing", StringComparison.OrdinalIgnoreCase) ||
            snapshot.IsPaused)
            return MarketMafiosoUiTheme.Header;

        if (status.Contains("Arrived", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("Recorded", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("complete", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("started", StringComparison.OrdinalIgnoreCase))
            return MarketMafiosoUiTheme.Success;

        return MarketMafiosoUiTheme.Muted;
    }

    private static System.Numerics.Vector4 GetGuidedRouteStopColor(string state) =>
        state switch
        {
            "Complete" or "Bought" => MarketMafiosoUiTheme.Success,
            "Partial" or "Buying" or "Traveling" or "Arrived" => MarketMafiosoUiTheme.Header,
            "Blocked" or "Failed" => MarketMafiosoUiTheme.Error,
            _ => MarketMafiosoUiTheme.Muted,
        };

    private static System.Numerics.Vector4 GetRouteLineStateColor(string state) =>
        state switch
        {
            "Complete" or "Purchasing" or "Buying" => MarketMafiosoUiTheme.Success,
            "Pending" => MarketMafiosoUiTheme.Muted,
            _ when state.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase) => MarketMafiosoUiTheme.Muted,
            _ when state.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
                   state.Equals("Blocked", StringComparison.OrdinalIgnoreCase) => MarketMafiosoUiTheme.Error,
            _ => MarketMafiosoUiTheme.Header,
        };

    private static string GetGuidedRouteStopKey(MarketAcquisitionRouteStopRow row) =>
        $"{row.WorldName}|{row.DataCenter}";

    private static string FormatGil(uint gil) => $"{gil:N0} gil";

    private static string FormatRouteDataCenter(string dataCenter) =>
        string.IsNullOrWhiteSpace(dataCenter) ? "-" : dataCenter;

    private void RegisterLastControl(string id, string label, bool enabled, Action invoke) =>
        reviewRegistry.RegisterLastButton(id, label, enabled, invoke);

    private void RegisterLastReconciliationControl(string id, string label, bool purchaseOccurred) =>
        reviewRegistry.RegisterLastAction(
            id,
            label,
            AgentBridgeUiControlKind.Button,
            enabled: true,
            selected: false,
            value: null,
            arguments: null,
            _ =>
            {
                var result = reconcileTerminalPurchase(purchaseOccurred);
                return result.IsResolved
                    ? AgentBridgeUiActionResult.Ok(result.Message, receipt: result)
                    : AgentBridgeUiActionResult.Fail(result.Message, result);
            });
}
