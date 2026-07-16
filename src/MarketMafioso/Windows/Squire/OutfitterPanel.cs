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
    private readonly IOutfitterRetainerMetadataSource retainerMetadataSource;
    private readonly EquipmentLoadoutSolver solver = new();
    private readonly OutfitterMarketQuoteService quoteService;
    private readonly Func<bool> isMarketAcquisitionUnlocked;
    private IReadOnlyDictionary<uint, OutfitterMarketQuote> marketQuotes = new Dictionary<uint, OutfitterMarketQuote>();
    private CharacterEquipmentSnapshot? lastSnapshot;
    private IReadOnlyList<OutfitterTarget> targets = [];
    private OutfitterTarget? selectedTarget;
    private EquipmentLoadoutPlan? plan;
    private string search;
    private EquipmentLoadoutStrategy strategy;
    private int targetLevel;
    private OutfitterTargetView targetView;
    private string status = "Choose a target to begin.";
    private OutfitterPlanCacheKey? planCacheKey;
    private string retainerMetadataSignature = string.Empty;
    private DateTimeOffset nextRetainerMetadataRefreshAtUtc = DateTimeOffset.MinValue;
    private IReadOnlyList<OutfitterTarget>? visibleTargetSource;
    private IReadOnlyList<OutfitterTarget> visibleTargets = [];
    private string visibleTargetSearch = string.Empty;
    private OutfitterTargetView visibleTargetView;
    private int jobTargetCount;
    private int retainerTargetCount;
    private int marketQuoteRevision;
    private Task<IReadOnlyDictionary<uint, OutfitterMarketQuote>>? quoteRefreshTask;
    private CancellationTokenSource? quoteCancellation;
    private Action<IReadOnlyList<MarketAcquisitionRequestLineDocument>>? stageMarketLines;

    public OutfitterPanel(
        Configuration config,
        OutfitterCandidateCatalog candidateCatalog,
        OutfitterMarketQuoteService quoteService,
        IOutfitterRetainerMetadataSource retainerMetadataSource,
        Func<bool> isMarketAcquisitionUnlocked,
        AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.candidateCatalog = candidateCatalog ?? throw new ArgumentNullException(nameof(candidateCatalog));
        this.quoteService = quoteService ?? throw new ArgumentNullException(nameof(quoteService));
        this.retainerMetadataSource = retainerMetadataSource ?? throw new ArgumentNullException(nameof(retainerMetadataSource));
        this.isMarketAcquisitionUnlocked = isMarketAcquisitionUnlocked ?? throw new ArgumentNullException(nameof(isMarketAcquisitionUnlocked));
        this.reviewRegistry = reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry));
        search = config.Squire.OutfitterSearch;
        strategy = Enum.TryParse<EquipmentLoadoutStrategy>(config.Squire.OutfitterStrategy, out var storedStrategy)
            ? storedStrategy
            : EquipmentLoadoutStrategy.HighestItemLevel;
        targetLevel = config.Squire.OutfitterTargetLevel;
        targetView = Enum.TryParse<OutfitterTargetView>(config.Squire.OutfitterTargetView, out var storedView)
            ? storedView
            : OutfitterTargetView.Jobs;
    }

    public void ConnectMarketAcquisition(Action<IReadOnlyList<MarketAcquisitionRequestLineDocument>> stageLines) =>
        stageMarketLines = stageLines ?? throw new ArgumentNullException(nameof(stageLines));

    public void Draw(CharacterEquipmentSnapshot snapshot)
    {
        ObserveMarketPriceRefresh();
        RefreshTargets(snapshot);
        if (!ImGui.BeginTable(
                "##SquireOutfitterLayout",
                2,
                ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings,
                new Vector2(0, Math.Max(360f, ImGui.GetContentRegionAvail().Y))))
            return;

        ImGui.TableSetupColumn("Targets", ImGuiTableColumnFlags.WidthFixed, 310f);
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
        var now = DateTimeOffset.UtcNow;
        var snapshotChanged = lastSnapshot?.GenerationId != snapshot.GenerationId;
        if (!snapshotChanged && now < nextRetainerMetadataRefreshAtUtc)
            return;

        nextRetainerMetadataRefreshAtUtc = now.AddSeconds(1);
        var retainerMetadata = retainerMetadataSource.ReadAll();
        var metadataSignature = string.Join('|', retainerMetadata
            .OrderBy(value => value.RetainerId)
            .Select(value => $"{value.OwnerContentId}:{value.RetainerId}:{value.ClassJobId}:{value.Level}"));
        if (lastSnapshot?.GenerationId == snapshot.GenerationId &&
            string.Equals(retainerMetadataSignature, metadataSignature, StringComparison.Ordinal))
            return;

        lastSnapshot = snapshot;
        retainerMetadataSignature = metadataSignature;
        targets = targetCatalog.Build(snapshot, config.RetainerCache, retainerMetadata);
        jobTargetCount = targets.Count(target => target.Kind == OutfitterTargetKind.Job);
        retainerTargetCount = targets.Count(target => target.Kind == OutfitterTargetKind.Retainer);
        InvalidateVisibleTargets();
        selectedTarget = targets.FirstOrDefault(target =>
                             string.Equals(target.Key, config.Squire.OutfitterSelectedTargetKey, StringComparison.Ordinal))
                         ?? TargetsForView(targetView).FirstOrDefault()
                         ?? targets.FirstOrDefault(target => target.Kind == OutfitterTargetKind.Job)
                         ?? targets.FirstOrDefault();
        if (selectedTarget is not null)
        {
            config.Squire.OutfitterSelectedTargetKey = selectedTarget.Key;
            targetView = selectedTarget.Kind == OutfitterTargetKind.Retainer ? OutfitterTargetView.Retainers : OutfitterTargetView.Jobs;
            targetLevel = ResolveTargetLevel(selectedTarget);
        }
        planCacheKey = null;
    }

    private void DrawTargets()
    {
        DrawTargetViewButton(OutfitterTargetView.Jobs, $"Jobs  {jobTargetCount:N0}");
        ImGui.SameLine();
        DrawTargetViewButton(OutfitterTargetView.Retainers, $"Retainers  {retainerTargetCount:N0}");
        ImGui.SetNextItemWidth(-1);
        var searchHint = targetView == OutfitterTargetView.Jobs ? "Search jobs or roles" : "Search retainers or owners";
        if (ImGui.InputTextWithHint("##OutfitterTargetSearch", searchHint, ref search, 128))
            InvalidateVisibleTargets();
        if (ImGui.IsItemDeactivatedAfterEdit() && !string.Equals(config.Squire.OutfitterSearch, search, StringComparison.Ordinal))
        {
            config.Squire.OutfitterSearch = search;
            config.Save();
        }
        var visible = GetVisibleTargets();
        ImGui.BeginChild("##OutfitterTargets", new Vector2(0, -1), true);
        if (targetView == OutfitterTargetView.Retainers && visible.Count > 0 && visible.All(target => !target.IsCurrentCharacter))
        {
            var currentName = lastSnapshot?.Identity.Scope?.Name ?? "the current character";
            ImGui.PushStyleColor(ImGuiCol.Text, MarketMafiosoUiTheme.Warning);
            ImGui.TextWrapped($"No retainers registered for {currentName}.");
            ImGui.PopStyleColor();
            ImGui.TextWrapped("Showing other AutoRetainer characters and legacy MMF caches.");
            ImGui.Separator();
        }
        string? lastGroup = null;
        foreach (var target in visible)
        {
            var group = TargetGroup(target);
            if (!string.Equals(group, lastGroup, StringComparison.Ordinal))
            {
                if (lastGroup is not null)
                    ImGui.Spacing();
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, group.ToUpperInvariant());
                lastGroup = group;
            }
            DrawTargetRow(target);
        }
        if (visible.Count == 0)
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "No targets match this search.");
        ImGui.EndChild();
    }

    private void DrawTargetRow(OutfitterTarget target)
    {
        var selected = target.Kind == OutfitterTargetKind.Job
            ? selectedTarget?.Kind != OutfitterTargetKind.Retainer && selectedTarget?.Job?.ClassJobId == target.Job?.ClassJobId
            : selectedTarget?.Key == target.Key;
        if (!ImGui.BeginTable($"##OutfitterTargetRow{target.Key}", 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings))
            return;
        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Badge", ImGuiTableColumnFlags.WidthFixed, 42f);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        if (ImGui.Selectable($"{target.Name}##OutfitterTarget{target.Key}", selected, ImGuiSelectableFlags.SpanAllColumns))
            SelectPrimaryTarget(target);
        RegisterLastControl(
            $"squire.outfitter.target.{target.Key}",
            $"Select {target.Name}",
            AgentBridgeUiControlKind.Select,
            true,
            selected,
            target.Subtitle,
            () => SelectPrimaryTarget(target));
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

    private void DrawTargetViewButton(OutfitterTargetView view, string label)
    {
        var selected = targetView == view;
        if (ImGui.Selectable($"{label}##OutfitterView{view}", selected, ImGuiSelectableFlags.None, new Vector2(145f, 0)))
            SetTargetView(view);
        RegisterLastControl(
            $"squire.outfitter.view.{view.ToString().ToLowerInvariant()}",
            $"Show Outfitter {view.ToString().ToLowerInvariant()}",
            AgentBridgeUiControlKind.Select,
            true,
            selected,
            view.ToString(),
            () => SetTargetView(view));
    }

    private void SetTargetView(OutfitterTargetView view)
    {
        targetView = view;
        InvalidateVisibleTargets();
        config.Squire.OutfitterTargetView = view.ToString();
        if (selectedTarget is null || (view == OutfitterTargetView.Retainers) != (selectedTarget.Kind == OutfitterTargetKind.Retainer))
        {
            var first = TargetsForView(view).FirstOrDefault();
            if (first is not null)
                SelectPrimaryTarget(first);
        }
        config.Save();
    }

    private void SelectPrimaryTarget(OutfitterTarget target)
    {
        if (target.Kind == OutfitterTargetKind.Job && target.Job is { } job &&
            config.Squire.OutfitterSelectedGearsetByJob.TryGetValue(job.ClassJobId, out var preferredKey))
        {
            target = targets.FirstOrDefault(value => string.Equals(value.Key, preferredKey, StringComparison.Ordinal)) ?? target;
        }
        SelectTarget(target);
    }

    private void SelectTarget(OutfitterTarget target)
    {
        selectedTarget = target;
        config.Squire.OutfitterSelectedTargetKey = target.Key;
        if (target.Job is not null)
        {
            targetLevel = ResolveTargetLevel(target);
            config.Squire.OutfitterTargetLevels[TargetLevelKey(target)] = targetLevel;
            if (target.Kind != OutfitterTargetKind.Retainer)
                config.Squire.OutfitterSelectedGearsetByJob[target.Job.ClassJobId] = target.Key;
        }
        marketQuotes = new Dictionary<uint, OutfitterMarketQuote>();
        marketQuoteRevision++;
        planCacheKey = null;
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
        if (selectedTarget.Kind == OutfitterTargetKind.Retainer)
            DrawRetainerOverview(selectedTarget);
        if (!selectedTarget.IsReady || selectedTarget.Job is null)
        {
            ImGui.Separator();
            ImGui.TextWrapped(selectedTarget.Diagnostic ?? "This target cannot be planned yet.");
            ImGui.Spacing();
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "The target stays visible so stale or incomplete retainer state is never mistaken for a missing retainer.");
            return;
        }

        DrawGearsetPicker();
        DrawPolicyBar();
        EnsurePlan(snapshot);
        if (plan is null)
            return;
        DrawSummary(plan);
        DrawLoadoutTable(plan);
        DrawAcquisitionBar(plan);
    }

    private void DrawRetainerOverview(OutfitterTarget target)
    {
        ImGui.Separator();
        if (!ImGui.BeginTable("##OutfitterRetainerSummary", 4, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
            return;
        SummaryCell("Owner", FormatOwner(target), MarketMafiosoUiTheme.Header);
        SummaryCell("Job", target.Job?.Abbreviation ?? "Unknown", target.Job is null ? MarketMafiosoUiTheme.Warning : MarketMafiosoUiTheme.Header);
        SummaryCell("Level", target.RetainerMetadata?.Level > 0 ? target.RetainerMetadata.Level.ToString() : "Unknown", MarketMafiosoUiTheme.Muted);
        SummaryCell("Inventory", target.Retainer is null ? "Not cached" : FormatAge(target.Retainer.LastUpdated), target.Retainer is null ? MarketMafiosoUiTheme.Warning : MarketMafiosoUiTheme.Success);
        ImGui.EndTable();
    }

    private void DrawGearsetPicker()
    {
        if (selectedTarget?.Kind == OutfitterTargetKind.Retainer || selectedTarget?.Job is null)
            return;
        var gearsets = targets
            .Where(value => value.Kind == OutfitterTargetKind.Gearset && value.Job?.ClassJobId == selectedTarget.Job.ClassJobId)
            .ToArray();
        if (gearsets.Length == 0)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Warning, "No saved gearset is available for this job; current-slot comparison is unavailable.");
            return;
        }
        ImGui.SetNextItemWidth(260f);
        var preview = selectedTarget.Gearset?.Name ?? gearsets[0].Name;
        if (ImGui.BeginCombo("Gearset baseline##OutfitterGearset", preview))
        {
            foreach (var gearset in gearsets)
            {
                var selected = gearset.Gearset?.GearsetId == selectedTarget.Gearset?.GearsetId;
                if (ImGui.Selectable($"{gearset.Name}##OutfitterGearset{gearset.Key}", selected))
                    SelectTarget(gearset);
            }
            ImGui.EndCombo();
        }
        if (gearsets.Length > 1)
        {
            ImGui.SameLine();
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"{gearsets.Length:N0} saved gearsets for {selectedTarget.Job.Abbreviation}");
        }
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
        var quoteEnabled = strategy != EquipmentLoadoutStrategy.BestOwned && !QuoteRefreshRunning;
        if (ImGuiUi.Button(QuoteRefreshRunning ? "Refreshing prices...##OutfitterQuotes" : "Refresh market prices##OutfitterQuotes", quoteEnabled))
            RefreshMarketPrices();
        RegisterLastControl(
            "squire.outfitter.refresh-market",
            "Refresh Outfitter market prices",
            AgentBridgeUiControlKind.Button,
            quoteEnabled,
            false,
            QuoteRefreshRunning ? "running" : "ready",
            RefreshMarketPrices);
    }

    private void SetStrategy(EquipmentLoadoutStrategy value)
    {
        strategy = value;
        config.Squire.OutfitterStrategy = value.ToString();
        planCacheKey = null;
        config.Save();
    }

    private void SetTargetLevel(int value)
    {
        targetLevel = Math.Clamp(value, 1, checked((int)(selectedTarget?.Job?.Level ?? 1)));
        config.Squire.OutfitterTargetLevel = targetLevel;
        if (selectedTarget is not null)
            config.Squire.OutfitterTargetLevels[TargetLevelKey(selectedTarget)] = targetLevel;
        planCacheKey = null;
        config.Save();
    }

    private void EnsurePlan(CharacterEquipmentSnapshot snapshot)
    {
        if (selectedTarget?.Job is null)
            return;
        var key = new OutfitterPlanCacheKey(
            snapshot.GenerationId,
            selectedTarget.Key,
            targetLevel,
            strategy,
            marketQuoteRevision);
        if (planCacheKey == key)
            return;
        var offers = candidateCatalog.BuildOffers(snapshot, selectedTarget, checked((uint)targetLevel), marketQuotes);
        var current = candidateCatalog.BuildCurrentItems(snapshot, selectedTarget);
        plan = solver.Plan(new(
            selectedTarget.Job,
            checked((uint)targetLevel),
            strategy,
            offers,
            current));
        planCacheKey = key;
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
        if (!isMarketAcquisitionUnlocked())
            return;

        var canStage = marketEntries.Length > 0 && stageMarketLines is not null;
        ImGui.SameLine();
        if (ImGuiUi.Button($"Stage {marketEntries.Length:N0} market item{(marketEntries.Length == 1 ? string.Empty : "s")}##OutfitterStage", canStage))
            StageMarketLines(marketEntries);
        RegisterLastControl(
            "squire.outfitter.stage-market",
            "Stage Outfitter purchases for execution",
            AgentBridgeUiControlKind.Button,
            canStage,
            false,
            marketEntries.Length.ToString(),
            () => StageMarketLines(marketEntries));
    }

    private bool QuoteRefreshRunning => quoteRefreshTask is { IsCompleted: false };

    private void RefreshMarketPrices()
    {
        if (quoteRefreshTask is not null || plan is null)
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
        status = $"Refreshing {Math.Min(24, itemIds.Length):N0} market quote{(itemIds.Length == 1 ? string.Empty : "s")}...";
        var region = config.ActiveMarketAcquisitionRequestDocument?.Region;
        if (string.IsNullOrWhiteSpace(region))
            region = config.ActiveMarketAcquisitionClaim?.Region;
        if (string.IsNullOrWhiteSpace(region))
            region = "North America";
        quoteRefreshTask = quoteService.FetchAsync(region, itemIds, quoteCancellation.Token);
    }

    private void ObserveMarketPriceRefresh()
    {
        var completed = quoteRefreshTask;
        if (completed is not { IsCompleted: true })
            return;

        quoteRefreshTask = null;
        try
        {
            marketQuotes = completed.GetAwaiter().GetResult();
            marketQuoteRevision++;
            planCacheKey = null;
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
            quoteCancellation?.Dispose();
            quoteCancellation = null;
        }
    }

    private void StageMarketLines(IReadOnlyList<EquipmentLoadoutPlanEntry> entries)
    {
        if (stageMarketLines is null || entries.Count == 0)
            return;
        var result = OutfitterMarketStaging.Build(entries);
        stageMarketLines(result.Lines);
        status = $"Staged {result.Lines.Count:N0} purchase line{(result.Lines.Count == 1 ? string.Empty : "s")} for execution.";
        if (result.WasClamped)
            status += " Values above supported request limits were capped.";
    }

    private bool MatchesSearch(OutfitterTarget target) => string.IsNullOrWhiteSpace(search) ||
        target.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        target.Subtitle.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        (target.Job?.Abbreviation.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (target.Job?.Role?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (target.OwnerCharacterName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (target.OwnerHomeWorld?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);

    private IReadOnlyList<OutfitterTarget> GetVisibleTargets()
    {
        if (ReferenceEquals(visibleTargetSource, targets) &&
            visibleTargetView == targetView &&
            string.Equals(visibleTargetSearch, search, StringComparison.Ordinal))
        {
            return visibleTargets;
        }

        visibleTargetSource = targets;
        visibleTargetView = targetView;
        visibleTargetSearch = search;
        visibleTargets = TargetsForView(targetView)
            .Where(MatchesSearch)
            .OrderByDescending(value => value.IsCurrentCharacter)
            .ThenBy(TargetGroupSort)
            .ThenBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return visibleTargets;
    }

    private void InvalidateVisibleTargets() => visibleTargetSource = null;

    private int ResolveTargetLevel(OutfitterTarget target)
    {
        var jobLevel = checked((int)(target.Job?.Level ?? 1));
        return config.Squire.OutfitterTargetLevels.TryGetValue(TargetLevelKey(target), out var storedLevel) && storedLevel > 0
            ? Math.Clamp(storedLevel, 1, jobLevel)
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
        OutfitterTargetKind.Job => $"{target.Job?.Abbreviation} {target.Job?.Level ?? 0:N0}",
        OutfitterTargetKind.Gearset => $"#{(target.Gearset?.GearsetId ?? 0) + 1:N0}",
        OutfitterTargetKind.Retainer when target.Job is not null => $"{target.Job.Abbreviation} {target.RetainerMetadata?.Level ?? 0:N0}",
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

    private IEnumerable<OutfitterTarget> TargetsForView(OutfitterTargetView view) => view switch
    {
        OutfitterTargetView.Jobs => targets.Where(value => value.Kind == OutfitterTargetKind.Job),
        OutfitterTargetView.Retainers => targets.Where(value => value.Kind == OutfitterTargetKind.Retainer),
        _ => [],
    };

    private static string TargetGroup(OutfitterTarget target) => target.Kind == OutfitterTargetKind.Retainer
        ? $"{FormatOwner(target)}{(target.IsCurrentCharacter ? " · current" : string.Empty)}"
        : target.Job?.Discipline switch
        {
            EquipmentDiscipline.Crafter => "Crafters",
            EquipmentDiscipline.Gatherer => "Gatherers",
            _ => target.Job?.Role ?? "Combat jobs",
        };

    private static string TargetGroupSort(OutfitterTarget target) => target.Kind == OutfitterTargetKind.Retainer
        ? FormatOwner(target)
        : target.Job?.Discipline switch
        {
            EquipmentDiscipline.Combat => $"0:{target.Job.Role}",
            EquipmentDiscipline.Crafter => "1:Crafters",
            EquipmentDiscipline.Gatherer => "2:Gatherers",
            _ => "3:Other",
        };

    private static string FormatOwner(OutfitterTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.OwnerCharacterName))
            return "Legacy unscoped cache";
        return string.IsNullOrWhiteSpace(target.OwnerHomeWorld)
            ? target.OwnerCharacterName
            : $"{target.OwnerCharacterName} @ {target.OwnerHomeWorld}";
    }

    private static string TargetLevelKey(OutfitterTarget target) => target.Kind == OutfitterTargetKind.Retainer
        ? target.Key
        : $"job:{target.Job?.ClassJobId ?? 0}";

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
            ImGui.SetTooltip($"{offer.Definition.Name}\nItem level {offer.Definition.ItemLevel:N0}\nEquip level {offer.Definition.EquipLevel:N0}\nItem ID {offer.Definition.ItemId:N0}");
    }

    private void RegisterLastControl(
        string id,
        string label,
        AgentBridgeUiControlKind kind,
        bool enabled,
        bool selected,
        string? value,
        Action invoke) =>
        reviewRegistry.RegisterLastItem(id, label, kind, enabled, selected, value, invoke);

    public void Dispose()
    {
        quoteCancellation?.Cancel();
        quoteCancellation?.Dispose();
        if (quoteRefreshTask is { IsCompleted: false } pending)
        {
            _ = pending.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}

internal readonly record struct OutfitterPlanCacheKey(
    Guid SnapshotGenerationId,
    string TargetKey,
    int TargetLevel,
    EquipmentLoadoutStrategy Strategy,
    int MarketQuoteRevision);

internal enum OutfitterTargetView
{
    Jobs,
    Retainers,
}
