# Restock Inventory Browser Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the manual Restock add-item popup with an extracted inventory-first browser that only shows accessible stock and requires explicit desired quantities.

**Architecture:** Add pure RetainerRestock catalog/state services first, then render them through a dedicated `RetainerRestockBrowserPanel`. `MainWindow` should keep top-level tab orchestration, retainer refresh/run controls, and owner scope lookup, while the new panel owns browser state, stock rows, staged quantity validation, and plan-row mutation.

**Tech Stack:** C# 12, Dalamud ImGui bindings, xUnit, existing `Configuration`, `InventoryBag`, `CachedRetainer`, and `RetainerRestockPlanner` models.

---

## File Structure

- Create `src/MarketMafioso/RetainerRestock/RetainerRestockStockCatalog.cs`
  - Pure stock aggregation from scanned player bags plus owner-scoped cached retainers.
  - No Dalamud services, no ImGui, no config saving.

- Create `src/MarketMafioso/Windows/RetainerRestock/RetainerRestockBrowserState.cs`
  - UI-neutral browser state and quantity validation helpers.

- Create `src/MarketMafioso/Windows/RetainerRestock/RetainerRestockBrowserPanel.cs`
  - ImGui panel for the two-pane stock browser and plan queue.
  - Mutates `config.RetainerRestockPlanItems` only through explicit add/update/remove actions.

- Modify `src/MarketMafioso/Windows/MainWindow.cs`
  - Remove old restock autocomplete fields and popup.
  - Instantiate and render `RetainerRestockBrowserPanel`.
  - Continue to build plans through `RetainerRestockPlanner` and run through `WorkshopRetainerRestockService`.

- Create `tests/MarketMafioso.Tests/RetainerRestock/RetainerRestockStockCatalogTests.cs`
  - Pure tests for accessible-stock aggregation, owner scoping, source totals, player-only rows, search ordering, and cache age.

- Create `tests/MarketMafioso.Tests/Windows/RetainerRestock/RetainerRestockBrowserStateTests.cs`
  - Pure tests for explicit quantity validation and staged plan mutation.

---

### Task 1: Stock Catalog Models And Aggregation

**Files:**
- Create: `src/MarketMafioso/RetainerRestock/RetainerRestockStockCatalog.cs`
- Test: `tests/MarketMafioso.Tests/RetainerRestock/RetainerRestockStockCatalogTests.cs`

- [ ] **Step 1: Write failing stock catalog tests**

Create `tests/MarketMafioso.Tests/RetainerRestock/RetainerRestockStockCatalogTests.cs`:

```csharp
using MarketMafioso.RetainerRestock;

namespace MarketMafioso.Tests.RetainerRestock;

public sealed class RetainerRestockStockCatalogTests
{
    private static readonly DateTime NowUtc = new(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Build_IncludesOnlyItemsWithPositiveAccessibleStock()
    {
        var playerBags = new[]
        {
            Bag("Inventory1", Slot(100, "Darksteel Ore", 12), Slot(200, "Spruce Log", 0)),
        };
        var config = new Configuration
        {
            RetainerCache =
            {
                [10] = Retainer(10, "Eris", NowUtc.AddMinutes(-10), ("Darksteel Ore", 100u, 144u)),
            },
        };

        var rows = RetainerRestockStockCatalog.Build(playerBags, config, NowUtc, ownerScope: null);

        var row = Assert.Single(rows);
        Assert.Equal(100u, row.ItemId);
        Assert.Equal("Darksteel Ore", row.ItemName);
        Assert.Equal(156, row.TotalQuantity);
        Assert.Equal(12, row.PlayerQuantity);
        Assert.Equal(144, row.RetainerQuantity);
    }

    [Fact]
    public void Build_WithOwnerScope_ExcludesOtherCharactersAndLegacyUnscopedRetainers()
    {
        var current = Retainer(10, "Eris", NowUtc.AddMinutes(-10), ("Darksteel Ore", 100u, 144u));
        current.OwnerCharacterName = "Wei Ning";
        current.OwnerHomeWorld = "Maduin";
        var other = Retainer(11, "Other", NowUtc.AddMinutes(-5), ("Darksteel Ore", 100u, 999u));
        other.OwnerCharacterName = "Alt Character";
        other.OwnerHomeWorld = "Maduin";

        var config = new Configuration
        {
            RetainerCache =
            {
                [10] = current,
                [11] = other,
                [12] = Retainer(12, "Legacy", NowUtc.AddMinutes(-2), ("Darksteel Ore", 100u, 777u)),
            },
        };

        var rows = RetainerRestockStockCatalog.Build(
            [],
            config,
            NowUtc,
            new RetainerOwnerScope("Wei Ning", "Maduin"));

        var row = Assert.Single(rows);
        Assert.Equal(144, row.TotalQuantity);
        Assert.Equal("Eris", Assert.Single(row.RetainerSources).SourceName);
    }

    [Fact]
    public void Build_IncludesPlayerOnlyRowsAsAccessibleButNotWithdrawable()
    {
        var rows = RetainerRestockStockCatalog.Build(
            [Bag("Inventory1", Slot(300, "Cobalt Ingot", 18))],
            new Configuration(),
            NowUtc,
            ownerScope: null);

        var row = Assert.Single(rows);
        Assert.Equal(18, row.TotalQuantity);
        Assert.Equal(18, row.PlayerQuantity);
        Assert.Equal(0, row.RetainerQuantity);
        Assert.False(row.HasRetainerStock);
    }

    [Fact]
    public void Search_FiltersByNameAndOrdersPrefixMatchesBeforeContainsMatches()
    {
        var rows = RetainerRestockStockCatalog.Search(
            [
                Row(1, "Fire Shard", 20),
                Row(2, "Shard Glue", 10),
                Row(3, "Lightning Shard", 15),
            ],
            "shard");

        Assert.Equal(["Shard Glue", "Fire Shard", "Lightning Shard"], rows.Select(row => row.ItemName).ToArray());
    }

    [Fact]
    public void Build_ReportsOldestAndNewestRetainerCacheAge()
    {
        var config = new Configuration
        {
            RetainerCache =
            {
                [10] = Retainer(10, "Old", NowUtc.AddHours(-2), ("Darksteel Ore", 100u, 10u)),
                [11] = Retainer(11, "Fresh", NowUtc.AddMinutes(-15), ("Darksteel Ore", 100u, 20u)),
            },
        };

        var row = Assert.Single(RetainerRestockStockCatalog.Build([], config, NowUtc, ownerScope: null));

        Assert.Equal(TimeSpan.FromHours(2), row.OldestRetainerCacheAge);
        Assert.Equal(TimeSpan.FromMinutes(15), row.NewestRetainerCacheAge);
    }

    private static RetainerRestockStockRow Row(uint itemId, string itemName, int quantity) =>
        new(
            itemId,
            itemName,
            TotalQuantity: quantity,
            PlayerQuantity: quantity,
            RetainerQuantity: 0,
            Sources: [new RetainerRestockStockSource(RetainerRestockStockSourceKind.PlayerInventory, "Player", quantity, null, null)],
            RetainerSources: [],
            OldestRetainerCacheAge: null,
            NewestRetainerCacheAge: null);

    private static InventoryBag Bag(string name, params ItemSlot[] items) =>
        new() { BagName = name, Items = items.ToList() };

    private static ItemSlot Slot(uint itemId, string itemName, uint quantity) =>
        new() { ItemId = itemId, ItemName = itemName, Quantity = quantity };

    private static CachedRetainer Retainer(ulong id, string name, DateTime updated, params (string Name, uint Id, uint Quantity)[] items) =>
        new()
        {
            RetainerId = id,
            RetainerName = name,
            LastUpdated = updated,
            Bags =
            [
                new CachedBag
                {
                    BagName = "RetainerInventory",
                    Items = items.Select(item => new CachedItem
                    {
                        ItemId = item.Id,
                        ItemName = item.Name,
                        Quantity = item.Quantity,
                    }).ToList(),
                },
            ],
        };
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:

```powershell
dotnet test tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter RetainerRestockStockCatalogTests --no-restore /p:CraftArchitectCoreProject="F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\FFXIV Craft Architect C# Edition\src\FFXIV Craft Architect.Core\FFXIV Craft Architect.Core.csproj"
```

Expected: compile failure because `RetainerRestockStockCatalog`, `RetainerRestockStockRow`, `RetainerRestockStockSource`, and `RetainerRestockStockSourceKind` do not exist.

- [ ] **Step 3: Implement the stock catalog**

Create `src/MarketMafioso/RetainerRestock/RetainerRestockStockCatalog.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.RetainerRestock;

public enum RetainerRestockStockSourceKind
{
    PlayerInventory,
    Retainer,
    FcChest,
}

public sealed record RetainerRestockStockSource(
    RetainerRestockStockSourceKind Kind,
    string SourceName,
    int Quantity,
    ulong? RetainerId,
    DateTime? LastUpdatedUtc);

public sealed record RetainerRestockStockRow(
    uint ItemId,
    string ItemName,
    int TotalQuantity,
    int PlayerQuantity,
    int RetainerQuantity,
    IReadOnlyList<RetainerRestockStockSource> Sources,
    IReadOnlyList<RetainerRestockStockSource> RetainerSources,
    TimeSpan? OldestRetainerCacheAge,
    TimeSpan? NewestRetainerCacheAge)
{
    public bool HasRetainerStock => RetainerQuantity > 0;
}

public static class RetainerRestockStockCatalog
{
    public static IReadOnlyList<RetainerRestockStockRow> Build(
        IReadOnlyList<InventoryBag> playerBags,
        Configuration config,
        DateTime nowUtc,
        RetainerOwnerScope? ownerScope)
    {
        ArgumentNullException.ThrowIfNull(playerBags);
        ArgumentNullException.ThrowIfNull(config);

        var sourcesByItem = new Dictionary<uint, List<SourceAccumulator>>();
        foreach (var item in playerBags.SelectMany(bag => bag.Items).Where(item => item.Quantity > 0 && item.ItemId > 0))
        {
            AddSource(
                sourcesByItem,
                item.ItemId,
                item.ItemName,
                new RetainerRestockStockSource(
                    RetainerRestockStockSourceKind.PlayerInventory,
                    string.IsNullOrWhiteSpace(item.ItemName) ? "Player" : "Player",
                    checked((int)item.Quantity),
                    RetainerId: null,
                    LastUpdatedUtc: null));
        }

        foreach (var retainer in config.RetainerCache.Values.Where(retainer => ownerScope is null || ownerScope.Matches(retainer.OwnerCharacterName, retainer.OwnerHomeWorld)))
        {
            var retainerItems = retainer.Bags
                .SelectMany(bag => bag.Items)
                .Where(item => item.Quantity > 0 && item.ItemId > 0)
                .GroupBy(item => item.ItemId);

            foreach (var group in retainerItems)
            {
                var first = group.First();
                AddSource(
                    sourcesByItem,
                    group.Key,
                    first.ItemName,
                    new RetainerRestockStockSource(
                        RetainerRestockStockSourceKind.Retainer,
                        retainer.RetainerName,
                        group.Sum(item => checked((int)item.Quantity)),
                        retainer.RetainerId,
                        retainer.LastUpdated));
            }
        }

        return sourcesByItem
            .Select(pair => BuildRow(pair.Key, pair.Value, nowUtc))
            .Where(row => row.TotalQuantity > 0)
            .OrderBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.ItemId)
            .ToList();
    }

    public static IReadOnlyList<RetainerRestockStockRow> Search(
        IReadOnlyList<RetainerRestockStockRow> rows,
        string search,
        int limit = 50)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var trimmed = search.Trim();
        if (trimmed.Length == 0)
            return rows.Take(limit).ToList();

        return rows
            .Where(row => row.ItemName.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => row.ItemName.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(row => row.ItemName.Length)
            .ThenBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.ItemId)
            .Take(limit)
            .ToList();
    }

    private static void AddSource(
        Dictionary<uint, List<SourceAccumulator>> sourcesByItem,
        uint itemId,
        string? itemName,
        RetainerRestockStockSource source)
    {
        if (!sourcesByItem.TryGetValue(itemId, out var sources))
        {
            sources = [];
            sourcesByItem[itemId] = sources;
        }

        sources.Add(new SourceAccumulator(string.IsNullOrWhiteSpace(itemName) ? $"Item {itemId}" : itemName, source));
    }

    private static RetainerRestockStockRow BuildRow(uint itemId, IReadOnlyList<SourceAccumulator> accumulators, DateTime nowUtc)
    {
        var sources = accumulators.Select(accumulator => accumulator.Source).ToList();
        var retainerSources = sources
            .Where(source => source.Kind == RetainerRestockStockSourceKind.Retainer)
            .OrderByDescending(source => source.Quantity)
            .ThenByDescending(source => source.LastUpdatedUtc)
            .ThenBy(source => source.SourceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var retainerAges = retainerSources
            .Where(source => source.LastUpdatedUtc is not null)
            .Select(source => nowUtc - source.LastUpdatedUtc!.Value)
            .ToList();

        return new RetainerRestockStockRow(
            itemId,
            accumulators.Select(accumulator => accumulator.ItemName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? $"Item {itemId}",
            sources.Sum(source => source.Quantity),
            sources.Where(source => source.Kind == RetainerRestockStockSourceKind.PlayerInventory).Sum(source => source.Quantity),
            retainerSources.Sum(source => source.Quantity),
            sources,
            retainerSources,
            retainerAges.Count == 0 ? null : retainerAges.Max(),
            retainerAges.Count == 0 ? null : retainerAges.Min());
    }

    private sealed record SourceAccumulator(string ItemName, RetainerRestockStockSource Source);
}
```

- [ ] **Step 4: Run stock catalog tests to verify they pass**

Run:

```powershell
dotnet test tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter RetainerRestockStockCatalogTests --no-restore /p:CraftArchitectCoreProject="F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\FFXIV Craft Architect C# Edition\src\FFXIV Craft Architect.Core\FFXIV Craft Architect.Core.csproj"
```

Expected: all `RetainerRestockStockCatalogTests` pass.

- [ ] **Step 5: Commit**

```powershell
git add src\MarketMafioso\RetainerRestock\RetainerRestockStockCatalog.cs tests\MarketMafioso.Tests\RetainerRestock\RetainerRestockStockCatalogTests.cs
git commit -m "feat: add retainer restock stock catalog"
```

---

### Task 2: Browser State And Explicit Quantity Validation

**Files:**
- Create: `src/MarketMafioso/Windows/RetainerRestock/RetainerRestockBrowserState.cs`
- Test: `tests/MarketMafioso.Tests/Windows/RetainerRestock/RetainerRestockBrowserStateTests.cs`

- [ ] **Step 1: Write failing browser state tests**

Create `tests/MarketMafioso.Tests/Windows/RetainerRestock/RetainerRestockBrowserStateTests.cs`:

```csharp
using MarketMafioso.RetainerRestock;
using MarketMafioso.Windows.RetainerRestock;

namespace MarketMafioso.Tests.Windows.RetainerRestock;

public sealed class RetainerRestockBrowserStateTests
{
    [Fact]
    public void CanSaveStagedItem_RequiresSelectedStockRowAndPositiveDesiredQuantity()
    {
        var state = new RetainerRestockBrowserState();

        Assert.False(state.CanSaveStagedItem);
        Assert.Equal("Select an item and enter a desired quantity.", state.StagedValidationMessage);

        state.Stage(Row(100, "Darksteel Ore"));
        Assert.False(state.CanSaveStagedItem);
        Assert.Equal("Enter a desired quantity.", state.StagedValidationMessage);

        state.StagedDesiredQuantityText = "0";
        Assert.False(state.CanSaveStagedItem);
        Assert.Equal("Desired quantity must be greater than zero.", state.StagedValidationMessage);

        state.StagedDesiredQuantityText = "100";
        Assert.True(state.CanSaveStagedItem);
        Assert.Equal(100, state.StagedDesiredQuantity);
        Assert.Equal("Ready to add Darksteel Ore.", state.StagedValidationMessage);
    }

    [Fact]
    public void ApplyStagedItem_AddsNewPlanRowWithExplicitQuantity()
    {
        var state = new RetainerRestockBrowserState();
        state.Stage(Row(100, "Darksteel Ore"));
        state.StagedDesiredQuantityText = "100";
        var rows = new List<RetainerRestockPlanItem>();

        var changed = state.ApplyStagedItem(rows);

        Assert.True(changed);
        var row = Assert.Single(rows);
        Assert.Equal(100u, row.ItemId);
        Assert.Equal("Darksteel Ore", row.ItemName);
        Assert.Equal(100, row.DesiredPlayerQuantity);
        Assert.True(row.Enabled);
        Assert.Null(state.SelectedStockRow);
        Assert.Equal(string.Empty, state.StagedDesiredQuantityText);
    }

    [Fact]
    public void ApplyStagedItem_UpdatesExistingPlanRowOnlyAfterExplicitQuantity()
    {
        var state = new RetainerRestockBrowserState();
        state.Stage(Row(100, "Darksteel Ore"));
        state.StagedDesiredQuantityText = "240";
        var rows = new List<RetainerRestockPlanItem>
        {
            new()
            {
                ItemId = 100,
                ItemName = "Darksteel Ore",
                DesiredPlayerQuantity = 80,
                Enabled = false,
            },
        };

        var changed = state.ApplyStagedItem(rows);

        Assert.True(changed);
        var row = Assert.Single(rows);
        Assert.Equal(240, row.DesiredPlayerQuantity);
        Assert.True(row.Enabled);
    }

    [Fact]
    public void ApplyStagedItem_WhenInvalid_DoesNotMutatePlan()
    {
        var state = new RetainerRestockBrowserState();
        state.Stage(Row(100, "Darksteel Ore"));
        state.StagedDesiredQuantityText = string.Empty;
        var rows = new List<RetainerRestockPlanItem>();

        var changed = state.ApplyStagedItem(rows);

        Assert.False(changed);
        Assert.Empty(rows);
    }

    private static RetainerRestockStockRow Row(uint itemId, string itemName) =>
        new(
            itemId,
            itemName,
            TotalQuantity: 296,
            PlayerQuantity: 12,
            RetainerQuantity: 284,
            Sources: [],
            RetainerSources: [],
            OldestRetainerCacheAge: null,
            NewestRetainerCacheAge: null);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:

```powershell
dotnet test tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter RetainerRestockBrowserStateTests --no-restore /p:CraftArchitectCoreProject="F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\FFXIV Craft Architect C# Edition\src\FFXIV Craft Architect.Core\FFXIV Craft Architect.Core.csproj"
```

Expected: compile failure because `RetainerRestockBrowserState` does not exist.

- [ ] **Step 3: Implement browser state**

Create `src/MarketMafioso/Windows/RetainerRestock/RetainerRestockBrowserState.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using MarketMafioso.RetainerRestock;

namespace MarketMafioso.Windows.RetainerRestock;

public sealed class RetainerRestockBrowserState
{
    public string SearchText { get; set; } = string.Empty;
    public bool ShowPlayerStock { get; set; } = true;
    public bool ShowRetainerStock { get; set; } = true;
    public RetainerRestockStockRow? SelectedStockRow { get; private set; }
    public Guid? SelectedPlanItemId { get; set; }
    public string StagedDesiredQuantityText { get; set; } = string.Empty;

    public int? StagedDesiredQuantity =>
        int.TryParse(StagedDesiredQuantityText.Trim(), out var quantity)
            ? quantity
            : null;

    public bool CanSaveStagedItem =>
        SelectedStockRow is not null &&
        StagedDesiredQuantity is > 0;

    public string StagedValidationMessage
    {
        get
        {
            if (SelectedStockRow is null)
                return "Select an item and enter a desired quantity.";

            if (string.IsNullOrWhiteSpace(StagedDesiredQuantityText))
                return "Enter a desired quantity.";

            return StagedDesiredQuantity switch
            {
                > 0 => $"Ready to add {SelectedStockRow.ItemName}.",
                _ => "Desired quantity must be greater than zero.",
            };
        }
    }

    public void Stage(RetainerRestockStockRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        SelectedStockRow = row;
        StagedDesiredQuantityText = string.Empty;
    }

    public void ClearStagedItem()
    {
        SelectedStockRow = null;
        StagedDesiredQuantityText = string.Empty;
    }

    public IReadOnlyList<RetainerRestockStockRow> FilterRows(IReadOnlyList<RetainerRestockStockRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var filtered = rows.Where(row =>
            (ShowPlayerStock && row.PlayerQuantity > 0) ||
            (ShowRetainerStock && row.RetainerQuantity > 0));

        return RetainerRestockStockCatalog.Search(filtered.ToList(), SearchText);
    }

    public bool ApplyStagedItem(IList<RetainerRestockPlanItem> planItems)
    {
        ArgumentNullException.ThrowIfNull(planItems);
        if (!CanSaveStagedItem || SelectedStockRow is null || StagedDesiredQuantity is not { } desiredQuantity)
            return false;

        var existing = planItems.FirstOrDefault(item => item.ItemId == SelectedStockRow.ItemId);
        if (existing is null)
        {
            planItems.Add(new RetainerRestockPlanItem
            {
                ItemId = SelectedStockRow.ItemId,
                ItemName = SelectedStockRow.ItemName,
                DesiredPlayerQuantity = desiredQuantity,
                Enabled = true,
            });
        }
        else
        {
            existing.ItemName = SelectedStockRow.ItemName;
            existing.DesiredPlayerQuantity = desiredQuantity;
            existing.Enabled = true;
        }

        ClearStagedItem();
        return true;
    }
}
```

- [ ] **Step 4: Run browser state tests to verify they pass**

Run:

```powershell
dotnet test tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter RetainerRestockBrowserStateTests --no-restore /p:CraftArchitectCoreProject="F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\FFXIV Craft Architect C# Edition\src\FFXIV Craft Architect.Core\FFXIV Craft Architect.Core.csproj"
```

Expected: all `RetainerRestockBrowserStateTests` pass.

- [ ] **Step 5: Commit**

```powershell
git add src\MarketMafioso\Windows\RetainerRestock\RetainerRestockBrowserState.cs tests\MarketMafioso.Tests\Windows\RetainerRestock\RetainerRestockBrowserStateTests.cs
git commit -m "feat: add restock browser state"
```

---

### Task 3: Extract RetainerRestockBrowserPanel

**Files:**
- Create: `src/MarketMafioso/Windows/RetainerRestock/RetainerRestockBrowserPanel.cs`
- Modify: `src/MarketMafioso/Windows/MainWindow.cs`

- [ ] **Step 1: Add the panel class with no MainWindow integration**

Create `src/MarketMafioso/Windows/RetainerRestock/RetainerRestockBrowserPanel.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using MarketMafioso.RetainerRestock;

namespace MarketMafioso.Windows.RetainerRestock;

public sealed class RetainerRestockBrowserPanel
{
    private readonly Configuration config;
    private readonly RetainerRestockBrowserState state;
    private readonly Action saveConfig;

    public RetainerRestockBrowserPanel(
        Configuration config,
        RetainerRestockBrowserState state,
        Action saveConfig)
    {
        this.config = config;
        this.state = state;
        this.saveConfig = saveConfig;
    }

    public void Draw(
        IReadOnlyList<RetainerRestockStockRow> stockRows,
        RetainerRestockPlan plan,
        Vector4 headerColor,
        Vector4 successColor,
        Vector4 errorColor,
        Vector4 mutedColor)
    {
        var available = ImGui.GetContentRegionAvail();
        var leftWidth = MathF.Max(360f, available.X * 0.45f);
        if (ImGui.BeginTable("RetainerRestockInventoryBrowserLayout", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("Accessible Stock", ImGuiTableColumnFlags.WidthFixed, leftWidth);
            ImGui.TableSetupColumn("Plan Queue", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            DrawStockBrowser(stockRows, headerColor, successColor, mutedColor);

            ImGui.TableNextColumn();
            DrawPlanQueue(plan, headerColor, successColor, errorColor, mutedColor);

            ImGui.EndTable();
        }
    }

    private void DrawStockBrowser(
        IReadOnlyList<RetainerRestockStockRow> stockRows,
        Vector4 headerColor,
        Vector4 successColor,
        Vector4 mutedColor)
    {
        ImGui.TextColored(headerColor, "Accessible Stock");
        ImGui.Separator();

        ImGui.SetNextItemWidth(-1);
        var search = state.SearchText;
        if (ImGui.InputText("##RetainerRestockStockSearch", ref search, 160))
            state.SearchText = search;

        var showPlayer = state.ShowPlayerStock;
        if (ImGui.Checkbox("Player-held##RetainerRestockShowPlayer", ref showPlayer))
            state.ShowPlayerStock = showPlayer;
        ImGui.SameLine();
        var showRetainer = state.ShowRetainerStock;
        if (ImGui.Checkbox("Retainer-held##RetainerRestockShowRetainer", ref showRetainer))
            state.ShowRetainerStock = showRetainer;

        var rows = state.FilterRows(stockRows);
        if (rows.Count == 0)
        {
            ImGui.TextColored(mutedColor, "No accessible stock matches the current filter.");
            return;
        }

        if (!ImGui.BeginTable("RetainerRestockStockRows", 5, ImGuiUi.InteractiveTableFlags))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthFixed, 64);
        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthFixed, 64);
        ImGui.TableSetupColumn("Retainers", ImGuiTableColumnFlags.WidthFixed, 76);
        ImGui.TableSetupColumn("Sources", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        foreach (var row in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.Selectable($"{row.ItemName}##RetainerRestockStock{row.ItemId}", state.SelectedStockRow?.ItemId == row.ItemId, ImGuiSelectableFlags.SpanAllColumns))
                state.Stage(row);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.TotalQuantity.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.PlayerQuantity.ToString());
            ImGui.TableNextColumn();
            ImGui.TextColored(row.RetainerQuantity > 0 ? successColor : mutedColor, row.RetainerQuantity.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(FormatSources(row));
        }

        ImGui.EndTable();
    }

    private void DrawPlanQueue(
        RetainerRestockPlan plan,
        Vector4 headerColor,
        Vector4 successColor,
        Vector4 errorColor,
        Vector4 mutedColor)
    {
        ImGui.TextColored(headerColor, "Plan Queue");
        ImGui.Separator();
        DrawStagedItem(successColor, errorColor, mutedColor);
        ImGui.Spacing();
        DrawPlanRows(plan, headerColor, successColor, errorColor, mutedColor);
    }

    private void DrawStagedItem(Vector4 successColor, Vector4 errorColor, Vector4 mutedColor)
    {
        if (state.SelectedStockRow is null)
        {
            ImGui.TextColored(mutedColor, "Select accessible stock to stage a plan row.");
            return;
        }

        ImGui.TextUnformatted($"Selected: {state.SelectedStockRow.ItemName}");
        ImGui.TextColored(mutedColor, $"Accessible {state.SelectedStockRow.TotalQuantity}; player {state.SelectedStockRow.PlayerQuantity}; retainers {state.SelectedStockRow.RetainerQuantity}.");
        ImGui.SetNextItemWidth(120f);
        var desired = state.StagedDesiredQuantityText;
        if (ImGui.InputText("Desired quantity##RetainerRestockStagedDesired", ref desired, 32, ImGuiInputTextFlags.CharsDecimal))
            state.StagedDesiredQuantityText = desired;

        ImGui.TextColored(state.CanSaveStagedItem ? successColor : errorColor, state.StagedValidationMessage);
        if (ImGuiUi.Button("Save To Plan##RetainerRestockSaveStaged", state.CanSaveStagedItem))
        {
            if (state.ApplyStagedItem(config.RetainerRestockPlanItems))
                saveConfig();
        }
        ImGui.SameLine();
        if (ImGuiUi.Button("Clear Selection##RetainerRestockClearStaged", true))
            state.ClearStagedItem();
    }

    private void DrawPlanRows(
        RetainerRestockPlan plan,
        Vector4 headerColor,
        Vector4 successColor,
        Vector4 errorColor,
        Vector4 mutedColor)
    {
        if (config.RetainerRestockPlanItems.Count == 0)
        {
            ImGui.TextColored(mutedColor, "No restock rows yet.");
            return;
        }

        if (!ImGui.BeginTable("RetainerRestockPlanQueueRows", 8, ImGuiUi.InteractiveTableFlags))
            return;

        ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 44);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Desired", ImGuiTableColumnFlags.WidthFixed, 78);
        ImGui.TableSetupColumn("Need", ImGuiTableColumnFlags.WidthFixed, 64);
        ImGui.TableSetupColumn("Retainers", ImGuiTableColumnFlags.WidthFixed, 76);
        ImGui.TableSetupColumn("Missing", ImGuiTableColumnFlags.WidthFixed, 72);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 94);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 78);
        ImGui.TableHeadersRow();

        for (var index = 0; index < config.RetainerRestockPlanItems.Count; index++)
        {
            var row = config.RetainerRestockPlanItems[index];
            var planLine = plan.Lines.FirstOrDefault(line => line.PlanItemId == row.Id);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var enabled = row.Enabled;
            if (ImGui.Checkbox($"##RetainerRestockEnabled{row.Id}", ref enabled))
            {
                row.Enabled = enabled;
                saveConfig();
            }
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.IsNullOrWhiteSpace(row.ItemName) ? $"Item {row.ItemId}" : row.ItemName);
            ImGui.TableNextColumn();
            var desired = row.DesiredPlayerQuantity;
            ImGui.SetNextItemWidth(64f);
            if (ImGui.InputInt($"##RetainerRestockDesired{row.Id}", ref desired))
            {
                row.DesiredPlayerQuantity = Math.Max(1, desired);
                saveConfig();
            }
            ImGui.TableNextColumn();
            ImGui.TextColored(planLine is { NeededQuantity: > 0 } ? errorColor : successColor, planLine?.NeededQuantity.ToString() ?? "-");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(planLine?.CachedRetainerQuantity.ToString() ?? "-");
            ImGui.TableNextColumn();
            ImGui.TextColored(planLine is { MissingQuantity: > 0 } ? errorColor : successColor, planLine?.MissingQuantity.ToString() ?? "-");
            ImGui.TableNextColumn();
            ImGui.TextColored(planLine is null ? mutedColor : GetStatusColor(planLine.Status, successColor, errorColor, headerColor), planLine is null ? "Disabled" : FormatStatus(planLine.Status));
            ImGui.TableNextColumn();
            if (ImGuiUi.Button($"Remove##RetainerRestockRemove{row.Id}", true))
            {
                config.RetainerRestockPlanItems.RemoveAt(index);
                saveConfig();
                index--;
            }
        }

        ImGui.EndTable();
    }

    private static string FormatSources(RetainerRestockStockRow row)
    {
        if (row.RetainerSources.Count == 0)
            return row.PlayerQuantity > 0 ? $"Player x{row.PlayerQuantity}" : "-";

        return string.Join(", ", row.RetainerSources.Take(3).Select(source => $"{source.SourceName} x{source.Quantity}"));
    }

    private static string FormatStatus(RetainerRestockPlanLineStatus status) =>
        status switch
        {
            RetainerRestockPlanLineStatus.NoNeed => "No need",
            RetainerRestockPlanLineStatus.Ready => "Ready",
            RetainerRestockPlanLineStatus.Partial => "Partial",
            RetainerRestockPlanLineStatus.NoCachedStock => "No stock",
            _ => status.ToString(),
        };

    private static Vector4 GetStatusColor(
        RetainerRestockPlanLineStatus status,
        Vector4 successColor,
        Vector4 errorColor,
        Vector4 headerColor) =>
        status switch
        {
            RetainerRestockPlanLineStatus.NoNeed or RetainerRestockPlanLineStatus.Ready => successColor,
            RetainerRestockPlanLineStatus.Partial => headerColor,
            RetainerRestockPlanLineStatus.NoCachedStock => errorColor,
            _ => headerColor,
        };
}
```

- [ ] **Step 2: Build plugin to verify the standalone panel compiles**

Run:

```powershell
dotnet build src\MarketMafioso\MarketMafioso.csproj --no-restore
```

Expected: build succeeds.

- [ ] **Step 3: Commit**

```powershell
git add src\MarketMafioso\Windows\RetainerRestock\RetainerRestockBrowserPanel.cs
git commit -m "feat: add restock inventory browser panel"
```

---

### Task 4: Integrate Browser Panel Into MainWindow

**Files:**
- Modify: `src/MarketMafioso/Windows/MainWindow.cs`

- [ ] **Step 1: Remove old autocomplete restock fields**

In `src/MarketMafioso/Windows/MainWindow.cs`, remove:

```csharp
using MarketMafioso.Windows.ItemAutocomplete;
```

Remove fields:

```csharp
private readonly IReadOnlyList<AcquisitionItemOption> restockItemOptions;
private readonly ItemAutocompleteState restockItemAutocomplete = new();
private int restockDesiredQuantity = 1;
```

Add:

```csharp
using MarketMafioso.Windows.RetainerRestock;
```

Add fields near the other Restock fields:

```csharp
private readonly RetainerRestockBrowserState restockBrowserState = new();
private readonly RetainerRestockBrowserPanel restockBrowser;
```

- [ ] **Step 2: Instantiate the panel in the constructor**

In the constructor, remove:

```csharp
restockItemOptions = ItemAutocompleteControl.LoadItemOptions(dataManager);
```

After buffer setup and before other child windows are created, add:

```csharp
restockBrowser = new RetainerRestockBrowserPanel(
    config,
    restockBrowserState,
    config.Save);
```

- [ ] **Step 3: Replace the old editor/popup render path**

Replace `DrawRetainerRestockTab`, `DrawRetainerRestockEditor`, `DrawRetainerRestockEditorActions`, and `AddRetainerRestockPlanItem` with:

```csharp
private void DrawRetainerRestockTab()
{
    ImGui.Spacing();
    ImGui.TextColored(ColHeader, "Restock");
    ImGui.TextWrapped("Browse accessible stock, build an explicit plan, and pull matching items from cached retainers.");
    ImGui.Spacing();

    var playerBags = scanner.ScanPlayerInventory(config);
    var plan = GetRetainerRestockPlan(playerBags);
    var stockRows = RetainerRestockStockCatalog.Build(
        playerBags,
        config,
        DateTime.UtcNow,
        GetCurrentRetainerOwnerScope());

    restockBrowser.Draw(stockRows, plan, ColHeader, ColSuccess, ColError, ColMuted);
    ImGui.Spacing();
    DrawRetainerRestockControls(plan);
}
```

- [ ] **Step 4: Avoid double player-inventory scanning**

Replace existing `GetRetainerRestockPlan()` with:

```csharp
private RetainerRestockPlan GetRetainerRestockPlan(IReadOnlyList<InventoryBag>? playerBags = null)
{
    var playerInventory = (playerBags ?? scanner.ScanPlayerInventory(config))
        .SelectMany(bag => bag.Items)
        .GroupBy(item => item.ItemId)
        .ToDictionary(group => group.Key, group => group.Sum(item => (int)item.Quantity));

    return RetainerRestockPlanner.BuildPlan(
        config.RetainerRestockPlanItems,
        playerInventory,
        config,
        DateTime.UtcNow,
        GetCurrentRetainerOwnerScope());
}
```

Keep `DrawRetainerRestockPreview`, `FormatRetainerRestockStatus`, `FormatRetainerRestockCacheAge`, `FormatRetainerRestockCandidates`, and `GetRetainerRestockStatusColor` only if still used outside the new panel. If they are unused after compile, delete them.

- [ ] **Step 5: Build and fix any compile errors caused by unused methods/usings**

Run:

```powershell
dotnet build src\MarketMafioso\MarketMafioso.csproj --no-restore
```

Expected: build succeeds. If the compiler reports unused `FormatRetainerRestock...` methods are harmless, leave them only if still called. If it reports missing namespace imports, add `using MarketMafioso.Windows.RetainerRestock;`.

- [ ] **Step 6: Run focused tests**

Run:

```powershell
dotnet test tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "RetainerRestockStockCatalogTests|RetainerRestockBrowserStateTests|RetainerRestockPlannerTests" --no-restore /p:CraftArchitectCoreProject="F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\FFXIV Craft Architect C# Edition\src\FFXIV Craft Architect.Core\FFXIV Craft Architect.Core.csproj"
```

Expected: all focused Restock tests pass.

- [ ] **Step 7: Commit**

```powershell
git add src\MarketMafioso\Windows\MainWindow.cs
git commit -m "feat: wire restock inventory browser"
```

---

### Task 5: Verification, Deploy, And Visual Inspection

**Files:**
- Modify only if verification reveals necessary polish:
  - `src/MarketMafioso/Windows/RetainerRestock/RetainerRestockBrowserPanel.cs`
  - `src/MarketMafioso/Windows/MainWindow.cs`

- [ ] **Step 1: Run plugin build**

Run:

```powershell
dotnet build src\MarketMafioso\MarketMafioso.csproj --no-restore
```

Expected: build succeeds with 0 errors.

- [ ] **Step 2: Run focused Restock tests**

Run:

```powershell
dotnet test tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "RetainerRestockStockCatalogTests|RetainerRestockBrowserStateTests|RetainerRestockPlannerTests|WorkshopRetainerRestockCompletionTests" --no-restore /p:CraftArchitectCoreProject="F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\FFXIV Craft Architect C# Edition\src\FFXIV Craft Architect.Core\FFXIV Craft Architect.Core.csproj"
```

Expected: all focused Restock tests pass.

- [ ] **Step 3: Run full plugin test project if local fixtures are available**

Run:

```powershell
dotnet test tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --no-restore /p:CraftArchitectCoreProject="F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\FFXIV Craft Architect C# Edition\src\FFXIV Craft Architect.Core\FFXIV Craft Architect.Core.csproj"
```

Expected: either all tests pass, or the only failures are the known missing `craft-appraisal-quote.v1.sample.json` fixture failures. Do not report the suite as passing if those failures remain.

- [ ] **Step 4: Deploy to main XIVLauncher dev plugin**

Run:

```powershell
& .\src\MarketMafioso\tools\Deploy-DevPlugin.ps1 -Configuration Debug
```

Expected: script reports build success and verified destination DLL hash for `%APPDATA%\XIVLauncher\devPlugins\MarketMafioso\MarketMafioso.dll`.

- [ ] **Step 5: Deploy to Multibox dev plugin**

Run:

```powershell
& .\src\MarketMafioso\tools\Deploy-DevPlugin.ps1 -Configuration Debug -SkipBuild -TargetDll "$env:APPDATA\XIVLauncher-Multibox-2\devPlugins\MarketMafioso\MarketMafioso.dll"
```

Expected: script reports verified destination DLL hash for `%APPDATA%\XIVLauncher-Multibox-2\devPlugins\MarketMafioso\MarketMafioso.dll`.

- [ ] **Step 6: In-game visual smoke**

After reloading MarketMafioso in Dalamud:

1. Open `/mmf`.
2. Open the `Restock` tab.
3. Confirm the first viewport is split into accessible stock and plan queue.
4. Confirm stock rows only show items with positive player or owner-scoped retainer stock.
5. Select a stock row and confirm no desired quantity is prefilled.
6. Try saving without quantity and confirm the UI blocks it.
7. Enter a positive desired quantity and save the row.
8. Confirm the plan queue shows need, retainer coverage, missing quantity, and status.
9. Confirm `Restock From Retainers` remains disabled unless at least one plan line needs retainer stock and has candidates.

- [ ] **Step 7: Commit visual-polish fixes if any**

If verification required code changes:

```powershell
git add src\MarketMafioso\Windows\RetainerRestock\RetainerRestockBrowserPanel.cs src\MarketMafioso\Windows\MainWindow.cs
git commit -m "fix: polish restock inventory browser"
```

If no fixes were required, skip this commit.

---

## Self-Review

- Spec coverage:
  - Stock-backed browser: Task 1 and Task 4.
  - Explicit desired quantity: Task 2 and Task 3.
  - Owner-scoped retainers and legacy exclusion: Task 1.
  - Player-held vs retainer-withdrawable distinction: Task 1, Task 3, Task 5 visual smoke.
  - MainWindow extraction: Task 3 and Task 4.
  - Existing planner/run engine preservation: Task 4 and Task 5.

- Placeholder scan:
  - No `TBD`, `TODO`, or unspecified "add tests" steps remain.

- Type consistency:
  - `RetainerRestockStockCatalog`, `RetainerRestockStockRow`, `RetainerRestockStockSource`, `RetainerRestockBrowserState`, and `RetainerRestockBrowserPanel` are introduced before use.
  - Plan snippets consistently use existing `Configuration`, `InventoryBag`, `ItemSlot`, `CachedRetainer`, `CachedBag`, `CachedItem`, and `RetainerRestockPlanItem` types.
