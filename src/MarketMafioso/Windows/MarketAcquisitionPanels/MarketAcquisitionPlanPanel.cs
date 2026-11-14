using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Franthropy.Dalamud.UI.Tables;
using MarketMafioso.MarketAcquisition;
using MarketMafioso.Windows.Main;

namespace MarketMafioso.Windows.MarketAcquisitionPanels;

internal sealed class MarketAcquisitionPlanPanel
{
    private const int DefaultCoverageSlots = 3;
    // Declared slot columns. Changing this changes the table layout: bump the table ID with it.
    private const int MaximumCoverageSlots = 8;
    private const int LeadingColumns = 6;
    private const float ItemCoverageWidth = 210;

    private static readonly DalamudProgressCellStyle CoverageStyle = new(
        Fill: MarketMafiosoUiTheme.Header,
        Complete: MarketMafiosoUiTheme.Success,
        Muted: MarketMafiosoUiTheme.Muted,
        Track: new Vector4(1f, 1f, 1f, 0.06f),
        Overflow: MarketMafiosoUiTheme.Warning,
        Shortfall: MarketMafiosoUiTheme.Error,
        Highlight: new Vector4(1f, 1f, 1f, 1f),
        Text: new Vector4(1f, 1f, 1f, 1f),
        MutedText: MarketMafiosoUiTheme.Muted);

    private enum CoverageRow
    {
        Route,
        Skipped,
    }

    private sealed record PlanItemGroup(string Key, string Item, IReadOnlyList<MarketAcquisitionPlannedListing> Listings)
    {
        public uint Quantity => (uint)Math.Min(Listings.Sum(listing => (long)listing.Quantity), uint.MaxValue);
        public uint Gil => (uint)Math.Min(Listings.Sum(listing => (long)listing.TotalGil), uint.MaxValue);
        public uint MinUnit => Listings.Min(listing => listing.UnitPrice);
        public uint MaxUnit => Listings.Max(listing => listing.UnitPrice);
    }

    private readonly DalamudExpandableRows expandedStops = new();
    private readonly DalamudExpandableRows expandedItems = new();
    private readonly DalamudCrossRowHighlight coverageHighlight = new();
    private readonly DalamudRankedColumns coverageColumns;
    private MarketAcquisitionPlan? coveragePlan;
    private MarketAcquisitionRouteCoverage? coverage;

    public MarketAcquisitionPlanPanel(Configuration config)
    {
        coverageColumns = new DalamudRankedColumns(DefaultCoverageSlots, MaximumCoverageSlots, slots =>
        {
            config.MarketAcquisitionCoverageColumns = slots;
            config.Save();
        })
        {
            PreferredSlots = config.MarketAcquisitionCoverageColumns,
        };
    }

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

    private MarketAcquisitionRouteCoverage ResolveCoverage(MarketAcquisitionPlan plan)
    {
        if (!ReferenceEquals(plan, coveragePlan) || coverage == null)
        {
            coverage = MarketAcquisitionRouteCoverage.Build(plan);
            coveragePlan = plan;
        }

        return coverage;
    }

    private void DrawRouteStops(MarketAcquisitionPlan plan)
    {
        var routeCoverage = ResolveCoverage(plan);
        var skipped = routeCoverage.Skipped;
        if (plan.WorldBatches.Count == 0 && skipped.Count == 0)
            return;

        var label = skipped.Count == 0
            ? $"Route Stops ({plan.WorldBatches.Count:N0})"
            : $"Route Stops ({plan.WorldBatches.Count:N0}, {skipped.Count:N0} skipped)";
        if (!ImGui.CollapsingHeader($"{label}##MarketAcquisitionPlanRouteStops", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        // The ID carries the column layout: ImGui reapplies saved widths by table ID and column index.
        // The column count is fixed (every ranked slot is declared); hidden slots are disabled, not removed.
        if (!ImGui.BeginTable("MarketAcquisitionPlanRouteStopTable##ranked-coverage-v1",
                LeadingColumns + coverageColumns.ColumnCount,
                DalamudNestedTableFlags.Parent(resizable: true) | ImGuiTableFlags.Hideable))
            return;

        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoHide, 56);
        ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoHide, 160);
        ImGui.TableSetupColumn("Data Center", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultHide, 96);
        ImGui.TableSetupColumn("Items", ImGuiTableColumnFlags.WidthFixed, 64);
        ImGui.TableSetupColumn("Gil", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoHide, 110);
        ImGui.TableSetupColumn("Savings", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoHide, 110);
        coverageColumns.SetupColumns(LeadingColumns, routeCoverage.RequiredSlots,
            rank => rank == 0 ? "Top coverage" : DalamudRankedColumns.Ordinal(rank + 1));
        coverageColumns.HeadersRow();

        for (var index = 0; index < plan.WorldBatches.Count; index++)
        {
            var batch = plan.WorldBatches[index];
            var bars = routeCoverage.Stops[index];
            var stopKey = batch.WorldName;
            ImGui.TableNextRow();
            ImGui.PushID($"stop:{index}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted((index + 1).ToString());
            ImGui.TableNextColumn();
            if (batch.Listings.Count > 0)
            {
                expandedStops.DrawToggle(stopKey);
                ImGui.SameLine();
            }

            ImGui.TextUnformatted(batch.WorldName);
            DataCenterTooltip(batch.DataCenter);
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
            ImGui.TextUnformatted(FormatSavings(batch.BuyBelowSavingsGil));
            if (ImGui.IsItemHovered() && batch.BuyBelowSavingsGil > 0)
                ImGui.SetTooltip("Savings under buy-below ceilings, including eligible advisory listings; not guaranteed purchases.");
            DrawCoverageSlots(bars, CoverageRow.Route);
            ImGui.PopID();

            if (batch.Listings.Count > 0)
                expandedStops.DrawDetail(stopKey, () => DrawStopItems(batch, bars, routeCoverage));
        }

        if (routeCoverage.Shortfalls.Count > 0)
            DrawShortfallRow(routeCoverage.Shortfalls);

        for (var index = 0; index < skipped.Count; index++)
        {
            var decision = skipped[index];
            ImGui.TableNextRow();
            ImGui.PushID($"skipped:{index}");
            ImGui.TableNextColumn();
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "Skipped");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(decision.Reason);
            ImGui.TableNextColumn();
            var dataCenter = MarketAcquisitionWorldCatalog.ResolveDataCenter(decision.WorldName);
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, decision.WorldName);
            DataCenterTooltip(dataCenter);
            ImGui.TableNextColumn();
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, FormatRouteDataCenter(dataCenter));
            ImGui.TableNextColumn();
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, decision.PlannedQuantity.ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, FormatGil(decision.PlannedGil));
            ImGui.TableNextColumn();
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, FormatSavings(decision.BuyBelowSavingsGil));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(decision.Reason);
            DrawCoverageSlots(routeCoverage.SkippedBars[index], CoverageRow.Skipped);
            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawCoverageSlots(IReadOnlyList<RouteCoverageBar> bars, CoverageRow row)
    {
        for (var rank = 0; rank < bars.Count && coverageColumns.SlotColumn(rank); rank++)
            DrawCoverageBar($"slot{rank}", bars[rank], row);

        if (coverageColumns.HandleCell(bars.Count))
            HiddenEntriesTooltip(bars.Skip(coverageColumns.VisibleSlots).ToArray(),
                (bar, width) => DrawCoverageBar("hidden", bar, row, width, interactive: false));
    }

    private void DrawShortfallRow(IReadOnlyList<RouteCoverageShortfall> shortfalls)
    {
        ImGui.TableNextRow();
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1,
            ImGui.GetColorU32(MarketMafiosoUiTheme.Error with { W = 0.07f }));
        ImGui.PushID("short-after");
        ImGui.TableSetColumnIndex(1);
        ImGui.TextColored(MarketMafiosoUiTheme.Error, "Short after");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Target items still under their need after every route stop, biggest gap first.");

        for (var rank = 0; rank < shortfalls.Count && coverageColumns.SlotColumn(rank); rank++)
            DrawShortfallBar($"slot{rank}", shortfalls[rank]);

        if (coverageColumns.HandleCell(shortfalls.Count))
            HiddenEntriesTooltip(shortfalls.Skip(coverageColumns.VisibleSlots).ToArray(),
                (shortfall, width) => DrawShortfallBar("hidden", shortfall, width, interactive: false));
        ImGui.PopID();
    }

    private void HiddenEntriesTooltip<T>(IReadOnlyList<T> hidden, Action<T, float> draw)
    {
        const float width = 240;
        ImGui.BeginTooltip();
        for (var index = 0; index < hidden.Count; index++)
        {
            ImGui.PushID(index);
            draw(hidden[index], width);
            ImGui.PopID();
        }

        if (hidden.Count > 0 && (coverageColumns.CanReveal || coverageColumns.CanCollapse))
            ImGui.Spacing();
        coverageColumns.HandleHint();
        ImGui.EndTooltip();
    }

    private void DrawCoverageBar(string id, RouteCoverageBar bar, CoverageRow row, float width = 0, bool interactive = true)
    {
        var flags = DalamudProgressCellFlags.IconSlot;
        if (row == CoverageRow.Skipped)
            flags |= DalamudProgressCellFlags.Muted;
        if (bar.Complete)
            flags |= DalamudProgressCellFlags.Complete;
        if (bar.Overflow)
            flags |= DalamudProgressCellFlags.Overflow;
        if (coverageHighlight.IsHighlighted(bar.LineId))
            flags |= DalamudProgressCellFlags.Highlighted;

        var cell = new DalamudProgressCell(bar.ItemName, FormatShare(bar.Share), bar.PriorFraction,
            bar.CumulativeFraction - bar.PriorFraction)
        {
            Flags = flags,
            Icon = MarketMafiosoItemIcons.Handle(bar.ItemId),
        };
        if (!DalamudProgressCellRenderer.Draw(id, cell, CoverageStyle, width) || !interactive)
            return;

        coverageHighlight.MarkHovered(bar.LineId);
        ImGui.BeginTooltip();
        ImGui.TextColored(MarketMafiosoUiTheme.Header, bar.ItemName);
        if (BeginFigures())
        {
            if (row == CoverageRow.Skipped)
            {
                Figure("Route plans", $"{bar.PriorQuantity:N0} / {bar.Need:N0}  ({FormatPercent(bar.PriorFraction)})");
                Figure("This stop would add", $"{bar.StopQuantity:N0}  ({FormatPercent(bar.Share)})");
            }
            else
            {
                Figure("This stop", $"{bar.StopQuantity:N0}  ({FormatPercent(bar.Share)})");
                if (bar.PriorQuantity > 0)
                    Figure("Earlier stops", $"{bar.PriorQuantity:N0}  ({FormatPercent(bar.PriorFraction)})");
                Figure("After this stop", $"{bar.CumulativeQuantity:N0} / {bar.Need:N0}  ({FormatPercent(bar.CumulativeFraction)})",
                    bar.Complete ? MarketMafiosoUiTheme.Success : null);
                if (bar.Overflow)
                    Figure("Past need", $"+{bar.CumulativeQuantity - bar.Need:N0}", MarketMafiosoUiTheme.Warning);
            }

            ImGui.EndTable();
        }

        ImGui.EndTooltip();
    }

    private void DrawShortfallBar(string id, RouteCoverageShortfall shortfall, float width = 0, bool interactive = true)
    {
        var flags = DalamudProgressCellFlags.IconSlot | DalamudProgressCellFlags.Shortfall;
        if (coverageHighlight.IsHighlighted(shortfall.LineId))
            flags |= DalamudProgressCellFlags.Highlighted;

        var cell = new DalamudProgressCell(shortfall.ItemName, FormatShare(shortfall.Fraction), 0, shortfall.Fraction)
        {
            Flags = flags,
            Icon = MarketMafiosoItemIcons.Handle(shortfall.ItemId),
        };
        if (!DalamudProgressCellRenderer.Draw(id, cell, CoverageStyle, width) || !interactive)
            return;

        coverageHighlight.MarkHovered(shortfall.LineId);
        ImGui.BeginTooltip();
        ImGui.TextColored(MarketMafiosoUiTheme.Header, shortfall.ItemName);
        if (BeginFigures())
        {
            Figure("Route plans", $"{shortfall.Planned:N0} / {shortfall.Need:N0}  ({FormatPercent(shortfall.Fraction)})");
            Figure("Short", $"{shortfall.Gap:N0}", MarketMafiosoUiTheme.Error);
            ImGui.EndTable();
        }

        ImGui.EndTooltip();
    }

    private static bool BeginFigures()
    {
        if (!ImGui.BeginTable("##coverage-figures", 2, ImGuiTableFlags.SizingFixedFit))
            return false;
        ImGui.TableSetupColumn("label");
        ImGui.TableSetupColumn("value");
        return true;
    }

    private static void Figure(string label, string value, Vector4? color = null)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, label);
        ImGui.TableNextColumn();
        if (color is { } tint)
            ImGui.TextColored(tint, value);
        else
            ImGui.TextUnformatted(value);
    }

    private static void DataCenterTooltip(string dataCenter)
    {
        if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(dataCenter))
            ImGui.SetTooltip(dataCenter);
    }

    // Floor so "100%" only ever means the need is met; tiny shares stay visible as "<1%".
    private static string FormatShare(double share) =>
        share >= 1 ? "100%"
        : share <= 0 ? "0%"
        : share < 0.01 ? "<1%"
        : $"{Math.Floor(share * 100):0}%";

    private static string FormatPercent(double fraction) => $"{fraction * 100:0.##}%";

    private static string FormatSavings(ulong gil) => gil > 0 ? $"{gil:N0} gil" : "-";

    private void DrawStopItems(MarketAcquisitionWorldBatch batch, IReadOnlyList<RouteCoverageBar> bars,
        MarketAcquisitionRouteCoverage routeCoverage)
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

        // New ID for the added Coverage column; the old 5-column widths must not be reapplied.
        if (!ImGui.BeginTable($"PlanStopItems##coverage-v1:{batch.WorldName}", 6,
                DalamudNestedTableFlags.Parent()))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthFixed, 220);
        ImGui.TableSetupColumn("Listings", ImGuiTableColumnFlags.WidthFixed, 72);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 72);
        ImGui.TableSetupColumn("Unit", ImGuiTableColumnFlags.WidthFixed, 140);
        ImGui.TableSetupColumn("Gil", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("Coverage", ImGuiTableColumnFlags.WidthFixed, ItemCoverageWidth);
        ImGui.TableHeadersRow();

        foreach (var group in groups)
        {
            ImGui.TableNextRow();
            ImGui.PushID(group.Key);
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
            ImGui.TableNextColumn();
            DrawItemCoverage(group, bars, routeCoverage);
            ImGui.PopID();

            expandedItems.DrawDetail(group.Key, () => DrawItemListings(group));
        }

        ImGui.EndTable();
    }

    private void DrawItemCoverage(PlanItemGroup group, IReadOnlyList<RouteCoverageBar> bars,
        MarketAcquisitionRouteCoverage routeCoverage)
    {
        var lineIds = group.Listings.Select(listing => listing.LineId).ToHashSet(StringComparer.Ordinal);
        var bar = bars.FirstOrDefault(candidate => lineIds.Contains(candidate.LineId));
        if (bar != null)
        {
            DrawCoverageBar("coverage", bar, CoverageRow.Route);
            return;
        }

        var savings = group.Listings
            .Where(listing => routeCoverage.IsBuyBelow(listing.LineId))
            .Aggregate(0UL, (total, listing) =>
            {
                var ceiling = routeCoverage.Lines[listing.LineId].MaxUnitPrice;
                return total + (ulong)(ceiling - Math.Min(ceiling, listing.UnitPrice)) * listing.Quantity;
            });
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, savings > 0 ? $"saves {savings:N0} gil" : "-");
        if (savings > 0 && ImGui.IsItemHovered())
            ImGui.SetTooltip("Below the buy-below ceiling across this item's planned listings at this stop.");
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
