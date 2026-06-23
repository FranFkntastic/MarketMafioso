# Native Workshop Runner Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build an optional MarketMafioso-native runner that assembles the existing Workshop Prep queue without replacing VIWI Workshoppa handoff.

**Architecture:** Add a narrow workshop assembly subsystem under `MarketMafioso/WorkshopPrep/`. Pure planning and preflight logic should be tested first; unsafe addon automation stays isolated in one adapter-like service; `MainWindow` only renders controls and runner status.

**Tech Stack:** C# 12, `net8.0-windows` plugin, Dalamud services, FFXIVClientStructs for addon/agent access, xUnit tests in `MarketMafioso.Tests`.

---

## File Structure

- Create `MarketMafioso/WorkshopPrep/WorkshopAssemblyModels.cs`
  - Runner states, queue snapshot records, progress/status records, and action results.
- Create `MarketMafioso/WorkshopPrep/WorkshopAssemblyPlanBuilder.cs`
  - Converts `WorkshopPrepQueueItem` plus `WorkshopProjectDefinition` into a validated execution plan.
- Create `MarketMafioso/WorkshopPrep/WorkshopAssemblyPreflightService.cs`
  - Checks queue validity and player material availability before execution starts.
- Create `MarketMafioso/WorkshopPrep/WorkshopAssemblyTiming.cs`
  - Names timing constants, especially the post-contribution lockout.
- Create `MarketMafioso/WorkshopPrep/WorkshopAssemblyUiAutomation.cs`
  - Isolates unsafe UI reads/callbacks and diagnostic addon descriptions.
- Create `MarketMafioso/WorkshopPrep/WorkshopAssemblyRunner.cs`
  - Owns framework-update state machine, start/stop lifecycle, and status updates.
- Modify `MarketMafioso/Plugin.cs`
  - Construct and dispose the runner.
- Modify `MarketMafioso/Windows/MainWindow.cs`
  - Add native-runner controls and display status.
- Modify `AGENTS.md`
  - Update the product boundary from "must not operate Workshoppa projects" to "may execute MarketMafioso-owned prep queues."
- Modify `README.md`
  - Mention the native runner once it exists, while keeping VIWI attribution.
- Create `MarketMafioso.Tests/WorkshopPrep/WorkshopAssemblyPlanBuilderTests.cs`
- Create `MarketMafioso.Tests/WorkshopPrep/WorkshopAssemblyPreflightServiceTests.cs`
- Create `MarketMafioso.Tests/WorkshopPrep/WorkshopAssemblyTimingTests.cs`

## Task 1: Add Pure Assembly Models

**Files:**

- Create: `MarketMafioso/WorkshopPrep/WorkshopAssemblyModels.cs`
- Test: no direct test file yet; tests begin in Task 2 against these models.

- [ ] **Step 1: Create model file**

Add this file:

```csharp
using System;
using System.Collections.Generic;

namespace MarketMafioso.WorkshopPrep;

public enum WorkshopAssemblyRunnerState
{
    Idle,
    Preflight,
    WaitingForFabricationStation,
    OpeningProject,
    WaitingForMaterialRequest,
    SubmittingMaterial,
    WaitingForContributionLockout,
    ConfirmingContribution,
    AdvancingProject,
    Complete,
    Stopped,
    Failed,
}

public sealed record WorkshopAssemblyQueueEntry(
    uint WorkshopItemId,
    string ProjectName,
    int Quantity,
    IReadOnlyList<WorkshopMaterialRequirement> Materials);

public sealed record WorkshopAssemblyPlan(
    IReadOnlyList<WorkshopAssemblyQueueEntry> Entries,
    IReadOnlyList<WorkshopMaterialRequirement> TotalMaterials);

public sealed record WorkshopAssemblyProgress(
    WorkshopAssemblyRunnerState State,
    string Message,
    string? ActiveProjectName,
    uint? ActiveWorkshopItemId,
    uint? ActiveMaterialItemId,
    int CompletedProjects,
    int TotalProjects,
    DateTimeOffset UpdatedAt);

public sealed record WorkshopAssemblyActionResult(
    bool Success,
    string Message);

public sealed record WorkshopAssemblyPreflightResult(
    bool CanStart,
    string Message,
    WorkshopAssemblyPlan? Plan);
```

- [ ] **Step 2: Build plugin project**

Run:

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
```

Expected: build succeeds and Debug sync runs.

- [ ] **Step 3: Commit**

```powershell
git add MarketMafioso/WorkshopPrep/WorkshopAssemblyModels.cs
git commit -m "feat: add workshop assembly models"
```

## Task 2: Build Validated Assembly Plans

**Files:**

- Create: `MarketMafioso/WorkshopPrep/WorkshopAssemblyPlanBuilder.cs`
- Create: `MarketMafioso.Tests/WorkshopPrep/WorkshopAssemblyPlanBuilderTests.cs`

- [ ] **Step 1: Write failing tests**

Create `MarketMafioso.Tests/WorkshopPrep/WorkshopAssemblyPlanBuilderTests.cs`:

```csharp
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Tests.WorkshopPrep;

public sealed class WorkshopAssemblyPlanBuilderTests
{
    [Fact]
    public void Build_rejects_empty_queue()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WorkshopAssemblyPlanBuilder.Build([], [BuildProject(10, "Project", 100, 2)]));

        Assert.Equal("Workshop assembly queue is empty.", ex.Message);
    }

    [Fact]
    public void Build_rejects_unknown_project()
    {
        var queue = new[] { new WorkshopPrepQueueItem { WorkshopItemId = 99, Quantity = 1 } };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            WorkshopAssemblyPlanBuilder.Build(queue, [BuildProject(10, "Project", 100, 2)]));

        Assert.Equal("Unknown workshop project id 99 cannot be assembled.", ex.Message);
    }

    [Fact]
    public void Build_rejects_non_positive_quantity()
    {
        var queue = new[] { new WorkshopPrepQueueItem { WorkshopItemId = 10, Quantity = 0 } };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            WorkshopAssemblyPlanBuilder.Build(queue, [BuildProject(10, "Project", 100, 2)]));

        Assert.Equal("Workshop project Project has invalid quantity 0.", ex.Message);
    }

    [Fact]
    public void Build_aggregates_materials_across_quantities()
    {
        var queue =
            new[]
            {
                new WorkshopPrepQueueItem { WorkshopItemId = 10, Quantity = 2 },
                new WorkshopPrepQueueItem { WorkshopItemId = 11, Quantity = 1 },
            };

        var plan = WorkshopAssemblyPlanBuilder.Build(
            queue,
            [
                BuildProject(10, "Project A", 100, 3),
                BuildProject(11, "Project B", 100, 5),
            ]);

        Assert.Equal(2, plan.Entries.Count);
        Assert.Equal(2, plan.Entries[0].Quantity);
        var material = Assert.Single(plan.TotalMaterials);
        Assert.Equal((uint)100, material.ItemId);
        Assert.Equal(11, material.Quantity);
    }

    private static WorkshopProjectDefinition BuildProject(
        uint workshopItemId,
        string name,
        uint materialItemId,
        int materialQuantity)
    {
        return new WorkshopProjectDefinition(
            workshopItemId,
            workshopItemId + 1000,
            name,
            0,
            [new WorkshopMaterialRequirement(materialItemId, "Material", 0, materialQuantity)]);
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~WorkshopAssemblyPlanBuilderTests"
```

Expected: compile fails because `WorkshopAssemblyPlanBuilder` does not exist.

- [ ] **Step 3: Implement plan builder**

Create `MarketMafioso/WorkshopPrep/WorkshopAssemblyPlanBuilder.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.WorkshopPrep;

public static class WorkshopAssemblyPlanBuilder
{
    public static WorkshopAssemblyPlan Build(
        IReadOnlyList<WorkshopPrepQueueItem> queue,
        IReadOnlyList<WorkshopProjectDefinition> projects)
    {
        if (queue.Count == 0)
            throw new InvalidOperationException("Workshop assembly queue is empty.");

        var projectById = projects.ToDictionary(x => x.WorkshopItemId);
        var entries = new List<WorkshopAssemblyQueueEntry>();
        var materialTotals = new Dictionary<uint, WorkshopMaterialRequirement>();

        foreach (var queueItem in queue)
        {
            if (!projectById.TryGetValue(queueItem.WorkshopItemId, out var project))
                throw new InvalidOperationException($"Unknown workshop project id {queueItem.WorkshopItemId} cannot be assembled.");

            if (queueItem.Quantity <= 0)
                throw new InvalidOperationException($"Workshop project {project.Name} has invalid quantity {queueItem.Quantity}.");

            entries.Add(new WorkshopAssemblyQueueEntry(
                project.WorkshopItemId,
                project.Name,
                queueItem.Quantity,
                project.Materials));

            foreach (var material in project.Materials)
            {
                var requiredQuantity = material.Quantity * queueItem.Quantity;
                if (materialTotals.TryGetValue(material.ItemId, out var existing))
                {
                    materialTotals[material.ItemId] = existing with
                    {
                        Quantity = existing.Quantity + requiredQuantity,
                    };
                }
                else
                {
                    materialTotals.Add(material.ItemId, material with
                    {
                        Quantity = requiredQuantity,
                    });
                }
            }
        }

        return new WorkshopAssemblyPlan(
            entries,
            materialTotals.Values.OrderBy(x => x.ItemName, StringComparer.OrdinalIgnoreCase).ToList());
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~WorkshopAssemblyPlanBuilderTests"
```

Expected: all `WorkshopAssemblyPlanBuilderTests` pass.

- [ ] **Step 5: Commit**

```powershell
git add MarketMafioso/WorkshopPrep/WorkshopAssemblyPlanBuilder.cs MarketMafioso.Tests/WorkshopPrep/WorkshopAssemblyPlanBuilderTests.cs
git commit -m "feat: build workshop assembly plans"
```

## Task 3: Add Player-Material Preflight

**Files:**

- Create: `MarketMafioso/WorkshopPrep/WorkshopAssemblyPreflightService.cs`
- Create: `MarketMafioso.Tests/WorkshopPrep/WorkshopAssemblyPreflightServiceTests.cs`

- [ ] **Step 1: Write failing tests**

Create `MarketMafioso.Tests/WorkshopPrep/WorkshopAssemblyPreflightServiceTests.cs`:

```csharp
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Tests.WorkshopPrep;

public sealed class WorkshopAssemblyPreflightServiceTests
{
    [Fact]
    public void Check_returns_blocked_when_materials_are_missing()
    {
        var queue = new[] { new WorkshopPrepQueueItem { WorkshopItemId = 10, Quantity = 1 } };
        var projects = new[] { BuildProject(10, "Project", 100, "Cedar Lumber", 9) };
        var playerInventory = new Dictionary<uint, int> { [100] = 4 };

        var result = WorkshopAssemblyPreflightService.Check(queue, projects, playerInventory);

        Assert.False(result.CanStart);
        Assert.Null(result.Plan);
        Assert.Equal("Missing player materials: Cedar Lumber x5.", result.Message);
    }

    [Fact]
    public void Check_returns_plan_when_materials_are_available()
    {
        var queue = new[] { new WorkshopPrepQueueItem { WorkshopItemId = 10, Quantity = 2 } };
        var projects = new[] { BuildProject(10, "Project", 100, "Cedar Lumber", 9) };
        var playerInventory = new Dictionary<uint, int> { [100] = 18 };

        var result = WorkshopAssemblyPreflightService.Check(queue, projects, playerInventory);

        Assert.True(result.CanStart);
        Assert.NotNull(result.Plan);
        Assert.Equal("Workshop assembly preflight complete.", result.Message);
    }

    private static WorkshopProjectDefinition BuildProject(
        uint workshopItemId,
        string name,
        uint materialItemId,
        string materialName,
        int materialQuantity)
    {
        return new WorkshopProjectDefinition(
            workshopItemId,
            workshopItemId + 1000,
            name,
            0,
            [new WorkshopMaterialRequirement(materialItemId, materialName, 0, materialQuantity)]);
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~WorkshopAssemblyPreflightServiceTests"
```

Expected: compile fails because `WorkshopAssemblyPreflightService` does not exist.

- [ ] **Step 3: Implement preflight service**

Create `MarketMafioso/WorkshopPrep/WorkshopAssemblyPreflightService.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.WorkshopPrep;

public static class WorkshopAssemblyPreflightService
{
    public static WorkshopAssemblyPreflightResult Check(
        IReadOnlyList<WorkshopPrepQueueItem> queue,
        IReadOnlyList<WorkshopProjectDefinition> projects,
        IReadOnlyDictionary<uint, int> playerInventory)
    {
        var plan = WorkshopAssemblyPlanBuilder.Build(queue, projects);
        var missing = plan.TotalMaterials
            .Select(material =>
            {
                playerInventory.TryGetValue(material.ItemId, out var available);
                return new
                {
                    material.ItemName,
                    Missing = material.Quantity - available,
                };
            })
            .Where(x => x.Missing > 0)
            .OrderBy(x => x.ItemName)
            .ToList();

        if (missing.Count > 0)
        {
            var missingText = string.Join(", ", missing.Select(x => $"{x.ItemName} x{x.Missing}"));
            return new(false, $"Missing player materials: {missingText}.", null);
        }

        return new(true, "Workshop assembly preflight complete.", plan);
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~WorkshopAssemblyPreflightServiceTests"
```

Expected: all `WorkshopAssemblyPreflightServiceTests` pass.

- [ ] **Step 5: Commit**

```powershell
git add MarketMafioso/WorkshopPrep/WorkshopAssemblyPreflightService.cs MarketMafioso.Tests/WorkshopPrep/WorkshopAssemblyPreflightServiceTests.cs
git commit -m "feat: add workshop assembly preflight"
```

## Task 4: Centralize Assembly Timing

**Files:**

- Create: `MarketMafioso/WorkshopPrep/WorkshopAssemblyTiming.cs`
- Create: `MarketMafioso.Tests/WorkshopPrep/WorkshopAssemblyTimingTests.cs`

- [ ] **Step 1: Write failing tests**

Create `MarketMafioso.Tests/WorkshopPrep/WorkshopAssemblyTimingTests.cs`:

```csharp
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Tests.WorkshopPrep;

public sealed class WorkshopAssemblyTimingTests
{
    [Fact]
    public void PostContributionLockout_starts_conservative()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), WorkshopAssemblyTiming.PostContributionLockout);
    }

    [Fact]
    public void AddonTimeout_allows_visible_state_diagnostics_before_failure()
    {
        Assert.True(WorkshopAssemblyTiming.AddonTimeout > WorkshopAssemblyTiming.PostContributionLockout);
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~WorkshopAssemblyTimingTests"
```

Expected: compile fails because `WorkshopAssemblyTiming` does not exist.

- [ ] **Step 3: Implement timing constants**

Create `MarketMafioso/WorkshopPrep/WorkshopAssemblyTiming.cs`:

```csharp
using System;

namespace MarketMafioso.WorkshopPrep;

public static class WorkshopAssemblyTiming
{
    public static readonly TimeSpan AddonTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan PostContributionLockout = TimeSpan.FromSeconds(1);
}
```

- [ ] **Step 4: Run tests to verify pass**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~WorkshopAssemblyTimingTests"
```

Expected: all `WorkshopAssemblyTimingTests` pass.

- [ ] **Step 5: Commit**

```powershell
git add MarketMafioso/WorkshopPrep/WorkshopAssemblyTiming.cs MarketMafioso.Tests/WorkshopPrep/WorkshopAssemblyTimingTests.cs
git commit -m "feat: name workshop assembly timing rules"
```

## Task 5: Add UI Automation Adapter Skeleton

**Files:**

- Create: `MarketMafioso/WorkshopPrep/WorkshopAssemblyUiAutomation.cs`

- [ ] **Step 1: Add adapter skeleton**

Create `MarketMafioso/WorkshopPrep/WorkshopAssemblyUiAutomation.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MarketMafioso.WorkshopPrep;

public sealed class WorkshopAssemblyUiAutomation
{
    private const string SelectStringAddon = "SelectString";
    private const string RequestAddon = "Request";
    private const string SelectYesNoAddon = "SelectYesno";
    private const string CompanyCraftRecipeNoteBookAddon = "CompanyCraftRecipeNoteBook";
    private const string CompanyCraftMaterialAddon = "CompanyCraftMaterial";

    private readonly IGameGui gameGui;

    public WorkshopAssemblyUiAutomation(IGameGui gameGui)
    {
        this.gameGui = gameGui;
    }

    public unsafe bool IsFabricationStationUiReady()
    {
        return IsAddonReady(CompanyCraftRecipeNoteBookAddon) ||
               IsAddonReady(CompanyCraftMaterialAddon) ||
               IsAddonReady(SelectStringAddon);
    }

    public WorkshopAssemblyActionResult TryOpenProject(WorkshopAssemblyQueueEntry entry)
    {
        return new(false, $"Workshop project {entry.ProjectName} cannot be opened because the live project-selection callback has not been mapped. {DescribeUiState()}");
    }

    public WorkshopAssemblyActionResult TrySubmitNextMaterial(WorkshopAssemblyQueueEntry entry)
    {
        return new(false, $"Workshop material for {entry.ProjectName} cannot be submitted because the live material-request callback has not been mapped. {DescribeUiState()}");
    }

    public WorkshopAssemblyActionResult TryConfirmContribution()
    {
        return new(false, $"Workshop material contribution cannot be confirmed because the live confirmation callback has not been mapped. {DescribeUiState()}");
    }

    public unsafe string DescribeUiState()
    {
        var trackedAddons = new[]
        {
            SelectStringAddon,
            RequestAddon,
            SelectYesNoAddon,
            CompanyCraftRecipeNoteBookAddon,
            CompanyCraftMaterialAddon,
        };

        var activeAddons = new List<string>();
        foreach (var addonName in trackedAddons)
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
            if (addon == null)
                continue;

            activeAddons.Add($"{addonName}({(addon->IsReady ? "ready" : "not ready")}, {(addon->IsVisible ? "visible" : "hidden")})");
        }

        return activeAddons.Count == 0
            ? "Workshop UI state: no tracked addons present."
            : $"Workshop UI state: {string.Join(", ", activeAddons)}.";
    }

    private unsafe bool IsAddonReady(string addonName)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName, 1);
        return addon != null && addon->IsReady && addon->IsVisible;
    }
}
```

- [ ] **Step 2: Build plugin project**

Run:

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
```

Expected: build succeeds.

- [ ] **Step 3: Commit**

```powershell
git add MarketMafioso/WorkshopPrep/WorkshopAssemblyUiAutomation.cs
git commit -m "feat: add workshop assembly ui adapter"
```

## Task 6: Add Runner Lifecycle

**Files:**

- Create: `MarketMafioso/WorkshopPrep/WorkshopAssemblyRunner.cs`
- Modify: `MarketMafioso/Plugin.cs`

- [ ] **Step 1: Add runner service**

Create `MarketMafioso/WorkshopPrep/WorkshopAssemblyRunner.cs`:

```csharp
using System;
using System.Linq;
using Dalamud.Plugin.Services;

namespace MarketMafioso.WorkshopPrep;

public sealed class WorkshopAssemblyRunner : IDisposable
{
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly WorkshopAssemblyUiAutomation uiAutomation;
    private WorkshopAssemblyPlan? activePlan;
    private DateTimeOffset continueAt = DateTimeOffset.MinValue;
    private int activeEntryIndex;
    private int activeEntryCompletedQuantity;

    public WorkshopAssemblyRunner(
        IFramework framework,
        IPluginLog log,
        WorkshopAssemblyUiAutomation uiAutomation)
    {
        this.framework = framework;
        this.log = log;
        this.uiAutomation = uiAutomation;
        Progress = BuildProgress(WorkshopAssemblyRunnerState.Idle, "Workshop assembly has not run.");
    }

    public WorkshopAssemblyProgress Progress { get; private set; }
    public bool IsRunning => Progress.State is not WorkshopAssemblyRunnerState.Idle
        and not WorkshopAssemblyRunnerState.Complete
        and not WorkshopAssemblyRunnerState.Stopped
        and not WorkshopAssemblyRunnerState.Failed;

    public WorkshopAssemblyActionResult Start(WorkshopAssemblyPlan plan)
    {
        if (IsRunning)
            return new(false, "Workshop assembly is already running.");

        activePlan = plan;
        activeEntryIndex = 0;
        activeEntryCompletedQuantity = 0;
        continueAt = DateTimeOffset.MinValue;
        Progress = BuildProgress(WorkshopAssemblyRunnerState.WaitingForFabricationStation, "Waiting for fabrication station UI.");
        framework.Update += OnFrameworkUpdate;
        log.Information("[MarketMafioso] Native workshop assembly started.");
        return new(true, "Native workshop assembly started.");
    }

    public void Stop()
    {
        if (!IsRunning)
            return;

        framework.Update -= OnFrameworkUpdate;
        Progress = BuildProgress(WorkshopAssemblyRunnerState.Stopped, "Workshop assembly stopped by user.");
        log.Information("[MarketMafioso] Native workshop assembly stopped by user.");
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!IsRunning || activePlan == null || DateTimeOffset.Now < continueAt)
            return;

        try
        {
            Tick();
        }
        catch (Exception ex)
        {
            framework.Update -= OnFrameworkUpdate;
            Progress = BuildProgress(WorkshopAssemblyRunnerState.Failed, $"Workshop assembly failed. {ex.Message}");
            log.Error(ex, "[MarketMafioso] Native workshop assembly failed.");
        }
    }

    private void Tick()
    {
        if (activePlan == null)
            throw new InvalidOperationException("Workshop assembly plan is unavailable.");

        if (activeEntryIndex >= activePlan.Entries.Count)
        {
            framework.Update -= OnFrameworkUpdate;
            Progress = BuildProgress(WorkshopAssemblyRunnerState.Complete, "Workshop assembly complete.");
            log.Information("[MarketMafioso] Native workshop assembly complete.");
            return;
        }

        var entry = activePlan.Entries[activeEntryIndex];
        if (!uiAutomation.IsFabricationStationUiReady())
        {
            Progress = BuildProgress(
                WorkshopAssemblyRunnerState.WaitingForFabricationStation,
                $"Waiting for fabrication station UI. {uiAutomation.DescribeUiState()}");
            return;
        }

        Progress = BuildProgress(
            WorkshopAssemblyRunnerState.OpeningProject,
            $"Ready to assemble {entry.ProjectName}; project selection automation is next.");
        Stop();
    }

    private WorkshopAssemblyProgress BuildProgress(WorkshopAssemblyRunnerState state, string message)
    {
        var entry = activePlan?.Entries.ElementAtOrDefault(activeEntryIndex);
        var completedProjects = activePlan == null
            ? 0
            : activePlan.Entries.Take(activeEntryIndex).Sum(x => x.Quantity) + activeEntryCompletedQuantity;
        var totalProjects = activePlan?.Entries.Sum(x => x.Quantity) ?? 0;

        return new WorkshopAssemblyProgress(
            state,
            message,
            entry?.ProjectName,
            entry?.WorkshopItemId,
            null,
            completedProjects,
            totalProjects,
            DateTimeOffset.Now);
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
    }
}
```

- [ ] **Step 2: Wire runner in plugin constructor**

Modify `MarketMafioso/Plugin.cs`:

```csharp
private readonly WorkshopAssemblyRunner workshopAssemblyRunner;
```

Create it after `workshopRetainerRestock`:

```csharp
workshopAssemblyRunner = new WorkshopAssemblyRunner(
    Framework,
    Log,
    new WorkshopAssemblyUiAutomation(GameGui));
```

Pass it into `MainWindow` after `workshopRetainerRestock`.

Dispose it before `reporter.Dispose()`:

```csharp
workshopAssemblyRunner.Dispose();
```

- [ ] **Step 3: Build plugin project and fix constructor compile errors**

Run:

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
```

Expected: compile fails until `MainWindow` accepts the runner. Add the `MainWindow` constructor parameter and field in Task 7 before committing.

## Task 7: Add Native Runner Controls

**Files:**

- Modify: `MarketMafioso/Windows/MainWindow.cs`
- Continue previous modifications in: `MarketMafioso/Plugin.cs`

- [ ] **Step 1: Add `MainWindow` dependency**

In `MainWindow`, add:

```csharp
private readonly WorkshopAssemblyRunner workshopAssemblyRunner;
```

Add constructor parameter after `WorkshopRetainerRestockService workshopRetainerRestock`:

```csharp
WorkshopAssemblyRunner workshopAssemblyRunner,
```

Assign:

```csharp
this.workshopAssemblyRunner = workshopAssemblyRunner;
```

- [ ] **Step 2: Add start and stop controls**

In `DrawWorkshopPrepActions()`, after `Restock Materials From Retainers`, add:

```csharp
ImGui.SameLine();
if (workshopAssemblyRunner.IsRunning)
{
    if (ImGui.Button("Stop Assembly"))
        workshopAssemblyRunner.Stop();
}
else if (ImGuiUi.Button("Start Native Assembly", hasPrepQueue))
{
    try
    {
        var preflight = WorkshopAssemblyPreflightService.Check(
            config.WorkshopPrepQueue,
            workshopCatalog.GetProjects(),
            scanner.CountPlayerInventory(config));
        if (!preflight.CanStart || preflight.Plan == null)
        {
            workshopStatus = preflight.Message;
        }
        else
        {
            var result = workshopAssemblyRunner.Start(preflight.Plan);
            workshopStatus = result.Message;
        }
    }
    catch (Exception ex)
    {
        workshopStatus = $"Unable to start workshop assembly. {ex.Message}";
        log.Warning(ex, "[MarketMafioso] Native workshop assembly preflight failed.");
    }
}
```

Below existing status text, add:

```csharp
var progress = workshopAssemblyRunner.Progress;
ImGui.TextColored(workshopAssemblyRunner.IsRunning ? ColHeader : ColMuted, progress.Message);
```

Keep `Send Queue To VIWI` and `Clear Prep Queue` behavior unchanged.

- [ ] **Step 3: Build plugin project**

Run:

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
```

Expected: build succeeds.

- [ ] **Step 4: Commit lifecycle and controls**

```powershell
git add MarketMafioso/Plugin.cs MarketMafioso/Windows/MainWindow.cs MarketMafioso/WorkshopPrep/WorkshopAssemblyRunner.cs
git commit -m "feat: add native workshop runner lifecycle"
```

## Task 8: Implement First Real UI Action Behind Adapter

**Files:**

- Modify: `MarketMafioso/WorkshopPrep/WorkshopAssemblyUiAutomation.cs`
- Modify: `MarketMafioso/WorkshopPrep/WorkshopAssemblyRunner.cs`

- [ ] **Step 1: Investigate current addon callback fields in game**

Run the Debug build, open `/mmf`, stand at a company workshop fabrication station, and open the station UI manually. Use plugin logs from `DescribeUiState()` to confirm which of these addons appears:

- `CompanyCraftRecipeNoteBook`
- `CompanyCraftMaterial`
- `SelectString`
- `Request`
- `SelectYesno`

Expected: logs identify at least one ready fabrication-station addon. Record any addon name differences in this task before editing callbacks.

- [ ] **Step 2: Implement only the safest first callback**

Implement project-opening only after confirming the correct addon and callback shape. Keep the adapter method signature:

```csharp
public WorkshopAssemblyActionResult TryOpenProject(WorkshopAssemblyQueueEntry entry)
```

The method must return failure with `DescribeUiState()` when the expected addon is missing. It must not assume project order if project id or result id cannot be matched.

- [ ] **Step 3: Update runner transition**

Change `Tick()` so `OpeningProject` calls `TryOpenProject(entry)` and moves to `WaitingForMaterialRequest` only on success. On failure, keep waiting until `WorkshopAssemblyTiming.AddonTimeout` elapses, then throw an `InvalidOperationException` with the adapter message.

- [ ] **Step 4: Build plugin project**

Run:

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
```

Expected: build succeeds and the dev plugin DLL is refreshed through Debug sync.

- [ ] **Step 5: Manual verify**

In game:

1. Queue one project with materials in player inventory.
2. Stand at the fabrication station.
3. Click `Start Native Assembly`.
4. Confirm the runner opens the intended project or fails with the addon diagnostic.

Expected: no silent failure; either the correct project opens or the status explains the missing/mismatched UI state.

- [ ] **Step 6: Commit**

```powershell
git add MarketMafioso/WorkshopPrep/WorkshopAssemblyUiAutomation.cs MarketMafioso/WorkshopPrep/WorkshopAssemblyRunner.cs
git commit -m "feat: open workshop projects from native runner"
```

## Task 9: Implement Material Submission And Confirmation

**Files:**

- Modify: `MarketMafioso/WorkshopPrep/WorkshopAssemblyUiAutomation.cs`
- Modify: `MarketMafioso/WorkshopPrep/WorkshopAssemblyRunner.cs`

- [ ] **Step 1: Implement material request detection**

Update `TrySubmitNextMaterial(WorkshopAssemblyQueueEntry entry)` so it only acts when the material request addon is ready and the requested material can be matched by item id or exact localized name.

Failure message format:

```text
Workshop material request is not actionable for <ProjectName>. <DescribeUiState output>
```

- [ ] **Step 2: Implement confirmation**

Update `TryConfirmContribution()` so it only confirms the exact material-delivery confirmation dialog. If `SelectYesno` is present for another reason, return failure with a diagnostic instead of clicking it.

- [ ] **Step 3: Apply lockout state**

After successful material submission and confirmation, set runner state to `WaitingForContributionLockout` and set `continueAt = DateTimeOffset.Now + WorkshopAssemblyTiming.PostContributionLockout`.

- [ ] **Step 4: Advance progress**

After the lockout, observe whether the active project moved to the next material or completed. Increment `activeEntryCompletedQuantity` only after visible progress proves completion. When `activeEntryCompletedQuantity == entry.Quantity`, advance `activeEntryIndex`.

- [ ] **Step 5: Build plugin project**

Run:

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
```

Expected: build succeeds and Debug sync refreshes the dev plugin DLL.

- [ ] **Step 6: Manual verify one material**

In game:

1. Queue a simple one-project item.
2. Ensure materials are in player inventory.
3. Start native assembly.
4. Watch one material contribution.

Expected: the runner contributes one material, confirms the dialog, waits the named lockout, and then reports the next material or project completion.

- [ ] **Step 7: Commit**

```powershell
git add MarketMafioso/WorkshopPrep/WorkshopAssemblyUiAutomation.cs MarketMafioso/WorkshopPrep/WorkshopAssemblyRunner.cs
git commit -m "feat: contribute workshop materials natively"
```

## Task 10: Complete Queue Execution And Recovery Semantics

**Files:**

- Modify: `MarketMafioso/WorkshopPrep/WorkshopAssemblyRunner.cs`
- Modify: `MarketMafioso/Windows/MainWindow.cs`

- [ ] **Step 1: Preserve prep queue on completion**

Keep `Configuration.WorkshopPrepQueue` unchanged after successful native assembly. Do not silently clear the queue; the user can use `Clear Prep Queue`.

- [ ] **Step 2: Stop cleanly**

Ensure user stop sets:

```csharp
Progress = BuildProgress(WorkshopAssemblyRunnerState.Stopped, "Workshop assembly stopped by user.");
```

Expected: user stop never reports failure.

- [ ] **Step 3: Fail cleanly**

Ensure unexpected exceptions set:

```csharp
Progress = BuildProgress(WorkshopAssemblyRunnerState.Failed, $"Workshop assembly failed. {ex.Message}");
```

Expected: failures leave the queue intact and log the exception.

- [ ] **Step 4: Display progress counts**

In `MainWindow`, show progress counts when `progress.TotalProjects > 0`:

```csharp
ImGui.TextColored(
    ColMuted,
    $"Assembly progress: {progress.CompletedProjects}/{progress.TotalProjects}");
```

- [ ] **Step 5: Build plugin project**

Run:

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
```

Expected: build succeeds.

- [ ] **Step 6: Manual verify queue semantics**

In game:

1. Queue two different projects.
2. Start native assembly.
3. Stop during the second project.
4. Confirm the UI says stopped, not failed.
5. Confirm the prep queue still exists.

Expected: queue is intact and status is accurate.

- [ ] **Step 7: Commit**

```powershell
git add MarketMafioso/WorkshopPrep/WorkshopAssemblyRunner.cs MarketMafioso/Windows/MainWindow.cs
git commit -m "feat: finalize workshop assembly runner status"
```

## Task 11: Update Docs And Product Boundary

**Files:**

- Modify: `AGENTS.md`
- Modify: `README.md`
- Modify: `docs/design/2026-06-23-native-workshop-runner.md` if implementation discoveries change the design.

- [ ] **Step 1: Update `AGENTS.md` boundary**

Replace the old Workshop Prep boundary bullet with:

```markdown
- Workshop Prep owns project selection, prep queue state, material requirements, retainer availability, VIWI queue handoff, retainer material withdrawal, and native execution of MarketMafioso-owned prep queues.
```

Replace the old "must not operate Workshoppa projects" bullet with:

```markdown
- MarketMafioso may execute its own Workshop Prep queue, but must not mirror VIWI Workshoppa's full module surface or treat Workshoppa state as source of truth.
```

- [ ] **Step 2: Update README feature list**

Update the Workshop Prep bullet to say:

```markdown
- **Workshop Prep** builds Free Company workshop material prep queues, checks player and retainer stock, withdraws available materials from retainers, can execute the MarketMafioso prep queue natively, and can still send the prepared queue to VIWI Workshoppa.
```

- [ ] **Step 3: Build and format**

Run:

```powershell
dotnet build "MarketMafioso.sln" -c Debug
dotnet format "MarketMafioso.sln" --verify-no-changes
```

Expected: build succeeds; format reports no changes needed.

- [ ] **Step 4: Run plugin tests**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug -v minimal
```

Expected: all plugin tests pass.

- [ ] **Step 5: Commit**

```powershell
git add AGENTS.md README.md docs/design/2026-06-23-native-workshop-runner.md
git commit -m "docs: document native workshop runner boundary"
```

## Task 12: Runtime Verification And Dev Plugin Deploy

**Files:**

- No source edits expected unless runtime verification finds a defect.

- [ ] **Step 1: Explicitly deploy dev plugin**

Run:

```powershell
.\MarketMafioso\tools\Deploy-DevPlugin.ps1 -Configuration Debug
```

Expected: script reports source and installed DLL hashes match.

- [ ] **Step 2: Reload plugin in Dalamud**

Use Dalamud plugin installer or `/xldev` workflow to reload MarketMafioso from the dev plugin path.

Expected: `/mmf` opens the build from this branch.

- [ ] **Step 3: Verify blocked start**

In game:

1. Stand away from a fabrication station.
2. Add one valid project to Workshop Prep.
3. Click `Start Native Assembly`.

Expected: status says the runner is waiting for fabrication station UI and includes addon diagnostics.

- [ ] **Step 4: Verify missing-material preflight**

In game:

1. Queue a project with a known missing material.
2. Click `Start Native Assembly`.

Expected: status begins `Missing player materials:` and no UI automation begins.

- [ ] **Step 5: Verify one-project execution**

In game:

1. Put all materials in player inventory.
2. Queue one project.
3. Open the fabrication station.
4. Click `Start Native Assembly`.

Expected: project completes, status says `Workshop assembly complete.`, and the queue remains visible.

- [ ] **Step 6: Verify VIWI handoff regression**

In game:

1. Queue one project.
2. Click `Send Queue To VIWI`.
3. Confirm sync.

Expected: VIWI queue handoff still succeeds.

- [ ] **Step 7: Final commit if runtime fixes were required**

If runtime verification required fixes, commit them:

```powershell
git add MarketMafioso AGENTS.md README.md docs
git commit -m "fix: stabilize native workshop runner"
```

If no fixes were required, do not create an empty commit.

## Self-Review Checklist

- Spec coverage: this plan covers native runner scope, UI, timing, errors, persistence, tests, docs, and runtime verification.
- Placeholder scan: no steps rely on unspecified behavior; the unsafe project/material callbacks are intentionally deferred until live addon shape is confirmed, with explicit investigation and expected outcomes.
- Type consistency: model names used by later tasks are defined in Tasks 1-4.
- Scope control: the plan does not import Workshoppa wholesale, add leveling mode, or persist transient automation progress.
