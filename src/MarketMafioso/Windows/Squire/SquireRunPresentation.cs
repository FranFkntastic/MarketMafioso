using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.Equipment;
using MarketMafioso.Squire;
using MarketMafioso.Windows.Main;

namespace MarketMafioso.Windows.Squire;

internal sealed record SquireRunPresentation(
    SquireActionPlan Plan,
    SquireRunResult Result,
    string AuditPath,
    IReadOnlyList<SquireReviewedSelection> Completed,
    IReadOnlyList<SquireReviewedSelection> Failed,
    IReadOnlyList<SquireReviewedSelection> Remaining,
    string? FailureMessage)
{
    public static SquireRunPresentation Create(SquireActionPlan plan, SquireRunResult result, string auditPath)
    {
        var stopped = result.Events.LastOrDefault(value => value.Kind == "RunStopped");
        var failedFingerprint = stopped?.Item;
        var completedFingerprints = result.Events
            .Where(value => value.Kind is "ActionResult" or "DiagnosticActionResult")
            .Where(value => failedFingerprint is null || value.Item != failedFingerprint)
            .Select(value => value.Item)
            .OfType<EquipmentInstanceFingerprint>()
            .ToHashSet(EquipmentInstanceFingerprintComparer.Instance);
        var completed = plan.Actions.Where(action => completedFingerprints.Contains(action.Fingerprint)).ToArray();
        var failed = failedFingerprint is { } failedItem
            ? plan.Actions.Where(action => EquipmentInstanceFingerprintComparer.Instance.Equals(action.Fingerprint, failedItem)).ToArray()
            : [];
        var remaining = plan.Actions
            .Where(action => !completedFingerprints.Contains(action.Fingerprint))
            .Where(action => failedFingerprint is null || !EquipmentInstanceFingerprintComparer.Instance.Equals(action.Fingerprint, failedFingerprint))
            .ToArray();
        return new SquireRunPresentation(plan, result, auditPath, completed, failed, remaining, stopped?.Message);
    }

    public IReadOnlyList<SquireReviewedSelection> Retryable => Failed.Concat(Remaining).ToArray();
}

internal sealed class SquireRunResultPanel
{
    public void Draw(
        SquireRunPresentation presentation,
        Action refreshAndReview,
        Action prepareRetry,
        Action openAuditLocation)
    {
        ImGui.Separator();
        ImGui.TextColored(MarketMafiosoUiTheme.Header,
            presentation.Result.Success ? "Last cleanup run completed" : "Last cleanup run stopped");
        ImGui.TextUnformatted(
            $"Completed {presentation.Completed.Count} | Failed {presentation.Failed.Count} | Remaining {presentation.Remaining.Count} | Audit {Path.GetFileName(presentation.AuditPath)}");
        if (!string.IsNullOrWhiteSpace(presentation.FailureMessage))
            ImGui.TextColored(MarketMafiosoUiTheme.Error, presentation.FailureMessage);

        if (presentation.Retryable.Count > 0 &&
            ImGui.BeginTable("##SquireRunRecovery", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 75);
            ImGui.TableSetupColumn("Item ID", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Location");
            ImGui.TableSetupColumn("Disposition");
            ImGui.TableHeadersRow();
            foreach (var action in presentation.Failed)
                DrawAction("Failed", action);
            foreach (var action in presentation.Remaining)
                DrawAction("Remaining", action);
            ImGui.EndTable();
        }

        if (presentation.Retryable.Count > 0)
        {
            if (ImGui.Button("Refresh and review remaining"))
                refreshAndReview();
            ImGui.SameLine();
            if (ImGui.Button("Prepare retry from fresh analysis"))
                prepareRetry();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Re-evaluates every remaining exact item and builds a new unconfirmed cleanup batch. It never replays the old plan.");
            ImGui.SameLine();
        }
        if (ImGui.Button("Open audit location"))
            openAuditLocation();
    }

    private static void DrawAction(string state, SquireReviewedSelection action)
    {
        ImGui.TableNextRow();
        Cell(state);
        Cell(action.Fingerprint.ItemId.ToString());
        Cell(SquirePresentation.FormatLocation(action.Fingerprint));
        Cell(SquirePresentation.FormatDisposition(action.Disposition));
    }

    private static void Cell(string value)
    {
        ImGui.TableNextColumn();
        ImGui.TextWrapped(value);
    }
}
