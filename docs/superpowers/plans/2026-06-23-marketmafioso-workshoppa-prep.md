# MarketMafioso Workshoppa Prep Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a MarketMafioso-owned workshop prep queue that computes direct workshop material demand, checks player/retainer availability, restocks materials from retainers, and optionally sends the prep queue to VIWI Workshoppa through public IPC.

**Architecture:** Keep MarketMafioso in the preparation role: local prep queue, material catalog, availability math, and retainer withdrawal. VIWI integration is an explicit handoff through `VIWI.Workshoppa.AddQueueItem` and `VIWI.Workshoppa.ClearQueue`; no VIWI references, reflection, or internal state reads. Retainer cache chooses candidate retainers, but live retainer inventory pages are authoritative for withdrawal.

**Tech Stack:** C# 12, .NET 10 SDK, Dalamud.NET.Sdk/15.0.0, Lumina Excel sheets, FFXIVClientStructs inventory APIs, xUnit for pure domain tests.

---

## File Structure

- Create `MarketMafioso/WorkshopPrep/WorkshopPrepModels.cs`: pure queue, project, material, and availability DTOs.
- Create `MarketMafioso/WorkshopPrep/WorkshopProjectCatalog.cs`: Lumina-backed company workshop project catalog and direct material aggregation.
- Create `MarketMafioso/WorkshopPrep/WorkshopMaterialAvailabilityService.cs`: live player inventory plus cached retainer availability math.
- Create `MarketMafioso/WorkshopPrep/VIWIWorkshoppaIpc.cs`: optional public IPC handoff wrapper.
- Create `MarketMafioso/WorkshopPrep/WorkshopRetainerRestockService.cs`: manual restock state machine and live retainer inventory withdrawal runner.
- Modify `MarketMafioso/Configuration.cs`: persist `WorkshopPrepQueue`.
- Modify `MarketMafioso/Plugin.cs`: wire new services and command status.
- Modify `MarketMafioso/Windows/MainWindow.cs`: add `Workshop Prep` tab and compact controls.
- Create `MarketMafioso.Tests/MarketMafioso.Tests.csproj`: plugin-domain unit tests.
- Create `MarketMafioso.Tests/WorkshopPrep/WorkshopMaterialAvailabilityServiceTests.cs`: availability tests.
- Create `MarketMafioso.Tests/WorkshopPrep/VIWIWorkshoppaIpcTests.cs`: handoff behavior tests with fake IPC adapter.
- Modify `MarketMafioso.sln`: add `MarketMafioso.Tests`.

### Task 1: Add Workshop Prep Models And Config

**Files:**
- Create: `MarketMafioso/WorkshopPrep/WorkshopPrepModels.cs`
- Modify: `MarketMafioso/Configuration.cs`

- [ ] **Step 1: Add model file**

Create `MarketMafioso/WorkshopPrep/WorkshopPrepModels.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace MarketMafioso.WorkshopPrep;

public sealed class WorkshopPrepQueueItem
{
    public uint WorkshopItemId { get; set; }
    public int Quantity { get; set; }
}

public sealed record WorkshopProjectDefinition(
    uint WorkshopItemId,
    uint ResultItemId,
    string Name,
    ushort IconId,
    IReadOnlyList<WorkshopMaterialRequirement> Materials);

public sealed record WorkshopMaterialRequirement(
    uint ItemId,
    string ItemName,
    ushort IconId,
    int Quantity);

public sealed record WorkshopMaterialAvailability(
    uint ItemId,
    string ItemName,
    ushort IconId,
    int Required,
    int PlayerInventory,
    int RetainerCache,
    int Shortage,
    IReadOnlyList<RetainerMaterialCandidate> CandidateRetainers);

public sealed record RetainerMaterialCandidate(
    ulong RetainerId,
    string RetainerName,
    DateTime LastUpdated,
    int Quantity);
```

- [ ] **Step 2: Persist prep queue in configuration**

Modify `MarketMafioso/Configuration.cs`:

```csharp
using MarketMafioso.WorkshopPrep;
```

Add the property below `RetainerCache`:

```csharp
public List<WorkshopPrepQueueItem> WorkshopPrepQueue { get; set; } = new();
```

- [ ] **Step 3: Build**

Run:

```powershell
dotnet build "MarketMafioso.sln" -c Debug
```

Expected: build succeeds. Debug build may sync to `%APPDATA%\XIVLauncher\devPlugins\MarketMafioso`.

- [ ] **Step 4: Commit**

```powershell
git add MarketMafioso/WorkshopPrep/WorkshopPrepModels.cs MarketMafioso/Configuration.cs
git commit -m "feat: add workshop prep queue models"
```

### Task 2: Add Test Project For Plugin-Domain Logic

**Files:**
- Create: `MarketMafioso.Tests/MarketMafioso.Tests.csproj`
- Modify: `MarketMafioso.sln`

- [ ] **Step 1: Create test project**

Create `MarketMafioso.Tests/MarketMafioso.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows7.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <Platforms>x64</Platforms>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\MarketMafioso\MarketMafioso.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add project to solution**

Run:

```powershell
dotnet sln "MarketMafioso.sln" add "MarketMafioso.Tests/MarketMafioso.Tests.csproj"
```

Expected: solution reports that the project was added.

- [ ] **Step 3: Verify empty test project**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -v minimal
```

Expected: test project builds and reports no tests or passes with no tests. If referencing the plugin project fails because Dalamud SDK test builds need x64, run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -p:Platform=x64 -v minimal
```

- [ ] **Step 4: Commit**

```powershell
git add MarketMafioso.Tests/MarketMafioso.Tests.csproj MarketMafioso.sln
git commit -m "test: add marketmafioso plugin test project"
```

### Task 3: Implement Availability Math With Tests

**Files:**
- Create: `MarketMafioso/WorkshopPrep/WorkshopMaterialAvailabilityService.cs`
- Create: `MarketMafioso.Tests/WorkshopPrep/WorkshopMaterialAvailabilityServiceTests.cs`

- [ ] **Step 1: Write failing availability tests**

Create `MarketMafioso.Tests/WorkshopPrep/WorkshopMaterialAvailabilityServiceTests.cs`:

```csharp
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Tests.WorkshopPrep;

public sealed class WorkshopMaterialAvailabilityServiceTests
{
    [Fact]
    public void BuildAvailability_UsesRetainersOnlyForShortage()
    {
        var requirements = new[]
        {
            new WorkshopMaterialRequirement(100, "Elm Lumber", 123, 55),
        };
        var playerInventory = new Dictionary<uint, int>
        {
            [100] = 20,
        };
        var config = new Configuration
        {
            RetainerCache =
            {
                [(ulong)10] = new CachedRetainer
                {
                    RetainerId = 10,
                    RetainerName = "A",
                    LastUpdated = new DateTime(2026, 6, 23, 12, 0, 0, DateTimeKind.Utc),
                    Bags =
                    [
                        new CachedBag
                        {
                            BagName = "RetainerInventory",
                            Items =
                            [
                                new CachedItem { ItemId = 100, ItemName = "Elm Lumber", Quantity = 99 },
                            ],
                        },
                    ],
                },
            },
        };

        var result = WorkshopMaterialAvailabilityService.BuildAvailability(requirements, playerInventory, config);

        var item = Assert.Single(result);
        Assert.Equal(55, item.Required);
        Assert.Equal(20, item.PlayerInventory);
        Assert.Equal(99, item.RetainerCache);
        Assert.Equal(35, item.Shortage);
        Assert.Equal(10UL, Assert.Single(item.CandidateRetainers).RetainerId);
    }

    [Fact]
    public void BuildAvailability_ReportsNoShortageWhenPlayerHasEnough()
    {
        var requirements = new[]
        {
            new WorkshopMaterialRequirement(100, "Elm Lumber", 123, 55),
        };
        var playerInventory = new Dictionary<uint, int>
        {
            [100] = 60,
        };

        var result = WorkshopMaterialAvailabilityService.BuildAvailability(requirements, playerInventory, new Configuration());

        var item = Assert.Single(result);
        Assert.Equal(0, item.Shortage);
        Assert.Empty(item.CandidateRetainers);
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" --filter "FullyQualifiedName~WorkshopMaterialAvailabilityServiceTests" -p:Platform=x64 -v minimal
```

Expected: fail because `WorkshopMaterialAvailabilityService` does not exist.

- [ ] **Step 3: Implement availability service**

Create `MarketMafioso/WorkshopPrep/WorkshopMaterialAvailabilityService.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.WorkshopPrep;

public static class WorkshopMaterialAvailabilityService
{
    public static IReadOnlyList<WorkshopMaterialAvailability> BuildAvailability(
        IReadOnlyList<WorkshopMaterialRequirement> requirements,
        IReadOnlyDictionary<uint, int> playerInventory,
        Configuration config)
    {
        return requirements
            .GroupBy(x => x.ItemId)
            .Select(group =>
            {
                var first = group.First();
                var required = group.Sum(x => x.Quantity);
                var playerCount = playerInventory.TryGetValue(first.ItemId, out var count) ? count : 0;
                var shortage = Math.Max(0, required - playerCount);
                var candidates = shortage == 0
                    ? []
                    : BuildCandidates(first.ItemId, config);
                var retainerCount = candidates.Sum(x => x.Quantity);

                return new WorkshopMaterialAvailability(
                    first.ItemId,
                    first.ItemName,
                    first.IconId,
                    required,
                    playerCount,
                    retainerCount,
                    shortage,
                    candidates);
            })
            .OrderBy(x => x.ItemName)
            .ToList();
    }

    private static IReadOnlyList<RetainerMaterialCandidate> BuildCandidates(uint itemId, Configuration config)
    {
        return config.RetainerCache.Values
            .Select(retainer => new
            {
                Retainer = retainer,
                Quantity = retainer.Bags
                    .SelectMany(x => x.Items)
                    .Where(x => x.ItemId == itemId)
                    .Sum(x => (int)x.Quantity),
            })
            .Where(x => x.Quantity > 0)
            .OrderByDescending(x => x.Quantity)
            .Select(x => new RetainerMaterialCandidate(
                x.Retainer.RetainerId,
                x.Retainer.RetainerName,
                x.Retainer.LastUpdated,
                x.Quantity))
            .ToList();
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" --filter "FullyQualifiedName~WorkshopMaterialAvailabilityServiceTests" -p:Platform=x64 -v minimal
```

Expected: both tests pass.

- [ ] **Step 5: Commit**

```powershell
git add MarketMafioso/WorkshopPrep/WorkshopMaterialAvailabilityService.cs MarketMafioso.Tests/WorkshopPrep/WorkshopMaterialAvailabilityServiceTests.cs
git commit -m "feat: calculate workshop material availability"
```

### Task 4: Implement Workshop Project Catalog

**Files:**
- Create: `MarketMafioso/WorkshopPrep/WorkshopProjectCatalog.cs`

- [ ] **Step 1: Add Lumina-backed catalog**

Create `MarketMafioso/WorkshopPrep/WorkshopProjectCatalog.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace MarketMafioso.WorkshopPrep;

public sealed class WorkshopProjectCatalog
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private IReadOnlyList<WorkshopProjectDefinition>? cachedProjects;

    public WorkshopProjectCatalog(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
    }

    public IReadOnlyList<WorkshopProjectDefinition> GetProjects()
    {
        if (cachedProjects != null)
            return cachedProjects;

        var supplyItems = dataManager.GetExcelSheet<CompanyCraftSupplyItem>()
            .Where(x => x.RowId > 0)
            .ToDictionary(x => x.RowId, x => x.Item.Value);

        cachedProjects = dataManager.GetExcelSheet<CompanyCraftSequence>()
            .Where(x => x.RowId > 0 && x.ResultItem.RowId > 0)
            .Select(x => BuildProject(x, supplyItems))
            .Where(x => x.Materials.Count > 0)
            .OrderBy(x => x.Name)
            .ToList();

        log.Information($"[MarketMafioso] Loaded {cachedProjects.Count} workshop prep project(s).");
        return cachedProjects;
    }

    public IReadOnlyList<WorkshopMaterialRequirement> BuildRequirements(IReadOnlyList<WorkshopPrepQueueItem> queue)
    {
        var projects = GetProjects().ToDictionary(x => x.WorkshopItemId);
        return queue
            .Where(x => x.Quantity > 0 && projects.ContainsKey(x.WorkshopItemId))
            .SelectMany(x => projects[x.WorkshopItemId].Materials.Select(material => material with
            {
                Quantity = material.Quantity * x.Quantity,
            }))
            .GroupBy(x => x.ItemId)
            .Select(x =>
            {
                var first = x.First();
                return first with { Quantity = x.Sum(y => y.Quantity) };
            })
            .OrderBy(x => x.ItemName)
            .ToList();
    }

    private static WorkshopProjectDefinition BuildProject(
        CompanyCraftSequence sequence,
        IReadOnlyDictionary<uint, Item> supplyItems)
    {
        var materials = sequence.CompanyCraftPart
            .Where(part => part.RowId != 0)
            .SelectMany(part => part.Value.CompanyCraftProcess)
            .SelectMany(process => Enumerable.Range(0, process.Value.SupplyItem.Count)
                .Select(index => new
                {
                    SupplyItemId = process.Value.SupplyItem[index].RowId,
                    Quantity = process.Value.SetQuantity[index] * process.Value.SetsRequired[index],
                }))
            .Where(x => x.SupplyItemId > 0 && x.Quantity > 0 && supplyItems.ContainsKey(x.SupplyItemId))
            .Select(x =>
            {
                var item = supplyItems[x.SupplyItemId];
                return new WorkshopMaterialRequirement(
                    item.RowId,
                    item.Name.ToString(),
                    item.Icon,
                    x.Quantity);
            })
            .GroupBy(x => x.ItemId)
            .Select(x =>
            {
                var first = x.First();
                return first with { Quantity = x.Sum(y => y.Quantity) };
            })
            .OrderBy(x => x.ItemName)
            .ToList();

        return new WorkshopProjectDefinition(
            sequence.RowId,
            sequence.ResultItem.RowId,
            sequence.ResultItem.Value.Name.ToString(),
            sequence.ResultItem.Value.Icon,
            materials);
    }
}
```

- [ ] **Step 2: Build plugin**

Run:

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug -p:Platform=x64
```

Expected: build succeeds.

- [ ] **Step 3: Commit**

```powershell
git add MarketMafioso/WorkshopPrep/WorkshopProjectCatalog.cs
git commit -m "feat: load workshop project material catalog"
```

### Task 5: Implement VIWI Public IPC Handoff

**Files:**
- Create: `MarketMafioso/WorkshopPrep/VIWIWorkshoppaIpc.cs`
- Create: `MarketMafioso.Tests/WorkshopPrep/VIWIWorkshoppaIpcTests.cs`

- [ ] **Step 1: Write failing IPC wrapper tests**

Create `MarketMafioso.Tests/WorkshopPrep/VIWIWorkshoppaIpcTests.cs`:

```csharp
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Tests.WorkshopPrep;

public sealed class VIWIWorkshoppaIpcTests
{
    [Fact]
    public void SendQueue_LeavesQueueUntouchedWhenClearFails()
    {
        var ipc = new VIWIWorkshoppaIpc(new FakeAdapter(clearResult: false, addResult: true));
        var queue = new List<WorkshopPrepQueueItem>
        {
            new() { WorkshopItemId = 1002, Quantity = 2 },
        };

        var result = ipc.SendQueue(queue, clearExisting: true);

        Assert.False(result.Success);
        Assert.Single(queue);
        Assert.Contains("clear", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SendQueue_AddsEveryQueuedItem()
    {
        var adapter = new FakeAdapter(clearResult: true, addResult: true);
        var ipc = new VIWIWorkshoppaIpc(adapter);
        var queue = new List<WorkshopPrepQueueItem>
        {
            new() { WorkshopItemId = 1002, Quantity = 2 },
            new() { WorkshopItemId = 1003, Quantity = 1 },
        };

        var result = ipc.SendQueue(queue, clearExisting: true);

        Assert.True(result.Success);
        Assert.Equal([(uint)1002, (uint)1003], adapter.Added.Select(x => x.WorkshopItemId).ToArray());
    }

    private sealed class FakeAdapter : IVIWIWorkshoppaIpcAdapter
    {
        private readonly bool clearResult;
        private readonly bool addResult;

        public FakeAdapter(bool clearResult, bool addResult)
        {
            this.clearResult = clearResult;
            this.addResult = addResult;
        }

        public List<(uint WorkshopItemId, int Quantity)> Added { get; } = new();
        public bool IsAvailable => true;
        public bool ClearQueue() => clearResult;
        public bool AddQueueItem(uint workshopItemId, int quantity)
        {
            Added.Add((workshopItemId, quantity));
            return addResult;
        }
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" --filter "FullyQualifiedName~VIWIWorkshoppaIpcTests" -p:Platform=x64 -v minimal
```

Expected: fail because `VIWIWorkshoppaIpc` does not exist.

- [ ] **Step 3: Implement IPC wrapper**

Create `MarketMafioso/WorkshopPrep/VIWIWorkshoppaIpc.cs`:

```csharp
using System.Collections.Generic;
using Dalamud.Plugin;
using System.Linq;

namespace MarketMafioso.WorkshopPrep;

public interface IVIWIWorkshoppaIpcAdapter
{
    bool IsAvailable { get; }
    bool ClearQueue();
    bool AddQueueItem(uint workshopItemId, int quantity);
}

public sealed record VIWIWorkshoppaIpcResult(bool Success, string Message);

public sealed class VIWIWorkshoppaIpc
{
    private readonly IVIWIWorkshoppaIpcAdapter adapter;

    public VIWIWorkshoppaIpc(IVIWIWorkshoppaIpcAdapter adapter)
    {
        this.adapter = adapter;
    }

    public VIWIWorkshoppaIpcResult SendQueue(IReadOnlyList<WorkshopPrepQueueItem> queue, bool clearExisting)
    {
        if (!adapter.IsAvailable)
            return new(false, "VIWI Workshoppa IPC is not available.");

        if (clearExisting && !adapter.ClearQueue())
            return new(false, "Unable to clear VIWI Workshoppa queue.");

        foreach (var item in queue)
        {
            if (item.WorkshopItemId == 0 || item.Quantity <= 0)
                continue;

            if (!adapter.AddQueueItem(item.WorkshopItemId, item.Quantity))
                return new(false, $"Unable to add workshop item {item.WorkshopItemId} to VIWI.");
        }

        return new(true, "Sent prep queue to VIWI Workshoppa.");
    }
}

public sealed class DalamudVIWIWorkshoppaIpcAdapter : IVIWIWorkshoppaIpcAdapter
{
    private const string AddQueueItemIpc = "VIWI.Workshoppa.AddQueueItem";
    private const string ClearQueueIpc = "VIWI.Workshoppa.ClearQueue";

    private readonly IDalamudPluginInterface pluginInterface;

    public DalamudVIWIWorkshoppaIpcAdapter(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
    }

    public bool IsAvailable => pluginInterface.InstalledPlugins.Any(x => x.InternalName == "VIWI" && x.IsLoaded);

    public bool ClearQueue() => IsAvailable && pluginInterface.GetIpcSubscriber<bool>(ClearQueueIpc).InvokeFunc();

    public bool AddQueueItem(uint workshopItemId, int quantity) =>
        IsAvailable && pluginInterface.GetIpcSubscriber<uint, int, bool>(AddQueueItemIpc).InvokeFunc(workshopItemId, quantity);
}
```

- [ ] **Step 4: Run tests to verify pass**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" --filter "FullyQualifiedName~VIWIWorkshoppaIpcTests" -p:Platform=x64 -v minimal
```

Expected: tests pass.

- [ ] **Step 5: Commit**

```powershell
git add MarketMafioso/WorkshopPrep/VIWIWorkshoppaIpc.cs MarketMafioso.Tests/WorkshopPrep/VIWIWorkshoppaIpcTests.cs
git commit -m "feat: add viwi workshoppa queue handoff"
```

### Task 6: Add Player Inventory Count Helper

**Files:**
- Modify: `MarketMafioso/InventoryScanner.cs`

- [ ] **Step 1: Add public player count method**

Modify `InventoryScanner.cs` by adding this method after `ScanPlayerInventory`:

```csharp
public Dictionary<uint, int> CountPlayerInventory(Configuration config)
{
    return ScanPlayerInventory(config)
        .SelectMany(x => x.Items)
        .GroupBy(x => x.ItemId)
        .ToDictionary(x => x.Key, x => x.Sum(y => (int)y.Quantity));
}
```

- [ ] **Step 2: Build**

Run:

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug -p:Platform=x64
```

Expected: build succeeds.

- [ ] **Step 3: Commit**

```powershell
git add MarketMafioso/InventoryScanner.cs
git commit -m "feat: count player inventory for workshop prep"
```

### Task 7: Wire Services Into Plugin

**Files:**
- Modify: `MarketMafioso/Plugin.cs`
- Modify: `MarketMafioso/Windows/MainWindow.cs`

- [ ] **Step 1: Add service fields**

Modify `Plugin.cs` using directives:

```csharp
using MarketMafioso.WorkshopPrep;
```

Add fields near existing service fields:

```csharp
private readonly WorkshopProjectCatalog workshopCatalog;
private readonly VIWIWorkshoppaIpc viwiWorkshoppaIpc;
```

Initialize after `scanner`:

```csharp
workshopCatalog = new WorkshopProjectCatalog(DataManager, Log);
viwiWorkshoppaIpc = new VIWIWorkshoppaIpc(new DalamudVIWIWorkshoppaIpcAdapter(PluginInterface));
```

Update `MainWindow` construction:

```csharp
mainWindow = new MainWindow(
    Configuration,
    reporter,
    scanner,
    autoRetainerRefresh,
    workshopCatalog,
    viwiWorkshoppaIpc,
    Log);
```

- [ ] **Step 2: Update MainWindow constructor**

Modify `MainWindow.cs` using directives:

```csharp
using System.Collections.Generic;
using MarketMafioso.WorkshopPrep;
```

Add fields:

```csharp
private readonly WorkshopProjectCatalog workshopCatalog;
private readonly VIWIWorkshoppaIpc viwiWorkshoppaIpc;
private string workshopSearch = string.Empty;
private bool confirmViwiClear;
private string workshopStatus = "Workshop prep has not run.";
```

Update constructor parameters and assignments:

```csharp
WorkshopProjectCatalog workshopCatalog,
VIWIWorkshoppaIpc viwiWorkshoppaIpc,
IPluginLog log)
```

```csharp
this.workshopCatalog = workshopCatalog;
this.viwiWorkshoppaIpc = viwiWorkshoppaIpc;
```

- [ ] **Step 3: Build**

Run:

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug -p:Platform=x64
```

Expected: build succeeds.

- [ ] **Step 4: Commit**

```powershell
git add MarketMafioso/Plugin.cs MarketMafioso/Windows/MainWindow.cs
git commit -m "feat: wire workshop prep services"
```

### Task 8: Add Workshop Prep UI For Queue And Availability

**Files:**
- Modify: `MarketMafioso/Windows/MainWindow.cs`

- [ ] **Step 1: Add tab**

In `Draw()`, add a new tab after `Inventory Reporter`:

```csharp
if (ImGui.BeginTabItem("Workshop Prep"))
{
    DrawWorkshopPrepTab();
    ImGui.EndTabItem();
}
```

- [ ] **Step 2: Add queue and material UI methods**

Add methods to `MainWindow.cs`:

```csharp
private void DrawWorkshopPrepTab()
{
    ImGui.Spacing();
    ImGui.TextColored(ColHeader, "Workshop Prep");
    ImGui.TextWrapped("Prepare workshop project materials with MarketMafioso, then hand the prep queue to VIWI Workshoppa when ready.");
    ImGui.Spacing();

    DrawWorkshopProjectPicker();
    ImGui.Spacing();
    DrawWorkshopPrepQueue();
    ImGui.Spacing();
    DrawWorkshopMaterialSummary();
    ImGui.Spacing();
    DrawWorkshopPrepActions();
}

private void DrawWorkshopProjectPicker()
{
    ImGui.TextColored(ColHeader, "Add Project");
    ImGui.Separator();
    ImGui.SetNextItemWidth(-1);
    ImGui.InputTextWithHint("##workshopSearch", "Search workshop projects...", ref workshopSearch, 128);

    var projects = workshopCatalog.GetProjects()
        .Where(x => x.Name.Contains(workshopSearch, StringComparison.OrdinalIgnoreCase))
        .Take(20);

    foreach (var project in projects)
    {
        if (ImGui.Selectable($"{project.Name}##workshop{project.WorkshopItemId}"))
        {
            var existing = config.WorkshopPrepQueue.FirstOrDefault(x => x.WorkshopItemId == project.WorkshopItemId);
            if (existing == null)
                config.WorkshopPrepQueue.Add(new WorkshopPrepQueueItem { WorkshopItemId = project.WorkshopItemId, Quantity = 1 });
            else
                existing.Quantity++;

            config.Save();
        }
    }
}

private void DrawWorkshopPrepQueue()
{
    ImGui.TextColored(ColHeader, "Prep Queue");
    ImGui.Separator();

    var projects = workshopCatalog.GetProjects().ToDictionary(x => x.WorkshopItemId);
    WorkshopPrepQueueItem? itemToRemove = null;
    foreach (var item in config.WorkshopPrepQueue)
    {
        if (!projects.TryGetValue(item.WorkshopItemId, out var project))
            continue;

        var quantity = item.Quantity;
        ImGui.SetNextItemWidth(80);
        if (ImGui.InputInt($"##qty{item.WorkshopItemId}", ref quantity))
        {
            item.Quantity = Math.Max(1, quantity);
            config.Save();
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(project.Name);
        ImGui.SameLine();
        if (ImGui.SmallButton($"Remove##remove{item.WorkshopItemId}"))
            itemToRemove = item;
    }

    if (itemToRemove != null)
    {
        config.WorkshopPrepQueue.Remove(itemToRemove);
        config.Save();
    }
}

private IReadOnlyList<WorkshopMaterialAvailability> GetWorkshopAvailability()
{
    var requirements = workshopCatalog.BuildRequirements(config.WorkshopPrepQueue);
    var playerInventory = scanner.CountPlayerInventory(config);
    return WorkshopMaterialAvailabilityService.BuildAvailability(requirements, playerInventory, config);
}

private void DrawWorkshopMaterialSummary()
{
    ImGui.TextColored(ColHeader, "Materials");
    ImGui.Separator();

    var availability = GetWorkshopAvailability();
    if (availability.Count == 0)
    {
        ImGui.TextColored(ColMuted, "No workshop materials yet. Add projects to the prep queue.");
        return;
    }

    if (ImGui.BeginTable("WorkshopPrepMaterials", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
    {
        ImGui.TableSetupColumn("Item");
        ImGui.TableSetupColumn("Required");
        ImGui.TableSetupColumn("Player");
        ImGui.TableSetupColumn("Retainers");
        ImGui.TableSetupColumn("Shortage");
        ImGui.TableSetupColumn("Candidates");
        ImGui.TableHeadersRow();

        foreach (var item in availability)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.ItemName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.Required.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.PlayerInventory.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(item.RetainerCache.ToString());
            ImGui.TableNextColumn();
            ImGui.TextColored(item.Shortage > 0 ? ColError : ColSuccess, item.Shortage.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(string.Join(", ", item.CandidateRetainers.Select(x => x.RetainerName)));
        }

        ImGui.EndTable();
    }
}
```

- [ ] **Step 3: Add actions**

Add this method to `MainWindow.cs`:

```csharp
private void DrawWorkshopPrepActions()
{
    ImGui.TextColored(ColHeader, "Actions");
    ImGui.Separator();

    if (ImGui.Button("Refresh Retainer Cache"))
        autoRetainerRefresh.StartFullRefresh();

    ImGui.SameLine();
    if (ImGui.Button("Send Queue To VIWI"))
        confirmViwiClear = true;

    if (confirmViwiClear)
    {
        ImGui.TextColored(ColMuted, "This will clear VIWI Workshoppa's queue and send the MarketMafioso prep queue.");
        if (ImGui.Button("Confirm VIWI Queue Sync"))
        {
            var result = viwiWorkshoppaIpc.SendQueue(config.WorkshopPrepQueue, clearExisting: true);
            workshopStatus = result.Message;
            confirmViwiClear = false;
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel VIWI Queue Sync"))
            confirmViwiClear = false;
    }

    if (ImGui.Button("Clear Prep Queue"))
    {
        config.WorkshopPrepQueue.Clear();
        config.Save();
        workshopStatus = "Cleared prep queue.";
    }

    ImGui.Spacing();
    ImGui.TextColored(GetWorkshopStatusColor(), workshopStatus);
}

private Vector4 GetWorkshopStatusColor()
{
    if (workshopStatus.Contains("unable", StringComparison.OrdinalIgnoreCase) ||
        workshopStatus.Contains("failed", StringComparison.OrdinalIgnoreCase))
        return ColError;

    if (workshopStatus.Contains("sent", StringComparison.OrdinalIgnoreCase) ||
        workshopStatus.Contains("cleared", StringComparison.OrdinalIgnoreCase))
        return ColSuccess;

    return ColMuted;
}
```

- [ ] **Step 4: Build**

Run:

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug -p:Platform=x64
```

Expected: build succeeds and syncs debug plugin.

- [ ] **Step 5: Commit**

```powershell
git add MarketMafioso/Windows/MainWindow.cs
git commit -m "feat: add workshop prep ui"
```

### Task 9: Implement Live Retainer Inventory Scan For Restock

**Files:**
- Create: `MarketMafioso/WorkshopPrep/WorkshopRetainerRestockService.cs`
- Modify: `MarketMafioso/Plugin.cs`
- Modify: `MarketMafioso/Windows/MainWindow.cs`

- [ ] **Step 1: Add restock service skeleton and live slot scan**

Create `MarketMafioso/WorkshopPrep/WorkshopRetainerRestockService.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace MarketMafioso.WorkshopPrep;

public sealed class WorkshopRetainerRestockService
{
    private static readonly InventoryType[] RetainerPages =
    [
        InventoryType.RetainerPage1,
        InventoryType.RetainerPage2,
        InventoryType.RetainerPage3,
        InventoryType.RetainerPage4,
        InventoryType.RetainerPage5,
        InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    private readonly IPluginLog log;
    private bool isRunning;
    private string lastStatus = "Workshop material restock has not run.";

    public WorkshopRetainerRestockService(IPluginLog log)
    {
        this.log = log;
    }

    public bool IsRunning => isRunning;
    public string LastStatus => lastStatus;

    public Task StartAsync(IReadOnlyList<WorkshopMaterialAvailability> availability)
    {
        if (isRunning)
        {
            lastStatus = "Workshop material restock is already running.";
            return Task.CompletedTask;
        }

        var shortages = availability.Where(x => x.Shortage > 0).ToList();
        if (shortages.Count == 0)
        {
            lastStatus = "No workshop material shortages to restock.";
            return Task.CompletedTask;
        }

        isRunning = true;
        try
        {
            lastStatus = "Workshop material restock planning complete. Retainer UI withdrawal is the next implementation slice.";
            log.Information($"[MarketMafioso] Planned workshop restock for {shortages.Count} shortage item(s).");
        }
        finally
        {
            isRunning = false;
        }

        return Task.CompletedTask;
    }

    public unsafe IReadOnlyList<LiveRetainerStack> ScanLiveRetainerStacks(IReadOnlySet<uint> itemIds)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return [];

        var stacks = new List<LiveRetainerStack>();
        foreach (var page in RetainerPages)
        {
            var container = inventoryManager->GetInventoryContainer(page);
            if (container == null || !container->IsLoaded)
                continue;

            for (var slotIndex = 0; slotIndex < container->Size; slotIndex++)
            {
                var slot = container->GetInventorySlot(slotIndex);
                if (slot == null || slot->ItemId == 0 || !itemIds.Contains(slot->ItemId))
                    continue;

                stacks.Add(new LiveRetainerStack(page, slotIndex, slot->ItemId, slot->Quantity));
            }
        }

        return stacks;
    }
}

public sealed record LiveRetainerStack(
    InventoryType Page,
    int SlotIndex,
    uint ItemId,
    int Quantity);
```

- [ ] **Step 2: Wire service**

Modify `Plugin.cs`:

```csharp
private readonly WorkshopRetainerRestockService workshopRetainerRestock;
```

Initialize:

```csharp
workshopRetainerRestock = new WorkshopRetainerRestockService(Log);
```

Pass to `MainWindow`:

```csharp
workshopRetainerRestock,
```

Modify `MainWindow.cs` constructor and fields:

```csharp
private readonly WorkshopRetainerRestockService workshopRetainerRestock;
```

```csharp
WorkshopRetainerRestockService workshopRetainerRestock,
```

```csharp
this.workshopRetainerRestock = workshopRetainerRestock;
```

- [ ] **Step 3: Add UI button for the runner**

Add the `Restock Materials From Retainers` action before `Send Queue To VIWI`:

```csharp
if (ImGui.Button("Restock Materials From Retainers"))
    _ = workshopRetainerRestock.StartAsync(GetWorkshopAvailability());
```

After action status, show runner status:

```csharp
ImGui.TextColored(workshopRetainerRestock.IsRunning ? ColHeader : ColMuted, workshopRetainerRestock.LastStatus);
```

- [ ] **Step 4: Build**

Run:

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug -p:Platform=x64
```

Expected: build succeeds. The button now plans restock but does not yet drive retainer UI.

- [ ] **Step 5: Commit**

```powershell
git add MarketMafioso/WorkshopPrep/WorkshopRetainerRestockService.cs MarketMafioso/Plugin.cs MarketMafioso/Windows/MainWindow.cs
git commit -m "feat: add workshop retainer restock runner scaffold"
```

### Task 10: Implement Retainer UI Withdrawal State Machine

**Files:**
- Modify: `MarketMafioso/WorkshopPrep/WorkshopRetainerRestockService.cs`
- Optionally modify: `MarketMafioso/AutoRetainerRefreshService.cs` if shared helper extraction is needed

- [ ] **Step 1: Add explicit state enum**

Add to `WorkshopRetainerRestockService.cs`:

```csharp
public enum WorkshopRetainerRestockState
{
    Idle,
    Planning,
    WaitingForRetainerList,
    OpeningRetainer,
    OpeningInventory,
    WithdrawingItems,
    ClosingRetainer,
    Complete,
    Failed,
}
```

Add property:

```csharp
public WorkshopRetainerRestockState State { get; private set; } = WorkshopRetainerRestockState.Idle;
```

- [ ] **Step 2: Replace scaffold StartAsync with state transitions**

Replace `StartAsync` body with:

```csharp
public async Task StartAsync(IReadOnlyList<WorkshopMaterialAvailability> availability)
{
    if (isRunning)
    {
        lastStatus = "Workshop material restock is already running.";
        return;
    }

    var shortages = availability.Where(x => x.Shortage > 0).ToList();
    if (shortages.Count == 0)
    {
        lastStatus = "No workshop material shortages to restock.";
        return;
    }

    isRunning = true;
    State = WorkshopRetainerRestockState.Planning;
    try
    {
        var remaining = shortages.ToDictionary(x => x.ItemId, x => x.Shortage);
        State = WorkshopRetainerRestockState.WaitingForRetainerList;
        await WaitForRetainerListAsync().ConfigureAwait(false);

        foreach (var candidate in shortages.SelectMany(x => x.CandidateRetainers).DistinctBy(x => x.RetainerId))
        {
            State = WorkshopRetainerRestockState.OpeningRetainer;
            await OpenRetainerAsync(candidate).ConfigureAwait(false);

            State = WorkshopRetainerRestockState.OpeningInventory;
            await OpenRetainerInventoryAsync().ConfigureAwait(false);

            State = WorkshopRetainerRestockState.WithdrawingItems;
            await WithdrawFromOpenRetainerAsync(remaining).ConfigureAwait(false);

            State = WorkshopRetainerRestockState.ClosingRetainer;
            await CloseRetainerAsync().ConfigureAwait(false);

            if (remaining.Values.All(x => x <= 0))
                break;
        }

        State = WorkshopRetainerRestockState.Complete;
        lastStatus = remaining.Values.All(x => x <= 0)
            ? "Workshop material restock complete."
            : $"Workshop material restock finished with remaining shortages: {string.Join(", ", remaining.Where(x => x.Value > 0).Select(x => $"{x.Key}:{x.Value}"))}.";
    }
    catch (Exception ex)
    {
        State = WorkshopRetainerRestockState.Failed;
        lastStatus = $"Workshop material restock failed during {State}. {ex.Message}";
        log.Error(ex, "[MarketMafioso] Workshop material restock failed.");
    }
    finally
    {
        isRunning = false;
        if (State is not WorkshopRetainerRestockState.Complete and not WorkshopRetainerRestockState.Failed)
            State = WorkshopRetainerRestockState.Idle;
    }
}
```

- [ ] **Step 3: Implement framework-wait helpers**

Add methods that mirror existing `AutoRetainerRefreshService` discipline:

```csharp
private static async Task WaitForRetainerListAsync()
{
    await Plugin.Framework.RunOnTick(() => true);
}

private static async Task OpenRetainerAsync(RetainerMaterialCandidate candidate)
{
    await Plugin.Framework.RunOnTick(() =>
    {
        Plugin.Log.Information($"[MarketMafioso] Selected candidate retainer {candidate.RetainerName} ({candidate.RetainerId}) for workshop material retrieval.");
    });
}

private static async Task OpenRetainerInventoryAsync()
{
    await Plugin.Framework.RunOnTick(() => true);
}

private async Task WithdrawFromOpenRetainerAsync(Dictionary<uint, int> remaining)
{
    var itemIds = remaining.Where(x => x.Value > 0).Select(x => x.Key).ToHashSet();
    var liveStacks = await Plugin.Framework.RunOnTick(() => ScanLiveRetainerStacks(itemIds)).ConfigureAwait(false);
    foreach (var stack in liveStacks)
    {
        if (!remaining.TryGetValue(stack.ItemId, out var needed) || needed <= 0)
            continue;

        var taken = Math.Min(needed, stack.Quantity);
        remaining[stack.ItemId] -= taken;
        log.Information($"[MarketMafioso] Planned retrieval of {taken}x item {stack.ItemId} from {stack.Page}/{stack.SlotIndex}.");
    }
}

private static async Task CloseRetainerAsync()
{
    await Plugin.Framework.RunOnTick(() => true);
}
```

This first state-machine task logs planned live-stack retrievals and proves candidate selection plus live page scanning before item-retrieval callbacks are enabled. The next task enables context-menu retrieval using the same state transitions.

- [ ] **Step 4: Build and manually inspect behavior**

Run:

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug -p:Platform=x64
```

Expected: build succeeds. In-game, pressing `Restock Materials From Retainers` should plan and log without retrieving items yet.

- [ ] **Step 5: Commit**

```powershell
git add MarketMafioso/WorkshopPrep/WorkshopRetainerRestockService.cs
git commit -m "feat: add workshop restock state machine"
```

### Task 11: Enable Retainer Item Retrieval Callbacks

**Files:**
- Modify: `MarketMafioso/WorkshopPrep/WorkshopRetainerRestockService.cs`

- [ ] **Step 1: Add addon and callback imports**

Add using directives:

```csharp
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
```

- [ ] **Step 2: Add retrieval result model**

Add below `LiveRetainerStack`:

```csharp
public sealed record RetainerRetrievalResult(
    bool Success,
    int Retrieved,
    string Message);
```

- [ ] **Step 3: Replace planned retrieval decrement with actual retrieval call**

In `WithdrawFromOpenRetainerAsync`, replace the block that decrements `remaining` from live stacks with:

```csharp
foreach (var stack in liveStacks)
{
    if (!remaining.TryGetValue(stack.ItemId, out var needed) || needed <= 0)
        continue;

    var quantity = Math.Min(needed, stack.Quantity);
    var result = await Plugin.Framework.RunOnTick(() => RetrieveFromLiveStack(stack, quantity)).ConfigureAwait(false);
    if (!result.Success)
        throw new InvalidOperationException(result.Message);

    remaining[stack.ItemId] -= result.Retrieved;
    log.Information($"[MarketMafioso] Retrieved {result.Retrieved}x item {stack.ItemId} from {stack.Page}/{stack.SlotIndex}.");
}
```

- [ ] **Step 4: Add context menu retrieval method**

Add this method to `WorkshopRetainerRestockService`:

```csharp
private unsafe RetainerRetrievalResult RetrieveFromLiveStack(LiveRetainerStack stack, int quantity)
{
    if (quantity <= 0)
        return new(false, 0, $"Invalid retrieval quantity {quantity} for item {stack.ItemId}.");

    var inventoryManager = InventoryManager.Instance();
    if (inventoryManager == null)
        return new(false, 0, "Inventory manager is unavailable.");

    var container = inventoryManager->GetInventoryContainer(stack.Page);
    if (container == null || !container->IsLoaded)
        return new(false, 0, $"Retainer inventory page {stack.Page} is not loaded.");

    var slot = container->GetInventorySlot(stack.SlotIndex);
    if (slot == null || slot->ItemId != stack.ItemId || slot->Quantity <= 0)
        return new(false, 0, $"Expected item {stack.ItemId} was not found at {stack.Page}/{stack.SlotIndex}.");

    var retrieveQuantity = Math.Min(quantity, slot->Quantity);
    var agent = AgentInventoryContext.Instance();
    agent->OpenForItemSlot(stack.Page, stack.SlotIndex, 0, AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer)->GetAddonId());

    var contextMenu = Plugin.GameGui.GetAddonByName<AtkUnitBase>("ContextMenu", 1);
    if (contextMenu == null || !contextMenu->IsVisible)
        return new(false, 0, $"Context menu did not open for item {stack.ItemId}.");

    var retrieveAllIndex = -1;
    var retrieveQuantityIndex = -1;
    var contextAgent = AgentInventoryContext.Instance();
    for (var index = 0; index < contextAgent->EventParamsSpan.Length; index++)
    {
        var value = contextAgent->EventParamsSpan[index];
        if (value.Type != AtkValueType.String || value.String == null)
            continue;

        var label = MemoryHelper.ReadSeStringNullTerminated((nint)value.String).TextValue;
        if (label == Plugin.DataManager.GetExcelSheet<Addon>().GetRow(98).Text.ExtractText())
            retrieveAllIndex = index;
        if (label == Plugin.DataManager.GetExcelSheet<Addon>().GetRow(773).Text.ExtractText())
            retrieveQuantityIndex = index;
    }

    if (retrieveQuantity >= slot->Quantity)
    {
        if (retrieveAllIndex < 0)
            return new(false, 0, $"Retrieve-all action was not available for item {stack.ItemId}.");

        contextMenu->FireCallbackInt(retrieveAllIndex);
        return new(true, retrieveQuantity, "Retrieved full stack.");
    }

    if (retrieveQuantityIndex < 0)
        return new(false, 0, $"Retrieve-quantity action was not available for item {stack.ItemId}.");

    contextMenu->FireCallbackInt(retrieveQuantityIndex);
    var numeric = Plugin.GameGui.GetAddonByName<AtkUnitBase>("InputNumeric", 1);
    if (numeric == null || !numeric->IsVisible)
        return new(false, 0, $"Numeric quantity popup did not open for item {stack.ItemId}.");

    numeric->FireCallbackInt(retrieveQuantity);
    return new(true, retrieveQuantity, "Retrieved partial stack.");
}
```

- [ ] **Step 5: Build**

Run:

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug -p:Platform=x64
```

Expected: build succeeds. If `EventParamsSpan` is unavailable in the installed FFXIVClientStructs version, use the same indexed `EventParams` loop pattern already present in Artisan's `RetainerHandlers.OpenItemContextMenu`.

- [ ] **Step 6: Manual in-game smoke test**

Run MarketMafioso from the debug dev-plugin sync, open `/mmf`, build a one-project prep queue, refresh retainer cache, and run `Restock Materials From Retainers` while on the retainer list. Expected: the runner retrieves only missing direct turn-in materials and stops with either complete status or a phase-specific failure.

- [ ] **Step 7: Commit**

```powershell
git add MarketMafioso/WorkshopPrep/WorkshopRetainerRestockService.cs
git commit -m "feat: retrieve workshop materials from retainers"
```

### Task 12: Final Verification

**Files:**
- No source changes expected unless verification exposes fixes.

- [ ] **Step 1: Run focused tests**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -p:Platform=x64 -v minimal
```

Expected: workshop prep tests pass.

- [ ] **Step 2: Run full solution tests**

Run:

```powershell
dotnet test "MarketMafioso.sln" -p:Platform=x64 -v minimal
```

Expected: all tests pass.

- [ ] **Step 3: Build Debug**

Run:

```powershell
dotnet build "MarketMafioso.sln" -c Debug -p:Platform=x64
```

Expected: build succeeds and debug plugin sync runs.

- [ ] **Step 4: Verify format**

Run:

```powershell
dotnet format "MarketMafioso.sln" --verify-no-changes
```

Expected: no formatting changes needed.

- [ ] **Step 5: Inspect git diff**

Run:

```powershell
git status --short
git diff --stat
```

Expected: only workshop-prep source/test/doc files are changed, plus solution/project updates. Existing unrelated modified files remain untouched.
