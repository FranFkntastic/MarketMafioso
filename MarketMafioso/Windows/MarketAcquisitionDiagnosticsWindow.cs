using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using MarketMafioso.MarketAcquisition;

namespace MarketMafioso.Windows;

public sealed class MarketAcquisitionDiagnosticsWindow : Window
{
    private readonly Func<MarketBoardReadResult?> getReadResult;
    private readonly Func<MarketBoardListingReconciliation?> getReconciliation;
    private readonly Func<MarketAcquisitionLiveCandidatePlan?> getCandidatePlan;
    private readonly Func<bool> canProbeLiveListings;
    private readonly Action probeLiveListings;
    private readonly Action captureInputState;
    private readonly Func<bool> canFinalizeInputCaptureLog;
    private readonly Action finalizeInputCaptureLog;
    private readonly Func<string?> getDiagnosticFilePath;

    private static readonly Vector4 ColHeader = new(0.38f, 0.73f, 1.00f, 1f);
    private static readonly Vector4 ColSuccess = new(0.45f, 0.90f, 0.55f, 1f);
    private static readonly Vector4 ColError = new(1.00f, 0.40f, 0.40f, 1f);
    private static readonly Vector4 ColMuted = new(0.60f, 0.60f, 0.60f, 1f);

    public MarketAcquisitionDiagnosticsWindow(
        Func<MarketBoardReadResult?> getReadResult,
        Func<MarketBoardListingReconciliation?> getReconciliation,
        Func<MarketAcquisitionLiveCandidatePlan?> getCandidatePlan,
        Func<bool> canProbeLiveListings,
        Action probeLiveListings,
        Action captureInputState,
        Func<bool> canFinalizeInputCaptureLog,
        Action finalizeInputCaptureLog,
        Func<string?> getDiagnosticFilePath)
        : base("Market Acquisition Diagnostics##MarketAcquisitionDiagnostics")
    {
        this.getReadResult = getReadResult;
        this.getReconciliation = getReconciliation;
        this.getCandidatePlan = getCandidatePlan;
        this.canProbeLiveListings = canProbeLiveListings;
        this.probeLiveListings = probeLiveListings;
        this.captureInputState = captureInputState;
        this.canFinalizeInputCaptureLog = canFinalizeInputCaptureLog;
        this.finalizeInputCaptureLog = finalizeInputCaptureLog;
        this.getDiagnosticFilePath = getDiagnosticFilePath;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        var readResult = getReadResult();
        var reconciliation = getReconciliation();
        var candidatePlan = getCandidatePlan();

        ImGui.TextColored(ColHeader, "Live Market Board Diagnostics");
        ImGui.TextWrapped("Manual probe tools, input capture, and candidate decisions for the current Market Acquisition request.");
        ImGui.Separator();

        DrawDiagnosticControls();
        ImGui.Separator();

        if (readResult == null)
        {
            ImGui.TextColored(ColMuted, "No live market board probe has run this session.");
            return;
        }

        DrawReadResult(readResult);
        ImGui.Spacing();
        DrawReconciliation(reconciliation);
        ImGui.Spacing();
        DrawLiveCandidatePlan(candidatePlan);
    }

    private void DrawDiagnosticControls()
    {
        if (ImGuiUi.Button("Read Live Listings", canProbeLiveListings()))
            probeLiveListings();

        ImGui.SameLine();
        if (ImGuiUi.Button("Capture Input State", true))
            captureInputState();

        ImGui.SameLine();
        if (ImGuiUi.Button("Finish Capture Log", canFinalizeInputCaptureLog()))
            finalizeInputCaptureLog();

        var diagnosticFilePath = getDiagnosticFilePath();
        if (!string.IsNullOrWhiteSpace(diagnosticFilePath))
            ImGui.TextColored(ColMuted, $"Diagnostics: {diagnosticFilePath}");
    }

    private static void DrawReadResult(MarketBoardReadResult readResult)
    {
        ImGui.TextColored(
            readResult.Status == "Ready" ? ColSuccess : ColMuted,
            $"Read status: {readResult.Status}");
        ImGui.TextWrapped(readResult.Message);
    }

    private static void DrawReconciliation(MarketBoardListingReconciliation? reconciliation)
    {
        ImGuiUi.SectionHeader("Strict Reconciliation", ColHeader);
        if (reconciliation == null)
        {
            ImGui.TextColored(ColMuted, "No strict reconciliation rows are available.");
            return;
        }

        ImGui.TextColored(
            reconciliation.Status == "Ready" ? ColSuccess : ColError,
            $"Reconciliation: {reconciliation.Status}");

        if (ImGui.BeginTable("MarketBoardProbeReconciliationDiagnostics", 6, ImGuiUi.InteractiveTableFlags))
        {
            ImGui.TableSetupColumn("Status");
            ImGui.TableSetupColumn("Retainer");
            ImGui.TableSetupColumn("Qty");
            ImGui.TableSetupColumn("Unit");
            ImGui.TableSetupColumn("HQ");
            ImGui.TableSetupColumn("Message");
            ImGui.TableHeadersRow();

            foreach (var row in reconciliation.Listings)
            {
                var listing = row.LiveListing;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(row.Status == "Matched" ? ColSuccess : ColError, row.Status);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(listing?.RetainerName)
                    ? row.PlannedListing.RetainerName
                    : listing.RetainerName);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted((listing?.Quantity ?? row.PlannedListing.Quantity).ToString("N0"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatGil(listing?.UnitPrice ?? row.PlannedListing.UnitPrice));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted((listing?.IsHq ?? row.PlannedListing.IsHq) ? "HQ" : "NQ");
                ImGui.TableNextColumn();
                ImGui.TextWrapped(row.Message);
            }

            ImGui.EndTable();
        }
    }

    private static void DrawLiveCandidatePlan(MarketAcquisitionLiveCandidatePlan? candidatePlan)
    {
        ImGuiUi.SectionHeader("Live Candidates", ColHeader);
        if (candidatePlan == null)
        {
            ImGui.TextColored(ColMuted, "No live candidate rows are available.");
            return;
        }

        var summary = MarketAcquisitionLiveCandidatePresenter.BuildSummary(candidatePlan);
        ImGui.TextColored(
            summary.Status == "Ready" ? ColSuccess : ColHeader,
            $"Live candidate: {summary.Status} - would buy {summary.WouldBuyQuantity:N0}/{summary.RequestedQuantity:N0}, spend {FormatGil(summary.WouldSpendGil)}");
        ImGui.TextColored(ColMuted, $"{summary.WouldBuyRows:N0} buy row(s), {summary.SkippedRows:N0} skipped row(s), {summary.TotalRows:N0} total live row(s).");
        ImGui.TextWrapped(summary.Message);

        if (ImGui.BeginTable("MarketAcquisitionLiveCandidatePlanDiagnostics", 7, ImGuiUi.InteractiveTableFlags))
        {
            ImGui.TableSetupColumn("Decision");
            ImGui.TableSetupColumn("Reason");
            ImGui.TableSetupColumn("Retainer");
            ImGui.TableSetupColumn("Qty");
            ImGui.TableSetupColumn("Unit");
            ImGui.TableSetupColumn("HQ");
            ImGui.TableSetupColumn("Message");
            ImGui.TableHeadersRow();

            foreach (var row in candidatePlan.Rows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(row.Decision == "WouldBuy" ? ColSuccess : ColMuted, row.Decision);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Reason);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.LiveListing.RetainerName);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.LiveListing.Quantity.ToString("N0"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatGil(row.LiveListing.UnitPrice));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.LiveListing.IsHq ? "HQ" : "NQ");
                ImGui.TableNextColumn();
                ImGui.TextWrapped(row.Message);
            }

            ImGui.EndTable();
        }
    }

    private static string FormatGil(uint gil) => $"{gil:N0} gil";
}
