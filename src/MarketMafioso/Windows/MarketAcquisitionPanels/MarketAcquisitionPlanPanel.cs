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

    // Any projection drives Franthropy's detail rows; the row type is irrelevant here.
    private static readonly DalamudTableProjection<MarketAcquisitionWorldBatch> detailLayout = new(
        [new DalamudTableColumn<MarketAcquisitionWorldBatch>("World", 120, batch => batch.WorldName)]);

    private readonly HashSet<string> expandedStops = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> expandedItems = new(StringComparer.OrdinalIgnoreCase);

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

        if (!ImGui.BeginTable("MarketAcquisitionPlanRouteStopTable", 5,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit))
            return;

        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 56);
        ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, 160);
        ImGui.TableSetupColumn("Data Center", ImGuiTableColumnFlags.WidthFixed, 96);
        ImGui.TableSetupColumn("Items", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
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
                DrawExpander(expandedStops, stopKey, $"plan-stop-{stopKey}");
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
            ImGui.TextUnformatted(FormatGil((uint)Math.Min(batch.EstimatedValueGil, uint.MaxValue)));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(batch.EstimatedValueGil == batch.PlannedGil
                    ? plan.Diagnostics.RouteOrderingBasis
                    : $"Planned {batch.PlannedGil:N0} gil; buy-below listings under ceiling add {batch.EstimatedValueGil - Math.Min(batch.EstimatedValueGil, batch.PlannedGil):N0} gil.\n{plan.Diagnostics.RouteOrderingBasis}");

            if (expandedStops.Contains(stopKey) && batch.Listings.Count > 0)
                detailLayout.DrawDetailRow($"plan-stop-detail-{stopKey}", () => DrawStopItems(batch));
        }

        foreach (var decision in skipped.OrderByDescending(decision => decision.EstimatedValueGil))
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
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "—");
            ImGui.TableNextColumn();
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, FormatGil((uint)Math.Min(decision.EstimatedValueGil, uint.MaxValue)));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(decision.Reason);
        }

        ImGui.EndTable();
    }

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
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Listings", ImGuiTableColumnFlags.WidthFixed, 72);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 72);
        ImGui.TableSetupColumn("Unit", ImGuiTableColumnFlags.WidthFixed, 140);
        ImGui.TableSetupColumn("Gil", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableHeadersRow();

        foreach (var group in groups)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            DrawExpander(expandedItems, group.Key, $"plan-item-{group.Key}");
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

            if (expandedItems.Contains(group.Key))
                detailLayout.DrawDetailRow($"plan-item-detail-{group.Key}", () => DrawItemListings(group));
        }

        ImGui.EndTable();
    }

    private static void DrawItemListings(PlanItemGroup group)
    {
        if (!ImGui.BeginTable($"PlanItemListings##{group.Key}", 6,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            return;

        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 56);
        ImGui.TableSetupColumn("Unit", ImGuiTableColumnFlags.WidthFixed, 96);
        ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Reviewed", ImGuiTableColumnFlags.WidthFixed, 96);
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
            ImGui.TableNextColumn();
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, FormatReviewAge(listing.LastReviewTimeUtc));
        }

        ImGui.EndTable();
    }

    private static void DrawExpander(HashSet<string> expanded, string key, string id)
    {
        var isExpanded = expanded.Contains(key);
        if (!ImGui.SmallButton(isExpanded ? $"v##{id}" : $">##{id}"))
            return;
        if (isExpanded)
            expanded.Remove(key);
        else
            expanded.Add(key);
    }

    private static string FormatReviewAge(DateTimeOffset reviewed)
    {
        if (reviewed == default)
            return "-";
        var age = DateTimeOffset.UtcNow - reviewed;
        if (age < TimeSpan.FromMinutes(1))
            return "just now";
        if (age < TimeSpan.FromHours(1))
            return $"{(int)age.TotalMinutes}m ago";
        if (age < TimeSpan.FromDays(1))
            return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
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
