using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Equipment;
using Franthropy.Dalamud.UI.Plots;
using MarketMafioso.AgentBridge;
using MarketMafioso.Squire.Outfitter.Utility;
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
    private readonly ParetoFrontierPlotBuilder plotBuilder = new();
    private readonly DalamudPlotRenderer plotRenderer = new();
    private MinerBotanistUtilityContextKind context = MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark;
    private MinerBotanistReadOnlyAdvice? lastAdvice;
    private string? selectedSolutionId;
#if DEBUG
    private MinerBotanistReadOnlyAdvice? syntheticReviewAdvice;
    private readonly HashSet<MinerBotanistUtilityContextKind> visibleSyntheticContexts =
        [MinerBotanistUtilityContextKind.OrdinaryResourceBenchmark];
#endif

    public MinerBotanistAdvisorPanel(
        Configuration config,
        MinerBotanistAdvisorSession session,
        AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.reviewRegistry = reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry));
        if (Enum.TryParse<MinerBotanistUtilityContextKind>(config.Squire.OutfitterAdvisorContext, out var stored))
            context = stored;
    }

    public void Draw()
    {
        var state = session.State;
        MinerBotanistReadOnlyAdvice? displayedAdvice = state.Advice;
#if DEBUG
        var syntheticReviewActive = syntheticReviewAdvice is not null;
        displayedAdvice = syntheticReviewAdvice ?? displayedAdvice;
#endif
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Outfitter — cost / utility advisor");
        ImGui.TextWrapped(MinerBotanistReadOnlyAdvisor.AdvisoryRule);
        DrawControls(state);
#if DEBUG
        if (syntheticReviewActive)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Warning, "DEBUG REPLAY — model decisions with frozen evidence prices");
            ImGui.TextColored(MarketMafiosoUiTheme.Muted,
                "Item names are game data; only marketable components use Aether sale-history medians. No live character or live listing is used.");
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, MinerBotanistAdvisorSyntheticReview.PriceEvidenceLabel);
            ImGui.TextColored(MarketMafiosoUiTheme.Success, displayedAdvice!.Diagnostic);
        }
        else
#endif
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, state.CoverageLabel);
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
        context = value;
        config.Squire.OutfitterAdvisorContext = value.ToString();
        config.Save();
        lastAdvice = null;
        selectedSolutionId = null;
#if DEBUG
        if (syntheticReviewAdvice is not null)
        {
            syntheticReviewAdvice = MinerBotanistAdvisorSyntheticReview.Build(context);
            visibleSyntheticContexts.Add(context);
        }
#endif
    }

#if DEBUG
    private void ToggleSyntheticReview()
    {
        syntheticReviewAdvice = syntheticReviewAdvice is null
            ? MinerBotanistAdvisorSyntheticReview.Build(context)
            : null;
        ResetVisibleSyntheticContexts();
        lastAdvice = null;
        selectedSolutionId = null;
    }

    public void LoadSyntheticReview()
    {
        syntheticReviewAdvice = MinerBotanistAdvisorSyntheticReview.Build(context);
        ResetVisibleSyntheticContexts();
        lastAdvice = null;
        selectedSolutionId = null;
    }

    private void ResetVisibleSyntheticContexts()
    {
        visibleSyntheticContexts.Clear();
        visibleSyntheticContexts.Add(context);
    }

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
        if (!ImGui.BeginTable("##SquireAdvisorSummary", 4, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
            return;
        SummaryCell("Profile", $"{MinerBotanistUtilityProfile.ProfileVersion} · {ContextLabel(context)}", MarketMafiosoUiTheme.Header);
        SummaryCell("Advisor", advice.Nomination is null ? "Abstained" : FormatCost(advice.Nomination.AcquisitionCostGil),
            advice.Nomination is null ? MarketMafiosoUiTheme.Warning : MarketMafiosoUiTheme.Success);
        SummaryCell("Selected", FormatCost(selected.AcquisitionCostGil), MarketMafiosoUiTheme.Link);
        SummaryCell("Utility", selected.Utility.UtilityScore.ToString("N1"), MarketMafiosoUiTheme.Header);
        ImGui.EndTable();
    }

    private void DrawFrontier(MinerBotanistReadOnlyAdvice advice, EquipmentDecisionSolution selected)
    {
#if DEBUG
        if (syntheticReviewAdvice is not null)
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
        var result = plotRenderer.Draw("SquireAdvisorFrontier", model.Spec, new Vector2(0, 285f), interaction);
        if (result.ClickedDatumId is { } clicked && model.SolutionsByDatumId.ContainsKey(clicked))
            selectedSolutionId = clicked;
        if (result.HoveredDatumId is { } hovered && model.SolutionsByDatumId.TryGetValue(hovered, out var solution))
        {
            ImGui.BeginTooltip();
            ImGui.TextColored(MarketMafiosoUiTheme.Header,
                solution.VariantLabels.FirstOrDefault() ?? solution.Candidate.SolutionId);
            ImGui.TextUnformatted($"{FormatCost(solution.AcquisitionCostGil)} · utility {solution.Utility.UtilityScore:N1}");
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
        var result = plotRenderer.Draw("SquireAdvisorFrontierOverlay", overlay.Spec, new Vector2(0, 285f), interaction);
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
                ImGui.TextUnformatted($"{FormatCost(solution.AcquisitionCostGil)} · utility {solution.Utility.UtilityScore:N1}");
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
        ImGui.TableSetupColumn("Cost", ImGuiTableColumnFlags.WidthFixed, 105f);
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
        ImGui.TableSetupColumn("Cost", ImGuiTableColumnFlags.WidthFixed, 95f);
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

    private static void DrawAcquisitionChecklist(MinerBotanistReadOnlyAdvice advice, EquipmentDecisionSolution selected)
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
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Read-only release: selection does not purchase, equip, or stage anything.");
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

    private static string FormatCost(ulong value) => value == 0 ? "No gil" : $"{value:N0} gil";
    private static string FormatSignedGil(long value) => value switch
    {
        > 0 => $"+{value:N0} gil",
        < 0 => $"-{Math.Abs(value):N0} gil",
        _ => "same cost",
    };
    private static string FormatQuality(EquipmentQuality value) => value == EquipmentQuality.High ? "HQ" : "NQ";

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
}
