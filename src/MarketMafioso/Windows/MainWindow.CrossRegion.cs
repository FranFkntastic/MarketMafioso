using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.UI.Inputs;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Quartermaster;

namespace MarketMafioso.Windows;

/// <summary>Cross-regional shopping: Workbench group, planning world, and the 3-stage Guided Route pipeline.</summary>
public partial class MainWindow
{
    private string? crossRegionActionMessage;

    private CrossRegionAcquisitionCoordinator CrossRegion => acquisitionServices.CrossRegion;

    private static string? RegionOfWorld(string? world) =>
        string.IsNullOrWhiteSpace(world)
            ? null
            : MarketAcquisitionWorldCatalog.Regions
                .FirstOrDefault(region => region.Value.Values.Any(worlds => worlds.Contains(world, StringComparer.OrdinalIgnoreCase)))
                .Key;

    private string? HomeRegion =>
        playerState.HomeWorld.IsValid ? RegionOfWorld(playerState.HomeWorld.Value.Name.ToString()) : null;

    private string? DocumentRegion
    {
        get
        {
            try { return MarketAcquisitionWorldCatalog.NormalizeRegion(acquisitionRequestBuilder.CurrentDocument.Region); }
            catch (InvalidOperationException) { return null; }
        }
    }

    private bool IsCrossRegionDocument =>
        DocumentRegion is { } region && HomeRegion is { } home && !string.Equals(region, home, StringComparison.OrdinalIgnoreCase);

    private bool ShowCrossRegion => IsCrossRegionDocument || CrossRegion.HasSession;

    private CrossRegionCharacter? ResolveCrossRegionShopper(
        QuartermasterCourierStatus? status,
        string region,
        out IReadOnlyList<QuartermasterCourierRosterEntry> candidates)
    {
        candidates = status is null
            ? []
            : status.Roster
                .Where(entry => !entry.IsCarrier && CrossRegionAcquisitionCoordinator.RegionMatches(entry.RegionName, region))
                .ToArray();
        var chosen = candidates.FirstOrDefault(entry => entry.LocalContentId == config.CrossRegionShopperContentId) ??
                     (candidates.Count == 1 ? candidates[0] : null);
        return chosen is null
            ? null
            : new CrossRegionCharacter(chosen.LocalContentId, chosen.HomeWorldId, chosen.CharacterName, chosen.HomeWorldName);
    }

    /// <summary>A foreign-region plan is priced from the shopper's home world, not the primary's.</summary>
    private string ResolvePlanningWorld(string currentWorld)
    {
        if (!IsCrossRegionDocument || DocumentRegion is not { } region)
            return currentWorld;
        var shopper = ResolveCrossRegionShopper(CrossRegion.GetCourierStatus(out _), region, out _);
        return shopper?.HomeWorldName is { Length: > 0 } world ? world : currentWorld;
    }

    // ---- Workbench group --------------------------------------------------------------------

    private void DrawCrossRegionWorkbenchSection()
    {
        if (!ShowCrossRegion)
            return;
        ImGui.Spacing();
        ImGuiUi.SectionHeader("Cross-regional shopping", ColHeader);
        var session = CrossRegion.Session;
        if (CrossRegion.HasSession)
        {
            ImGui.TextColored(PhaseColor(session.Phase),
                $"{CrossRegionAcquisitionCoordinator.Describe(session.Phase)} · {session.Shopper?.Label} · {session.TargetRegion}");
            return;
        }

        var region = DocumentRegion!;
        var status = CrossRegion.GetCourierStatus(out _);
        var shopper = ResolveCrossRegionShopper(status, region, out var candidates);
        ImGui.BeginDisabled(IsMarketAcquisitionRouteActive() || acquisitionWorkspace.IsBusy);
        ImGui.SetNextItemWidth(240);
        var preview = shopper?.Label ?? (candidates.Count == 0 ? $"No roster character in {region}" : "Select shopper");
        if (ImGui.BeginCombo("Shopper##CrossRegionShopper", preview))
        {
            foreach (var candidate in candidates)
            {
                var selected = shopper?.LocalContentId == candidate.LocalContentId;
                if (ImGui.Selectable($"{candidate.CharacterName} · {candidate.HomeWorldName}##{candidate.LocalContentId}", selected))
                {
                    config.CrossRegionShopperContentId = candidate.LocalContentId;
                    config.Save();
                }
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        long gil = config.CrossRegionFundGil;
        ImGui.SetNextItemWidth(180);
        if (DalamudGroupedIntegerInput.InputGroupedInt("Transfer gil##CrossRegionFundGil", ref gil, 0, 0))
        {
            config.CrossRegionFundGil = (int)Math.Clamp(gil, 0, CrossRegionAcquisitionCoordinator.MaxCarriedGil);
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Exact carried gil to transfer from primary to shopper via courier (retainers are never touched; 0 skips gil transfer).");
        ImGui.EndDisabled();

        var readiness = CrossRegion.EvaluateReadiness(shopper, region, config.CrossRegionFundGil);
        if (readiness.Ready)
        {
            var carrier = readiness.Status!.Carrier!;
            var bridge = readiness.Status.Bridge?.Label ?? "Ravana — Ul'dah";
            var summary = config.CrossRegionFundGil > 0
                ? $"Ready · {config.CrossRegionFundGil:N0} gil → {shopper!.Label}"
                : $"Ready · no gil transfer · {shopper!.Label}";
            ImGui.TextColored(ColSuccess, $"{summary} · Carrier: {carrier.CharacterName} ({carrier.FreeBagSlots} free slots) · {bridge}");
        }
        else
            ImGui.TextColored(readiness.Status is null ? ColError : ColWarning, readiness.Blocker ?? "Not ready.");
    }

    // ---- Guided Route pipeline --------------------------------------------------------------

    private static readonly string[] CrossRegionStageLabels = ["1. Fund Shopper", "2. Shop", "3. Return Haul"];

    private static int StageOf(CrossRegionAcquisitionPhase? phase) => phase switch
    {
        CrossRegionAcquisitionPhase.FundingShopper => 0,
        CrossRegionAcquisitionPhase.SwitchingToShopper or CrossRegionAcquisitionPhase.PreparingShopRoute or
            CrossRegionAcquisitionPhase.Shopping or CrossRegionAcquisitionPhase.Paused => 1,
        CrossRegionAcquisitionPhase.ClosingMarketBoardForHaul or CrossRegionAcquisitionPhase.HaulingToPrimary => 2,
        CrossRegionAcquisitionPhase.Completed => 3,
        _ => -1,
    };

    private static Vector4 PhaseColor(CrossRegionAcquisitionPhase phase) => phase switch
    {
        CrossRegionAcquisitionPhase.Failed => ColError,
        CrossRegionAcquisitionPhase.Stopped or CrossRegionAcquisitionPhase.Paused => ColWarning,
        CrossRegionAcquisitionPhase.Completed => ColSuccess,
        _ => ColHeader,
    };

    private void DrawCrossRegionPipeline()
    {
        var session = CrossRegion.Session;
        var phase = session.Phase;
        var interrupted = phase is CrossRegionAcquisitionPhase.Failed or CrossRegionAcquisitionPhase.Stopped;
        var stage = StageOf(interrupted ? session.InterruptedPhase : phase);
        var region = CrossRegion.HasSession ? session.TargetRegion : DocumentRegion ?? string.Empty;

        for (var index = 0; index < CrossRegionStageLabels.Length; index++)
        {
            if (index > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(ColMuted, "→");
                ImGui.SameLine();
            }
            var label = index == 1 && region.Length > 0 ? $"2. Shop on {region}" : CrossRegionStageLabels[index];
            var skipped = index == 0 && CrossRegion.HasSession && session.FundGil == 0;
            var color = !CrossRegion.HasSession || skipped ? ColMuted
                : index < stage || phase == CrossRegionAcquisitionPhase.Completed ? ColSuccess
                : index == stage ? interrupted ? phase == CrossRegionAcquisitionPhase.Failed ? ColError : ColWarning : ColHeader
                : ColMuted;
            ImGui.TextColored(color, skipped ? $"{label} (skipped)" : label);
        }

        if (CrossRegion.HasSession)
        {
            ImGui.TextColored(PhaseColor(phase), session.LastMessage);
            if (!string.IsNullOrWhiteSpace(session.CourierPhaseSummary))
                ImGui.TextColored(ColMuted, $"Courier: {session.CourierPhaseSummary}");
            if (session.PurchasedUnits > 0)
                ImGui.TextColored(ColMuted, $"Bought {session.PurchasedUnits:N0} item(s) · {session.TotalSpentGil:N0} gil");
        }

        DrawCrossRegionActions();
        if (!string.IsNullOrWhiteSpace(crossRegionActionMessage))
            ImGui.TextColored(ColWarning, crossRegionActionMessage);
    }

    private void DrawCrossRegionActions()
    {
        if (!CrossRegion.HasSession)
        {
            var canStart = CanStartCrossRegionRun();
            if (ImGuiUi.PrimaryButton("Start Cross-Regional Run##CrossRegionStart", canStart))
                StartCrossRegionRun();
            AgentReviewRegistry.RegisterLastButton("acquisition.cross-region.start", "Start the cross-regional shopping run", canStart, StartCrossRegionRun);
            return;
        }

        var drewAny = false;
        DrawCrossRegionButton(ref drewAny, "Pause##CrossRegionPause", "acquisition.cross-region.pause", "Pause cross-regional shopping",
            CrossRegion.CanPause, () => CrossRegion.Pause());
        DrawCrossRegionButton(ref drewAny,
            CrossRegion.Phase is CrossRegionAcquisitionPhase.Paused ? "Resume Shopping##CrossRegionResume" : "Resume##CrossRegionResume",
            "acquisition.cross-region.resume", "Resume the cross-regional run", CrossRegion.CanResume, () => CrossRegion.Resume());
        DrawCrossRegionButton(ref drewAny, "Haul Now##CrossRegionHaulNow", "acquisition.cross-region.haul-now",
            "Haul purchased items back to the primary now", CrossRegion.CanHaulNow, () => CrossRegion.HaulNow());
        DrawCrossRegionButton(ref drewAny, "Stop##CrossRegionStop", "acquisition.cross-region.stop", "Stop the cross-regional run",
            CrossRegion.CanStop, () => CrossRegion.Stop());
        DrawCrossRegionButton(ref drewAny, "Clear##CrossRegionClear", "acquisition.cross-region.clear", "Clear the finished cross-regional session",
            CrossRegion.CanClear, () => CrossRegion.Clear());
    }

    private void DrawCrossRegionButton(ref bool drewAny, string label, string id, string description, bool enabled, Func<CrossRegionActionResult> action)
    {
        if (drewAny)
            ImGui.SameLine();
        drewAny = true;
        void Invoke()
        {
            var result = action();
            crossRegionActionMessage = result.Success ? null : result.Message;
        }
        if (ImGuiUi.Button(label, enabled))
            Invoke();
        AgentReviewRegistry.RegisterLastButton(id, description, enabled, Invoke);
    }

    private bool CanStartCrossRegionRun() =>
        !CrossRegion.HasSession &&
        IsCrossRegionDocument &&
        !routeEngine.IsRouteActive &&
        !acquisitionWorkspace.IsBusy &&
        acquisitionWorkspace.PreparedPlan is { Status: "Ready", WorldBatches.Count: > 0 } &&
        !acquisitionWorkspace.IsPreparedPlanStale() &&
        DocumentRegion is { } region &&
        CrossRegion.EvaluateReadiness(ResolveCrossRegionShopper(CrossRegion.GetCourierStatus(out _), region, out _), region,
            config.CrossRegionFundGil).Ready;

    private void StartCrossRegionRun()
    {
        crossRegionActionMessage = null;
        if (!CanStartCrossRegionRun() || DocumentRegion is not { } region)
        {
            crossRegionActionMessage = "Prepare a fresh plan and resolve the readiness blockers first.";
            return;
        }
        var shopper = ResolveCrossRegionShopper(CrossRegion.GetCourierStatus(out _, force: true), region, out _);
        if (shopper is null)
        {
            crossRegionActionMessage = $"Select a shopper in {region}.";
            return;
        }
        var document = acquisitionRequestBuilder.CurrentDocument;
        var request = acquisitionWorkspace.ResolveLocalExecutionRequest(document);
        // The primary's own stock (bags plus retainers where the line's basis counts them) is the target baseline.
        var baselines = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var line in MarketAcquisitionCrossRegionShoppingRoute.TargetLines(request))
        {
            var observed = acquisitionServices.InventoryObserver.Observe(line.ItemId, line.HqPolicy, line.TargetBasis);
            if (!observed.IsAvailable)
            {
                crossRegionActionMessage = $"Cannot read {line.ItemName} stock on the primary: {observed.Message}";
                return;
            }
            baselines[line.LineId] = observed.Quantity;
        }
        var result = CrossRegion.Start(new CrossRegionStartRequest(shopper, region, config.CrossRegionFundGil, document, baselines));
        crossRegionActionMessage = result.Success ? null : result.Message;
    }
}
