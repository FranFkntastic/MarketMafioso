using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Windows.Main;

namespace MarketMafioso.Windows.MarketAcquisitionPanels;

internal sealed class MarketAcquisitionPlanPanel
{
    private sealed record AdvisoryPlanRow(
        int RouteOrdinal,
        string Item,
        string World,
        string DataCenter,
        uint Quantity,
        uint Gil,
        uint Unit,
        bool IsHq,
        string Listing,
        bool ExceedsRequestedQuantity);

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

        if (plan.WorldBatches.Count == 0)
            return;

        if (!ImGui.CollapsingHeader("World Listings##MarketAcquisitionPlanWorldListings"))
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Expand to inspect advisory listings. Live market-board rows remain authoritative.");
            return;
        }

        var tableFlags =
            ImGuiTableFlags.Borders |
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.Resizable |
            ImGuiTableFlags.ScrollX |
            ImGuiTableFlags.Sortable;
        if (ImGui.BeginTable("MarketAcquisitionPlanBatches", 8, tableFlags))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort);
            ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableSetupColumn("Data Center", ImGuiTableColumnFlags.WidthFixed, 96);
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 72);
            ImGui.TableSetupColumn("Gil", ImGuiTableColumnFlags.WidthFixed, 96);
            ImGui.TableSetupColumn("Unit", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, 48);
            ImGui.TableSetupColumn("Listing", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            var rows = SortAdvisoryPlanRows(BuildAdvisoryPlanRows(plan), ImGui.TableGetSortSpecs());
            foreach (var row in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Item);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.World);
                if (row.ExceedsRequestedQuantity)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(MarketMafiosoUiTheme.Muted, "(over)");
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatRouteDataCenter(row.DataCenter));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Quantity.ToString("N0"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatGil(row.Gil));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatGil(row.Unit));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.IsHq ? "HQ" : "NQ");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Listing);
            }

            ImGui.EndTable();
        }
    }

    private static string FormatPlannedListingItem(MarketAcquisitionPlannedListing listing) =>
        string.IsNullOrWhiteSpace(listing.ItemName)
            ? $"Item {listing.ItemId}"
            : $"{listing.ItemName} ({listing.ItemId})";

    private static IReadOnlyList<AdvisoryPlanRow> BuildAdvisoryPlanRows(MarketAcquisitionPlan plan)
    {
        var rows = new List<AdvisoryPlanRow>();
        var ordinal = 0;
        foreach (var batch in plan.WorldBatches)
        {
            foreach (var listing in batch.Listings)
            {
                rows.Add(new AdvisoryPlanRow(
                    ordinal++,
                    FormatPlannedListingItem(listing),
                    batch.WorldName,
                    batch.DataCenter,
                    listing.Quantity,
                    listing.TotalGil,
                    listing.UnitPrice,
                    listing.IsHq,
                    $"{listing.RetainerName} / {listing.ListingId}",
                    batch.ExceedsRequestedQuantity));
            }
        }

        return rows;
    }

    private static IReadOnlyList<AdvisoryPlanRow> SortAdvisoryPlanRows(
        IReadOnlyList<AdvisoryPlanRow> rows,
        ImGuiTableSortSpecsPtr sortSpecs)
    {
        if (sortSpecs.SpecsCount == 0)
            return rows.OrderBy(row => row.RouteOrdinal).ToList();

        var spec = sortSpecs.Specs;
        return spec.ColumnIndex switch
        {
            0 => SortAdvisoryPlanRowsBy(rows, row => row.Item, spec.SortDirection),
            1 => SortAdvisoryPlanRowsBy(rows, row => row.World, spec.SortDirection),
            2 => SortAdvisoryPlanRowsBy(rows, row => row.DataCenter, spec.SortDirection),
            3 => SortAdvisoryPlanRowsBy(rows, row => row.Quantity, spec.SortDirection),
            4 => SortAdvisoryPlanRowsBy(rows, row => row.Gil, spec.SortDirection),
            5 => SortAdvisoryPlanRowsBy(rows, row => row.Unit, spec.SortDirection),
            6 => SortAdvisoryPlanRowsBy(rows, row => row.IsHq, spec.SortDirection),
            7 => SortAdvisoryPlanRowsBy(rows, row => row.Listing, spec.SortDirection),
            _ => rows.OrderBy(row => row.RouteOrdinal).ToList(),
        };
    }

    private static IReadOnlyList<AdvisoryPlanRow> SortAdvisoryPlanRowsBy<TKey>(
        IReadOnlyList<AdvisoryPlanRow> rows,
        Func<AdvisoryPlanRow, TKey> keySelector,
        ImGuiSortDirection direction)
    {
        var ordered = direction == ImGuiSortDirection.Descending
            ? rows.OrderByDescending(keySelector).ThenBy(row => row.RouteOrdinal)
            : rows.OrderBy(keySelector).ThenBy(row => row.RouteOrdinal);

        return ordered.ToList();
    }

    private static string FormatGil(uint gil) => $"{gil:N0} gil";

    private static string FormatRouteDataCenter(string dataCenter) =>
        string.IsNullOrWhiteSpace(dataCenter) ? "-" : dataCenter;

    private static string FormatWorldMode(string worldMode) =>
        string.Equals(worldMode, "AllWorlds", StringComparison.OrdinalIgnoreCase) ? "All worlds" : worldMode;
}
