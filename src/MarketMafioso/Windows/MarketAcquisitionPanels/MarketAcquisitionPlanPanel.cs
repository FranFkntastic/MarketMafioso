using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.UI.Tables;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Windows.Main;

namespace MarketMafioso.Windows.MarketAcquisitionPanels;

internal sealed class MarketAcquisitionPlanPanel
{
    private sealed record PlanItemGroup(string Key, string Item, IReadOnlyList<MarketAcquisitionPlannedListing> Listings)
    {
        public uint Quantity => (uint)Math.Min(Listings.Sum(listing => (long)listing.Quantity), uint.MaxValue);
        public uint Gil => (uint)Math.Min(Listings.Sum(listing => (long)listing.TotalGil), uint.MaxValue);
        public uint MinUnit => Listings.Min(listing => listing.UnitPrice);
        public uint MaxUnit => Listings.Max(listing => listing.UnitPrice);
    }

    private readonly DalamudExpandableRows expandedStops = new();
    private readonly DalamudExpandableRows expandedItems = new();

    public void Draw(MarketAcquisitionPlan? plan, bool isStale)
    {
        ImGuiUi.SectionHeader("Plan", MarketMafiosoUiTheme.Header);

        if (plan == null)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "No plan prepared.");
            return;
        }

        ImGui.TextColored(
            plan.Status == "Ready" ? MarketMafiosoUiTheme.Success : MarketMafiosoUiTheme.Muted,
            $"Status: {plan.Status}  -  Mode: {FormatWorldMode(plan.WorldMode)}  -  Planned {plan.PlannedQuantity:N0}/{plan.RequestedQuantity:N0} item(s), {FormatGil(plan.PlannedGil)}");

        if (isStale)
            ImGui.TextColored(MarketMafiosoUiTheme.Error, "Request changed after this plan was prepared. Update the request and prepare a fresh plan before starting.");

        DrawRouteStops(plan);
    }

    private void DrawRouteStops(MarketAcquisitionPlan plan)
    {
        var skipped = plan.Diagnostics.StopDecisions.Where(decision => decision.Skipped).ToArray();
        if (plan.WorldBatches.Count == 0 && skipped.Length == 0)
            return;

        var label = skipped.Length == 0
            ? $"Route Stops ({plan.WorldBatches.Count:N0})"
            : $"Route Stops ({plan.WorldBatches.Count:N0}, {skipped.Length:N0} skipped)";
        if (!ImGui.CollapsingHeader($"{label}##MarketAcquisitionPlanRouteStops", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        // The ID carries the column layout: ImGui would otherwise reapply the saved 5-column widths.
        if (!ImGui.BeginTable("MarketAcquisitionPlanRouteStopTable##coverage-v1", 7,
                DalamudNestedTableFlags.Parent(resizable: true)))
            return;

        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 56);
        ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, 160);
        ImGui.TableSetupColumn("Data Center", ImGuiTableColumnFlags.WidthFixed, 96);
        ImGui.TableSetupColumn("Items", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Gil", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("Coverage", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Savings", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableHeadersRow();

        var ordinal = 0;
        foreach (var batch in plan.WorldBatches)
        {
            var stopKey = batch.WorldName;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted((++ordinal).ToString());
            ImGui.TableNextColumn();
            if (batch.Listings.Count > 0)
            {
                expandedStops.DrawToggle(stopKey);
                ImGui.SameLine();
            }

            ImGui.TextUnformatted(batch.WorldName);
            if (batch.ExceedsRequestedQuantity)
            {
                ImGui.SameLine();
                ImGui.TextColored(MarketMafiosoUiTheme.Muted, "(over)");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("This stop's listings buy past the requested quantity.");
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatRouteDataCenter(batch.DataCenter));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(batch.PlannedQuantity.ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatGil(batch.PlannedGil));
            ImGui.TableNextColumn();
            DrawCoverage(batch.LineCoverage, false);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatSavings(batch.BuyBelowSavingsGil));
            if (ImGui.IsItemHovered() && batch.BuyBelowSavingsGil > 0)
                ImGui.SetTooltip("Savings under buy-below ceilings, including eligible advisory listings; not guaranteed purchases.");

            if (batch.Listings.Count > 0)
                expandedStops.DrawDetail(stopKey, () => DrawStopItems(batch));
        }

        foreach (var decision in skipped.OrderByDescending(decision => decision.CoverageScore).ThenByDescending(decision => decision.BuyBelowSavingsGil))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Skipped");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(decision.Reason);
            ImGui.TableNextColumn();
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, decision.WorldName);
            ImGui.TableNextColumn();
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, FormatRouteDataCenter(MarketAcquisitionWorldCatalog.ResolveDataCenter(decision.WorldName)));
            ImGui.TableNextColumn();
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, decision.PlannedQuantity.ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, FormatGil(decision.PlannedGil));
            ImGui.TableNextColumn();
            DrawCoverage(decision.LineCoverage, true);
            ImGui.TableNextColumn();
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, FormatSavings(decision.BuyBelowSavingsGil));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(decision.Reason);
        }

        ImGui.EndTable();
    }

    private static void DrawCoverage(IReadOnlyList<MarketAcquisitionStopLineCoverage> shares, bool muted)
    {
        var covered = shares.Where(line => line.Share > 0).OrderByDescending(line => line.Share).ToArray();
        var text = covered.Length == 0 ? "-" :
            $"{covered[0].ItemName ?? "Unnamed item"} {covered[0].Share * 100:0.##}%" +
            (covered.Length > 1 ? $" +{covered.Length - 1}" : string.Empty);
        if (muted)
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, text);
        else
            ImGui.TextUnformatted(text);
        if (ImGui.IsItemHovered() && covered.Length > 0)
            ImGui.SetTooltip(string.Join("\n", covered.Select(line =>
                $"{line.ItemName ?? "Unnamed item"}: {line.Share * 100:0.##}% ({line.PlannedQuantity:N0}/{line.RemainingNeed:N0})")));
    }

    private static string FormatSavings(ulong gil) => gil > 0 ? $"{gil:N0} gil" : "-";

    private void DrawStopItems(MarketAcquisitionWorldBatch batch)
    {
        var groups = batch.Listings
            .GroupBy(listing => listing.ItemId != 0 ? listing.ItemId.ToString() : FormatPlannedListingItem(listing))
            .Select(group => new PlanItemGroup(
                $"{batch.WorldName}:{group.Key}",
                FormatPlannedListingItem(group.First()),
                group.OrderBy(listing => listing.UnitPrice).ToArray()))
            .OrderByDescending(group => group.Gil)
            .ThenBy(group => group.Item, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!ImGui.BeginTable($"PlanStopItems##{batch.WorldName}", 5,
                DalamudNestedTableFlags.Parent()))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthFixed, 220);
        ImGui.TableSetupColumn("Listings", ImGuiTableColumnFlags.WidthFixed, 72);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 72);
        ImGui.TableSetupColumn("Unit", ImGuiTableColumnFlags.WidthFixed, 140);
        ImGui.TableSetupColumn("Gil", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableHeadersRow();

        foreach (var group in groups)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            expandedItems.DrawToggle(group.Key);
            ImGui.SameLine();
            ImGui.TextUnformatted(group.Item);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(group.Listings.Count.ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(group.Quantity.ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(group.MinUnit == group.MaxUnit
                ? FormatGil(group.MinUnit)
                : $"{group.MinUnit:N0}-{group.MaxUnit:N0} gil");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatGil(group.Gil));

            expandedItems.DrawDetail(group.Key, () => DrawItemListings(group));
        }

        ImGui.EndTable();
    }

    private static void DrawItemListings(PlanItemGroup group)
    {
        if (!ImGui.BeginTable($"PlanItemListings##{group.Key}", 5,
                DalamudNestedTableFlags.Child))
            return;

        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 56);
        ImGui.TableSetupColumn("Unit", ImGuiTableColumnFlags.WidthFixed, 96);
        ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthFixed, 160);

        ImGui.TableHeadersRow();

        foreach (var listing in group.Listings)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(listing.Quantity.ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatGil(listing.UnitPrice));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatGil(listing.TotalGil));
            ImGui.TableNextColumn();
            ImGui.TextColored(listing.IsHq ? MarketMafiosoUiTheme.Success : MarketMafiosoUiTheme.Muted, listing.IsHq ? "HQ" : "NQ");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(listing.RetainerName) ? "-" : listing.RetainerName);
            if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(listing.ListingId))
                ImGui.SetTooltip($"Listing {listing.ListingId}");

        }

        ImGui.EndTable();
    }

    private static string FormatPlannedListingItem(MarketAcquisitionPlannedListing listing) =>
        string.IsNullOrWhiteSpace(listing.ItemName)
            ? "Unavailable item"
            : listing.ItemName;

    private static string FormatGil(uint gil) => $"{gil:N0} gil";

    private static string FormatRouteDataCenter(string dataCenter) =>
        string.IsNullOrWhiteSpace(dataCenter) ? "-" : dataCenter;

    private static string FormatWorldMode(string worldMode) =>
        worldMode switch
        {
            "AllWorldSweep" => "All-world sweep",
            "CurrentWorldOnly" => "Current world only",
            _ => worldMode,
        };
}
