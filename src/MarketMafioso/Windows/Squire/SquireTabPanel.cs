using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using MarketMafioso.Squire;
using MarketMafioso.Squire.Observation;
using MarketMafioso.AgentBridge;
using MarketMafioso.Windows.Main;
using Newtonsoft.Json;
using Franthropy.Dalamud.AgentBridge;
using Franthropy.Dalamud.Equipment;

namespace MarketMafioso.Windows.Squire;

internal sealed class SquireTabPanel : IDisposable
{
    private readonly ICharacterEquipmentSnapshotSource snapshotSource;
    private readonly ISquireActionGameAdapter actionAdapter;
    private readonly ISquireDispositionCapabilitySource capabilitySource;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;
    private readonly Configuration config;
    private readonly SquireCandidateEvaluator evaluator = new();
    private readonly SquireReviewState review = new();
    private readonly string diagnosticDirectory;
    private SquireAnalysis? analysis;
    private string search = string.Empty;
    private bool showProtected;
    private bool showNonEquipment;
    private bool selectionMode;
    private readonly HashSet<EquipmentInstanceFingerprint> tableSelection = [];
    private EquipmentInstanceFingerprint? selectionAnchor;
    private readonly string[] columnFilters = new string[7];
    private int selectionDragStart = -1;
    private bool selectionDragValue;
    private EquipmentInstanceFingerprint? focusedItem;
    private uint? pendingHighRarityOverrideItemId;
    private string status = "Refresh to capture a read-only equipment snapshot.";
    private bool runConfirmed;
    private CancellationTokenSource? runCancellation;
    private Task? activeRun;

    public SquireTabPanel(
        Configuration config,
        ICharacterEquipmentSnapshotSource snapshotSource,
        ISquireActionGameAdapter actionAdapter,
        ISquireDispositionCapabilitySource capabilitySource,
        AgentBridgeUiReviewRegistry reviewRegistry,
        string diagnosticDirectory)
    {
        this.config = config;
        this.snapshotSource = snapshotSource;
        this.actionAdapter = actionAdapter;
        this.capabilitySource = capabilitySource;
        this.reviewRegistry = reviewRegistry;
        this.diagnosticDirectory = diagnosticDirectory;
        search = config.Squire.Search;
        showProtected = config.Squire.ShowProtected;
        showNonEquipment = config.Squire.ShowNonEquipment;
    }

    public void Draw()
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Squire V1 - cleanup selection");
        ImGui.TextWrapped("Squire identifies obsolete leveling gear. Refresh only analyzes; it never disposes of an item.");
        if (ImGui.Button("Refresh##Squire"))
            Refresh();
        RegisterLastControl("squire.refresh", "Refresh Squire analysis", AgentBridgeUiControlKind.Button, true, false, null, Refresh);
        ImGui.SameLine();
        if (analysis is not null && ImGui.Button("Export diagnostics##Squire"))
            Export();
        ImGui.SameLine();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, status);
        ImGui.Separator();

        if (analysis is null)
            return;
        DrawSummary(analysis);
        DrawDiagnostics(analysis);
        ImGui.Separator();
        ImGui.SetNextItemWidth(280);
        if (ImGui.InputTextWithHint("##SquireSearch", "Search item, location, or reason", ref search, 160))
        {
            config.Squire.Search = search;
            config.Save();
        }
        ImGui.SameLine();
        if (ImGui.Checkbox("Show protected", ref showProtected))
        {
            config.Squire.ShowProtected = showProtected;
            config.Save();
        }
        ImGui.SameLine();
        if (ImGui.Checkbox("Show non-equipment", ref showNonEquipment))
        {
            config.Squire.ShowNonEquipment = showNonEquipment;
            config.Save();
        }
        ImGui.SameLine();
        ImGui.Checkbox("Selection mode", ref selectionMode);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Select any rows for inspection. Ctrl-click toggles rows; Shift-click selects the range from the anchor. Only executable candidates enter the action batch.");
        if (selectionMode && tableSelection.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear selection"))
            {
                tableSelection.Clear();
                selectionAnchor = null;
                review.Clear();
            }
        }
        DrawTable(analysis);
        DrawFocusedItem(analysis);
        DrawRunPanel(analysis);
    }

    private void Refresh()
    {
        if (activeRun is { IsCompleted: false })
        {
            status = "Refresh is blocked while Squire owns an active run.";
            return;
        }
        try
        {
            var snapshot = snapshotSource.Capture();
            var policy = CreateProtectionPolicy(snapshot.Identity.Scope?.LocalContentId);
            analysis = evaluator.Evaluate(
                snapshot,
                capabilitySource.Capture(),
                policy);
            review.Adopt(analysis);
            tableSelection.Clear();
            selectionAnchor = null;
            focusedItem = null;
            selectionDragStart = -1;
            runConfirmed = false;
            var executable = analysis.Candidates.Count(candidate => candidate.IsExecutable);
            status = analysis.IsActionable
                ? $"Complete snapshot; {executable} executable candidate(s)."
                : "Snapshot is incomplete; actions are blocked.";
        }
        catch (Exception ex)
        {
            analysis = null;
            review.Invalidate();
            status = $"Refresh failed: {ex.Message}";
        }
    }

    public void RefreshForBridge() => Refresh();

    public AgentBridgeSquireTruth CreateAgentBridgeTruth()
    {
        if (analysis is null)
        {
            return new AgentBridgeSquireTruth
            {
                HasSnapshot = false,
                Status = status,
                CharacterName = null,
                HomeWorldId = null,
                CapturedAtUtc = null,
                IsComplete = false,
                UnlockedJobCount = 0,
                ValidGearsetCount = 0,
                InstanceCount = 0,
                CandidateCount = 0,
                ProtectedCount = 0,
                EvaluationFailureCount = 0,
                UnsupportedCount = 0,
                BlockingDiagnostics = [],
                EvaluationFailureGroups = [],
                ProtectionGroups = [],
                ExecutableCandidates = [],
            };
        }

        var snapshot = analysis.Snapshot;
        return new AgentBridgeSquireTruth
        {
            HasSnapshot = true,
            Status = status,
            CharacterName = snapshot.Identity.Scope?.Name,
            HomeWorldId = snapshot.Identity.Scope?.HomeWorldId.ToString(),
            CapturedAtUtc = snapshot.Identity.CapturedAt,
            IsComplete = snapshot.Diagnostics.IsComplete,
            UnlockedJobCount = snapshot.Jobs.Count(job => job.IsUnlocked == true),
            ValidGearsetCount = snapshot.Gearsets.Count(gearset => gearset.IsValid),
            InstanceCount = snapshot.Instances.Count,
            CandidateCount = analysis.Candidates.Count(candidate => candidate.Assessment == SquireAssessment.Candidate),
            ProtectedCount = analysis.Candidates.Count(candidate => candidate.Assessment == SquireAssessment.Protected),
            EvaluationFailureCount = analysis.Candidates.Count(candidate => candidate.Assessment == SquireAssessment.EvaluationFailure),
            UnsupportedCount = analysis.Candidates.Count(candidate => candidate.Assessment == SquireAssessment.Unsupported),
            BlockingDiagnostics = snapshot.Diagnostics.Blocking.Select(value => $"{value.Component}:{value.Status}:{value.Message}").ToArray(),
            EvaluationFailureGroups = analysis.Candidates
                .Where(candidate => candidate.Assessment == SquireAssessment.EvaluationFailure)
                .SelectMany(candidate => candidate.Reasons.Select(reason => new { candidate.Definition.Name, reason.Code, reason.Message }))
                .GroupBy(value => new { value.Code, value.Message })
                .OrderByDescending(group => group.Count())
                .Select(group => $"{group.Key.Code}:{group.Count()}:{group.First().Name}:{group.Key.Message}")
                .ToArray(),
            ProtectionGroups = analysis.Candidates
                .Where(candidate => candidate.Assessment == SquireAssessment.Protected)
                .SelectMany(candidate => candidate.Reasons.Select(reason => new { reason.Code, candidate.Definition.NormalizedRarity }))
                .GroupBy(value => new { value.Code, value.NormalizedRarity })
                .OrderByDescending(group => group.Count())
                .Select(group => $"{group.Key.Code}:{group.Key.NormalizedRarity}:{group.Count()}")
                .ToArray(),
            ExecutableCandidates = analysis.Candidates
                .Where(candidate => candidate.IsExecutable)
                .Select(candidate =>
                {
                    var revalidation = actionAdapter.Revalidate(candidate.Instance.Fingerprint, candidate.RecommendedDisposition);
                    return new AgentBridgeSquireCandidateTruth
                    {
                    ItemId = candidate.Definition.ItemId,
                    ItemName = candidate.Definition.Name,
                    Container = candidate.Instance.Fingerprint.Container,
                    SlotIndex = candidate.Instance.Fingerprint.SlotIndex,
                    EquipLevel = candidate.Definition.EquipLevel,
                    ItemLevel = candidate.Definition.ItemLevel,
                    RecommendedDisposition = candidate.RecommendedDisposition.ToString(),
                    ReasonCodes = candidate.Reasons.Select(reason => reason.Code).ToArray(),
                    JobComparisons = candidate.UseAnalysis?.Comparisons
                        .Select(comparison => $"{comparison.Job.Abbreviation}:{comparison.Job.Level}:{comparison.Status}:{comparison.Baseline?.Name ?? "none"}:{comparison.Baseline?.ItemLevel.ToString() ?? "none"}:witnesses={string.Join(",", comparison.WitnessRequirement?.ViableWitnesses.Select(witness => $"{witness.ItemName}@{witness.Fingerprint.Container}:{witness.Fingerprint.SlotIndex}:{(witness.IsGearsetReferenced ? "saved" : "loose")}{(witness.Fingerprint.IsHighQuality ? ":HQ" : string.Empty)}") ?? [])}")
                        .ToArray() ?? [],
                    RevalidationCode = revalidation.Code,
                    RevalidationSucceeded = revalidation.Success,
                    };
                })
                .ToArray(),
        };
    }

    private static void DrawSummary(SquireAnalysis value)
    {
        var snapshot = value.Snapshot;
        var scope = snapshot.Identity.Scope;
        ImGui.TextUnformatted(scope is null ? "No active character" : $"{scope.Name} @ world {scope.HomeWorldId}");
        ImGui.TextUnformatted($"Captured: {snapshot.Identity.CapturedAt.LocalDateTime:G}");
        ImGui.TextUnformatted($"Unlocked jobs: {snapshot.Jobs.Count(job => job.IsUnlocked == true)} | Valid gearsets: {snapshot.Gearsets.Count(set => set.IsValid)} | Items: {snapshot.Instances.Count}");
    }

    private static void DrawDiagnostics(SquireAnalysis value)
    {
        foreach (var diagnostic in value.Snapshot.Diagnostics.Components.Where(component => component.Status != Franthropy.Dalamud.Characters.SnapshotComponentStatus.Complete))
            ImGui.TextColored(MarketMafiosoUiTheme.Error, $"{diagnostic.Component}: {diagnostic.Status} - {diagnostic.Message}");
    }

    private void DrawTable(SquireAnalysis value)
    {
        var baseRows = value.Candidates
            .Where(candidate => showNonEquipment || candidate.Definition.IsEquipment)
            .Where(candidate => showProtected || candidate.Assessment != SquireAssessment.Protected)
            .Where(candidate => string.IsNullOrWhiteSpace(search)
                || candidate.Definition.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || candidate.Instance.Fingerprint.Container.Contains(search, StringComparison.OrdinalIgnoreCase)
                || candidate.Reasons.Any(reason => reason.Message.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var tableFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY |
                         ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable;
        var tableHeight = Math.Max(260f, ImGui.GetContentRegionAvail().Y * 0.62f);
        if (!ImGui.BeginTable("##SquireCandidatesV2", 7, tableFlags, new System.Numerics.Vector2(0, tableHeight)))
            return;
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort, 180);
        ImGui.TableSetupColumn("Location", ImGuiTableColumnFlags.WidthFixed, 135);
        ImGui.TableSetupColumn("Equip Lv", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortDescending, 65);
        ImGui.TableSetupColumn("Item Lv", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortDescending, 60);
        ImGui.TableSetupColumn("Assessment", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("Disposition", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("Reason", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();
        DrawColumnFilters();
        var filteredRows = ApplyColumnFilters(baseRows, columnFilters);
        var rows = SortCandidates(filteredRows, ImGui.TableGetSortSpecs());
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var candidate = rows[rowIndex];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var fingerprint = candidate.Instance.Fingerprint;
            var selected = tableSelection.Contains(fingerprint);
            var itemCursor = ImGui.GetCursorPos();
            var itemWidth = Math.Max(1f, ImGui.GetContentRegionAvail().X);
            var itemHeight = Math.Max(ImGui.GetTextLineHeightWithSpacing(), ImGui.CalcTextSize(candidate.Definition.Name, false, itemWidth).Y);
            ImGui.Selectable(
                $"##SquireRow{fingerprint.Container}{fingerprint.SlotIndex}",
                selected,
                ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap,
                new System.Numerics.Vector2(0, itemHeight));
            HandleRowInteraction(value, rows, rowIndex, candidate);
            if (candidate.IsExecutable)
            {
                var controlId = $"squire.select.{fingerprint.Container}.{fingerprint.SlotIndex}";
                RegisterLastControl(
                    controlId,
                    $"Select {candidate.Definition.Name}",
                    AgentBridgeUiControlKind.Toggle,
                    true,
                    selected,
                    candidate.RecommendedDisposition.ToString(),
                    () => SetSelection(value, candidate, !tableSelection.Contains(fingerprint)));
            }
            ImGui.SetCursorPos(itemCursor);
            ImGui.PushTextWrapPos(itemCursor.X + itemWidth);
            DrawItemLink(value, candidate);
            ImGui.PopTextWrapPos();
            Cell($"{candidate.Instance.Fingerprint.Container}:{candidate.Instance.Fingerprint.SlotIndex}");
            Cell(candidate.Definition.EquipLevel.ToString());
            Cell(candidate.Definition.ItemLevel.ToString());
            Cell(candidate.Assessment.ToString());
            Cell(candidate.RecommendedDisposition.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatReasonSummary(candidate));
            if (ImGui.IsItemHovered())
                DrawReasonTooltip(candidate);
        }
        if (selectionDragStart >= 0 && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            selectionDragStart = -1;
        ImGui.EndTable();
    }

    private void HandleRowInteraction(SquireAnalysis analysis, SquireCandidate[] rows, int rowIndex, SquireCandidate candidate)
    {
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            focusedItem = candidate.Instance.Fingerprint;
            if (selectionMode)
            {
                var io = ImGui.GetIO();
                if (io.KeyShift && selectionAnchor is { } anchor)
                {
                    var anchorIndex = Array.FindIndex(rows, row => row.Instance.Fingerprint == anchor);
                    if (anchorIndex >= 0)
                    {
                        if (!io.KeyCtrl)
                            ClearSelectionOnly();
                        var rangeFirst = Math.Min(anchorIndex, rowIndex);
                        var rangeLast = Math.Max(anchorIndex, rowIndex);
                        for (var index = rangeFirst; index <= rangeLast; index++)
                            SetSelection(analysis, rows[index], true);
                    }
                }
                else if (io.KeyCtrl)
                {
                    SetSelection(analysis, candidate, !tableSelection.Contains(candidate.Instance.Fingerprint));
                    selectionAnchor = candidate.Instance.Fingerprint;
                }
                else
                {
                    ClearSelectionOnly();
                    SetSelection(analysis, candidate, true);
                    selectionAnchor = candidate.Instance.Fingerprint;
                }
                selectionDragStart = rowIndex;
                selectionDragValue = true;
            }
        }
        if (!selectionMode || selectionDragStart < 0 || !ImGui.IsItemHovered() || !ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            return;
        var first = Math.Min(selectionDragStart, rowIndex);
        var last = Math.Max(selectionDragStart, rowIndex);
        for (var index = first; index <= last; index++)
            SetSelection(analysis, rows[index], selectionDragValue);
    }

    private void SetSelection(SquireAnalysis analysis, SquireCandidate candidate, bool selected)
    {
        var fingerprint = candidate.Instance.Fingerprint;
        if (selected)
            tableSelection.Add(fingerprint);
        else
            tableSelection.Remove(fingerprint);
        if (selected && candidate.IsExecutable && !review.Selections.ContainsKey(fingerprint))
            review.TrySelect(analysis, fingerprint, candidate.RecommendedDisposition);
        else if ((!selected || !candidate.IsExecutable) && review.Selections.ContainsKey(fingerprint))
            review.Remove(fingerprint);
    }

    private static void DrawItemLink(SquireAnalysis analysis, SquireCandidate candidate)
    {
        var start = ImGui.GetCursorScreenPos();
        ImGui.TextColored(MarketMafiosoUiTheme.Link, candidate.Definition.Name);
        var end = new System.Numerics.Vector2(start.X + ImGui.GetItemRectSize().X, ImGui.GetItemRectMax().Y);
        ImGui.GetWindowDrawList().AddLine(new(start.X, end.Y), end, ImGui.GetColorU32(MarketMafiosoUiTheme.Link));
        if (!ImGui.IsItemHovered())
            return;

        var definition = candidate.Definition;
        var fingerprint = candidate.Instance.Fingerprint;
        var eligibleJobs = analysis.Snapshot.Jobs
            .Where(job => job.IsUnlocked == true && definition.EligibleClassJobIds.Contains(job.ClassJobId))
            .Select(job => $"{job.Abbreviation} Lv. {job.Level}")
            .Distinct().Order().ToArray();
        var effectiveProfile = EquipmentInstanceStats.Resolve(candidate.Instance, definition);
        var stats = effectiveProfile?.Parameters
            .Where(stat => stat.Value != 0)
            .GroupBy(stat => stat.Semantic)
            .Select(group => $"{group.Key} +{group.Max(stat => stat.Value)}")
            .ToList() ?? [];
        if (effectiveProfile is { } profile)
        {
            if (profile.PhysicalDamage > 0) stats.Add($"Physical Damage {profile.PhysicalDamage}");
            if (profile.MagicalDamage > 0) stats.Add($"Magical Damage {profile.MagicalDamage}");
            if (profile.PhysicalDefense > 0) stats.Add($"Physical Defense {profile.PhysicalDefense}");
            if (profile.MagicalDefense > 0) stats.Add($"Magical Defense {profile.MagicalDefense}");
        }

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Math.Max(360f, ImGui.GetMainViewport().Size.X * 0.4f));
        ImGui.TextColored(MarketMafiosoUiTheme.Header, definition.Name);
        ImGui.TextUnformatted($"Item {definition.ItemId} | {definition.NormalizedRarity} | {definition.Slot} | Equip Lv. {definition.EquipLevel} | Item Lv. {definition.ItemLevel}");
        ImGui.TextUnformatted($"Location: {fingerprint.Container}:{fingerprint.SlotIndex}{(fingerprint.IsHighQuality ? " | HQ" : string.Empty)}");
        ImGui.Separator();
        ImGui.TextUnformatted($"Eligible obtained jobs: {(eligibleJobs.Length == 0 ? "none" : string.Join(", ", eligibleJobs))}");
        ImGui.TextWrapped($"Stats: {(stats.Count == 0 ? "none" : string.Join(", ", stats))}");
        if (candidate.UseAnalysis is { Comparisons.Count: > 0 } use)
        {
            ImGui.Separator();
            ImGui.TextColored(MarketMafiosoUiTheme.Header, "Saved-gearset comparisons");
            foreach (var comparison in use.Comparisons)
            {
                var sets = comparison.ContributingGearsets.Count == 0
                    ? "no contributing gearset"
                    : string.Join(", ", comparison.ContributingGearsets.Select(set => set.Name).Distinct());
                var baseline = comparison.Baseline is null
                    ? "no baseline"
                    : $"{comparison.Baseline.Name} (iLv {comparison.Baseline.ItemLevel})";
                ImGui.BulletText($"{comparison.Job.Abbreviation}: {comparison.Status}; {baseline}; from {sets}");
                if (comparison.WitnessRequirement is { } requirement)
                    foreach (var witness in requirement.ViableWitnesses)
                        ImGui.BulletText($"  {(witness.IsGearsetReferenced ? "Saved-gearset" : "Owned loose-item")} witness: {witness.ItemName} at {witness.Fingerprint.Container}:{witness.Fingerprint.SlotIndex}{(witness.Fingerprint.IsHighQuality ? " HQ" : string.Empty)}");
            }
        }
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private void ClearSelectionOnly()
    {
        tableSelection.Clear();
        review.Clear();
    }

    private void DrawColumnFilters()
    {
        ImGui.TableNextRow();
        for (var column = 0; column < columnFilters.Length; column++)
        {
            ImGui.TableSetColumnIndex(column);
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint($"##SquireColumnFilter{column}", "Filter...", ref columnFilters[column], 96);
        }
    }

    internal static SquireCandidate[] ApplyColumnFilters(IEnumerable<SquireCandidate> rows, IReadOnlyList<string> filters) =>
        rows.Where(candidate =>
            MatchesFilter(candidate.Definition.Name, filters[0]) &&
            MatchesFilter($"{candidate.Instance.Fingerprint.Container}:{candidate.Instance.Fingerprint.SlotIndex}", filters[1]) &&
            MatchesFilter(candidate.Definition.EquipLevel.ToString(), filters[2]) &&
            MatchesFilter(candidate.Definition.ItemLevel.ToString(), filters[3]) &&
            MatchesFilter(candidate.Assessment.ToString(), filters[4]) &&
            MatchesFilter(candidate.RecommendedDisposition.ToString(), filters[5]) &&
            MatchesFilter(FormatReasons(candidate), filters[6])).ToArray();

    private static bool MatchesFilter(string value, string filter) =>
        string.IsNullOrWhiteSpace(filter) || value.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);

    private void DrawFocusedItem(SquireAnalysis analysis)
    {
        if (focusedItem is not { } fingerprint)
            return;
        var candidate = analysis.Candidates.FirstOrDefault(value => value.Instance.Fingerprint == fingerprint);
        if (candidate is null)
            return;
        ImGui.Spacing();
        ImGui.TextColored(MarketMafiosoUiTheme.Header, candidate.Definition.Name);
        ImGui.SameLine();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"{fingerprint.Container}:{fingerprint.SlotIndex} | {candidate.Assessment} | {candidate.RecommendedDisposition}");
        foreach (var reason in candidate.Reasons)
        {
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextWrapped(reason.Message);
        }
        if (candidate.Definition.NormalizedRarity is EquipmentRarity.Rare or EquipmentRarity.Relic)
        {
            var itemId = candidate.Definition.ItemId;
            var contentId = analysis.Snapshot.Identity.Scope?.LocalContentId;
            var overridden = GetHighRarityOverrides(contentId).Contains(itemId);
            if (!overridden)
            {
                if (ImGui.Button($"Review high-rarity cleanup override##SquireRarity{itemId}"))
                    pendingHighRarityOverrideItemId = itemId;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Persistently removes only Squire's rarity protection for this character and item ID. Every other safety rule still applies.");
                if (pendingHighRarityOverrideItemId == itemId)
                {
                    ImGui.TextWrapped($"Confirm that {candidate.Definition.Name} ({candidate.Definition.NormalizedRarity}) may be evaluated as a cleanup candidate for this character. Other protections remain active.");
                    if (ImGui.Button($"Confirm cleanup override##SquireRarityConfirm{itemId}"))
                    {
                        SetHighRarityOverride(contentId, itemId, true);
                        pendingHighRarityOverrideItemId = null;
                        Refresh();
                    }
                    ImGui.SameLine();
                    if (ImGui.Button($"Cancel##SquireRarityCancel{itemId}"))
                        pendingHighRarityOverrideItemId = null;
                }
            }
            else if (ImGui.Button($"Restore high-rarity protection##SquireRarity{itemId}"))
            {
                SetHighRarityOverride(contentId, itemId, false);
                Refresh();
            }
        }
    }

    private IReadOnlySet<uint> GetHighRarityOverrides(ulong? contentId)
    {
        if (contentId is null || !config.Squire.HighRarityCleanupItemIdsByCharacter.TryGetValue(contentId.Value.ToString(), out var values))
            return new HashSet<uint>();
        return values.ToHashSet();
    }

    private SquireProtectionPolicy CreateProtectionPolicy(ulong? contentId) => new(
        config.Squire.ProtectPlayerSignedGear,
        config.Squire.ProtectFutureLevelingGearOptIn,
        GetHighRarityOverrides(contentId));

    private void SetHighRarityOverride(ulong? contentId, uint itemId, bool enabled)
    {
        if (contentId is null)
            return;
        var key = contentId.Value.ToString();
        if (!config.Squire.HighRarityCleanupItemIdsByCharacter.TryGetValue(key, out var values))
            config.Squire.HighRarityCleanupItemIdsByCharacter[key] = values = [];
        if (enabled && !values.Contains(itemId)) values.Add(itemId);
        if (!enabled) values.Remove(itemId);
        config.Save();
    }

    internal static SquireCandidate[] SortCandidates(SquireCandidate[] rows, ImGuiTableSortSpecsPtr sortSpecs)
    {
        if (sortSpecs.SpecsCount == 0)
            return rows;
        var spec = sortSpecs.Specs;
        return spec.ColumnIndex switch
        {
            0 => SortCandidatesBy(rows, candidate => candidate.Definition.Name, spec.SortDirection),
            1 => SortCandidatesBy(rows, candidate => $"{candidate.Instance.Fingerprint.Container}:{candidate.Instance.Fingerprint.SlotIndex:D3}", spec.SortDirection),
            2 => SortCandidatesBy(rows, candidate => candidate.Definition.EquipLevel, spec.SortDirection),
            3 => SortCandidatesBy(rows, candidate => candidate.Definition.ItemLevel, spec.SortDirection),
            4 => SortCandidatesBy(rows, candidate => candidate.Assessment, spec.SortDirection),
            5 => SortCandidatesBy(rows, candidate => candidate.RecommendedDisposition, spec.SortDirection),
            6 => SortCandidatesBy(rows, FormatReasons, spec.SortDirection),
            _ => rows,
        };
    }

    internal static string FormatReasons(SquireCandidate candidate) =>
        string.Join("\n", candidate.Reasons.Select(reason => $"• {reason.Message}"));

    internal static string FormatReasonSummary(SquireCandidate candidate) => candidate.Reasons.Count switch
    {
        0 => "No reason recorded",
        1 => candidate.Reasons[0].Message,
        _ => $"{candidate.Reasons[0].Message} (+{candidate.Reasons.Count - 1} more)",
    };

    private static void DrawReasonTooltip(SquireCandidate candidate)
    {
        var maximumWidth = Math.Max(1f, ImGui.GetMainViewport().Size.X * 0.5f);
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + maximumWidth);
        ImGui.TextWrapped(FormatReasons(candidate));
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private static SquireCandidate[] SortCandidatesBy<TKey>(SquireCandidate[] rows, Func<SquireCandidate, TKey> keySelector, ImGuiSortDirection direction)
    {
        var ordered = direction == ImGuiSortDirection.Descending
            ? rows.OrderByDescending(keySelector)
            : rows.OrderBy(keySelector);
        return ordered
            .ThenBy(candidate => candidate.Instance.Fingerprint.Container)
            .ThenBy(candidate => candidate.Instance.Fingerprint.SlotIndex)
            .ToArray();
    }

    private void DrawRunPanel(SquireAnalysis value)
    {
        ImGui.Separator();
        var selections = review.Selections;
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Reviewed batch");
        ImGui.TextUnformatted($"Selected: {selections.Count} | GC Delivery: {selections.Count(pair => pair.Value == SquireDisposition.ExpertDelivery)} | Desynthesize: {selections.Count(pair => pair.Value == SquireDisposition.Desynthesize)} | Vendor: {selections.Count(pair => pair.Value == SquireDisposition.VendorSell)} | Discard: {selections.Count(pair => pair.Value == SquireDisposition.Discard)}");
        if (!value.Snapshot.Diagnostics.IsComplete)
            ImGui.TextColored(MarketMafiosoUiTheme.Error, "Execution blocked: snapshot is incomplete.");
        var selectedDesynthesis = selections.Where(pair => pair.Value == SquireDisposition.Desynthesize).Select(pair => pair.Key).ToArray();
        var selectedExpertDelivery = selections.Where(pair => pair.Value == SquireDisposition.ExpertDelivery).Select(pair => pair.Key).ToArray();
        var running = activeRun is { IsCompleted: false };
        var canRunExpertDelivery = value.Snapshot.Diagnostics.IsComplete && selectedExpertDelivery.Length > 0 && selectedExpertDelivery.Length == selections.Count && !running;
        if (!canRunExpertDelivery)
            ImGui.BeginDisabled();
        ImGui.Checkbox("I confirm this reviewed Expert Delivery batch", ref runConfirmed);
        if (!canRunExpertDelivery)
            ImGui.EndDisabled();
        var expertDeliveryEnabled = canRunExpertDelivery && runConfirmed;
        if (!expertDeliveryEnabled)
            ImGui.BeginDisabled();
        if (ImGui.Button("Run selected Expert Delivery##Squire"))
            StartRun(value, SquireDisposition.ExpertDelivery, selectedExpertDelivery);
        RegisterLastControl(
            "squire.run.expert-delivery",
            "Run the explicitly confirmed Grand Company Expert Delivery batch",
            AgentBridgeUiControlKind.Button,
            expertDeliveryEnabled,
            false,
            selectedExpertDelivery.Length.ToString(),
            () => StartRun(value, SquireDisposition.ExpertDelivery, selectedExpertDelivery));
        if (!expertDeliveryEnabled)
            ImGui.EndDisabled();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Open the Grand Company Expert Delivery list before starting this batch.");

        var canRunDesynthesis = value.Snapshot.Diagnostics.IsComplete && selectedDesynthesis.Length > 0 && selectedDesynthesis.Length == selections.Count && !running;
        if (!canRunDesynthesis)
            ImGui.BeginDisabled();
        ImGui.Checkbox("I confirm this reviewed desynthesis batch", ref runConfirmed);
        RegisterLastControl(
            "squire.run.desynthesize.confirm",
            "Confirm the reviewed desynthesis batch",
            AgentBridgeUiControlKind.Toggle,
            canRunDesynthesis,
            runConfirmed,
            selectedDesynthesis.Length.ToString(),
            () => runConfirmed = !runConfirmed);
        if (!canRunDesynthesis)
            ImGui.EndDisabled();

        var runEnabled = canRunDesynthesis && runConfirmed;
        if (!runEnabled)
            ImGui.BeginDisabled();
        if (ImGui.Button("Run selected desynthesis##Squire"))
            StartRun(value, SquireDisposition.Desynthesize, selectedDesynthesis);
        RegisterLastControl(
            "squire.run.desynthesize",
            "Run the explicitly confirmed desynthesis batch",
            AgentBridgeUiControlKind.Button,
            runEnabled,
            false,
            selectedDesynthesis.Length.ToString(),
            () => StartRun(value, SquireDisposition.Desynthesize, selectedDesynthesis));
        if (!runEnabled)
            ImGui.EndDisabled();
        if (running)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel active Squire run##Squire"))
                runCancellation?.Cancel();
        }

        if (actionAdapter is DalamudSquireActionGameAdapter liveAdapter && selections.Count == 1)
        {
            var fingerprint = selections.Keys.Single();
            if (ImGui.Button("Probe normal item menu##Squire"))
                status = liveAdapter.OpenContextMenuProbe(fingerprint).Message;
            RegisterLastControl(
                "squire.probe.open-context-menu",
                "Open the selected item's normal context menu",
                AgentBridgeUiControlKind.Button,
                true,
                false,
                null,
                () => status = liveAdapter.OpenContextMenuProbe(fingerprint).Message);
            ImGui.SameLine();
            if (ImGui.Button("Read menu probe##Squire"))
                status = liveAdapter.DescribeContextMenuProbe();
            RegisterLastControl(
                "squire.probe.read-context-menu",
                "Read the open item context menu",
                AgentBridgeUiControlKind.Button,
                true,
                false,
                null,
                () => status = liveAdapter.DescribeContextMenuProbe());
        }
        if (actionAdapter is DalamudSquireActionGameAdapter cleanupAdapter)
        {
            if (selections.Count == 1)
                ImGui.SameLine();
            if (ImGui.Button("Close Squire item UI##Squire"))
                status = cleanupAdapter.CloseContextMenuProbe();
            RegisterLastControl(
                "squire.probe.close-context-menu",
                "Close visible Squire item UI",
                AgentBridgeUiControlKind.Button,
                true,
                false,
                null,
                () => status = cleanupAdapter.CloseContextMenuProbe());
        }
    }

    private void StartRun(SquireAnalysis value, SquireDisposition disposition, EquipmentInstanceFingerprint[] selected)
    {
        if (!runConfirmed || activeRun is { IsCompleted: false })
            return;
        try
        {
            var plan = new SquireActionPlanner().Create(value, disposition, selected, DateTimeOffset.UtcNow,
                CreateProtectionPolicy(value.Snapshot.Identity.Scope?.LocalContentId));
            runConfirmed = false;
            runCancellation = new CancellationTokenSource();
            activeRun = RunAsync(plan, runCancellation.Token);
            status = $"Started explicitly confirmed {disposition} run for {plan.Actions.Count} item(s).";
        }
        catch (Exception ex)
        {
            status = $"Run blocked: {ex.Message}";
        }
    }

    private async Task RunAsync(SquireActionPlan plan, CancellationToken cancellationToken)
    {
        var result = await new SquireRunner(actionAdapter).RunAsync(plan, explicitlyConfirmed: true, cancellationToken);
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "unknown";
        var auditPath = new SquireAuditLog(Path.Combine(diagnosticDirectory, "runs")).Write(plan, result, version);
        status = result.Success
            ? $"Run completed. Audit: {Path.GetFileName(auditPath)}"
            : $"Run stopped ({result.Code}). Audit: {Path.GetFileName(auditPath)}";
    }

    public void Dispose()
    {
        runCancellation?.Cancel();
        actionAdapter.ReleaseOwnedState();
        runCancellation?.Dispose();
    }

    private void Export()
    {
        try
        {
            Directory.CreateDirectory(diagnosticDirectory);
            var path = Path.Combine(diagnosticDirectory, $"squire-snapshot-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, JsonConvert.SerializeObject(analysis, Formatting.Indented));
            status = $"Exported {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            status = $"Export failed: {ex.Message}";
        }
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
        Action invoke) =>
        reviewRegistry.Register(id, label, kind, ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), enabled, selected, value, invoke);
}
