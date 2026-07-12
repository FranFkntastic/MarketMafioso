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
using MarketMafioso.Diagnostics;

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
    private readonly UiStateCaptureService uiStateCapture;
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
        string diagnosticDirectory,
        UiStateCaptureService uiStateCapture)
    {
        this.config = config;
        this.snapshotSource = snapshotSource;
        this.actionAdapter = actionAdapter;
        this.capabilitySource = capabilitySource;
        this.reviewRegistry = reviewRegistry;
        this.diagnosticDirectory = diagnosticDirectory;
        this.uiStateCapture = uiStateCapture;
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
        if (analysis is not null && ImGui.Button("Export evaluation snapshot##Squire"))
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
            .Where(candidate => showProtected || candidate.Assessment is not (SquireAssessment.Protected or SquireAssessment.EvaluationFailure))
            .Where(candidate => string.IsNullOrWhiteSpace(search)
                || candidate.Definition.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || candidate.Instance.Fingerprint.Container.Contains(search, StringComparison.OrdinalIgnoreCase)
                || FormatLocation(candidate.Instance.Fingerprint).Contains(search, StringComparison.OrdinalIgnoreCase)
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
                    () =>
                    {
                        focusedItem = fingerprint;
                        SetSelection(value, candidate, !tableSelection.Contains(fingerprint));
                    });
            }
            ImGui.SetCursorPos(itemCursor);
            ImGui.PushTextWrapPos(itemCursor.X + itemWidth);
            DrawItemLink(value, candidate);
            ImGui.PopTextWrapPos();
            Cell(FormatLocation(candidate.Instance.Fingerprint));
            Cell(candidate.Definition.EquipLevel.ToString());
            Cell(candidate.Definition.ItemLevel.ToString());
            Cell(FormatAssessment(candidate.Assessment));
            Cell(FormatDisposition(candidate.RecommendedDisposition));
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
        ImGui.TextUnformatted($"Location: {FormatLocation(fingerprint)}{(fingerprint.IsHighQuality ? " | HQ" : string.Empty)}");
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
                        ImGui.BulletText($"  {(witness.IsGearsetReferenced ? "Saved-gearset" : "Owned loose-item")} witness: {witness.ItemName} at {FormatLocation(witness.Fingerprint)}{(witness.Fingerprint.IsHighQuality ? " HQ" : string.Empty)}");
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
            MatchesFilter(FormatLocation(candidate.Instance.Fingerprint), filters[1]) &&
            MatchesFilter(candidate.Definition.EquipLevel.ToString(), filters[2]) &&
            MatchesFilter(candidate.Definition.ItemLevel.ToString(), filters[3]) &&
            MatchesFilter(FormatAssessment(candidate.Assessment), filters[4]) &&
            MatchesFilter(FormatDisposition(candidate.RecommendedDisposition), filters[5]) &&
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
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"{FormatLocation(fingerprint)} | {FormatAssessment(candidate.Assessment)} | {FormatDisposition(candidate.RecommendedDisposition)}");
        DrawItemEvidence(candidate);
        DrawRuleEvidence(analysis, candidate);
        DrawJobComparisonEvidence(candidate);
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

    private static void DrawItemEvidence(SquireCandidate candidate)
    {
        var fingerprint = candidate.Instance.Fingerprint;
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Item and cleanup route");
        if (!ImGui.BeginTable("##SquireItemEvidence", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("Location");
        ImGui.TableSetupColumn("Item facts");
        ImGui.TableSetupColumn("Assessment");
        ImGui.TableSetupColumn("Authorized route");
        ImGui.TableHeadersRow();
        ImGui.TableNextRow();
        EvidenceCell(FormatLocation(fingerprint));
        EvidenceCell($"{candidate.Definition.NormalizedRarity}, {(fingerprint.IsHighQuality ? "HQ" : "NQ")}, equip {candidate.Definition.EquipLevel}, iLv {candidate.Definition.ItemLevel}, materia {fingerprint.MateriaIds.Count}");
        EvidenceCell(FormatAssessment(candidate.Assessment));
        EvidenceCell(candidate.SupportedDispositions.Count == 0
            ? "Keep"
            : $"{FormatDisposition(candidate.RecommendedDisposition)} (supported: {string.Join(", ", candidate.SupportedDispositions.Order().Select(FormatDisposition))})");
        ImGui.EndTable();
    }

    private static void DrawRuleEvidence(SquireAnalysis analysis, SquireCandidate candidate)
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Evaluation rules");
        if (!ImGui.BeginTable("##SquireRuleEvidence", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("Rule", ImGuiTableColumnFlags.WidthFixed, 190);
        ImGui.TableSetupColumn("Effect", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("Observed evidence");
        ImGui.TableHeadersRow();
        foreach (var reason in candidate.Reasons)
        {
            ImGui.TableNextRow();
            EvidenceCell(ReasonLabel(reason.Code));
            EvidenceCell(DescribeReasonEffect(reason));
            EvidenceCell(DescribeReasonEvidence(analysis, candidate, reason));
        }
        ImGui.EndTable();
    }

    private static void DrawJobComparisonEvidence(SquireCandidate candidate)
    {
        if (candidate.UseAnalysis is not { Comparisons.Count: > 0 } use)
            return;
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Trusted baseline proof");
        if (!ImGui.BeginTable("##SquireJobEvidence", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("Trusted baseline");
        ImGui.TableSetupColumn("Evidence source");
        ImGui.TableSetupColumn("Covers jobs");
        ImGui.TableSetupColumn("Result", ImGuiTableColumnFlags.WidthFixed, 135);
        ImGui.TableHeadersRow();
        var groups = use.Comparisons.GroupBy(comparison => new
        {
            BaselineItemId = comparison.Baseline?.ItemId ?? 0,
            BaselineName = comparison.Baseline?.Name ?? "No trusted baseline",
            BaselineLevel = comparison.Baseline?.ItemLevel,
            comparison.Status,
            Source = DescribeComparisonSource(comparison),
            comparison.Diagnostic,
        });
        foreach (var group in groups)
        {
            ImGui.TableNextRow();
            EvidenceCell(group.Key.BaselineLevel is { } itemLevel
                ? $"{group.Key.BaselineName} (iLv {itemLevel})"
                : group.Key.BaselineName);
            EvidenceCell(group.Key.Source);
            EvidenceCell(string.Join(", ", group.Select(value => $"{value.Job.Abbreviation} {value.Job.Level}").Distinct().Order()));
            EvidenceCell(FormatComparisonStatus(group.Key.Status));
        }
        ImGui.EndTable();
    }

    private static string DescribeComparisonSource(EquipmentJobComparison comparison)
    {
        var witnesses = comparison.WitnessRequirement?.ViableWitnesses ?? [];
        if (witnesses.Count > 0)
            return string.Join("; ", witnesses.Select(value => $"{FormatLocation(value.Fingerprint)}{(value.Fingerprint.IsHighQuality ? " HQ" : string.Empty)}").Distinct().Order());
        if (comparison.ContributingGearsets.Count > 0)
            return $"Saved gearset: {string.Join(", ", comparison.ContributingGearsets.Select(value => value.Name).Distinct().Order())}";
        return comparison.Diagnostic ?? "No trusted source";
    }

    private static string FormatComparisonStatus(EquipmentUseStatus status) => status switch
    {
        EquipmentUseStatus.Obsolete => "Strictly better",
        EquipmentUseStatus.FutureUse => "Future-use check",
        EquipmentUseStatus.BaselineNotBetter => "Does not dominate",
        EquipmentUseStatus.NoObtainedEligibleJob => "No obtained job",
        EquipmentUseStatus.LikelyCosmetic => "Likely cosmetic",
        EquipmentUseStatus.EvaluationFailure => "Evaluation failed",
        _ => status.ToString(),
    };

    private static string DescribeReasonEffect(SquireReason reason) => reason.Code switch
    {
        "MateriaRetrievalRequired" => "Pre-cleanup note",
        "DesynthesisNotUnlocked" => "Limits route",
        "HighRarityCleanupOverride" => "Policy note",
        _ => reason.Severity switch
        {
            SquireReasonSeverity.Blocking => "Protects item",
            SquireReasonSeverity.Warning => "Caution",
            _ => "Supports verdict",
        },
    };

    private static string DescribeReasonEvidence(SquireAnalysis analysis, SquireCandidate candidate, SquireReason reason)
    {
        var fingerprint = candidate.Instance.Fingerprint;
        var comparisons = candidate.UseAnalysis?.Comparisons ?? [];
        return reason.Code switch
        {
            "StrictlyWorseForAllUnlockedJobs" => $"{comparisons.Count} relevant obtained job(s) checked; every comparison has a strictly better owned or saved baseline. See the proof table below.",
            "BaselineNotStrictlyBetter" => $"{comparisons.Count(value => value.Status == EquipmentUseStatus.BaselineNotBetter)} job comparison(s) found no baseline that dominates this item. See the proof table below.",
            "FutureUnlockedJobUse" => string.Join(", ", comparisons.Where(value => value.Status == EquipmentUseStatus.FutureUse).Select(value => $"{value.Job.Abbreviation} {value.Job.Level} < equip {candidate.Definition.EquipLevel}")),
            "FutureLevelingUseNotProtected" => $"Future-use protection is disabled; {comparisons.Count(value => value.Status == EquipmentUseStatus.FutureUse)} lower-level obtained job comparison(s) do not block cleanup.",
            "NoObtainedEligibleJob" => DescribeEligibleJobs(analysis, candidate),
            "MateriaRetrievalRequired" => $"Exact slot currently contains {fingerprint.MateriaIds.Count} materia. Squire will retrieve and revalidate each one before {FormatDisposition(candidate.RecommendedDisposition)}.",
            "CurrentlyEquipped" => $"The exact {FormatLocation(fingerprint)} instance is equipped in the live snapshot.",
            "ReferencedByGearset" => DescribeGearsetReferences(analysis, candidate),
            "HighRarityEquipment" => $"The item is {candidate.Definition.NormalizedRarity}; no character-scoped cleanup override exists for item {candidate.Definition.ItemId}.",
            "HighRarityCleanupOverride" => $"A character-scoped override exists for item {candidate.Definition.ItemId}; all non-rarity protections still apply.",
            "DesynthesisNotUnlocked" => $"Desynthesis is absent from the supported routes; current authorized routes: {string.Join(", ", candidate.SupportedDispositions.Order().Select(FormatDisposition))}.",
            "StatlessAllClassesEquipment" => "The resolved item profile contains no functional damage, defense, or class-relevant attributes, so it is treated as likely cosmetic.",
            "NoSupportedDisposition" => "The eligibility evaluator produced no authorized cleanup route; execution remains disabled.",
            _ when candidate.UseAnalysis?.Diagnostic is { Length: > 0 } diagnostic => diagnostic,
            _ => reason.Message,
        };
    }

    private static string DescribeEligibleJobs(SquireAnalysis analysis, SquireCandidate candidate)
    {
        var eligible = analysis.Snapshot.Jobs
            .Where(job => candidate.Definition.EligibleClassJobIds.Contains(job.ClassJobId))
            .Select(job => $"{job.Abbreviation}: {(job.IsUnlocked == true ? $"obtained, level {job.Level}" : "unobtained")}")
            .Distinct().Order().ToArray();
        return eligible.Length == 0
            ? "The item definition exposes no class/job-specific consumer; its all-classes stat evaluation supplies the verdict."
            : $"Eligible jobs observed: {string.Join("; ", eligible)}. None of the eligible jobs are obtained by this character.";
    }

    private static string DescribeGearsetReferences(SquireAnalysis analysis, SquireCandidate candidate)
    {
        var sets = analysis.Snapshot.Gearsets
            .Where(set => set.IsValid && set.Items.Any(item => item.ItemId == candidate.Definition.ItemId))
            .Select(set => set.Name).Distinct().Order().ToArray();
        return sets.Length == 0
            ? $"Item {candidate.Definition.ItemId} was marked gearset-referenced, but no named valid set was available in this presentation snapshot."
            : $"Referenced by valid gearset(s): {string.Join(", ", sets)}.";
    }

    private static void EvidenceCell(string value)
    {
        ImGui.TableNextColumn();
        ImGui.TextWrapped(string.IsNullOrWhiteSpace(value) ? "—" : value);
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
            1 => SortCandidatesBy(rows, candidate => $"{FormatContainer(candidate.Instance.Fingerprint.Container)}:{candidate.Instance.Fingerprint.SlotIndex:D3}", spec.SortDirection),
            2 => SortCandidatesBy(rows, candidate => candidate.Definition.EquipLevel, spec.SortDirection),
            3 => SortCandidatesBy(rows, candidate => candidate.Definition.ItemLevel, spec.SortDirection),
            4 => SortCandidatesBy(rows, candidate => FormatAssessment(candidate.Assessment), spec.SortDirection),
            5 => SortCandidatesBy(rows, candidate => FormatDisposition(candidate.RecommendedDisposition), spec.SortDirection),
            6 => SortCandidatesBy(rows, FormatReasons, spec.SortDirection),
            _ => rows,
        };
    }

    internal static string FormatReasons(SquireCandidate candidate) =>
        string.Join("\n", candidate.Reasons.Select(reason => $"• {reason.Message}"));

    internal static string FormatLocation(EquipmentInstanceFingerprint fingerprint) =>
        $"{FormatContainer(fingerprint.Container)}, Slot {fingerprint.SlotIndex}";

    internal static string FormatContainer(string container) => container switch
    {
        "Inventory1" => "Inventory Bag 1",
        "Inventory2" => "Inventory Bag 2",
        "Inventory3" => "Inventory Bag 3",
        "Inventory4" => "Inventory Bag 4",
        "ArmoryMainHand" => "Armory Chest: Main Hand",
        "ArmoryOffHand" => "Armory Chest: Off Hand",
        "ArmoryHead" => "Armory Chest: Head",
        "ArmoryBody" => "Armory Chest: Body",
        "ArmoryHands" => "Armory Chest: Hands",
        "ArmoryLegs" => "Armory Chest: Legs",
        "ArmoryFeet" => "Armory Chest: Feet",
        "ArmoryEar" => "Armory Chest: Earrings",
        "ArmoryNeck" => "Armory Chest: Necklaces",
        "ArmoryWrist" => "Armory Chest: Wrists",
        "ArmoryRings" => "Armory Chest: Rings",
        "ArmorySoulCrystal" => "Armory Chest: Soul Crystals",
        _ => SplitReasonCode(container),
    };

    internal static string FormatDisposition(SquireDisposition disposition) => disposition switch
    {
        SquireDisposition.ExpertDelivery => "Expert Delivery",
        SquireDisposition.VendorSell => "Vendor Sale",
        SquireDisposition.Desynthesize => "Desynthesize",
        SquireDisposition.Discard => "Discard",
        SquireDisposition.Keep => "Keep",
        SquireDisposition.Unsupported => "No supported route",
        _ => disposition.ToString(),
    };

    internal static string FormatAssessment(SquireAssessment assessment) => assessment switch
    {
        SquireAssessment.EvaluationFailure => "Evaluation Failure",
        SquireAssessment.Protected => "Protected",
        SquireAssessment.Candidate => "Candidate",
        SquireAssessment.Unsupported => "Unsupported",
        _ => assessment.ToString(),
    };

    internal static string FormatReasonSummary(SquireCandidate candidate) => candidate.Reasons.Count switch
    {
        0 => "No evaluation result",
        1 => ReasonLabel(candidate.Reasons[0].Code),
        _ => $"{ReasonLabel(candidate.Reasons[0].Code)} (+{candidate.Reasons.Count - 1} rule{(candidate.Reasons.Count == 2 ? string.Empty : "s")})",
    };

    internal static string ReasonLabel(string code) => code switch
    {
        "StrictlyWorseForAllUnlockedJobs" => "Better baseline for every relevant job",
        "BaselineNotStrictlyBetter" => "No strictly better baseline",
        "FutureUnlockedJobUse" => "Potential future use",
        "FutureLevelingUseNotProtected" => "Future-use protection disabled",
        "NoObtainedEligibleJob" => "No obtained eligible job",
        "MateriaRetrievalRequired" => "Materia retrieval required",
        "CurrentlyEquipped" => "Currently equipped",
        "ReferencedByGearset" => "Referenced by a gearset",
        "NotEquipment" => "Not equipment",
        "SoulCrystal" => "Soul crystal",
        "ProtectedItemFamily" => "Protected item family",
        "UnknownItemRarity" => "Unknown rarity",
        "HighRarityEquipment" => "High-rarity protection",
        "HighRarityCleanupOverride" => "High-rarity override",
        "ExpertDeliveryEligibilityUnknown" => "Expert Delivery eligibility unknown",
        "PlayerSignature" => "Player signature protection",
        "ArmoireEligibilityUnknown" => "Armoire eligibility unknown",
        "ArmoireEligible" => "Armoire eligible",
        "RecoverabilityUnknown" => "Recoverability unknown",
        "StatlessAllClassesEquipment" => "Likely cosmetic",
        "DesynthesisNotUnlocked" => "Desynthesis unavailable",
        "NoSupportedDisposition" => "No authorized cleanup route",
        "PartialSnapshot" => "Incomplete snapshot",
        _ => SplitReasonCode(code),
    };

    private static string SplitReasonCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return "Unclassified rule";
        var result = new System.Text.StringBuilder(code.Length + 8);
        for (var index = 0; index < code.Length; index++)
        {
            var character = code[index];
            if (index > 0 && char.IsUpper(character) && !char.IsUpper(code[index - 1]))
                result.Append(' ');
            result.Append(character);
        }
        return result.ToString();
    }

    private static void DrawReasonTooltip(SquireCandidate candidate)
    {
        var maximumWidth = Math.Max(1f, ImGui.GetMainViewport().Size.X * 0.5f);
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + maximumWidth);
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Rule outcomes");
        foreach (var reason in candidate.Reasons)
        {
            ImGui.BulletText($"{ReasonLabel(reason.Code)} — {reason.Severity}");
        }
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Select the row for the supporting evidence.");
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
        var running = activeRun is { IsCompleted: false };
        var supportedBatch = selections.Values.All(disposition => disposition is SquireDisposition.ExpertDelivery or SquireDisposition.Desynthesize);
        var canRun = value.Snapshot.Diagnostics.IsComplete && selections.Count > 0 && supportedBatch && !running;
        if (!canRun)
            ImGui.BeginDisabled();
        ImGui.Checkbox("I confirm this reviewed cleanup batch", ref runConfirmed);
        RegisterLastControl(
            "squire.run.confirm",
            "Confirm the reviewed cleanup batch",
            AgentBridgeUiControlKind.Toggle,
            canRun,
            runConfirmed,
            selections.Count.ToString(),
            () => runConfirmed = !runConfirmed);
        if (!canRun)
            ImGui.EndDisabled();

        var runEnabled = canRun && runConfirmed;
        if (!runEnabled)
            ImGui.BeginDisabled();
        if (ImGui.Button("Run cleanup with diagnostics##Squire"))
            StartDiagnosticRun(value);
        RegisterLastControl(
            "squire.run.diagnostic",
            "Run the explicitly confirmed cleanup batch with catchall UI-state recording enabled",
            AgentBridgeUiControlKind.Button,
            runEnabled,
            false,
            selections.Count.ToString(),
            () => StartDiagnosticRun(value));
        if (!runEnabled)
            ImGui.EndDisabled();
        ImGui.SameLine();
        if (!runEnabled)
            ImGui.BeginDisabled();
        if (ImGui.Button("Run selected cleanup##Squire"))
            StartRun(value);
        RegisterLastControl(
            "squire.run.cleanup",
            "Run the explicitly confirmed cleanup batch using each item's disposition",
            AgentBridgeUiControlKind.Button,
            runEnabled,
            false,
            selections.Count.ToString(),
            () => StartRun(value));
        if (!runEnabled)
            ImGui.EndDisabled();
        if (!supportedBatch && selections.Count > 0)
            ImGui.TextColored(MarketMafiosoUiTheme.Error, "Execution is not implemented for one or more selected dispositions.");
        if (selections.Values.Contains(SquireDisposition.ExpertDelivery))
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Expert deliveries travel to your Grand Company through Lifestream, then open the delivery desk automatically.");
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

    private void StartRun(SquireAnalysis value)
    {
        if (!runConfirmed || activeRun is { IsCompleted: false })
            return;
        try
        {
            var plan = new SquireActionPlanner().Create(value, review.Selections, DateTimeOffset.UtcNow,
                CreateProtectionPolicy(value.Snapshot.Identity.Scope?.LocalContentId));
            runConfirmed = false;
            runCancellation = new CancellationTokenSource();
            activeRun = RunAsync(plan, runCancellation.Token);
            status = $"Started explicitly confirmed cleanup run for {plan.Actions.Count} item(s).";
        }
        catch (Exception ex)
        {
            status = $"Run blocked: {ex.Message}";
        }
    }

    private void StartDiagnosticRun(SquireAnalysis value)
    {
        if (!runConfirmed || activeRun is { IsCompleted: false })
            return;
        if (uiStateCapture.IsRecording)
        {
            status = "Diagnostic run blocked: the catchall UI-state recorder is already active.";
            return;
        }
        try
        {
            var plan = new SquireActionPlanner().Create(value, review.Selections, DateTimeOffset.UtcNow,
                CreateProtectionPolicy(value.Snapshot.Identity.Scope?.LocalContentId));
            runConfirmed = false;
            runCancellation = new CancellationTokenSource();
            uiStateCapture.Start("squire-cleanup-diagnostic");
            uiStateCapture.Mark("squire-diagnostic-start", new Dictionary<string, string?>
            {
                ["actionCount"] = plan.Actions.Count.ToString(),
                ["snapshotGenerationId"] = plan.SnapshotGenerationId.ToString(),
            });
            activeRun = DiagnosticRunAsync(plan, runCancellation.Token);
            status = $"Started destructive cleanup run with diagnostics for {plan.Actions.Count} item(s).";
        }
        catch (Exception ex)
        {
            status = $"Diagnostic run blocked: {ex.Message}";
        }
    }

    private async Task DiagnosticRunAsync(SquireActionPlan plan, CancellationToken cancellationToken)
    {
        SquireRunResult result;
        try
        {
            result = await new SquireRunner(actionAdapter, runEvent =>
            {
                uiStateCapture.Mark($"squire-{runEvent.Kind}", new Dictionary<string, string?>
                {
                    ["code"] = runEvent.Code,
                    ["message"] = runEvent.Message,
                    ["container"] = runEvent.Item?.Container,
                    ["slotIndex"] = runEvent.Item?.SlotIndex.ToString(),
                    ["itemId"] = runEvent.Item?.ItemId.ToString(),
                });
                if (runEvent.Kind is "DispositionGroupStart" or "DiagnosticActionStart")
                    status = runEvent.Message;
            }).RunDiagnosticAsync(plan, explicitlyConfirmed: true, cancellationToken);
        }
        finally
        {
            uiStateCapture.Stop();
        }
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "unknown";
        var auditPath = new SquireAuditLog(Path.Combine(diagnosticDirectory, "runs")).Write(plan, result, version);
        var captureName = Path.GetFileName(uiStateCapture.LastCapturePath);
        status = result.Success
            ? $"Diagnostic cleanup completed. Audit: {Path.GetFileName(auditPath)} | UI capture: {captureName}"
            : $"Diagnostic cleanup stopped ({result.Code}). Audit: {Path.GetFileName(auditPath)} | UI capture: {captureName}";
    }

    private async Task RunAsync(SquireActionPlan plan, CancellationToken cancellationToken)
    {
        var result = await new SquireRunner(actionAdapter, runEvent =>
        {
            if (runEvent.Kind is "DispositionGroupStart" or "ActionStart")
                status = runEvent.Message;
        }).RunAsync(plan, explicitlyConfirmed: true, cancellationToken);
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
