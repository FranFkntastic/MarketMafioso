using System;
using System.IO;
using System.Linq;
using Dalamud.Bindings.ImGui;
using MarketMafioso.Squire;
using MarketMafioso.Squire.Observation;
using MarketMafioso.AgentBridge;
using MarketMafioso.Windows.Main;
using Newtonsoft.Json;

namespace MarketMafioso.Windows.Squire;

internal sealed class SquireTabPanel
{
    private readonly ICharacterEquipmentSnapshotSource snapshotSource;
    private readonly ISquireActionGameAdapter actionAdapter;
    private readonly Configuration config;
    private readonly SquireCandidateEvaluator evaluator = new();
    private readonly SquireReviewState review = new();
    private readonly string diagnosticDirectory;
    private SquireAnalysis? analysis;
    private string search = string.Empty;
    private bool showProtected;
    private string status = "Refresh to capture a read-only equipment snapshot.";

    public SquireTabPanel(
        Configuration config,
        ICharacterEquipmentSnapshotSource snapshotSource,
        ISquireActionGameAdapter actionAdapter,
        string diagnosticDirectory)
    {
        this.config = config;
        this.snapshotSource = snapshotSource;
        this.actionAdapter = actionAdapter;
        this.diagnosticDirectory = diagnosticDirectory;
        search = config.Squire.Search;
        showProtected = config.Squire.ShowProtected;
    }

    public void Draw()
    {
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Squire V1 - read-only review");
        ImGui.TextWrapped("Squire identifies obsolete leveling gear. Refresh only analyzes; it never disposes of an item.");
        if (ImGui.Button("Refresh##Squire"))
            Refresh();
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
        DrawTable(analysis);
        DrawRunPanel(analysis);
    }

    private void Refresh()
    {
        try
        {
            analysis = evaluator.Evaluate(snapshotSource.Capture());
            review.Adopt(analysis);
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
        var rows = value.Candidates
            .Where(candidate => showProtected || candidate.Assessment != SquireAssessment.Protected)
            .Where(candidate => string.IsNullOrWhiteSpace(search)
                || candidate.Definition.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || candidate.Instance.Fingerprint.Container.Contains(search, StringComparison.OrdinalIgnoreCase)
                || candidate.Reasons.Any(reason => reason.Message.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (!ImGui.BeginTable("##SquireCandidates", 8, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable))
            return;
        ImGui.TableSetupColumn("Select", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn("Item");
        ImGui.TableSetupColumn("Location");
        ImGui.TableSetupColumn("Equip Lv");
        ImGui.TableSetupColumn("Item Lv");
        ImGui.TableSetupColumn("Assessment");
        ImGui.TableSetupColumn("Disposition");
        ImGui.TableSetupColumn("Reason");
        ImGui.TableHeadersRow();
        foreach (var candidate in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var fingerprint = candidate.Instance.Fingerprint;
            var selected = review.Selections.ContainsKey(fingerprint);
            if (!candidate.IsExecutable)
                ImGui.BeginDisabled();
            if (ImGui.Checkbox($"##SquireSelect{fingerprint.Container}{fingerprint.SlotIndex}", ref selected))
            {
                if (selected)
                    review.TrySelect(value, fingerprint, candidate.RecommendedDisposition);
                else
                    review.Remove(fingerprint);
            }
            if (!candidate.IsExecutable)
                ImGui.EndDisabled();
            Cell(candidate.Definition.Name);
            Cell($"{candidate.Instance.Fingerprint.Container}:{candidate.Instance.Fingerprint.SlotIndex}");
            Cell(candidate.Definition.EquipLevel.ToString());
            Cell(candidate.Definition.ItemLevel.ToString());
            Cell(candidate.Assessment.ToString());
            Cell(candidate.RecommendedDisposition.ToString());
            Cell(candidate.Reasons.FirstOrDefault()?.Message ?? string.Empty);
        }
        ImGui.EndTable();
    }

    private void DrawRunPanel(SquireAnalysis value)
    {
        ImGui.Separator();
        var selections = review.Selections;
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Reviewed batch");
        ImGui.TextUnformatted($"Selected: {selections.Count} | Desynthesize: {selections.Count(pair => pair.Value == SquireDisposition.Desynthesize)} | Vendor: {selections.Count(pair => pair.Value == SquireDisposition.VendorSell)} | Discard: {selections.Count(pair => pair.Value == SquireDisposition.Discard)}");
        if (!value.Snapshot.Diagnostics.IsComplete)
            ImGui.TextColored(MarketMafiosoUiTheme.Error, "Execution blocked: snapshot is incomplete.");
        ImGui.BeginDisabled();
        ImGui.Button("Run selected disposition##Squire");
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Execution remains disabled until exact-slot action adapters pass live validation.");
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
}
