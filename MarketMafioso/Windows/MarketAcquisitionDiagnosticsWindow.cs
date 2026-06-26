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
    private readonly Func<MarketAcquisitionLiveDryRun?> getDryRun;

    private static readonly Vector4 ColHeader = new(0.38f, 0.73f, 1.00f, 1f);
    private static readonly Vector4 ColSuccess = new(0.45f, 0.90f, 0.55f, 1f);
    private static readonly Vector4 ColError = new(1.00f, 0.40f, 0.40f, 1f);
    private static readonly Vector4 ColMuted = new(0.60f, 0.60f, 0.60f, 1f);

    public MarketAcquisitionDiagnosticsWindow(
        Func<MarketBoardReadResult?> getReadResult,
        Func<MarketBoardListingReconciliation?> getReconciliation,
        Func<MarketAcquisitionLiveDryRun?> getDryRun)
        : base("Market Acquisition Diagnostics##MarketAcquisitionDiagnostics")
    {
        this.getReadResult = getReadResult;
        this.getReconciliation = getReconciliation;
        this.getDryRun = getDryRun;

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
        var dryRun = getDryRun();

        ImGui.TextColored(ColHeader, "Live Market Board Diagnostics");
        ImGui.TextWrapped("Read-only probe data and candidate decisions for the current Market Acquisition request.");
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
        DrawDryRun(dryRun);
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

    private static void DrawDryRun(MarketAcquisitionLiveDryRun? dryRun)
    {
        ImGuiUi.SectionHeader("Live Candidate Dry Run", ColHeader);
        if (dryRun == null)
        {
            ImGui.TextColored(ColMuted, "No live candidate dry-run rows are available.");
            return;
        }

        var summary = MarketAcquisitionLiveDryRunPresenter.BuildSummary(dryRun);
        ImGui.TextColored(
            summary.Status == "Ready" ? ColSuccess : ColHeader,
            $"Dry-run: {summary.Status} - would buy {summary.WouldBuyQuantity:N0}/{summary.RequestedQuantity:N0}, spend {FormatGil(summary.WouldSpendGil)}");
        ImGui.TextColored(ColMuted, $"{summary.WouldBuyRows:N0} buy row(s), {summary.SkippedRows:N0} skipped row(s), {summary.TotalRows:N0} total live row(s).");
        ImGui.TextWrapped(summary.Message);

        if (ImGui.BeginTable("MarketAcquisitionLiveDryRunDiagnostics", 7, ImGuiUi.InteractiveTableFlags))
        {
            ImGui.TableSetupColumn("Decision");
            ImGui.TableSetupColumn("Reason");
            ImGui.TableSetupColumn("Retainer");
            ImGui.TableSetupColumn("Qty");
            ImGui.TableSetupColumn("Unit");
            ImGui.TableSetupColumn("HQ");
            ImGui.TableSetupColumn("Message");
            ImGui.TableHeadersRow();

            foreach (var row in dryRun.Rows)
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
