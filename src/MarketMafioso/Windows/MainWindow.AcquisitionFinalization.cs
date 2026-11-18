using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using MarketMafioso.MarketAcquisition;
using Lumina.Excel.Sheets;

namespace MarketMafioso.Windows;

public partial class MainWindow
{
    private MarketAcquisitionPlan? acquisitionFinalizationPlan;
    private string? acquisitionFinalizationHash;
    private bool acquisitionFinalizationLoading;
    private bool acquisitionFinalizationOpen;
    private readonly List<(string[] Cells, string? Warning)> acquisitionFinalizationRows = [];
    private string acquisitionFinalizationFooter = "No data";

    private async Task OpenAcquisitionFinalizationAsync()
    {
        if (acquisitionFinalizationLoading || routeEngine.IsRouteActive || acquisitionWorkspace.IsBusy) return;
        acquisitionFinalizationLoading = true;
        try
        {
            await acquisitionRequestBuilder.WaitForRefreshAsync();
            var document = acquisitionRequestBuilder.CurrentDocument;
            var result = await acquisitionWorkspace.PreviewPlanAsync(document, acquisitionServices.GetCurrentWorldName(),
                acquisitionServices.GetMarketAcquisitionRecentWorldTtl(), config.MarketAcquisitionIgnoreRecentWorldVisitsForSweep);
            acquisitionFinalizationPlan = result.Plan;
            acquisitionFinalizationHash = MarketAcquisitionRequestDocumentHasher.ComputeIntentHash(document with { RunSpendCap = 0 });
            acquisitionFinalizationOpen = true;
        }
        catch (Exception ex) { acquisitionRequestBuilder.SetStatus($"Cannot review plan: {ex.Message}"); }
        finally { acquisitionFinalizationLoading = false; }
    }

    private void BuildAcquisitionFinalizationSummary(MarketAcquisitionPlan plan)
    {
        acquisitionFinalizationRows.Clear();
        decimal worstTotal = 0;
        ulong stacks = 0, crystals = 0;
        var uncapped = 0;
        var slotsKnown = true;
        var sheet = dataManager.GetExcelSheet<Item>();
        foreach (var line in plan.Lines)
        {
            var stock = line.QuantityMode == "TargetQuantity";
            var need = stock ? line.RequestedQuantity - Math.Min(line.RequestedQuantity, line.InitialOnHandQuantity) : 0;
            var listings = plan.AdvisoryListings?.Where(l => l.ItemId == line.ItemId && l.Quantity > 0 && l.UnitPrice > 0 &&
                MarketAcquisitionPolicy.HqMatches(line.HqPolicy, l.IsHq)).OrderBy(l => l.UnitPrice).ToArray();
            var known = listings is { Length: > 0 };
            var supplyKnown = plan.AdvisoryListings is not null;
            decimal median = 0;
            ulong supply = 0;
            if (known)
            {
                var units = listings!.Aggregate(0UL, (sum, l) => sum + l.Quantity);
                ulong seen = 0;
                uint? lower = null;
                foreach (var listing in listings!)
                {
                    seen += listing.Quantity;
                    if (seen >= (units + 1) / 2) lower ??= listing.UnitPrice;
                    if (seen >= units / 2 + 1) { median = ((decimal)lower!.Value + listing.UnitPrice) / 2; break; }
                }
                supply = listings!.Where(l => l.UnitPrice <= line.MaxUnitPrice).Aggregate(0UL, (sum, l) => sum + l.Quantity);
            }
            decimal? worst = stock && line.MaxUnitPrice > 0 ? ((decimal)need + (need > 0 ? line.MaximumOverage : 0)) * line.MaxUnitPrice : null;
            if (line.GilCap > 0) worst = Math.Min(worst ?? decimal.MaxValue, line.GilCap);
            if (worst.HasValue) worstTotal += worst.Value; else uncapped++;
            var warning = known && median > 0 && line.MaxUnitPrice > 3 * median ? "Ceiling exceeds 3x regional unit-weighted median; check for a typo." : null;
            if (supplyKnown && stock && supply < need) warning = (warning is null ? "" : warning + "\n") + "Advisory supply under the ceiling cannot fill the remaining target.";
            var tier = "No data";
            if (known && !stock)
            {
                var eligible = listings!.Where(l => l.UnitPrice <= line.MaxUnitPrice).ToArray();
                var low = eligible.Where(l => l.UnitPrice * 2UL <= median * 3).ToArray();
                var high = eligible.Where(l => l.UnitPrice * 2UL > median * 3).ToArray();
                ulong Units(IEnumerable<MarketAcquisitionListing> ls) => ls.Aggregate(0UL, (s, l) => s + l.Quantity);
                ulong Gil(IEnumerable<MarketAcquisitionListing> ls) => ls.Aggregate(0UL, (s, l) => s + (ulong)l.Quantity * l.UnitPrice);
                tier = $"{Units(low):N0} at <= {median * 1.5m:N0} ({Gil(low):N0} gil)";
                if (high.Length > 0) tier += $" / {Units(high):N0} at {high.Min(l => l.UnitPrice):N0}-{high.Max(l => l.UnitPrice):N0} ({Gil(high):N0} gil; +{Gil(high) - Units(high) * median:N0})";
            }
            acquisitionFinalizationRows.Add(([
                line.ItemName ?? "No data", stock ? $"Stock up to {line.RequestedQuantity:N0}" : "Buy below",
                line.MaxUnitPrice == 0 ? "Unset" : $"{line.MaxUnitPrice:N0}" + (known ? $" · {line.MaxUnitPrice / median:0.0}x" : ""),
                known ? $"{median:N0}" : "No data",
                supplyKnown ? stock && supply >= need ? "Fills" : stock ? $"{supply:N0} of {need:N0}" : $"{supply:N0}" : "No data",
                worst.HasValue ? $"{worst:N0}" : "Uncapped", stock ? "—" : tier], warning));
            if (stock && need > 0)
            {
                if (line.ItemId is >= 2 and <= 19) crystals += need;
                else if (sheet.GetRowOrDefault(line.ItemId) is { StackSize: > 0 } item) stacks += ((ulong)need + item.StackSize - 1) / item.StackSize;
                else slotsKnown = false;
            }
        }
        var capacity = new DalamudMarketAcquisitionInventoryObserver(dataManager).Capacity(1, false);
        acquisitionFinalizationFooter = $"Worst case {worstTotal:N0} gil + {uncapped:N0} uncapped lines · {plan.WorldBatches.Count:N0} worlds\n" +
            $"Bag stacks needed: {(slotsKnown ? stacks.ToString("N0") : "No data")} / free slots: {(capacity.IsAvailable ? capacity.FreeSlots.ToString("N0") : "No data")} · Crystal pouch: {crystals:N0} units";
    }

    private void DrawAcquisitionFinalizationModal()
    {
        const string popup = "Finalize acquisition plan";
        if (acquisitionFinalizationLoading) ImGui.TextColored(ColMuted, "Verifying plan...");
        if (acquisitionFinalizationOpen)
        {
            BuildAcquisitionFinalizationSummary(acquisitionFinalizationPlan!);
            ImGui.OpenPopup(popup);
            acquisitionFinalizationOpen = false;
        }
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(1100, 0), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal(popup, ImGuiWindowFlags.AlwaysAutoResize)) return;
        if (ImGui.BeginTable("##FinalizeSummary", 7, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new(0, 320)))
        {
            foreach (var title in new[] { "Item", "Rule", "Ceiling / median", "Median", "Supply", "Worst gil", "Price tiers" }) ImGui.TableSetupColumn(title);
            ImGui.TableHeadersRow();
            foreach (var row in acquisitionFinalizationRows)
            {
                ImGui.TableNextRow();
                foreach (var cell in row.Cells)
                {
                    ImGui.TableNextColumn();
                    if (row.Warning is not null) ImGui.TextColored(ColWarning, cell); else ImGui.TextUnformatted(cell);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(row.Warning ?? "Regional advisory listings; unit-weighted median. Not a live purchase quote.");
                }
            }
            ImGui.EndTable();
        }
        ImGui.TextUnformatted(acquisitionFinalizationFooter);
        acquisitionRequestBuilder.DrawRunBudgetInput();
        var unchanged = acquisitionFinalizationHash == MarketAcquisitionRequestDocumentHasher.ComputeIntentHash(acquisitionRequestBuilder.CurrentDocument with { RunSpendCap = 0 });
        ImGui.BeginDisabled(!unchanged || routeEngine.IsRouteActive || acquisitionWorkspace.IsBusy);
        if (ImGui.Button("Confirm")) { ImGui.CloseCurrentPopup(); _ = FinalizeMarketAcquisitionPlanAsync(); }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Back")) { acquisitionFinalizationPlan = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }
}
