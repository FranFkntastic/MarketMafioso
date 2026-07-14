using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Windows.Main;
using MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

namespace MarketMafioso.Windows.MarketAcquisitionPanels;

internal sealed class MarketAcquisitionAcceptedRequestPanel
{
    private readonly Action acceptRequest;
    private readonly Action rejectRequest;
    private readonly Action removeLocalRequest;
    private readonly Action preparePlan;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;

    public MarketAcquisitionAcceptedRequestPanel(
        Action acceptRequest,
        Action rejectRequest,
        Action removeLocalRequest,
        Action preparePlan,
        AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.acceptRequest = acceptRequest ?? throw new ArgumentNullException(nameof(acceptRequest));
        this.rejectRequest = rejectRequest ?? throw new ArgumentNullException(nameof(rejectRequest));
        this.removeLocalRequest = removeLocalRequest ?? throw new ArgumentNullException(nameof(removeLocalRequest));
        this.preparePlan = preparePlan ?? throw new ArgumentNullException(nameof(preparePlan));
        this.reviewRegistry = reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry));
    }

    public void Draw(MarketAcquisitionClaimView? claimedRequest, bool isBusy, bool canPrepare)
    {
        ImGuiUi.SectionHeader("Accepted Request", MarketMafiosoUiTheme.Header);

        if (claimedRequest == null)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "No accepted request is loaded in this plugin session.");
            return;
        }

        DrawClaimedBatchSummary(claimedRequest);
        DrawAcceptedRequestRecoveryHint(claimedRequest);
        ImGui.Spacing();
        DrawClaimedBatchLines(claimedRequest);
        ImGui.Spacing();
        DrawClaimedBatchActions(claimedRequest, isBusy, canPrepare);
    }

    private void DrawClaimedBatchActions(MarketAcquisitionClaimView claimed, bool isBusy, bool canPrepare)
    {
        var canMutateClaim = !isBusy &&
                             string.Equals(claimed.Status, "Claimed", StringComparison.OrdinalIgnoreCase);
        if (canMutateClaim)
        {
            if (ImGuiUi.PrimaryButton("Accept Request", true))
                acceptRequest();
            RegisterLastControl("acquisition.accept", "Accept the claimed Market Acquisition request", true, claimed.Id, acceptRequest);

            ImGui.SameLine();
            if (ImGuiUi.Button("Reject Request", true))
                rejectRequest();
            RegisterLastControl("acquisition.reject", "Reject the claimed Market Acquisition request", true, claimed.Id, rejectRequest);

            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Accept this request before preparing its market plan.");
        }
        else
        {
            if (ImGuiUi.PrimaryButton("Prepare Plan", canPrepare))
                preparePlan();
            RegisterLastControl("acquisition.prepare", "Prepare the accepted Market Acquisition request", canPrepare, claimed.Id, preparePlan);

            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Preparing a plan reads remote market data. Guided routes validate live rows before purchasing.");
        }

        if (ImGuiUi.Button("Remove Local", !isBusy))
            removeLocalRequest();
        RegisterLastControl("acquisition.remove-local", "Remove the local Market Acquisition claim", !isBusy, claimed.Id, removeLocalRequest);
    }

    private static void DrawAcceptedRequestRecoveryHint(MarketAcquisitionClaimView claimed)
    {
        if (!string.Equals(claimed.Status, "Failed", StringComparison.OrdinalIgnoreCase))
            return;

        ImGui.TextColored(
            MarketMafiosoUiTheme.Error,
            "This accepted request is failed. Remove local state, check the dashboard, or prepare a fresh plan before retrying.");
    }

    private static void DrawClaimedBatchSummary(MarketAcquisitionClaimView claimed)
    {
        if (!ImGui.BeginTable("MarketAcquisitionClaimedBatchSummary", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            return;

        DrawClaimedRequestRow("Status", claimed.Status);
        DrawClaimedRequestRow("Target", $"{claimed.TargetCharacterName} @ {claimed.TargetWorld}");
        DrawClaimedRequestRow("Lines", FormatAcquisitionLineCount(claimed));
        DrawClaimedRequestRow("Routing", FormatClaimedBatchRouting(claimed));
        DrawClaimedRequestRow("Latest recorded activity", FormatClaimedBatchLatest(claimed));
        ImGui.EndTable();
    }

    private static void DrawClaimedBatchLines(MarketAcquisitionClaimView claimed)
    {
        if (!ImGui.BeginTable(
                "MarketAcquisitionClaimedBatchLines",
                9,
                AcquisitionRequestTableStyle.ClaimedBatchLineTableFlags,
                AcquisitionRequestTableStyle.FiveLineTableSize()))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthFixed, 220);
        ImGui.TableSetupColumn("Mode", ImGuiTableColumnFlags.WidthFixed, 132);
        ImGui.TableSetupColumn("Max Unit", ImGuiTableColumnFlags.WidthFixed, 96);
        ImGui.TableSetupColumn("Max Qty", ImGuiTableColumnFlags.WidthFixed, 88);
        ImGui.TableSetupColumn("Gil Cap", ImGuiTableColumnFlags.WidthFixed, 96);
        ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, 64);
        ImGui.TableSetupColumn("Bought", ImGuiTableColumnFlags.WidthFixed, 88);
        ImGui.TableSetupColumn("Spent", ImGuiTableColumnFlags.WidthFixed, 88);
        ImGui.TableSetupColumn("Recorded Status", ImGuiTableColumnFlags.WidthFixed, 128);
        ImGui.TableHeadersRow();

        foreach (var line in GetAcquisitionPlanLines(claimed).OrderBy(line => line.Ordinal))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatLineItem(line));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(MarketAcquisitionQuantityModePresenter.FormatMode(line.QuantityMode));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatGil(line.MaxUnitPrice));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatLineMaxQuantity(line));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatGilCap(line.GilCap));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.HqPolicy);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.PurchasedQuantity == 0 ? "-" : line.PurchasedQuantity.ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.SpentGil == 0 ? "-" : FormatGil(line.SpentGil));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(line.Status) ? "-" : line.Status);
        }

        ImGui.EndTable();
    }

    private static void DrawClaimedRequestRow(string label, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, label);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(value);
    }

    private static IReadOnlyList<MarketAcquisitionBatchLineView> GetAcquisitionPlanLines(MarketAcquisitionClaimView claimed) =>
        MarketAcquisitionPlanPreparationService.GetPlanLines(claimed);

    private static string FormatAcquisitionLineCount(MarketAcquisitionRequestView request) =>
        request.Lines.Count == 0 ? "1 line" : $"{request.Lines.Count:N0} line(s)";

    private static string FormatClaimedBatchRouting(MarketAcquisitionClaimView claimed) =>
        $"{FormatWorldMode(claimed.WorldMode)} / {claimed.Region}";

    private static string FormatClaimedBatchLatest(MarketAcquisitionClaimView claimed)
    {
        var latestLineMessage = claimed.Lines
            .OrderByDescending(line => line.Ordinal)
            .Select(line => line.LatestMessage)
            .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message));
        if (!string.IsNullOrWhiteSpace(latestLineMessage))
            return latestLineMessage;

        if (!string.IsNullOrWhiteSpace(claimed.LatestAttemptResult))
            return claimed.LatestAttemptResult;

        return "-";
    }

    private static string FormatLineItem(MarketAcquisitionBatchLineView line)
    {
        var name = string.IsNullOrWhiteSpace(line.ItemName)
            ? $"Item {line.ItemId}"
            : line.ItemName;
        return $"{name} ({line.ItemId})";
    }

    private static string FormatLineMaxQuantity(MarketAcquisitionBatchLineView line)
    {
        if (line.MaxQuantity == 0)
            return "No cap";

        return line.MaxQuantity.ToString("N0");
    }

    private static string FormatGil(uint gil) => $"{gil:N0} gil";

    private static string FormatGilCap(uint gil) => gil == 0 ? "No cap" : FormatGil(gil);

    private static string FormatWorldMode(string worldMode) =>
        worldMode switch
        {
            "AllWorldSweep" => "All-world sweep",
            "CurrentWorldOnly" => "Current world only",
            _ => worldMode,
        };

    private void RegisterLastControl(string id, string label, bool enabled, string? value, Action invoke) =>
        reviewRegistry.Register(
            id,
            label,
            AgentBridgeUiControlKind.Button,
            ImGui.GetItemRectMin(),
            ImGui.GetItemRectMax(),
            enabled,
            false,
            value,
            invoke);
}
