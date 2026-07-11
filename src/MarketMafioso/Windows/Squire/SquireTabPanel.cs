using System;
using System.IO;
using System.Linq;
using Dalamud.Bindings.ImGui;
using MarketMafioso.Squire;
using MarketMafioso.Squire.Observation;
using MarketMafioso.Windows.Main;
using Newtonsoft.Json;

namespace MarketMafioso.Windows.Squire;

internal sealed class SquireTabPanel
{
    private readonly ICharacterEquipmentSnapshotSource snapshotSource;
    private readonly SquireCandidateEvaluator evaluator = new();
    private readonly string diagnosticDirectory;
    private SquireAnalysis? analysis;
    private string search = string.Empty;
    private bool showProtected;
    private string status = "Refresh to capture a read-only equipment snapshot.";

    public SquireTabPanel(ICharacterEquipmentSnapshotSource snapshotSource, string diagnosticDirectory)
    {
        this.snapshotSource = snapshotSource;
        this.diagnosticDirectory = diagnosticDirectory;
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
        ImGui.InputTextWithHint("##SquireSearch", "Search item, location, or reason", ref search, 160);
        ImGui.SameLine();
        ImGui.Checkbox("Show protected", ref showProtected);
        DrawTable(analysis);
    }

    private void Refresh()
    {
        try
        {
            analysis = evaluator.Evaluate(snapshotSource.Capture());
            var executable = analysis.Candidates.Count(candidate => candidate.IsExecutable);
            status = analysis.IsActionable
                ? $"Complete snapshot; {executable} executable candidate(s)."
                : "Snapshot is incomplete; actions are blocked.";
        }
        catch (Exception ex)
        {
            analysis = null;
            status = $"Refresh failed: {ex.Message}";
        }
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
        if (!ImGui.BeginTable("##SquireCandidates", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable))
            return;
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
