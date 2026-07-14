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
        UtilityWorkspaceUi.DrawModuleHeader("Restock", "Build a local plan and pull matching items from cached retainers.");

        var ownerScope = getOwnerScope();
        var playerBags = scanner.ScanPlayerInventory(config);
        var plan = BuildPlan(playerBags, ownerScope);
        var stockRows = RetainerRestockStockCatalog.Build(
            playerBags,
            config,
            DateTime.UtcNow,
            ownerScope);

        var summary = RetainerRestockWorkspaceSummary.Build(plan, ownerScope, config.RetainerCache.Values);
        UtilityWorkspaceUi.DrawStatusStrip(
            "##retainerRestockStatus",
            [
                new("Character", summary.Owner),
                new("Plan", $"{summary.ReadyLineCount}/{summary.PlanLineCount} lines ready", summary.ReadyLineCount > 0 ? MarketMafiosoUiTheme.Success : MarketMafiosoUiTheme.Muted),
                new("Retrieve", $"{summary.UnitsToRetrieve:N0} units"),
                new("Unresolved", $"{summary.MissingUnits:N0} units", summary.MissingUnits > 0 ? MarketMafiosoUiTheme.Warning : MarketMafiosoUiTheme.Success),
                new("Retainer cache", summary.CachedRetainerCount == 0 ? "No cached retainers" : $"{summary.CachedRetainerCount} retainers; newest {summary.NewestCacheUtc:HH:mm} UTC", summary.CachedRetainerCount > 0 ? MarketMafiosoUiTheme.Header : MarketMafiosoUiTheme.Muted),
            ]);
        ImGui.Spacing();
        controls.Draw(plan, ownerScope);
        ImGui.Spacing();

        if (!ImGui.BeginTabBar("##retainerRestockWorkspace"))
            return;

        if (ImGui.BeginTabItem("Browse stock", GetRequestedViewFlags("Browse stock", requestedView)))
        {
            browser.DrawBrowse(stockRows, MarketMafiosoUiTheme.Header, MarketMafiosoUiTheme.Muted);
            ImGui.EndTabItem();
        }

        var planLabel = config.RetainerRestockPlanItems.Count == 0
            ? "Plan and run"
            : $"Plan and run ({config.RetainerRestockPlanItems.Count})";
        if (ImGui.BeginTabItem(planLabel, GetRequestedViewFlags("Plan and run", requestedView)))
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

    private static ImGuiTabItemFlags GetRequestedViewFlags(string viewName, string? requestedView) =>
        string.Equals(viewName, requestedView, StringComparison.Ordinal)
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
