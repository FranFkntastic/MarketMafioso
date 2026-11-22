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
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(PhaseColor(session.Phase),
                $"{CrossRegionAcquisitionCoordinator.DescribeTitle(session.Phase)} · {session.Shopper?.Label} · {session.TargetRegion}");
            ImGui.SameLine();
            void OpenRoute() => QueueAgentTabSelection("Market Acquisition", "Route");
            if (ImGuiUi.Button("Open Route##CrossRegionOpenRoute", true))
                OpenRoute();
            AgentReviewRegistry.RegisterLastButton("acquisition.cross-region.open-route", "Open the Route tab for the cross-regional run", true, OpenRoute);
            return;
        }

        var region = DocumentRegion!;
        var status = CrossRegion.GetCourierStatus(out _);
        var shopper = ResolveCrossRegionShopper(status, region, out var candidates);
        var locked = IsMarketAcquisitionRouteActive() || acquisitionWorkspace.IsBusy;
        ImGui.BeginDisabled(locked);
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
        if (acquisitionWorkspace.PreparedPlan is { Status: "Ready", PlannedGil: > 0 } plan && !acquisitionWorkspace.IsPreparedPlanStale())
        {
            ImGui.SameLine();
            var matched = (int)Math.Clamp((long)plan.PlannedGil, 0, CrossRegionAcquisitionCoordinator.MaxCarriedGil);
            void MatchPlan()
            {
                config.CrossRegionFundGil = matched;
                config.Save();
            }
            var canMatch = !locked && config.CrossRegionFundGil != matched;
            if (ImGuiUi.Button($"Match plan ({plan.PlannedGil:N0})##CrossRegionMatchPlan", canMatch))
                MatchPlan();
            if (!canMatch && !locked && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Already matches the plan");
            AgentReviewRegistry.RegisterLastButton("acquisition.cross-region.match-plan", "Set the transfer gil to the prepared plan's total", canMatch, MatchPlan);
        }
        ImGui.EndDisabled();

        var readiness = CrossRegion.EvaluateReadiness(shopper, region, config.CrossRegionFundGil, acquisitionRequestBuilder.CurrentDocument);
        if (readiness.Ready)
        {
            var carrier = readiness.Status!.Carrier!;
            var bridge = readiness.Status.Bridge?.Label ?? "Ravana — Ul'dah";
            var primary = readiness.Status.LoggedIn is { } loggedIn ? $"{loggedIn.CharacterName} carries {loggedIn.CarriedGil:N0} gil · " : string.Empty;
            var transfer = config.CrossRegionFundGil > 0
                ? $"{config.CrossRegionFundGil:N0} gil → {shopper!.Label}"
                : $"No gil transfer → {shopper!.Label}";
            ImGui.TextColored(ColSuccess, $"Ready · {primary}{transfer} · Carrier {carrier.CharacterName} ({carrier.FreeBagSlots} free slots) · {bridge}");
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
        CrossRegionAcquisitionPhase.ClosingMarketBoardForHaul or CrossRegionAcquisitionPhase.HaulingToPrimary or
            CrossRegionAcquisitionPhase.ReturningToPrimary => 2,
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
        }

        DrawCrossRegionActions();
        if (!string.IsNullOrWhiteSpace(crossRegionActionMessage))
            ImGui.TextColored(ColWarning, crossRegionActionMessage);
        if (CrossRegion.HasSession && session.PurchasedManifest.Count > 0)
            DrawCrossRegionPurchasedTable(session);
    }

    private const int CrossRegionTableMaxVisibleRows = 8;

    private static void DrawCrossRegionPurchasedTable(CrossRegionAcquisitionSession session)
    {
        var showHaul = session.HaulManifest.Count > 0;
        var hauled = session.HaulManifest.ToDictionary(line => (line.ItemId, line.IsHq), line => line.Quantity);
        var rows = session.PurchasedManifest.OrderBy(line => line.ItemName, StringComparer.OrdinalIgnoreCase).ThenBy(line => line.IsHq).ToArray();
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp;
        var outer = Vector2.Zero;
        if (rows.Length > CrossRegionTableMaxVisibleRows)
        {
            flags |= ImGuiTableFlags.ScrollY;
            outer = new Vector2(0, ImGui.GetFrameHeightWithSpacing() * (CrossRegionTableMaxVisibleRows + 1));
        }
        ImGui.Spacing();
        if (!ImGui.BeginTable("CrossRegionPurchased", showHaul ? 5 : 4, flags, outer))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthStretch, 0.5f);
        ImGui.TableSetupColumn("Bought", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Spent", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        if (showHaul)
            ImGui.TableSetupColumn("Haul", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableHeadersRow();
        foreach (var line in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.ItemName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.IsHq ? "HQ" : string.Empty);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.Quantity.ToString("N0"));
            ImGui.TableNextColumn();
            // Sessions saved before per-line spend tracking have no per-line figure.
            ImGui.TextUnformatted(line.SpentGil > 0 ? $"{line.SpentGil:N0}" : "—");
            if (showHaul)
            {
                ImGui.TableNextColumn();
                var haul = hauled.GetValueOrDefault((line.ItemId, line.IsHq));
                if (haul < line.Quantity)
                    ImGui.TextColored(ColWarning, haul.ToString("N0"));
                else
                    ImGui.TextUnformatted(haul.ToString("N0"));
            }
        }
        ImGui.EndTable();
    }

    private void DrawCrossRegionActions()
    {
        if (!CrossRegion.HasSession)
        {
            var blocker = GetCrossRegionStartBlocker();
            var canStart = blocker is null;
            if (ImGuiUi.PrimaryButton("Start Cross-Regional Run##CrossRegionStart", canStart))
                StartCrossRegionRun();
            if (blocker is not null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(blocker);
            AgentReviewRegistry.RegisterLastButton("acquisition.cross-region.start", "Start the cross-regional shopping run", canStart, StartCrossRegionRun);
            if (blocker is not null)
                ImGui.TextColored(ColWarning, blocker);
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

    /// <summary>The single reason the cross-regional run cannot start, or null when it can.</summary>
    private string? GetCrossRegionStartBlocker()
    {
        if (CrossRegion.HasSession)
            return "A cross-regional session is still unresolved; finish or clear it first.";
        if (!IsCrossRegionDocument || DocumentRegion is not { } region)
            return "Set the request region to a region other than your home region.";
        if (routeEngine.IsRouteActive)
            return "Another Market Acquisition route is active.";
        if (acquisitionWorkspace.IsBusy)
            return "Market Acquisition is busy.";
        if (acquisitionWorkspace.PreparedPlan is not { Status: "Ready", WorldBatches.Count: > 0 })
            return "Prepare a plan in Workbench before starting.";
        if (acquisitionWorkspace.IsPreparedPlanStale())
            return "Request changed after this plan was prepared. Prepare a fresh plan first.";
        var shopper = ResolveCrossRegionShopper(CrossRegion.GetCourierStatus(out _), region, out _);
        var readiness = CrossRegion.EvaluateReadiness(shopper, region, config.CrossRegionFundGil, acquisitionRequestBuilder.CurrentDocument);
        return readiness.Ready ? null : readiness.Blocker ?? "Cross-regional shopping is not ready.";
    }

    private bool CanStartCrossRegionRun() => GetCrossRegionStartBlocker() is null;

    private void StartCrossRegionRun()
    {
        crossRegionActionMessage = null;
        if (GetCrossRegionStartBlocker() is { } blocker)
        {
            crossRegionActionMessage = blocker;
            return;
        }
        var region = DocumentRegion!;
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
