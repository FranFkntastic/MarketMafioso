using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Equipment;
using Franthropy.Dalamud.UI.Plots;
using MarketMafioso.AgentBridge;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Squire.Outfitter.Utility;
using MarketMafioso.Squire.Outfitter.Acquisition;
using MarketMafioso.Windows.Main;

namespace MarketMafioso.Windows.Squire;

internal sealed class MinerBotanistAdvisorPanel
{
    private static readonly MinerBotanistUtilityContextKind[] ContextOrder =
    [
        MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark,
        MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield,
        MinerBotanistUtilityContextKind.CollectableEfficiency,
    ];

    private readonly Configuration config;
    private readonly MinerBotanistAdvisorSession session;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly Action<OutfitterWorkbenchTransfer> stageTransfer;
    private readonly IMarketAcquisitionListingSource listingSource;
    private readonly ParetoFrontierPlotBuilder plotBuilder = new();
    private readonly DalamudPlotContainer plotContainer = new();
    private MinerBotanistUtilityContextKind context = MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark;
    private MinerBotanistReadOnlyAdvice? lastAdvice;
    private string? selectedSolutionId;
    private string? handoffStatus;
#if DEBUG
    private static readonly MinerBotanistAdvisorSyntheticScenarioKind[] SyntheticScenarioOrder =
    [
        MinerBotanistAdvisorSyntheticScenarioKind.Success,
        MinerBotanistAdvisorSyntheticScenarioKind.Refreshing,
        MinerBotanistAdvisorSyntheticScenarioKind.StaleEvidence,
        MinerBotanistAdvisorSyntheticScenarioKind.IncompleteEvidence,
        MinerBotanistAdvisorSyntheticScenarioKind.Abstention,
    ];
    private MinerBotanistReadOnlyAdvice? syntheticReviewAdvice;
    private MinerBotanistAdvisorDryRunFixture? dryRunFixture;
    private Task<MinerBotanistAdvisorDryRunFixture>? dryRunFixtureTask;
    private string? dryRunFixtureStatus;
    private MinerBotanistAdvisorSyntheticScenarioKind syntheticScenarioKind;
    private readonly HashSet<MinerBotanistUtilityContextKind> visibleSyntheticContexts =
        [MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark];
#endif

    public MinerBotanistAdvisorPanel(
        Configuration config,
        MinerBotanistAdvisorSession session,
        AgentBridgeUiReviewRegistry reviewRegistry,
        IMarketAcquisitionListingSource listingSource,
        Action<OutfitterWorkbenchTransfer> stageTransfer)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.reviewRegistry = reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry));
        this.listingSource = listingSource ?? throw new ArgumentNullException(nameof(listingSource));
        this.stageTransfer = stageTransfer ?? throw new ArgumentNullException(nameof(stageTransfer));
        if (Enum.TryParse<MinerBotanistUtilityContextKind>(config.Squire.OutfitterAdvisorContext, out var stored))
            context = stored;
    }

    public void Draw()
    {
#if DEBUG
        PumpDryRunFixture();
#endif
        var state = session.State;
        MinerBotanistReadOnlyAdvice? displayedAdvice = state.Advice;
#if DEBUG
        var syntheticReviewActive = syntheticReviewAdvice is not null;
        var syntheticPresentation = syntheticReviewAdvice is null
            ? null
            : MinerBotanistAdvisorSyntheticReview.Present(syntheticScenarioKind, syntheticReviewAdvice);
        displayedAdvice = syntheticPresentation is { ShowPriorFrontier: true }
            ? syntheticReviewAdvice
            : syntheticReviewActive ? null : displayedAdvice;
#endif
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Outfitter — cost / utility advisor");
        ImGui.TextWrapped(MinerBotanistReadOnlyAdvisor.AdvisoryRule);
        DrawControls(state);
#if DEBUG
        if (syntheticReviewActive)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Warning, dryRunFixture is null
                ? "DEBUG REPLAY — model decisions with frozen evidence prices"
                : "DEBUG INTEGRATION FIXTURE — current listing, permanently dry-run-only");
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, dryRunFixture?.Diagnostic ??
                "Item names are game data; only marketable components use Aether sale-history medians. No live character or live listing is used.");
            if (dryRunFixture is null)
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, MinerBotanistAdvisorSyntheticReview.PriceEvidenceLabel);
            ImGui.TextColored(syntheticPresentation!.AdviceIsRetained ? MarketMafiosoUiTheme.Warning : StatusColor(syntheticPresentation.Stage),
                syntheticPresentation.AdviceIsRetained
                    ? $"LAST VALID FRONTIER · {syntheticPresentation.Label}"
                    : syntheticPresentation.Label);
            if (syntheticPresentation.ShowProgress)
                ImGui.ProgressBar((float)syntheticPresentation.Completed / syntheticPresentation.Total, new Vector2(-1, 0),
                    $"{syntheticPresentation.Completed:N0} / {syntheticPresentation.Total:N0}");
            ImGui.TextColored(StatusColor(syntheticPresentation.Stage), syntheticPresentation.Message);
        }
        else
#endif
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, state.CoverageLabel);
            if (state.AdviceIsRetained)
                ImGui.TextColored(MarketMafiosoUiTheme.Warning,
                    RetainedAdviceLabel(state.Stage));
            if (state.IsBusy)
            {
                var fraction = state.Total is > 0 ? Math.Clamp((float)state.Completed / state.Total.Value, 0f, 1f) : 0f;
                ImGui.ProgressBar(fraction, new Vector2(-1, 0), state.Total is > 0
                    ? $"{state.Completed:N0} / {state.Total:N0}"
                    : state.Stage.ToString());
            }
            ImGui.TextColored(StatusColor(state.Stage), state.Message);
        }
        ImGui.Separator();

        if (displayedAdvice is not { Frontier: { } frontier } advice || frontier.Pareto.Frontier.Count == 0)
        {
#if DEBUG
            if (syntheticReviewActive)
            {
                ImGui.TextWrapped("No recommendation was produced; the advisor stopped at the displayed abstention boundary.");
                return;
            }
#endif
            DrawEmptyState(state);
            return;
        }
        EnsureSelection(advice);
        var selected = frontier.Pareto.Frontier.FirstOrDefault(value => value.Candidate.SolutionId == selectedSolutionId)
            ?? advice.Nomination
            ?? frontier.Pareto.Frontier[0];
#if DEBUG
        if (syntheticReviewActive)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Header, selected.VariantLabels.FirstOrDefault() ?? selected.Candidate.SolutionId);
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, string.Join(" · ", selected.VariantLabels.Skip(2)));
        }
#endif
        DrawDecisionSummary(advice, selected);
        DrawFrontier(advice, selected);
        DrawSolutionRail(advice, selected);
        DrawAdjacentTradeoffs(frontier.Pareto, selected);
        DrawSelectedLoadout(advice, selected);
        DrawAcquisitionChecklist(advice, selected);
    }

    private void DrawControls(MinerBotanistAdvisorSessionState state)
    {
        ImGui.SetNextItemWidth(230f);
        if (ImGui.BeginCombo("Context##SquireAdvisorContext", ContextLabel(context)))
        {
            foreach (var candidate in ContextOrder)
            {
                if (ImGui.Selectable(ContextLabel(candidate), candidate == context))
                    SetContext(candidate);
            }
            ImGui.EndCombo();
        }
        var contextMin = ImGui.GetItemRectMin();
        var contextMax = ImGui.GetItemRectMax();
        foreach (var candidate in ContextOrder)
        {
            var captured = candidate;
            reviewRegistry.Register(
                $"squire.outfitter.advisor.context.{candidate.ToString().ToLowerInvariant()}",
                $"Use {ContextLabel(candidate)}",
                AgentBridgeUiControlKind.Select,
                contextMin,
                contextMax,
                !state.IsBusy,
                candidate == context,
                ContextLabel(candidate),
                () => SetContext(captured));
        }
        ImGui.SameLine();
        if (!state.IsBusy)
        {
            if (ImGui.Button("Observe and refresh##SquireAdvisor"))
                Begin();
            RegisterLastControl(
                "squire.outfitter.advisor.refresh",
                "Observe rendered equipment and refresh exact-quality evidence",
                AgentBridgeUiControlKind.Button,
                true,
                false,
                null,
                Begin);
        }
        else
        {
            if (ImGui.Button("Cancel##SquireAdvisor"))
                session.Cancel();
            RegisterLastControl(
                "squire.outfitter.advisor.cancel",
                "Cancel the current advisor observation or market refresh",
                AgentBridgeUiControlKind.Button,
                true,
                false,
                null,
                session.Cancel);
        }
#if DEBUG
        if (!state.IsBusy)
        {
            ImGui.SameLine();
            var label = syntheticReviewAdvice is null
                ? "Load synthetic review##SquireAdvisorSynthetic"
                : "Return to live view##SquireAdvisorSynthetic";
            if (ImGui.Button(label))
                ToggleSyntheticReview();
            RegisterLastControl(
                "squire.outfitter.advisor.synthetic-review",
                syntheticReviewAdvice is null ? "Load synthetic advisor review" : "Return to live advisor view",
                AgentBridgeUiControlKind.Button,
                true,
                syntheticReviewAdvice is not null,
                syntheticReviewAdvice is null ? "live" : "synthetic",
                ToggleSyntheticReview);
        }
        if (syntheticReviewAdvice is not null)
        {
            DrawSyntheticScenarioControl();
            var canBuildDryRunFixture = config.EnableMarketAcquisitionDryRunTools && dryRunFixtureTask is null;
            if (ImGuiUi.Button("Build live dry-run fixture", canBuildDryRunFixture))
                BeginDryRunFixture();
            RegisterLastControl(
                "squire.outfitter.advisor.build-dry-run-fixture",
                "Build a current-listing Squire integration fixture restricted to dry-run execution",
                AgentBridgeUiControlKind.Button,
                canBuildDryRunFixture,
                dryRunFixtureTask is not null,
                dryRunFixture is null ? "not-built" : "ready",
                BeginDryRunFixture);
            if (!string.IsNullOrWhiteSpace(dryRunFixtureStatus))
                ImGui.TextColored(dryRunFixture is null ? MarketMafiosoUiTheme.Warning : MarketMafiosoUiTheme.Success, dryRunFixtureStatus);
            if (syntheticScenarioKind == MinerBotanistAdvisorSyntheticScenarioKind.Abstention)
                return;
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "PLOT SERIES");
            ImGui.SameLine();
            foreach (var candidate in ContextOrder)
            {
                var captured = candidate;
                var visible = visibleSyntheticContexts.Contains(candidate);
                var canToggle = !visible || visibleSyntheticContexts.Count > 1;
                if (!canToggle)
                    ImGui.BeginDisabled();
                if (ImGui.Checkbox($"{ContextSeriesLabel(candidate)}##SquireAdvisorSyntheticSeries{candidate}", ref visible))
                    SetSyntheticSeriesVisible(candidate, visible);
                if (!canToggle)
                    ImGui.EndDisabled();
                RegisterLastControl(
                    $"squire.outfitter.advisor.synthetic-series.{ContextSeriesId(candidate)}",
                    $"{(visible ? "Hide" : "Show")} {ContextLabel(candidate)} plot series",
                    AgentBridgeUiControlKind.Toggle,
                    canToggle,
                    visible,
                    visible ? "visible" : "hidden",
                    () => ToggleSyntheticSeries(captured));
                if (candidate != ContextOrder[^1])
                    ImGui.SameLine();
            }
        }
#endif
    }

    private void Begin()
    {
#if DEBUG
        syntheticReviewAdvice = null;
#endif
        var region = config.ActiveMarketAcquisitionRequestDocument?.Region;
        if (string.IsNullOrWhiteSpace(region))
            region = config.ActiveMarketAcquisitionClaim?.Region;
        session.Begin(context, string.IsNullOrWhiteSpace(region) ? "North America" : region);
    }

    private void SetContext(MinerBotanistUtilityContextKind value)
    {
        if (session.State.IsBusy)
            return;
#if DEBUG
        if (dryRunFixtureTask is not null)
            return;
#endif
        context = value;
        config.Squire.OutfitterAdvisorContext = value.ToString();
        config.Save();
        lastAdvice = null;
        selectedSolutionId = null;
#if DEBUG
        if (syntheticReviewAdvice is not null)
        {
            dryRunFixture = null;
            dryRunFixtureStatus = null;
            syntheticReviewAdvice = MinerBotanistAdvisorSyntheticReview.Build(context);
            visibleSyntheticContexts.Add(context);
        }
#endif
    }

#if DEBUG
    private void ToggleSyntheticReview()
    {
        dryRunFixture = null;
        dryRunFixtureTask = null;
        dryRunFixtureStatus = null;
        syntheticReviewAdvice = syntheticReviewAdvice is null
            ? MinerBotanistAdvisorSyntheticReview.Build(context)
            : null;
        ResetVisibleSyntheticContexts();
        syntheticScenarioKind = MinerBotanistAdvisorSyntheticScenarioKind.Success;
        lastAdvice = null;
        selectedSolutionId = null;
    }

    public void LoadSyntheticReview()
    {
        dryRunFixture = null;
        dryRunFixtureTask = null;
        dryRunFixtureStatus = null;
        syntheticReviewAdvice = MinerBotanistAdvisorSyntheticReview.Build(context);
        ResetVisibleSyntheticContexts();
        syntheticScenarioKind = MinerBotanistAdvisorSyntheticScenarioKind.Success;
        lastAdvice = null;
        selectedSolutionId = null;
    }

    private void BeginDryRunFixture()
    {
        if (!config.EnableMarketAcquisitionDryRunTools || dryRunFixtureTask is not null)
            return;
        var region = config.ActiveMarketAcquisitionRequestDocument?.Region;
        if (string.IsNullOrWhiteSpace(region))
            region = config.ActiveMarketAcquisitionClaim?.Region;
        dryRunFixture = null;
        dryRunFixtureStatus = "Fetching one complete current listing generation for the marketable gathering set...";
        dryRunFixtureTask = MinerBotanistAdvisorSyntheticReview.BuildDryRunFixtureAsync(
            listingSource,
            string.IsNullOrWhiteSpace(region) ? "North America" : region,
            context);
    }

    private void PumpDryRunFixture()
    {
        if (dryRunFixtureTask is not { IsCompleted: true } completed)
            return;
        dryRunFixtureTask = null;
        try
        {
            dryRunFixture = completed.GetAwaiter().GetResult();
            syntheticReviewAdvice = dryRunFixture.Advice;
            syntheticScenarioKind = MinerBotanistAdvisorSyntheticScenarioKind.Success;
            lastAdvice = null;
            selectedSolutionId = dryRunFixture.SelectedSolutionId;
            dryRunFixtureStatus = dryRunFixture.Diagnostic;
        }
        catch (Exception exception)
        {
            dryRunFixture = null;
            dryRunFixtureStatus = $"Live dry-run fixture stopped safely: {exception.Message}";
        }
    }

    private void ResetVisibleSyntheticContexts()
    {
        visibleSyntheticContexts.Clear();
        visibleSyntheticContexts.Add(context);
    }

    private void DrawSyntheticScenarioControl()
    {
        ImGui.SetNextItemWidth(230f);
        if (ImGui.BeginCombo("Evidence state##SquireAdvisorSyntheticScenario", SyntheticScenarioLabel(syntheticScenarioKind)))
        {
            foreach (var candidate in SyntheticScenarioOrder)
            {
                if (ImGui.Selectable(SyntheticScenarioLabel(candidate), candidate == syntheticScenarioKind))
                    SetSyntheticScenario(candidate);
            }
            ImGui.EndCombo();
        }
        var minimum = ImGui.GetItemRectMin();
        var maximum = ImGui.GetItemRectMax();
        foreach (var candidate in SyntheticScenarioOrder)
        {
            var captured = candidate;
            reviewRegistry.Register(
                $"squire.outfitter.advisor.synthetic-scenario.{SyntheticScenarioId(candidate)}",
                $"Show {SyntheticScenarioLabel(candidate)} advisor evidence state",
                AgentBridgeUiControlKind.Select,
                minimum,
                maximum,
                true,
                candidate == syntheticScenarioKind,
                SyntheticScenarioLabel(candidate),
                () => SetSyntheticScenario(captured));
        }
    }

    private void SetSyntheticScenario(MinerBotanistAdvisorSyntheticScenarioKind value)
    {
        syntheticScenarioKind = value;
        lastAdvice = null;
        selectedSolutionId = null;
    }

    private static string SyntheticScenarioLabel(MinerBotanistAdvisorSyntheticScenarioKind value) => value switch
    {
        MinerBotanistAdvisorSyntheticScenarioKind.Refreshing => "Refreshing with prior frontier",
        MinerBotanistAdvisorSyntheticScenarioKind.StaleEvidence => "Stale evidence rejected",
        MinerBotanistAdvisorSyntheticScenarioKind.IncompleteEvidence => "Incomplete generation",
        MinerBotanistAdvisorSyntheticScenarioKind.Abstention => "Advisor abstention",
        _ => "Complete generation",
    };

    private static string SyntheticScenarioId(MinerBotanistAdvisorSyntheticScenarioKind value) => value switch
    {
        MinerBotanistAdvisorSyntheticScenarioKind.Refreshing => "refreshing",
        MinerBotanistAdvisorSyntheticScenarioKind.StaleEvidence => "stale",
        MinerBotanistAdvisorSyntheticScenarioKind.IncompleteEvidence => "incomplete",
        MinerBotanistAdvisorSyntheticScenarioKind.Abstention => "abstention",
        _ => "success",
    };

    private void ToggleSyntheticSeries(MinerBotanistUtilityContextKind value) =>
        SetSyntheticSeriesVisible(value, !visibleSyntheticContexts.Contains(value));

    private void SetSyntheticSeriesVisible(MinerBotanistUtilityContextKind value, bool visible)
    {
        if (visible)
            visibleSyntheticContexts.Add(value);
        else if (visibleSyntheticContexts.Count > 1)
            visibleSyntheticContexts.Remove(value);
        selectedSolutionId = null;
    }
#endif

    private void EnsureSelection(MinerBotanistReadOnlyAdvice advice)
    {
        if (ReferenceEquals(lastAdvice, advice))
            return;
        lastAdvice = advice;
        selectedSolutionId = advice.Nomination?.Candidate.SolutionId
            ?? advice.Frontier?.Pareto.Frontier.OrderBy(value => value.AcquisitionCostGil).FirstOrDefault()?.Candidate.SolutionId;
    }

    private void DrawDecisionSummary(MinerBotanistReadOnlyAdvice advice, EquipmentDecisionSolution selected)
    {
        var columnCount = selected.AcquisitionCostEstimate is null ? 4 : 5;
        if (!ImGui.BeginTable("##SquireAdvisorSummary", columnCount, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
            return;
        SummaryCell("Profile", $"{MinerBotanistUtilityProfile.ProfileVersion} · {ContextLabel(context)}", MarketMafiosoUiTheme.Header);
        SummaryCell("Advisor", advice.Nomination is null ? "Abstained" : FormatCost(advice.Nomination.AcquisitionCostGil),
            advice.Nomination is null ? MarketMafiosoUiTheme.Warning : MarketMafiosoUiTheme.Success);
        SummaryCell(selected.AcquisitionCostEstimate is null ? "Selected" : "Selected expected", FormatCost(selected.AcquisitionCostGil), MarketMafiosoUiTheme.Link);
        if (selected.AcquisitionCostEstimate is { } estimate)
            SummaryCell($"Selected {estimate.PlanningConfidence:P0} plan", FormatCost(estimate.PlanningCostGil), MarketMafiosoUiTheme.Warning);
        SummaryCell("Utility", selected.Utility.UtilityScore.ToString("N1"), MarketMafiosoUiTheme.Header);
        ImGui.EndTable();
    }

    private void DrawFrontier(MinerBotanistReadOnlyAdvice advice, EquipmentDecisionSolution selected)
    {
#if DEBUG
        if (syntheticReviewAdvice is not null && dryRunFixture is null)
        {
            DrawSyntheticOverlay(selected);
            return;
        }
#endif
        var model = plotBuilder.Build(advice.Frontier!.Pareto, "squire-min-btn-frontier");
        var warningIds = advice.AuthorityBySolutionId
            .Where(value => !value.Value.AdvisorMayConsider)
            .Select(value => value.Key)
            .ToHashSet(StringComparer.Ordinal);
        var interaction = new PlotInteractionState(
            new HashSet<string>(StringComparer.Ordinal) { selected.Candidate.SolutionId },
            advice.Nomination?.Candidate.SolutionId,
            warningIds,
            new HashSet<string>(StringComparer.Ordinal));
        var result = plotContainer.Draw("SquireAdvisorFrontier", model.Spec, new Vector2(0, 285f), interaction);
        RegisterPlotControls(result.Controls);
        if (result.ClickedDatumId is { } clicked && model.SolutionsByDatumId.ContainsKey(clicked))
            selectedSolutionId = clicked;
        if (result.HoveredDatumId is { } hovered && model.SolutionsByDatumId.TryGetValue(hovered, out var solution))
        {
            ImGui.BeginTooltip();
            ImGui.TextColored(MarketMafiosoUiTheme.Header,
                solution.VariantLabels.FirstOrDefault() ?? solution.Candidate.SolutionId);
            ImGui.TextUnformatted($"{FormatCost(solution.AcquisitionCostGil)}{(solution.AcquisitionCostEstimate is null ? "" : " expected")} · utility {solution.Utility.UtilityScore:N1}");
            DrawPlanningCost(solution);
            ImGui.TextColored(MarketMafiosoUiTheme.Muted,
                $"{solution.Burden.PurchaseTransactions:N0} purchase(s), {solution.Burden.WorldVisits:N0} world visit(s)");
            ImGui.EndTooltip();
        }
    }

#if DEBUG
    private void DrawSyntheticOverlay(EquipmentDecisionSolution selected)
    {
        var contexts = ContextOrder
            .Where(visibleSyntheticContexts.Contains)
            .ToArray();
        var adviceByContext = contexts.ToDictionary(
            value => value,
            value => value == context ? syntheticReviewAdvice! : MinerBotanistAdvisorSyntheticReview.Build(value));
        var models = adviceByContext.ToDictionary(
            value => value.Key,
            value => plotBuilder.Build(value.Value.Frontier!.Pareto, $"squire-min-btn-{ContextSeriesId(value.Key)}"));
        var overlay = PlotOverlayComposer.Compose(
            "squire-min-btn-context-overlay",
            contexts.Select(value => new PlotOverlaySeries(
                ContextSeriesId(value),
                models[value].Spec,
                OverlayStyle(value))).ToArray(),
            "Cost / utility frontiers by gathering context");
        var warningIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in adviceByContext)
        foreach (var authority in value.Value.AuthorityBySolutionId.Where(authority => !authority.Value.AdvisorMayConsider))
            warningIds.Add(PlotOverlayComposer.DatumId(ContextSeriesId(value.Key), authority.Key));
        var selectedDatumId = visibleSyntheticContexts.Contains(context)
            ? PlotOverlayComposer.DatumId(ContextSeriesId(context), selected.Candidate.SolutionId)
            : null;
        var nominatedDatumId = visibleSyntheticContexts.Contains(context) && adviceByContext[context].Nomination is { } nomination
            ? PlotOverlayComposer.DatumId(ContextSeriesId(context), nomination.Candidate.SolutionId)
            : null;
        var interaction = new PlotInteractionState(
            selectedDatumId is null ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal) { selectedDatumId },
            nominatedDatumId,
            warningIds,
            new HashSet<string>(StringComparer.Ordinal));

        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Shape identifies context · point color remains NQ/HQ mix");
        var result = plotContainer.Draw("SquireAdvisorFrontierOverlay", overlay.Spec, new Vector2(0, 285f), interaction);
        RegisterPlotControls(result.Controls);
        if (result.ClickedDatumId is { } clicked && overlay.DatumIdentities.TryGetValue(clicked, out var clickedIdentity))
        {
            var clickedContext = ContextFromSeriesId(clickedIdentity.SeriesId);
            SetContext(clickedContext);
            selectedSolutionId = clickedIdentity.SourceDatumId;
            lastAdvice = syntheticReviewAdvice;
        }
        if (result.HoveredDatumId is { } hovered && overlay.DatumIdentities.TryGetValue(hovered, out var hoveredIdentity))
        {
            var hoveredContext = ContextFromSeriesId(hoveredIdentity.SeriesId);
            if (models[hoveredContext].SolutionsByDatumId.TryGetValue(hoveredIdentity.SourceDatumId, out var solution))
            {
                ImGui.BeginTooltip();
                ImGui.TextColored(MarketMafiosoUiTheme.Header,
                    solution.VariantLabels.FirstOrDefault() ?? solution.Candidate.SolutionId);
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, ContextLabel(hoveredContext));
                ImGui.TextUnformatted($"{FormatCost(solution.AcquisitionCostGil)}{(solution.AcquisitionCostEstimate is null ? "" : " expected")} · utility {solution.Utility.UtilityScore:N1}");
                DrawPlanningCost(solution);
                ImGui.TextColored(MarketMafiosoUiTheme.Muted,
                    $"{solution.Burden.PurchaseTransactions:N0} purchase(s), {solution.Burden.WorldVisits:N0} world visit(s)");
                ImGui.EndTooltip();
            }
        }
    }

    private static PlotOverlayStyle OverlayStyle(MinerBotanistUtilityContextKind value) => value switch
    {
        MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield =>
            new(new(.92f, .57f, .20f, .78f), PlotPointShape.Diamond),
        MinerBotanistUtilityContextKind.CollectableEfficiency =>
            new(new(.67f, .45f, .94f, .78f), PlotPointShape.Triangle),
        _ => new(new(.35f, .67f, .98f, .78f), PlotPointShape.Circle),
    };

    private static string ContextSeriesId(MinerBotanistUtilityContextKind value) => value switch
    {
        MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield => "legendary",
        MinerBotanistUtilityContextKind.CollectableEfficiency => "collectables",
        _ => "ordinary",
    };

    private static string ContextSeriesLabel(MinerBotanistUtilityContextKind value) => value switch
    {
        MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield => "Diamond Legendary",
        MinerBotanistUtilityContextKind.CollectableEfficiency => "Triangle Collectables",
        _ => "Circle Ordinary",
    };

    private static MinerBotanistUtilityContextKind ContextFromSeriesId(string value) => value switch
    {
        "legendary" => MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield,
        "collectables" => MinerBotanistUtilityContextKind.CollectableEfficiency,
        _ => MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark,
    };
#endif

    private void DrawSolutionRail(MinerBotanistReadOnlyAdvice advice, EquipmentDecisionSolution selected)
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "FRONTIER SOLUTIONS");
        if (!ImGui.BeginTable("##SquireAdvisorRail", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp,
                new Vector2(0, Math.Min(150f, 30f + advice.Frontier!.Pareto.Frontier.Count * 25f))))
            return;
        ImGui.TableSetupColumn(selected.AcquisitionCostEstimate is null ? "Cost" : "Expected cost", ImGuiTableColumnFlags.WidthFixed, 105f);
        ImGui.TableSetupColumn("Utility", ImGuiTableColumnFlags.WidthFixed, 75f);
        ImGui.TableSetupColumn("Authority", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Burden", ImGuiTableColumnFlags.WidthFixed, 120f);
        foreach (var solution in advice.Frontier.Pareto.Frontier
                     .OrderBy(value => value.AcquisitionCostGil)
                     .ThenBy(value => value.Utility.UtilityScore))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.Selectable($"{FormatCost(solution.AcquisitionCostGil)}##{solution.Candidate.SolutionId}",
                    solution.Candidate.SolutionId == selected.Candidate.SolutionId,
                    ImGuiSelectableFlags.SpanAllColumns))
                selectedSolutionId = solution.Candidate.SolutionId;
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(solution.Utility.UtilityScore.ToString("N1"));
            ImGui.TableNextColumn();
            var authority = advice.AuthorityBySolutionId[solution.Candidate.SolutionId];
            ImGui.TextColored(authority.AdvisorMayConsider ? MarketMafiosoUiTheme.Success : MarketMafiosoUiTheme.Warning,
                authority.AdvisorMayConsider ? "Supported capability" : "Visible, not nominated");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{solution.Burden.PurchaseTransactions} buy · {solution.Burden.WorldVisits} world");
        }
        ImGui.EndTable();
    }

    private static void DrawAdjacentTradeoffs(EquipmentParetoResult frontier, EquipmentDecisionSolution selected)
    {
        var adjacent = frontier.Adjacencies
            .Where(value => value.FromSolutionId == selected.Candidate.SolutionId)
            .OrderBy(value => Math.Abs(value.CostDeltaGil))
            .Take(2)
            .ToArray();
        if (adjacent.Length == 0)
            return;
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "ADJACENT TRADEOFFS");
        foreach (var value in adjacent)
            ImGui.BulletText($"{FormatSignedGil(value.CostDeltaGil)}, {value.UtilityDelta:+0.0;-0.0;0.0} utility, {value.StructuralDiff.ChangedPositionCount} slot change(s)");
    }

    private static void DrawSelectedLoadout(MinerBotanistReadOnlyAdvice advice, EquipmentDecisionSolution selected)
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "SELECTED LOADOUT");
        if (!ImGui.BeginTable("##SquireAdvisorLoadout", 5,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp,
                new Vector2(0, 265f)))
            return;
        ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthFixed, 65f);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn(selected.AcquisitionCostEstimate is null ? "Cost" : "Expected cost", ImGuiTableColumnFlags.WidthFixed, 95f);
        ImGui.TableHeadersRow();
        foreach (var selection in selected.Candidate.Selections.OrderBy(value => value.Position))
        {
            if (!advice.OffersByAllocation.TryGetValue(selection.AllocationKey, out var offer))
                continue;
            ImGui.TableNextRow();
            Cell(selection.Position.ToString());
            Cell(offer.Offer.Definition.Name);
            Cell(selection.OfferKey.Quality == EquipmentQuality.High ? "HQ" : "NQ");
            Cell(offer.Offer.SourceLabel);
            Cell(offer.AcquisitionCostGil == 0 ? "—" : $"{offer.AcquisitionCostGil:N0}");
        }
        ImGui.EndTable();
    }

    private void DrawAcquisitionChecklist(MinerBotanistReadOnlyAdvice advice, EquipmentDecisionSolution selected)
    {
        var acquisitions = selected.Candidate.Selections
            .Select(value => advice.OffersByAllocation.GetValueOrDefault(value.AllocationKey))
            .Where(value => value is not null && value.Offer.SourceKind != EquipmentAcquisitionSourceKind.Owned)
            .DistinctBy(value => value!.AllocationKey)
            .Cast<EquipmentExactSolverOffer>()
            .ToArray();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "ACQUISITION CHECKLIST");
        if (acquisitions.Length == 0)
        {
            ImGui.TextUnformatted("No acquisitions; the selected solution uses the observed equipped items.");
            return;
        }
        foreach (var offer in acquisitions)
            ImGui.BulletText($"{offer.Offer.Definition.Name} {FormatQuality(offer.Offer.ResolvedQuality)} · {offer.Offer.SourceLabel} · {offer.AcquisitionCostGil:N0} gil");
        var evidence = session.CurrentEvidence;
        var canStage = session.State.Stage == MinerBotanistAdvisorSessionStage.Complete &&
                       !session.State.AdviceIsRetained &&
                       ReferenceEquals(advice, session.State.Advice) &&
                       evidence is not null &&
                       acquisitions.Any(offer => offer.Offer.SourceKind == EquipmentAcquisitionSourceKind.MarketBoard);
#if DEBUG
        if (dryRunFixture is not null &&
            ReferenceEquals(advice, dryRunFixture.Advice) &&
            syntheticScenarioKind == MinerBotanistAdvisorSyntheticScenarioKind.Success &&
            config.EnableMarketAcquisitionDryRunTools)
        {
            evidence = dryRunFixture.Evidence;
            canStage = true;
        }
        else if (syntheticReviewAdvice is not null)
        {
            canStage = false;
        }
#endif
        void Stage()
        {
            if (!canStage || evidence is null)
                return;
            try
            {
                var transfer = OutfitterWorkbenchTransferBuilder.Build(
                    advice,
                    selected.Candidate.SolutionId,
                    evidence,
#if DEBUG
                    dryRunOnly: dryRunFixture is not null
#else
                    dryRunOnly: false
#endif
                );
                stageTransfer(transfer);
                handoffStatus = "Exact-quality solution added to the Market Acquisition Workbench for review.";
            }
            catch (Exception exception)
            {
                handoffStatus = $"Workbench handoff stopped safely: {exception.Message}";
            }
        }
        if (ImGuiUi.PrimaryButton("Review in Workbench", canStage))
            Stage();
        RegisterLastControl(
            "squire.outfitter.advisor.stage-workbench",
            "Add the selected exact-quality solution to the Market Acquisition Workbench",
            AgentBridgeUiControlKind.Button,
            canStage,
            false,
            selected.Candidate.SolutionId,
            Stage);
        if (!string.IsNullOrWhiteSpace(handoffStatus))
            ImGui.TextColored(handoffStatus.StartsWith("Workbench handoff stopped", StringComparison.Ordinal)
                ? MarketMafiosoUiTheme.Error
                : MarketMafiosoUiTheme.Success, handoffStatus);
    }

    private static void DrawEmptyState(MinerBotanistAdvisorSessionState state)
    {
        if (state.Stage == MinerBotanistAdvisorSessionStage.Idle)
            ImGui.TextWrapped("The advisor opens and reads normal Character UI only after you press refresh. It never activates the game window, changes jobs, purchases, or equips.");
        else if (state.Stage is MinerBotanistAdvisorSessionStage.Abstained or MinerBotanistAdvisorSessionStage.Failed)
            ImGui.TextWrapped("No recommendation was produced. The incomplete evidence remains visible above instead of being replaced by a guess.");
    }

    private static string ContextLabel(MinerBotanistUtilityContextKind value) => value switch
    {
        MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield => "Legendary nodes · general yield",
        MinerBotanistUtilityContextKind.CollectableEfficiency => "Collectables · i730 efficiency",
        _ => "Ordinary nodes · research benchmark",
    };

    private static Vector4 StatusColor(MinerBotanistAdvisorSessionStage stage) => stage switch
    {
        MinerBotanistAdvisorSessionStage.Complete => MarketMafiosoUiTheme.Success,
        MinerBotanistAdvisorSessionStage.Abstained => MarketMafiosoUiTheme.Warning,
        MinerBotanistAdvisorSessionStage.Failed => MarketMafiosoUiTheme.Error,
        _ => MarketMafiosoUiTheme.Muted,
    };

    private static string RetainedAdviceLabel(MinerBotanistAdvisorSessionStage stage) => stage switch
    {
        MinerBotanistAdvisorSessionStage.ObservingStats or
        MinerBotanistAdvisorSessionStage.ObservingEquipment or
        MinerBotanistAdvisorSessionStage.DiscoveringMarket => "LAST VALID FRONTIER · refresh in progress",
        MinerBotanistAdvisorSessionStage.Cancelled => "LAST VALID FRONTIER · refresh cancelled",
        MinerBotanistAdvisorSessionStage.Failed => "LAST VALID FRONTIER · refresh failed",
        _ => "LAST VALID FRONTIER · refresh abstained",
    };

    private static string FormatCost(ulong value) => value == 0 ? "No gil" : $"{value:N0} gil";
    private static string FormatSignedGil(long value) => value switch
    {
        > 0 => $"+{value:N0} gil",
        < 0 => $"-{Math.Abs(value):N0} gil",
        _ => "same cost",
    };
    private static string FormatQuality(EquipmentQuality value) => value == EquipmentQuality.High ? "HQ" : "NQ";

    private static void DrawPlanningCost(EquipmentDecisionSolution solution)
    {
        if (solution.AcquisitionCostEstimate is not { } estimate || estimate.PlanningCostGil <= estimate.ExpectedCostGil)
            return;
        ImGui.TextColored(MarketMafiosoUiTheme.Muted,
            $"{estimate.PlanningConfidence:P0} whole-set stock: {FormatCost(estimate.PlanningCostGil)}");
    }

    private static void SummaryCell(string label, string value, Vector4 color)
    {
        ImGui.TableNextColumn();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, label);
        ImGui.TextColored(color, value);
    }

    private static void Cell(string text)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(text);
    }

    private void RegisterLastControl(
        string id,
        string label,
        AgentBridgeUiControlKind kind,
        bool enabled,
        bool selected,
        string? value,
        Action invoke)
    {
        reviewRegistry.Register(
            id,
            label,
            kind,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            enabled,
            selected,
            value,
            invoke);
    }

    private void RegisterPlotControls(IReadOnlyList<DalamudPlotContainerControl> controls)
    {
        foreach (var control in controls)
        {
            reviewRegistry.Register(
                $"squire.outfitter.advisor.plot.{control.Id}",
                control.Label,
                AgentBridgeUiControlKind.Button,
                control.Bounds.Minimum,
                control.Bounds.Maximum,
                control.Enabled,
                control.Selected,
                control.Value,
                control.Invoke);
        }
    }
}
