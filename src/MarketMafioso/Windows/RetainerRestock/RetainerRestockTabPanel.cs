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

    public void Draw()
    {
        ImGui.Spacing();
        ImGui.TextColored(MarketMafiosoUiTheme.Header, "Restock");
        ImGui.TextWrapped("Build a local plan and pull matching items from cached retainers.");
        ImGui.Spacing();

        var ownerScope = getOwnerScope();
        var playerBags = scanner.ScanPlayerInventory(config);
        var plan = BuildPlan(playerBags, ownerScope);
        var stockRows = RetainerRestockStockCatalog.Build(
            playerBags,
            config,
            DateTime.UtcNow,
            ownerScope);

        browser.Draw(
            stockRows,
            plan,
            MarketMafiosoUiTheme.Header,
            MarketMafiosoUiTheme.Success,
            MarketMafiosoUiTheme.Error,
            MarketMafiosoUiTheme.Muted);
        ImGui.Spacing();
        controls.Draw(plan, ownerScope);
    }

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
