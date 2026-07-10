using System;
using Dalamud.Bindings.ImGui;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Windows.Main;

namespace MarketMafioso.Windows.MarketAcquisitionPanels;

internal sealed class MarketAcquisitionRequestPickupPanel
{
    private readonly Action fetchDashboardRequests;
    private readonly Action<string> claimRequest;

    public MarketAcquisitionRequestPickupPanel(
        Action fetchDashboardRequests,
        Action<string> claimRequest)
    {
        this.fetchDashboardRequests = fetchDashboardRequests ?? throw new ArgumentNullException(nameof(fetchDashboardRequests));
        this.claimRequest = claimRequest ?? throw new ArgumentNullException(nameof(claimRequest));
    }

    public void Draw(MarketAcquisitionRequestPickupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ImGuiUi.SectionHeader("Dashboard Requests", MarketMafiosoUiTheme.Header);

        if (context.RouteOwnsUi)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Request pickup is hidden while a guided route is active.");
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
            if (ImGuiUi.Button("Check Dashboard##MarketAcquisitionFetchCompact", canFetchCompact))
                fetchDashboardRequests();

            ImGui.SameLine();
            ImGui.TextColored(context.VisibleStatusColor, context.VisibleStatus);
            return;
        }

        DrawCharacterScope(context);

        var canFetch = !context.IsBusy &&
                       context.HasApiKey &&
                       context.HasCharacterScope;
        if (ImGuiUi.Button("Check Dashboard##MarketAcquisitionFetch", canFetch))
            fetchDashboardRequests();

        ImGui.SameLine();
        ImGui.TextColored(context.VisibleStatusColor, context.VisibleStatus);

        if (context.PendingRequests.Count == 0)
        {
            ImGui.TextColored(
                context.HasApiKey ? MarketMafiosoUiTheme.Muted : MarketMafiosoUiTheme.Error,
                context.HasApiKey
                    ? "No pending dashboard requests are loaded."
                    : "Set the client API key in Settings before fetching dashboard requests.");
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
        var tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable;
        if (ImGui.BeginTable("MarketAcquisitionPendingRequests", 6, tableFlags))
        {
            ImGui.TableSetupColumn("Item");
            ImGui.TableSetupColumn("Qty");
            ImGui.TableSetupColumn("HQ");
            ImGui.TableSetupColumn("Max Unit");
            ImGui.TableSetupColumn("Mode");
            ImGui.TableSetupColumn("");
            ImGui.TableHeadersRow();

            foreach (var request in context.PendingRequests)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatAcquisitionItem(request));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(MarketAcquisitionQuantityModePresenter.FormatQuantity(request.QuantityMode, request.Quantity));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(request.HqPolicy);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatGil(request.MaxUnitPrice));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(MarketAcquisitionQuantityModePresenter.FormatMode(request.QuantityMode));
                ImGui.TableNextColumn();
                if (ImGuiUi.Button($"Claim##marketAcquisitionClaim{request.Id}", !context.IsBusy))
                    claimRequest(request.Id);
            }

            ImGui.EndTable();
        }
    }

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

    private static string FormatGil(uint gil) => $"{gil:N0} gil";
}
