using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using MarketMafioso.RetainerRestock;
using MarketMafioso.Windows.Main;
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Windows.RetainerRestock;

internal sealed class RetainerRestockTabPanel
{
    private readonly Configuration config;
    private readonly InventoryScanner scanner;
    private readonly Func<RetainerOwnerScope> getOwnerScope;
    private readonly RetainerRestockBrowserState browserState = new();
    private readonly RetainerRestockBrowserPanel browser;
    private readonly RetainerRestockControlsPanel controls;
    private string? appliedRequestedBrowserView;

    public RetainerRestockTabPanel(
        Configuration config,
        InventoryScanner scanner,
        AutoRetainerRefreshService autoRetainerRefresh,
        WorkshopRetainerRestockService workshopRetainerRestock,
        Func<RetainerOwnerScope> getOwnerScope)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        this.getOwnerScope = getOwnerScope ?? throw new ArgumentNullException(nameof(getOwnerScope));
        browser = new RetainerRestockBrowserPanel(config, browserState, config.Save);
        controls = new RetainerRestockControlsPanel(config, autoRetainerRefresh, workshopRetainerRestock);
    }

    public void Draw(string? requestedView = null)
    {
        UtilityWorkspaceUi.DrawModuleHeader("Retainers", "Move items between your character and the retainers that can accept or supply them.");

        var ownerScope = getOwnerScope();
        var playerBags = scanner.ScanPlayerInventory(config);
        var plan = BuildPlan(playerBags, ownerScope);
        var depositPlan = ElementalDepositPlanner.Build(
            scanner.CountPlayerCrystals(),
            config,
            ownerScope,
            scanner.ResolveItemName,
            DateTime.UtcNow);
        var browseProjection = RetainerRestockStockCatalog.BuildBrowseProjection(
            playerBags,
            config,
            ownerScope);

        var summary = RetainerRestockWorkspaceSummary.Build(
            plan,
            ownerScope,
            config.RetainerCache.Values,
            browseProjection.ItemGroups.Count);
        if (requestedView is "Browse stock" or "Browse listings")
        {
            if (!string.Equals(requestedView, appliedRequestedBrowserView, StringComparison.Ordinal))
            {
                browserState.SelectMode(string.Equals(requestedView, "Browse listings", StringComparison.Ordinal)
                    ? RetainerBrowseQueryMode.Listings
                    : RetainerBrowseQueryMode.Items);
                appliedRequestedBrowserView = requestedView;
            }
        }
        else
        {
            appliedRequestedBrowserView = null;
        }

        if (!ImGui.BeginTabBar("##retainerRestockWorkspace"))
            return;

        if (ImGui.BeginTabItem("Overview", GetRequestedViewFlags("Overview", requestedView)))
        {
            DrawOverview(summary, plan, depositPlan, ownerScope);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Browse stock", GetRequestedViewFlags("Browse stock", requestedView, "Browse listings")))
        {
            browser.DrawBrowse(browseProjection, MarketMafiosoUiTheme.Header, MarketMafiosoUiTheme.Muted);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Quick deposit", GetRequestedViewFlags("Quick deposit", requestedView)))
        {
            DrawQuickDeposit(depositPlan);
            ImGui.EndTabItem();
        }

        var planLabel = config.RetainerRestockPlanItems.Count == 0
            ? "Withdrawal plan"
            : $"Withdrawal plan ({config.RetainerRestockPlanItems.Count})";
        if (ImGui.BeginTabItem(planLabel, GetRequestedViewFlags("Withdrawal plan", requestedView)))
        {
            browser.DrawPlan(
                plan,
                MarketMafiosoUiTheme.Header,
                MarketMafiosoUiTheme.Success,
                MarketMafiosoUiTheme.Error,
                MarketMafiosoUiTheme.Muted);
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawOverview(
        RetainerRestockWorkspaceSummary summary,
        RetainerRestockPlan plan,
        ElementalDepositPlan depositPlan,
        RetainerOwnerScope ownerScope)
    {
        var depositReadiness = depositPlan.CanRun ? "Ready" : "Not ready";
        var withdrawalReadiness = summary.ReadyLineCount > 0 ? "Ready" : "Not ready";
        UtilityWorkspaceUi.DrawStatusStrip(
            "##retainerRestockOverviewStatus",
            [
                new("Active character", summary.Owner),
                new("Accessible stock", $"{summary.AccessibleItemCount:N0} item types", summary.AccessibleItemCount > 0 ? MarketMafiosoUiTheme.Header : MarketMafiosoUiTheme.Muted),
                new("Deposit", $"{depositReadiness}; {depositPlan.PlannedQuantity:N0} units", depositPlan.CanRun ? MarketMafiosoUiTheme.Success : MarketMafiosoUiTheme.Muted),
                new("Withdrawal", $"{withdrawalReadiness}; {summary.UnitsToRetrieve:N0} units", summary.ReadyLineCount > 0 ? MarketMafiosoUiTheme.Success : MarketMafiosoUiTheme.Muted),
                new("Observed retainers", summary.ObservedRetainerCount == 0 ? "None observed" : $"{summary.ObservedRetainerCount:N0} retainers", summary.ObservedRetainerCount > 0 ? MarketMafiosoUiTheme.Header : MarketMafiosoUiTheme.Muted),
            ]);
        ImGui.Spacing();
        ImGui.TextWrapped("Normal retainer interactions are observed, and live retainer identity and inventory are verified before transfer.");
        ImGui.Spacing();
        controls.Draw(plan, depositPlan, ownerScope);
    }

    private static void DrawQuickDeposit(ElementalDepositPlan plan)
    {
        if (plan.PlayerQuantity == 0)
        {
            ImGui.TextColored(MarketMafiosoUiTheme.Muted, "You are not carrying any elemental shards or crystals.");
            return;
        }

        if (plan.UnknownCrystalCacheCount > 0)
        {
            ImGui.TextColored(
                MarketMafiosoUiTheme.Warning,
                $"{plan.UnknownCrystalCacheCount} retainer observation entr{(plan.UnknownCrystalCacheCount == 1 ? "y needs" : "ies need")} a crystal capacity scan. Quick deposit will live-check them before moving anything.");
        }

        ImGui.TextColored(
            plan.UnplannedQuantity > 0 ? MarketMafiosoUiTheme.Warning : MarketMafiosoUiTheme.Muted,
            plan.UnplannedQuantity > 0
                ? $"Observed capacity covers {plan.PlannedQuantity:N0} of {plan.PlayerQuantity:N0} carried units; {plan.UnplannedQuantity:N0} will remain."
                : $"Quick deposit will live-check and move all {plan.PlayerQuantity:N0} carried units through {plan.Candidates.Count:N0} retainer(s) as needed.");
        ImGui.TextColored(MarketMafiosoUiTheme.Muted, "The 9,999 carry and retainer limits apply separately to each elemental type.");
        ImGui.Spacing();

        var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##elementalDepositPlan", 4, flags))
            return;

        ImGui.TableSetupColumn("Element", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("Carried", ImGuiTableColumnFlags.WidthFixed, 82);
        ImGui.TableSetupColumn("Space", ImGuiTableColumnFlags.WidthFixed, 92);
        ImGui.TableSetupColumn("Up to", ImGuiTableColumnFlags.WidthFixed, 82);
        ImGui.TableHeadersRow();
        foreach (var line in plan.Lines)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.ItemName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(line.PlayerQuantity.ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(plan.UnknownCrystalCacheCount > 0 ? "Live check" : line.PotentialCapacity.ToString("N0"));
            ImGui.TableNextColumn();
            ImGui.TextColored(
                line.UnplannedQuantity > 0 ? MarketMafiosoUiTheme.Warning : MarketMafiosoUiTheme.Success,
                line.PlannedQuantity.ToString("N0"));
        }

        ImGui.EndTable();
    }

    private static ImGuiTabItemFlags GetRequestedViewFlags(
        string viewName,
        string? requestedView,
        params string[] aliases) =>
        string.Equals(viewName, requestedView, StringComparison.Ordinal) ||
        aliases.Any(alias => string.Equals(alias, requestedView, StringComparison.Ordinal))
            ? ImGuiTabItemFlags.SetSelected
            : ImGuiTabItemFlags.None;

    private RetainerRestockPlan BuildPlan(IReadOnlyList<InventoryBag> playerBags, RetainerOwnerScope ownerScope)
    {
        var playerInventory = playerBags
            .SelectMany(bag => bag.Items)
            .Where(item => item.ItemId > 0 && item.Quantity > 0)
            .GroupBy(item => item.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(item => (int)item.Quantity));

        return RetainerRestockPlanner.BuildPlan(
            config.RetainerRestockPlanItems,
            playerInventory,
            config,
            DateTime.UtcNow,
            ownerScope);
    }
}
