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
    private readonly Configuration config;
    private readonly MinerBotanistAdvisorSession session;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly ParetoFrontierPlotBuilder plotBuilder = new();
    private readonly DalamudPlotRenderer plotRenderer = new();
    private MinerBotanistUtilityContextKind context = MinerBotanistUtilityContextKind.LegendaryNodeGeneralYield;
    private MinerBotanistReadOnlyAdvice? lastAdvice;
    private string? selectedSolutionId;

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
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Outfitter — cost / utility advisor");
        ImGui.TextWrapped(MinerBotanistReadOnlyAdvisor.AdvisoryRule);
        DrawControls(state);
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, state.CoverageLabel);
        if (state.IsBusy)
        {
            var fraction = state.Total is > 0 ? Math.Clamp((float)state.Completed / state.Total.Value, 0f, 1f) : 0f;
            ImGui.ProgressBar(fraction, new Vector2(-1, 0), state.Total is > 0
                ? $"{state.Completed:N0} / {state.Total:N0}"
                : state.Stage.ToString());
        }
        ImGui.TextColored(StatusColor(state.Stage), state.Message);
        ImGui.Separator();

        if (state.Advice is not { Frontier: { } frontier } advice || frontier.Pareto.Frontier.Count == 0)
        {
            DrawEmptyState(state);
            return;
        }
        EnsureSelection(advice);
        var selected = frontier.Pareto.Frontier.FirstOrDefault(value => value.Candidate.SolutionId == selectedSolutionId)
            ?? advice.Nomination
            ?? frontier.Pareto.Frontier[0];
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
            foreach (var candidate in Enum.GetValues<MinerBotanistUtilityContextKind>())
            {
                if (ImGui.Selectable(ContextLabel(candidate), candidate == context))
                    SetContext(candidate);
            }
            ImGui.EndCombo();
        }
        var contextMin = ImGui.GetItemRectMin();
        var contextMax = ImGui.GetItemRectMax();
        foreach (var candidate in Enum.GetValues<MinerBotanistUtilityContextKind>())
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
    }

    private void Begin()
    {
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
    }

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
            ImGui.TextUnformatted($"{FormatCost(solution.AcquisitionCostGil)} · utility {solution.Utility.UtilityScore:N1}");
            ImGui.TextColored(MarketMafiosoUiTheme.Muted,
                $"{solution.Burden.PurchaseTransactions:N0} purchase(s), {solution.Burden.WorldVisits:N0} world visit(s)");
            ImGui.EndTooltip();
        }
    }

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
