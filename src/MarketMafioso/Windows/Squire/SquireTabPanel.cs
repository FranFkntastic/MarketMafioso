using System;
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
    private bool reviewMode;
    private int selectionDragStart = -1;
    private bool selectionDragValue;
    private EquipmentInstanceFingerprint? focusedItem;
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
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Squire V1 - read-only review");
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
        ImGui.Checkbox("Review mode", ref reviewMode);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Click or drag rows to add or remove cleanup actions. Outside review mode, a row opens its decision details.");
        if (reviewMode && review.Selections.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear reviewed"))
                review.Clear();
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
            analysis = evaluator.Evaluate(
                snapshotSource.Capture(),
                capabilitySource.Capture(),
                new SquireProtectionPolicy(
                    config.Squire.ProtectPlayerSignedGear,
                    config.Squire.ProtectFutureLevelingGearOptIn));
            review.Adopt(analysis);
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
                NeedsReviewCount = 0,
                UnsupportedCount = 0,
                BlockingDiagnostics = [],
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
            NeedsReviewCount = analysis.Candidates.Count(candidate => candidate.Assessment == SquireAssessment.NeedsReview),
            UnsupportedCount = analysis.Candidates.Count(candidate => candidate.Assessment == SquireAssessment.Unsupported),
            BlockingDiagnostics = snapshot.Diagnostics.Blocking.Select(value => $"{value.Component}:{value.Status}:{value.Message}").ToArray(),
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
                        .Select(comparison => $"{comparison.Job.Abbreviation}:{comparison.Job.Level}:{comparison.Status}:{comparison.Baseline?.Name ?? "none"}:{comparison.Baseline?.ItemLevel.ToString() ?? "none"}")
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
        var filteredRows = value.Candidates
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
        if (!ImGui.BeginTable("##SquireCandidates", 7, tableFlags, new System.Numerics.Vector2(0, tableHeight)))
            return;
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.DefaultSort, 150);
        ImGui.TableSetupColumn("Location");
        ImGui.TableSetupColumn("Equip Lv", ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn("Item Lv", ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn("Assessment");
        ImGui.TableSetupColumn("Disposition");
        ImGui.TableSetupColumn("Reason");
        ImGui.TableHeadersRow();
        var rows = SortCandidates(filteredRows, ImGui.TableGetSortSpecs());
        for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            var candidate = rows[rowIndex];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var fingerprint = candidate.Instance.Fingerprint;
            var selected = review.Selections.ContainsKey(fingerprint);
            var itemCursor = ImGui.GetCursorPos();
            var itemWidth = Math.Max(1f, ImGui.GetContentRegionAvail().X);
            var itemHeight = Math.Max(ImGui.GetTextLineHeightWithSpacing(), ImGui.CalcTextSize(candidate.Definition.Name, false, itemWidth).Y);
            if (!candidate.IsExecutable)
                ImGui.BeginDisabled();
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
                        if (review.Selections.ContainsKey(fingerprint))
                            review.Remove(fingerprint);
                        else
                            review.TrySelect(value, fingerprint, candidate.RecommendedDisposition);
                    });
            }
            if (!candidate.IsExecutable)
                ImGui.EndDisabled();
            ImGui.SetCursorPos(itemCursor);
            ImGui.PushTextWrapPos(itemCursor.X + itemWidth);
            ImGui.TextWrapped(candidate.Definition.Name);
            ImGui.PopTextWrapPos();
            Cell($"{candidate.Instance.Fingerprint.Container}:{candidate.Instance.Fingerprint.SlotIndex}");
            Cell(candidate.Definition.EquipLevel.ToString());
            Cell(candidate.Definition.ItemLevel.ToString());
            Cell(candidate.Assessment.ToString());
            Cell(candidate.RecommendedDisposition.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatReasonSummary(candidate));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(FormatReasons(candidate));
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
            if (reviewMode && candidate.IsExecutable)
            {
                selectionDragStart = rowIndex;
                selectionDragValue = !review.Selections.ContainsKey(candidate.Instance.Fingerprint);
                SetSelection(analysis, candidate, selectionDragValue);
            }
        }
        if (!reviewMode || selectionDragStart < 0 || !ImGui.IsItemHovered() || !ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            return;
        var first = Math.Min(selectionDragStart, rowIndex);
        var last = Math.Max(selectionDragStart, rowIndex);
        for (var index = first; index <= last; index++)
            if (rows[index].IsExecutable)
                SetSelection(analysis, rows[index], selectionDragValue);
    }

    private void SetSelection(SquireAnalysis analysis, SquireCandidate candidate, bool selected)
    {
        var fingerprint = candidate.Instance.Fingerprint;
        if (selected && !review.Selections.ContainsKey(fingerprint))
            review.TrySelect(analysis, fingerprint, candidate.RecommendedDisposition);
        else if (!selected && review.Selections.ContainsKey(fingerprint))
            review.Remove(fingerprint);
    }

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
    }

    private void ToggleSelection(SquireAnalysis analysis, SquireCandidate candidate)
    {
        var fingerprint = candidate.Instance.Fingerprint;
        if (review.Selections.ContainsKey(fingerprint))
            review.Remove(fingerprint);
        else
            review.TrySelect(analysis, fingerprint, candidate.RecommendedDisposition);
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
        ImGui.TextUnformatted($"Selected: {selections.Count} | Desynthesize: {selections.Count(pair => pair.Value == SquireDisposition.Desynthesize)} | Vendor: {selections.Count(pair => pair.Value == SquireDisposition.VendorSell)} | Discard: {selections.Count(pair => pair.Value == SquireDisposition.Discard)}");
        if (!value.Snapshot.Diagnostics.IsComplete)
            ImGui.TextColored(MarketMafiosoUiTheme.Error, "Execution blocked: snapshot is incomplete.");
        var selectedDesynthesis = selections.Where(pair => pair.Value == SquireDisposition.Desynthesize).Select(pair => pair.Key).ToArray();
        var running = activeRun is { IsCompleted: false };
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
            var plan = new SquireActionPlanner().Create(value, disposition, selected, DateTimeOffset.UtcNow);
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
