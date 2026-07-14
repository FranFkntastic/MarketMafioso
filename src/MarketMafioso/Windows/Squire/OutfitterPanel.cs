using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.AgentBridge;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Squire.Outfitter;
using MarketMafioso.Windows.Main;

namespace MarketMafioso.Windows.Squire;

internal sealed class OutfitterPanel : IDisposable
{
    private readonly Configuration config;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly OutfitterTargetCatalog targetCatalog = new();
    private readonly OutfitterCandidateCatalog candidateCatalog;
    private readonly EquipmentLoadoutSolver solver = new();
    private readonly OutfitterMarketQuoteService quoteService;
    private IReadOnlyDictionary<uint, OutfitterMarketQuote> marketQuotes = new Dictionary<uint, OutfitterMarketQuote>();
    private CharacterEquipmentSnapshot? lastSnapshot;
    private IReadOnlyList<OutfitterTarget> targets = [];
    private OutfitterTarget? selectedTarget;
    private EquipmentLoadoutPlan? plan;
    private string search;
    private EquipmentLoadoutStrategy strategy;
    private int targetLevel;
    private string status = "Choose a target to begin.";
    private string planSignature = string.Empty;
    private bool quoteRefreshRunning;
    private CancellationTokenSource? quoteCancellation;
    private Action<IReadOnlyList<MarketAcquisitionRequestLineDocument>>? stageMarketLines;

    public OutfitterPanel(
        Configuration config,
        OutfitterCandidateCatalog candidateCatalog,
        OutfitterMarketQuoteService quoteService,
        AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.candidateCatalog = candidateCatalog ?? throw new ArgumentNullException(nameof(candidateCatalog));
        this.quoteService = quoteService ?? throw new ArgumentNullException(nameof(quoteService));
        this.reviewRegistry = reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry));
        search = config.Squire.OutfitterSearch;
        strategy = Enum.TryParse<EquipmentLoadoutStrategy>(config.Squire.OutfitterStrategy, out var storedStrategy)
            ? storedStrategy
            : EquipmentLoadoutStrategy.HighestItemLevel;
        targetLevel = config.Squire.OutfitterTargetLevel;
    }

    public void ConnectMarketAcquisition(Action<IReadOnlyList<MarketAcquisitionRequestLineDocument>> stageLines) =>
        stageMarketLines = stageLines ?? throw new ArgumentNullException(nameof(stageLines));

    public void Draw(CharacterEquipmentSnapshot snapshot)
    {
        RefreshTargets(snapshot);
        if (!ImGui.BeginTable(
                "##SquireOutfitterLayout",
                2,
                ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings,
                new Vector2(0, Math.Max(360f, ImGui.GetContentRegionAvail().Y))))
            return;

        ImGui.TableSetupColumn("Targets", ImGuiTableColumnFlags.WidthFixed, 235f);
        ImGui.TableSetupColumn("Loadout", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        DrawTargets();
        ImGui.TableNextColumn();
        DrawWorkspace(snapshot);
        ImGui.EndTable();
    }

    private void RefreshTargets(CharacterEquipmentSnapshot snapshot)
    {
        if (lastSnapshot?.GenerationId == snapshot.GenerationId)
            return;

        lastSnapshot = snapshot;
        targets = targetCatalog.Build(snapshot, config.RetainerCache);
        selectedTarget = targets.FirstOrDefault(target =>
                             string.Equals(target.Key, config.Squire.OutfitterSelectedTargetKey, StringComparison.Ordinal))
                         ?? targets.FirstOrDefault(target => target.Kind == OutfitterTargetKind.Job)
                         ?? targets.FirstOrDefault();
        if (selectedTarget is not null)
        {
            config.Squire.OutfitterSelectedTargetKey = selectedTarget.Key;
            targetLevel = ResolveTargetLevel(selectedTarget);
        }
        planSignature = string.Empty;
    }

    private void DrawTargets()
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Targets");
        ImGui.SameLine();
        var jobCount = targets.Count(target => target.Kind == OutfitterTargetKind.Job);
        var retainerCount = targets.Count(target => target.Kind == OutfitterTargetKind.Retainer);
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"{jobCount:N0} job{(jobCount == 1 ? string.Empty : "s")} · {retainerCount:N0} retainers");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##OutfitterTargetSearch", "Search jobs or retainers", ref search, 128))
        {
            config.Squire.OutfitterSearch = search;
            config.Save();
        }
        var visible = targets.Where(MatchesSearch).ToArray();
        ImGui.BeginChild("##OutfitterTargets", new Vector2(0, -1), true);
        var lastKind = (OutfitterTargetKind?)null;
        foreach (var target in visible)
        {
            var groupKind = target.Kind == OutfitterTargetKind.Gearset ? OutfitterTargetKind.Job : target.Kind;
            if (groupKind != lastKind)
            {
                if (lastKind is not null)
                    ImGui.Spacing();
                ImGui.TextColored(
                    MarketMafiosoUiTheme.Muted,
                    groupKind == OutfitterTargetKind.Retainer ? "RETAINERS" : "PLAYER JOBS");
                lastKind = groupKind;
            }
            DrawTargetRow(target);
        }
        if (visible.Length == 0)
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "No targets match this search.");
        ImGui.EndChild();
    }

    private void DrawTargetRow(OutfitterTarget target)
    {
        var selected = selectedTarget?.Key == target.Key;
        if (!ImGui.BeginTable($"##OutfitterTargetRow{target.Key}", 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings))
            return;
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Badge", ImGuiTableColumnFlags.WidthFixed, 42f);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        var prefix = target.Kind == OutfitterTargetKind.Gearset ? "  " : string.Empty;
        if (ImGui.Selectable($"{prefix}{target.Name}##OutfitterTarget{target.Key}", selected, ImGuiSelectableFlags.SpanAllColumns))
            SelectTarget(target);
        RegisterLastControl(
            $"squire.outfitter.target.{target.Key}",
            $"Select {target.Name}",
            AgentBridgeUiControlKind.Select,
            true,
            selected,
            target.Subtitle,
            () => SelectTarget(target));
        var rowHovered = ImGui.IsItemHovered();
        ImGui.TableNextColumn();
        ImGui.TextColored(target.IsReady ? MarketMafiosoUiTheme.Muted : MarketMafiosoUiTheme.Warning, FormatTargetBadge(target));
        rowHovered |= ImGui.IsItemHovered();
        if (rowHovered)
        {
            var tooltip = string.IsNullOrWhiteSpace(target.Diagnostic)
                ? target.Subtitle
                : $"{target.Subtitle}\n{target.Diagnostic}";
            ImGui.SetTooltip(tooltip);
        }
        ImGui.EndTable();
    }

    private void SelectTarget(OutfitterTarget target)
    {
        selectedTarget = target;
        config.Squire.OutfitterSelectedTargetKey = target.Key;
        if (target.Job is not null)
        {
            targetLevel = ResolveTargetLevel(target);
            config.Squire.OutfitterTargetLevel = targetLevel;
        }
        marketQuotes = new Dictionary<uint, OutfitterMarketQuote>();
        planSignature = string.Empty;
        status = target.IsReady ? "Target selected; loadout recomputed." : target.Diagnostic ?? "Target data is incomplete.";
        config.Save();
    }

    private void DrawWorkspace(CharacterEquipmentSnapshot snapshot)
    {
        if (selectedTarget is null)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "No outfit targets are available for this character.");
            return;
        }

        ImGui.TextColored(MarketMafiosoUiTheme.Header, selectedTarget.Name);
        ImGui.SameLine();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, selectedTarget.Subtitle);
        if (!selectedTarget.IsReady || selectedTarget.Job is null)
        {
            ImGui.Separator();
            ImGui.TextWrapped(selectedTarget.Diagnostic ?? "This target cannot be planned yet.");
            ImGui.Spacing();
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "The target stays visible so stale or incomplete retainer state is never mistaken for a missing retainer.");
            return;
        }

        DrawPolicyBar();
        EnsurePlan(snapshot);
        if (plan is null)
            return;
        DrawSummary(plan);
        DrawLoadoutTable(plan);
        DrawAcquisitionBar(plan);
    }

    private void DrawPolicyBar()
    {
        ImGui.SetNextItemWidth(150f);
        var strategies = new[]
        {
            EquipmentLoadoutStrategy.HighestItemLevel,
            EquipmentLoadoutStrategy.MinimizeSpend,
            EquipmentLoadoutStrategy.BestOwned,
        };
        var preview = FormatStrategy(strategy);
        if (ImGui.BeginCombo("Plan##OutfitterStrategy", preview))
        {
            foreach (var candidate in strategies)
            {
                if (ImGui.Selectable(FormatStrategy(candidate), candidate == strategy))
                    SetStrategy(candidate);
            }
            ImGui.EndCombo();
        }
        var strategyMin = ImGui.GetItemRectMin();
        var strategyMax = ImGui.GetItemRectMax();
        foreach (var candidate in strategies)
        {
            var captured = candidate;
            reviewRegistry.Register(
                $"squire.outfitter.strategy.{StrategyControlSuffix(candidate)}",
                $"Use {FormatStrategy(candidate)} Outfitter strategy",
                AgentBridgeUiControlKind.Select,
                strategyMin,
                strategyMax,
                true,
                candidate == strategy,
                FormatStrategy(candidate),
                () => SetStrategy(captured));
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90f);
        var level = targetLevel;
        if (ImGui.InputInt("Target Lv.##OutfitterLevel", ref level, 1, 10))
            SetTargetLevel(level);
        var levelMin = ImGui.GetItemRectMin();
        var levelMax = ImGui.GetItemRectMax();
        reviewRegistry.Register(
            "squire.outfitter.target-level.job-level",
            "Set Outfitter target to current job level",
            AgentBridgeUiControlKind.Button,
            levelMin,
            levelMax,
            selectedTarget?.Job is not null,
            false,
            selectedTarget?.Job?.Level.ToString(),
            () => SetTargetLevel(checked((int)(selectedTarget?.Job?.Level ?? 1))));
        ImGui.SameLine();
        var quoteEnabled = strategy != EquipmentLoadoutStrategy.BestOwned && !quoteRefreshRunning;
        if (ImGuiUi.Button(quoteRefreshRunning ? "Refreshing prices...##OutfitterQuotes" : "Refresh market prices##OutfitterQuotes", quoteEnabled))
            RefreshMarketPrices();
        RegisterLastControl(
            "squire.outfitter.refresh-market",
            "Refresh Outfitter market prices",
            AgentBridgeUiControlKind.Button,
            quoteEnabled,
            false,
            quoteRefreshRunning ? "running" : "ready",
            RefreshMarketPrices);
    }

    private void SetStrategy(EquipmentLoadoutStrategy value)
    {
        strategy = value;
        config.Squire.OutfitterStrategy = value.ToString();
        planSignature = string.Empty;
        config.Save();
    }

    private void SetTargetLevel(int value)
    {
        targetLevel = Math.Clamp(value, 1, checked((int)(selectedTarget?.Job?.Level ?? 1)));
        config.Squire.OutfitterTargetLevel = targetLevel;
        planSignature = string.Empty;
        config.Save();
    }

    private void EnsurePlan(CharacterEquipmentSnapshot snapshot)
    {
        if (selectedTarget?.Job is null)
            return;
        var signature = $"{snapshot.GenerationId}:{selectedTarget.Key}:{targetLevel}:{strategy}:{marketQuotes.Count}:{string.Join(',', marketQuotes.Select(value => $"{value.Key}:{value.Value.UnitPriceGil}"))}";
        if (string.Equals(signature, planSignature, StringComparison.Ordinal))
            return;
        var offers = candidateCatalog.BuildOffers(snapshot, selectedTarget, checked((uint)targetLevel), marketQuotes);
        var current = candidateCatalog.BuildCurrentItems(snapshot, selectedTarget);
        plan = solver.Plan(new(
            selectedTarget.Job,
            checked((uint)targetLevel),
            strategy,
            offers,
            current));
        planSignature = signature;
        status = marketQuotes.Count == 0
            ? $"Planned {plan.Entries.Count:N0} slots from {offers.Count:N0} accessible offers."
            : $"Plan uses {marketQuotes.Count:N0} current market quote{(marketQuotes.Count == 1 ? string.Empty : "s")} across {offers.Count:N0} accessible offers.";
    }

    private static void DrawSummary(EquipmentLoadoutPlan value)
    {
        if (!ImGui.BeginTable("##OutfitterSummary", 5, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
            return;
        SummaryCell("Current", value.CurrentAverageItemLevel == 0 ? "—" : $"i{value.CurrentAverageItemLevel:N0}", MarketMafiosoUiTheme.Muted);
        SummaryCell("Planned", value.RecommendedAverageItemLevel == 0 ? "—" : $"i{value.RecommendedAverageItemLevel:N0}", MarketMafiosoUiTheme.Header);
        SummaryCell("Upgrades", value.UpgradeCount.ToString("N0"), value.UpgradeCount > 0 ? MarketMafiosoUiTheme.Success : MarketMafiosoUiTheme.Muted);
        SummaryCell("Acquire", value.AcquisitionCount.ToString("N0"), value.AcquisitionCount > 0 ? MarketMafiosoUiTheme.Warning : MarketMafiosoUiTheme.Muted);
        SummaryCell("Estimate", value.EstimatedAcquisitionCost == 0 ? "—" : $"{value.EstimatedAcquisitionCost:N0} gil", MarketMafiosoUiTheme.Warning);
        ImGui.EndTable();
    }

    private static void SummaryCell(string label, string value, Vector4 color)
    {
        ImGui.TableNextColumn();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, label);
        ImGui.TextColored(color, value);
    }

    private static void DrawLoadoutTable(EquipmentLoadoutPlan value)
    {
        var flags = ImGuiUi.InteractiveTableFlags | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;
        var tableHeight = Math.Max(230f, ImGui.GetContentRegionAvail().Y - 58f);
        if (!ImGui.BeginTable("##OutfitterLoadout", 6, flags, new Vector2(0, tableHeight)))
            return;
        ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 76f);
        ImGui.TableSetupColumn("Current", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Planned", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Δ", ImGuiTableColumnFlags.WidthFixed, 42f);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 150f);
        ImGui.TableSetupColumn("Cost", ImGuiTableColumnFlags.WidthFixed, 86f);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();
        foreach (var entry in value.Entries)
        {
            ImGui.TableNextRow();
            Cell(FormatPosition(entry.Position));
            ItemCell(entry.Current, MarketMafiosoUiTheme.Muted);
            ItemCell(entry.Recommended, entry.RequiresAcquisition ? MarketMafiosoUiTheme.Warning : MarketMafiosoUiTheme.Link);
            ImGui.TableNextColumn();
            ImGui.TextColored(entry.ItemLevelDelta > 0 ? MarketMafiosoUiTheme.Success : MarketMafiosoUiTheme.Muted, entry.ItemLevelDelta == 0 ? "—" : $"+{entry.ItemLevelDelta:N0}");
            ImGui.TableNextColumn();
            var sourceLabel = entry.Recommended?.SourceLabel ?? entry.Diagnostic ?? "Unavailable";
            ImGui.TextColored(SourceColor(entry.Recommended), sourceLabel);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(sourceLabel);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Recommended?.UnitPriceGil is { } price ? $"{price:N0}" : "—");
        }
        ImGui.EndTable();
    }

    private void DrawAcquisitionBar(EquipmentLoadoutPlan value)
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, status);
        var marketEntries = value.Entries
            .Where(entry => entry.Recommended?.SourceKind == EquipmentAcquisitionSourceKind.MarketBoard)
            .ToArray();
        var canStage = marketEntries.Length > 0 && stageMarketLines is not null;
        ImGui.SameLine();
        if (ImGuiUi.Button($"Stage {marketEntries.Length:N0} market item{(marketEntries.Length == 1 ? string.Empty : "s")}##OutfitterStage", canStage))
            StageMarketLines(marketEntries);
        RegisterLastControl(
            "squire.outfitter.stage-market",
            "Stage Outfitter market items in Market Acquisition",
            AgentBridgeUiControlKind.Button,
            canStage,
            false,
            marketEntries.Length.ToString(),
            () => StageMarketLines(marketEntries));
    }

    private async void RefreshMarketPrices()
    {
        if (quoteRefreshRunning || plan is null)
            return;
        var itemIds = plan.Entries
            .SelectMany(entry => new[] { entry.Recommended }.Concat(entry.Alternatives))
            .Where(offer => offer?.SourceKind == EquipmentAcquisitionSourceKind.MarketBoard)
            .Select(offer => offer!.Definition.ItemId)
            .Distinct()
            .ToArray();
        if (itemIds.Length == 0)
        {
            status = "This plan does not contain market-board candidates.";
            return;
        }

        quoteCancellation?.Cancel();
        quoteCancellation?.Dispose();
        quoteCancellation = new CancellationTokenSource();
        quoteRefreshRunning = true;
        status = $"Refreshing {Math.Min(24, itemIds.Length):N0} market quote{(itemIds.Length == 1 ? string.Empty : "s")}...";
        try
        {
            var region = config.ActiveMarketAcquisitionRequestDocument?.Region;
            if (string.IsNullOrWhiteSpace(region))
                region = config.ActiveMarketAcquisitionClaim?.Region;
            if (string.IsNullOrWhiteSpace(region))
                region = "North America";
            marketQuotes = await quoteService.FetchAsync(region, itemIds, quoteCancellation.Token).ConfigureAwait(false);
            planSignature = string.Empty;
            status = $"Loaded {marketQuotes.Count:N0} current Universalis quote{(marketQuotes.Count == 1 ? string.Empty : "s")}.";
        }
        catch (OperationCanceledException)
        {
            status = "Market quote refresh canceled.";
        }
        catch (Exception ex)
        {
            status = $"Market quote refresh failed: {ex.Message}";
        }
        finally
        {
            quoteRefreshRunning = false;
        }
    }

    private void StageMarketLines(IReadOnlyList<EquipmentLoadoutPlanEntry> entries)
    {
        if (stageMarketLines is null || entries.Count == 0)
            return;
        var lines = entries
            .Where(entry => entry.Recommended is not null)
            .GroupBy(entry => entry.Recommended!.Definition.ItemId)
            .Select(group =>
            {
                var offer = group.First().Recommended!;
                var unitCeiling = offer.UnitPriceGil is { } quoted ? checked((uint)Math.Ceiling(quoted * 1.10d)) : 0;
                var quantity = checked((uint)group.Count());
                return new MarketAcquisitionRequestLineDocument
                {
                    ItemId = offer.Definition.ItemId,
                    ItemName = offer.Definition.Name,
                    ItemKind = "Equipment",
                    QuantityMode = "TargetQuantity",
                    TargetQuantity = quantity,
                    MaxQuantity = quantity,
                    HqPolicy = "Either",
                    MaxUnitPrice = unitCeiling,
                    GilCap = checked(unitCeiling * quantity),
                };
            })
            .ToArray();
        stageMarketLines(lines);
        status = $"Staged {lines.Length:N0} line{(lines.Length == 1 ? string.Empty : "s")} in Market Acquisition.";
    }

    private bool MatchesSearch(OutfitterTarget target) => string.IsNullOrWhiteSpace(search) ||
        target.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        target.Subtitle.Contains(search, StringComparison.OrdinalIgnoreCase);

    private int ResolveTargetLevel(OutfitterTarget target)
    {
        var jobLevel = checked((int)(target.Job?.Level ?? 1));
        return config.Squire.OutfitterTargetLevel > 0
            ? Math.Clamp(config.Squire.OutfitterTargetLevel, 1, jobLevel)
            : jobLevel;
    }

    private static string FormatStrategy(EquipmentLoadoutStrategy value) => value switch
    {
        EquipmentLoadoutStrategy.HighestItemLevel => "Best accessible",
        EquipmentLoadoutStrategy.MinimizeSpend => "Budget upgrade",
        EquipmentLoadoutStrategy.BestOwned => "Best owned",
        _ => value.ToString(),
    };

    private static string StrategyControlSuffix(EquipmentLoadoutStrategy value) => value switch
    {
        EquipmentLoadoutStrategy.HighestItemLevel => "best-accessible",
        EquipmentLoadoutStrategy.MinimizeSpend => "budget-upgrade",
        EquipmentLoadoutStrategy.BestOwned => "best-owned",
        _ => value.ToString().ToLowerInvariant(),
    };

    private static string FormatPosition(EquipmentLoadoutPosition value) => value switch
    {
        EquipmentLoadoutPosition.MainHand => "Main hand",
        EquipmentLoadoutPosition.OffHand => "Off hand",
        EquipmentLoadoutPosition.LeftRing => "Ring 1",
        EquipmentLoadoutPosition.RightRing => "Ring 2",
        _ => value.ToString(),
    };

    private static string FormatTargetBadge(OutfitterTarget target) => target.Kind switch
    {
        OutfitterTargetKind.Job => $"Lv {target.Job?.Level ?? 0:N0}",
        OutfitterTargetKind.Gearset => $"#{(target.Gearset?.GearsetId ?? 0) + 1:N0}",
        OutfitterTargetKind.Retainer => FormatAge(target.Retainer?.LastUpdated),
        _ => string.Empty,
    };

    private static string FormatAge(DateTime? capturedAt)
    {
        if (capturedAt is null)
            return "—";
        var age = DateTime.UtcNow - capturedAt.Value.ToUniversalTime();
        if (age.TotalHours < 1)
            return $"{Math.Max(1, (int)age.TotalMinutes):N0}m";
        if (age.TotalDays < 2)
            return $"{Math.Max(1, (int)age.TotalHours):N0}h";
        return $"{Math.Max(1, (int)age.TotalDays):N0}d";
    }

    private static Vector4 SourceColor(EquipmentLoadoutOffer? offer) => offer?.SourceKind switch
    {
        EquipmentAcquisitionSourceKind.Owned => MarketMafiosoUiTheme.Success,
        EquipmentAcquisitionSourceKind.GilVendor => MarketMafiosoUiTheme.Header,
        EquipmentAcquisitionSourceKind.MarketBoard => MarketMafiosoUiTheme.Warning,
        _ => MarketMafiosoUiTheme.Muted,
    };

    private static void Cell(string value)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(value);
    }

    private static void ItemCell(EquipmentLoadoutOffer? offer, Vector4 color)
    {
        ImGui.TableNextColumn();
        if (offer is null)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "—");
            return;
        }
        ImGui.TextColored(color, offer.Definition.Name);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Item level {offer.Definition.ItemLevel:N0}\nEquip level {offer.Definition.EquipLevel:N0}\nItem ID {offer.Definition.ItemId:N0}");
    }

    private void RegisterLastControl(
        string id,
        string label,
        AgentBridgeUiControlKind kind,
        bool enabled,
        bool selected,
        string? value,
        Action invoke) =>
        reviewRegistry.Register(id, label, kind, ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), enabled, selected, value, invoke);

    public void Dispose()
    {
        quoteCancellation?.Cancel();
        quoteCancellation?.Dispose();
    }
}
