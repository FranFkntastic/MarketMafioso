using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.AgentBridge;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Windows.Main;
using MarketMafioso.Windows.MarketAcquisitionRequestBuilder;

namespace MarketMafioso.Windows.MarketAcquisitionPanels;

internal sealed class MarketAcquisitionRequestPickupPanel
{
    private readonly Action fetchDashboardRequests;
    private readonly Action<string> claimRequest;
    private readonly Action<IReadOnlyList<MarketAcquisitionRequestLineDocument>> addToWorkbench;
    private readonly Action<IReadOnlyList<uint>> returnFromWorkbench;
    private readonly Action openWorkbench;
    private readonly AgentBridgeUiReviewRegistry reviewRegistry;

    public MarketAcquisitionRequestPickupPanel(
        Action fetchDashboardRequests,
        Action<string> claimRequest,
        Action<IReadOnlyList<MarketAcquisitionRequestLineDocument>> addToWorkbench,
        Action<IReadOnlyList<uint>> returnFromWorkbench,
        Action openWorkbench,
        AgentBridgeUiReviewRegistry reviewRegistry)
    {
        this.fetchDashboardRequests = fetchDashboardRequests ?? throw new ArgumentNullException(nameof(fetchDashboardRequests));
        this.claimRequest = claimRequest ?? throw new ArgumentNullException(nameof(claimRequest));
        this.addToWorkbench = addToWorkbench ?? throw new ArgumentNullException(nameof(addToWorkbench));
        this.returnFromWorkbench = returnFromWorkbench ?? throw new ArgumentNullException(nameof(returnFromWorkbench));
        this.openWorkbench = openWorkbench ?? throw new ArgumentNullException(nameof(openWorkbench));
        this.reviewRegistry = reviewRegistry ?? throw new ArgumentNullException(nameof(reviewRegistry));
    }

    public void Draw(MarketAcquisitionRequestPickupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ImGuiUi.SectionHeader("Work Order Inbox", MarketMafiosoUiTheme.Header);
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Durable work waits here. Take a whole request or only the lines you need into the Workbench.");
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "The same item from multiple requests becomes one editable Workbench line; its inbox copies remain durable.");

        if (context.RouteOwnsUi)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Inbox controls are paused while a guided route owns acquisition automation.");
            if (context.ClaimedRequest != null)
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"Active request: {FormatAcquisitionItem(context.ClaimedRequest)}");
            ImGui.SameLine();
            ImGui.TextColored(context.VisibleStatusColor, context.VisibleStatus);
            return;
        }

        if (context.CompactWhenClaimed && context.ClaimedRequest is not null && context.PendingRequests.Count == 0)
        {
            var canFetchCompact = !context.IsBusy &&
                                  context.HasApiKey &&
                                  context.HasCharacterScope;
            if (ImGuiUi.Button("Refresh inbox##MarketAcquisitionFetchCompact", canFetchCompact))
                fetchDashboardRequests();
            RegisterLastControl(
                "acquisition.fetch",
                "Check Workshop Host for Market Acquisition requests",
                canFetchCompact,
                null,
                fetchDashboardRequests);

            ImGui.SameLine();
            ImGui.TextColored(context.VisibleStatusColor, context.VisibleStatus);
            return;
        }

        DrawCharacterScope(context);

        var canFetch = !context.IsBusy &&
                       context.HasApiKey &&
                       context.HasCharacterScope;
        if (ImGuiUi.Button("Refresh inbox##MarketAcquisitionFetch", canFetch))
            fetchDashboardRequests();
        RegisterLastControl(
            "acquisition.fetch",
            "Check Workshop Host for Market Acquisition requests",
            canFetch,
            null,
            fetchDashboardRequests);

        ImGui.SameLine();
        ImGui.TextColored(context.VisibleStatusColor, context.VisibleStatus);

        if (context.WorkbenchItemIds.Count > 0)
        {
            ImGui.SameLine();
            if (ImGuiUi.Button($"Open Workbench ({context.WorkbenchItemIds.Count:N0})##MarketAcquisitionOpenWorkbench", true))
                openWorkbench();
            RegisterLastControl(
                "acquisition.workbench.open",
                "Open the Market Acquisition Workbench",
                true,
                context.WorkbenchItemIds.Count.ToString(),
                openWorkbench);
        }

        if (context.PendingRequests.Count == 0)
        {
            ImGui.TextColored(
                context.HasApiKey ? MarketMafiosoUiTheme.Muted : MarketMafiosoUiTheme.Error,
                context.HasApiKey
                    ? "No actionable work orders are loaded for this character. Shelved work remains on the dashboard."
                    : "Set an acquisition-capable key in Settings before refreshing the inbox.");
            return;
        }

        DrawPendingRequests(context);
    }

    private static void DrawCharacterScope(MarketAcquisitionRequestPickupContext context)
    {
        if (context.HasCharacterScope)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, $"Character scope: {context.CharacterName} @ {context.World}");
        }
        else if (context.IsExpectedCharacterScopeGap)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Character scope temporarily unavailable during route travel.");
        }
        else
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Error, "Character scope unavailable. Log into a character before fetching requests.");
        }
    }

    private void DrawPendingRequests(MarketAcquisitionRequestPickupContext context)
    {
        foreach (var request in context.PendingRequests)
        {
            var lines = MarketAcquisitionRequestDocumentMapper.GetRequestLines(request);
            var selectedCount = lines.Count(line => context.WorkbenchItemIds.Contains(line.ItemId));
            var header = $"{FormatAcquisitionItem(request)}  -  {selectedCount:N0}/{lines.Count:N0} in Workbench##MarketAcquisitionRequest{request.Id}";
            var flags = context.PendingRequests.Count <= 2 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
            if (!ImGui.CollapsingHeader(header, flags))
                continue;

            ImGui.TextColored(
                MarketMafiosoUiTheme.Muted,
                $"{request.Origin}  |  revision {request.Revision:N0}  |  {FormatRouting(request)}");

            var addable = lines.Where(line => !context.WorkbenchItemIds.Contains(line.ItemId)).ToList();
            var returnable = lines.Where(line => context.WorkbenchItemIds.Contains(line.ItemId)).Select(line => line.ItemId).ToList();
            if (ImGuiUi.Button($"Take all##MarketAcquisitionTakeAll{request.Id}", !context.IsBusy && addable.Count > 0))
                AddLines(addable);
            RegisterLastControl(
                $"acquisition.workbench.add-all.{request.Id}",
                $"Take all lines from work order {request.Id} into the Workbench",
                !context.IsBusy && addable.Count > 0,
                request.Id,
                () => AddLines(addable));

            ImGui.SameLine();
            if (ImGuiUi.Button($"Return all##MarketAcquisitionReturnAll{request.Id}", !context.IsBusy && returnable.Count > 0))
                returnFromWorkbench(returnable);
            RegisterLastControl(
                $"acquisition.workbench.return-all.{request.Id}",
                $"Return all lines from work order {request.Id} to the Inbox",
                !context.IsBusy && returnable.Count > 0,
                request.Id,
                () => returnFromWorkbench(returnable));

            ImGui.SameLine();
            var canUseAsIs = !context.IsBusy && context.WorkbenchItemIds.Count == 0;
            var useAsIsLabel = context.WorkbenchItemIds.Count == 0 ? "Use request as-is" : "Use as-is (Workbench not empty)";
            if (ImGuiUi.Button($"{useAsIsLabel}##MarketAcquisitionClaim{request.Id}", canUseAsIs))
                claimRequest(request.Id);
            RegisterLastControl(
                $"acquisition.claim.{request.Id}",
                $"Use work order {request.Id} as-is in the Workbench",
                canUseAsIs,
                request.Id,
                () => claimRequest(request.Id));

            DrawRequestLines(context, request, lines);
            ImGui.Spacing();
        }
    }

    private void DrawRequestLines(
        MarketAcquisitionRequestPickupContext context,
        MarketAcquisitionRequestView request,
        IReadOnlyList<MarketAcquisitionBatchLineView> lines)
    {
        var tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable;
        if (!ImGui.BeginTable($"MarketAcquisitionPendingRequestLines{request.Id}", 7, tableFlags))
            return;

        ImGui.TableSetupColumn("Item");
        ImGui.TableSetupColumn("Qty");
        ImGui.TableSetupColumn("HQ");
        ImGui.TableSetupColumn("Max Unit");
        ImGui.TableSetupColumn("Spend Cap");
        ImGui.TableSetupColumn("Mode");
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 100f);
        ImGui.TableHeadersRow();

        foreach (var line in lines)
        {
            var inWorkbench = context.WorkbenchItemIds.Contains(line.ItemId);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatLineItem(line));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(MarketAcquisitionQuantityModePresenter.FormatQuantity(line.QuantityMode, ResolveQuantity(line)));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.HqPolicy);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(RequestPricingFormatter.FormatOptionalGil(line.MaxUnitPrice));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(RequestPricingFormatter.FormatOptionalGil(line.GilCap));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(MarketAcquisitionQuantityModePresenter.FormatMode(line.QuantityMode));
            ImGui.TableNextColumn();

            var controlId = string.IsNullOrWhiteSpace(line.LineId) ? line.ItemId.ToString() : line.LineId;
            if (inWorkbench)
            {
                if (ImGuiUi.Button($"Return##MarketAcquisitionReturn{request.Id}{controlId}", !context.IsBusy))
                    returnFromWorkbench([line.ItemId]);
                RegisterLastControl(
                    $"acquisition.workbench.return.{request.Id}.{controlId}",
                    $"Return {FormatLineItem(line)} to the Inbox",
                    !context.IsBusy,
                    line.ItemId.ToString(),
                    () => returnFromWorkbench([line.ItemId]));
            }
            else
            {
                if (ImGuiUi.Button($"Take##MarketAcquisitionTake{request.Id}{controlId}", !context.IsBusy))
                    AddLines([line]);
                RegisterLastControl(
                    $"acquisition.workbench.add.{request.Id}.{controlId}",
                    $"Take {FormatLineItem(line)} into the Workbench",
                    !context.IsBusy,
                    line.ItemId.ToString(),
                    () => AddLines([line]));
            }
        }

        ImGui.EndTable();
    }

    private void AddLines(IReadOnlyList<MarketAcquisitionBatchLineView> lines) =>
        addToWorkbench(lines.Select(MarketAcquisitionRequestDocumentMapper.FromRequestLine).ToList());

    private static string FormatAcquisitionItem(MarketAcquisitionRequestView request)
    {
        if (request.Lines.Count > 1)
            return $"{request.Lines.Count:N0} item batch ({FormatPrimaryAcquisitionItem(request)})";

        return FormatPrimaryAcquisitionItem(request);
    }

    private static string FormatPrimaryAcquisitionItem(MarketAcquisitionRequestView request)
    {
        var name = string.IsNullOrWhiteSpace(request.ItemName)
            ? $"Item {request.ItemId}"
            : request.ItemName;
        return $"{name} ({request.ItemId})";
    }

    private static string FormatLineItem(MarketAcquisitionBatchLineView line) =>
        $"{(string.IsNullOrWhiteSpace(line.ItemName) ? $"Item {line.ItemId}" : line.ItemName)} ({line.ItemId})";

    private static uint ResolveQuantity(MarketAcquisitionBatchLineView line) =>
        line.QuantityMode == "TargetQuantity" ? line.TargetQuantity : line.MaxQuantity;

    private static string FormatRouting(MarketAcquisitionRequestView request) =>
        request.WorldMode.Equals("AllWorldSweep", StringComparison.OrdinalIgnoreCase)
            ? $"Sweep {request.SweepScope}"
            : "Recommended route";

    private void RegisterLastControl(string id, string label, bool enabled, string? value, Action invoke) =>
        reviewRegistry.RegisterLastButton(id, label, enabled, invoke, value);
}
