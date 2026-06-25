# Workshop Pause and Frozen Queues Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add runtime pause/resume, named frozen queue snapshots, and active queue decrementing after successful workshop product retrieval.

**Architecture:** Keep `Configuration.WorkshopPrepQueue` as the active working queue. Add frozen queues as separate persisted snapshots, add a focused queue service for copy/load/save/decrement operations, and extend `WorkshopAssemblyRunner` with an in-memory `Paused` state that preserves current runtime fields.

**Tech Stack:** C# 12, Dalamud plugin config persistence, ImGui.NET, xUnit tests, existing `MarketMafioso/WorkshopPrep` service patterns.

---

## File Structure

- Modify `MarketMafioso/Configuration.cs`: add persisted frozen queue list and active frozen queue id.
- Modify `MarketMafioso/WorkshopPrep/WorkshopPrepModels.cs`: add `WorkshopFrozenQueue` model.
- Create `MarketMafioso/WorkshopPrep/WorkshopQueueService.cs`: pure operations for frozen queues, active queue divergence, deep copies, and active queue decrementing.
- Add `MarketMafioso.Tests/WorkshopPrep/WorkshopQueueServiceTests.cs`: unit tests for frozen queue operations and active queue decrementing.
- Modify `MarketMafioso/WorkshopPrep/WorkshopAssemblyModels.cs`: add `Paused` runner state.
- Modify `MarketMafioso/WorkshopPrep/WorkshopAssemblyRunner.cs`: add `Pause()`, `Resume()`, `IsPaused`, and product-complete callback support.
- Modify `MarketMafioso.Tests/WorkshopPrep/WorkshopAssemblyRunnerTests.cs`: unit tests for pause/resume/stop behavior.
- Modify `MarketMafioso/Windows/MainWindow.cs`: add queue toolbar, frozen queue management popup/modal, pause/resume controls, and disabled queue mutation while runner is active or paused.

---

### Task 1: Add Frozen Queue Data Model

**Files:**
- Modify: `MarketMafioso/Configuration.cs`
- Modify: `MarketMafioso/WorkshopPrep/WorkshopPrepModels.cs`

- [ ] **Step 1: Add model type**

In `MarketMafioso/WorkshopPrep/WorkshopPrepModels.cs`, add this class after `WorkshopPrepQueueItem`:

```csharp
[Serializable]
public sealed class WorkshopFrozenQueue
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<WorkshopPrepQueueItem> Items { get; set; } = new();
}
```

- [ ] **Step 2: Add persisted config properties**

In `MarketMafioso/Configuration.cs`, add these properties beside `WorkshopPrepQueue`:

```csharp
public List<WorkshopFrozenQueue> FrozenWorkshopQueues { get; set; } = new();
public Guid? ActiveFrozenWorkshopQueueId { get; set; }
```

- [ ] **Step 3: Build to verify serialization types compile**

Run:

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
```

Expected: build succeeds.

- [ ] **Step 4: Commit**

```powershell
git add MarketMafioso/Configuration.cs MarketMafioso/WorkshopPrep/WorkshopPrepModels.cs
git commit -m "feat: add frozen workshop queue model"
```

---

### Task 2: Implement Pure Frozen Queue Operations

**Files:**
- Create: `MarketMafioso/WorkshopPrep/WorkshopQueueService.cs`
- Create: `MarketMafioso.Tests/WorkshopPrep/WorkshopQueueServiceTests.cs`

- [ ] **Step 1: Write failing tests**

Create `MarketMafioso.Tests/WorkshopPrep/WorkshopQueueServiceTests.cs`:

```csharp
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Tests.WorkshopPrep;

public sealed class WorkshopQueueServiceTests
{
    [Fact]
    public void FreezeCurrentQueue_creates_deep_copy_and_marks_active_frozen_queue()
    {
        var config = new Configuration
        {
            WorkshopPrepQueue =
            [
                new WorkshopPrepQueueItem { WorkshopItemId = 531, Quantity = 16 },
            ],
        };

        var result = WorkshopQueueService.FreezeCurrentQueue(config, "Shark parts", DateTime.UtcNow);

        Assert.True(result.Success);
        Assert.Single(config.FrozenWorkshopQueues);
        Assert.Equal(config.FrozenWorkshopQueues[0].Id, config.ActiveFrozenWorkshopQueueId);
        Assert.Equal("Shark parts", config.FrozenWorkshopQueues[0].Name);
        Assert.Equal(531u, config.FrozenWorkshopQueues[0].Items[0].WorkshopItemId);
        Assert.NotSame(config.WorkshopPrepQueue[0], config.FrozenWorkshopQueues[0].Items[0]);
    }

    [Fact]
    public void LoadFrozenQueue_replaces_active_queue_with_deep_copy()
    {
        var frozenId = Guid.NewGuid();
        var config = new Configuration
        {
            WorkshopPrepQueue =
            [
                new WorkshopPrepQueueItem { WorkshopItemId = 111, Quantity = 1 },
            ],
            FrozenWorkshopQueues =
            [
                new WorkshopFrozenQueue
                {
                    Id = frozenId,
                    Name = "Load me",
                    Items =
                    [
                        new WorkshopPrepQueueItem { WorkshopItemId = 531, Quantity = 16 },
                    ],
                },
            ],
        };

        var result = WorkshopQueueService.LoadFrozenQueue(config, frozenId);

        Assert.True(result.Success);
        Assert.Equal(frozenId, config.ActiveFrozenWorkshopQueueId);
        Assert.Single(config.WorkshopPrepQueue);
        Assert.Equal(531u, config.WorkshopPrepQueue[0].WorkshopItemId);
        Assert.NotSame(config.FrozenWorkshopQueues[0].Items[0], config.WorkshopPrepQueue[0]);
    }

    [Fact]
    public void FreezeCurrentQueue_rejects_duplicate_names_case_insensitively()
    {
        var config = new Configuration
        {
            WorkshopPrepQueue =
            [
                new WorkshopPrepQueueItem { WorkshopItemId = 531, Quantity = 1 },
            ],
            FrozenWorkshopQueues =
            [
                new WorkshopFrozenQueue { Name = "Shark Parts" },
            ],
        };

        var result = WorkshopQueueService.FreezeCurrentQueue(config, "shark parts", DateTime.UtcNow);

        Assert.False(result.Success);
        Assert.Contains("already exists", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DecrementActiveQueue_removes_row_when_quantity_reaches_zero()
    {
        var config = new Configuration
        {
            WorkshopPrepQueue =
            [
                new WorkshopPrepQueueItem { WorkshopItemId = 531, Quantity = 1 },
            ],
        };

        var result = WorkshopQueueService.DecrementActiveQueue(config, 531);

        Assert.True(result.Success);
        Assert.Empty(config.WorkshopPrepQueue);
    }

    [Fact]
    public void DecrementActiveQueue_does_not_mutate_frozen_queue()
    {
        var config = new Configuration
        {
            WorkshopPrepQueue =
            [
                new WorkshopPrepQueueItem { WorkshopItemId = 531, Quantity = 2 },
            ],
            FrozenWorkshopQueues =
            [
                new WorkshopFrozenQueue
                {
                    Name = "Original",
                    Items =
                    [
                        new WorkshopPrepQueueItem { WorkshopItemId = 531, Quantity = 2 },
                    ],
                },
            ],
        };

        WorkshopQueueService.DecrementActiveQueue(config, 531);

        Assert.Equal(1, config.WorkshopPrepQueue[0].Quantity);
        Assert.Equal(2, config.FrozenWorkshopQueues[0].Items[0].Quantity);
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug -v minimal --filter "FullyQualifiedName~WorkshopQueueServiceTests"
```

Expected: compile fails because `WorkshopQueueService` does not exist.

- [ ] **Step 3: Add service implementation**

Create `MarketMafioso/WorkshopPrep/WorkshopQueueService.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketMafioso.WorkshopPrep;

public sealed record WorkshopQueueOperationResult(bool Success, string Message, Guid? QueueId = null);

public static class WorkshopQueueService
{
    public static WorkshopQueueOperationResult FreezeCurrentQueue(Configuration config, string name, DateTime nowUtc)
    {
        var normalizedName = name.Trim();
        if (normalizedName.Length == 0)
            return new(false, "Frozen queue name is required.");

        if (config.WorkshopPrepQueue.Count == 0)
            return new(false, "Active workshop queue is empty.");

        if (config.FrozenWorkshopQueues.Any(x => string.Equals(x.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
            return new(false, $"A frozen queue named {normalizedName} already exists.");

        var frozen = new WorkshopFrozenQueue
        {
            Id = Guid.NewGuid(),
            Name = normalizedName,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc,
            Items = CloneItems(config.WorkshopPrepQueue),
        };

        config.FrozenWorkshopQueues.Add(frozen);
        config.ActiveFrozenWorkshopQueueId = frozen.Id;
        return new(true, $"Froze workshop queue {normalizedName}.", frozen.Id);
    }

    public static WorkshopQueueOperationResult LoadFrozenQueue(Configuration config, Guid queueId)
    {
        var frozen = config.FrozenWorkshopQueues.FirstOrDefault(x => x.Id == queueId);
        if (frozen == null)
            return new(false, "Frozen workshop queue was not found.");

        config.WorkshopPrepQueue = CloneItems(frozen.Items);
        config.ActiveFrozenWorkshopQueueId = frozen.Id;
        return new(true, $"Loaded frozen workshop queue {frozen.Name}.", frozen.Id);
    }

    public static WorkshopQueueOperationResult DecrementActiveQueue(Configuration config, uint workshopItemId)
    {
        var item = config.WorkshopPrepQueue.FirstOrDefault(x => x.WorkshopItemId == workshopItemId);
        if (item == null)
            return new(false, $"Active workshop queue does not contain project {workshopItemId}.");

        item.Quantity--;
        if (item.Quantity <= 0)
            config.WorkshopPrepQueue.Remove(item);

        return new(true, "Decremented active workshop queue.");
    }

    public static bool ActiveQueueMatchesFrozenQueue(Configuration config)
    {
        if (config.ActiveFrozenWorkshopQueueId == null)
            return false;

        var frozen = config.FrozenWorkshopQueues.FirstOrDefault(x => x.Id == config.ActiveFrozenWorkshopQueueId.Value);
        return frozen != null && ItemsEqual(config.WorkshopPrepQueue, frozen.Items);
    }

    public static void MarkActiveQueueEdited(Configuration config)
    {
        if (!ActiveQueueMatchesFrozenQueue(config))
            config.ActiveFrozenWorkshopQueueId = null;
    }

    public static List<WorkshopPrepQueueItem> CloneItems(IEnumerable<WorkshopPrepQueueItem> items)
    {
        return items
            .Select(x => new WorkshopPrepQueueItem
            {
                WorkshopItemId = x.WorkshopItemId,
                Quantity = x.Quantity,
            })
            .ToList();
    }

    private static bool ItemsEqual(IReadOnlyList<WorkshopPrepQueueItem> left, IReadOnlyList<WorkshopPrepQueueItem> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            if (left[index].WorkshopItemId != right[index].WorkshopItemId || left[index].Quantity != right[index].Quantity)
                return false;
        }

        return true;
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug -v minimal --filter "FullyQualifiedName~WorkshopQueueServiceTests"
```

Expected: tests pass.

- [ ] **Step 5: Commit**

```powershell
git add MarketMafioso/WorkshopPrep/WorkshopQueueService.cs MarketMafioso.Tests/WorkshopPrep/WorkshopQueueServiceTests.cs
git commit -m "feat: add frozen workshop queue service"
```

---

### Task 3: Decrement Active Queue After Product Retrieval

**Files:**
- Modify: `MarketMafioso/WorkshopPrep/WorkshopAssemblyRunner.cs`
- Modify: `MarketMafioso/Plugin.cs`
- Test: `MarketMafioso.Tests/WorkshopPrep/WorkshopAssemblyRunnerTests.cs`

- [ ] **Step 1: Write failing runner test**

Add a test to `WorkshopAssemblyRunnerTests` that starts a one-project plan, drives the fake UI to return `IsProjectComplete`, and verifies a supplied completion callback is invoked with the completed workshop item id.

Use this assertion shape:

```csharp
Assert.Equal(531u, completedWorkshopItemId);
```

If the existing fake UI helper does not expose project completion, add a fake result queue entry:

```csharp
fakeUi.SubmitMaterialResults.Enqueue(new WorkshopAssemblyActionResult(
    true,
    "Retrieved finished workshop project.",
    IsProjectComplete: true));
```

- [ ] **Step 2: Run test to verify failure**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug -v minimal --filter "FullyQualifiedName~WorkshopAssemblyRunnerTests"
```

Expected: the new test fails because the runner has no completion callback.

- [ ] **Step 3: Add completion callback to runner constructor**

Change `WorkshopAssemblyRunner` constructor to accept:

```csharp
private readonly Action<WorkshopAssemblyQueueEntry> onProjectRetrieved;
```

Constructor parameter:

```csharp
Action<WorkshopAssemblyQueueEntry>? onProjectRetrieved = null
```

Constructor assignment:

```csharp
this.onProjectRetrieved = onProjectRetrieved ?? (_ => { });
```

In `CompleteActiveProject`, before incrementing `activeEntryCompletedQuantity`, call:

```csharp
onProjectRetrieved(entry);
```

- [ ] **Step 4: Wire config decrement in `Plugin.cs`**

When constructing `WorkshopAssemblyRunner`, pass:

```csharp
entry =>
{
    var result = WorkshopQueueService.DecrementActiveQueue(Configuration, entry.WorkshopItemId);
    if (!result.Success)
        Log.Warning("[MarketMafioso] {Message}", result.Message);
    Configuration.Save();
}
```

- [ ] **Step 5: Run tests**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug -v minimal
```

Expected: all tests pass.

- [ ] **Step 6: Commit**

```powershell
git add MarketMafioso/WorkshopPrep/WorkshopAssemblyRunner.cs MarketMafioso/Plugin.cs MarketMafioso.Tests/WorkshopPrep/WorkshopAssemblyRunnerTests.cs
git commit -m "feat: decrement active workshop queue on retrieval"
```

---

### Task 4: Add Runner Pause and Resume

**Files:**
- Modify: `MarketMafioso/WorkshopPrep/WorkshopAssemblyModels.cs`
- Modify: `MarketMafioso/WorkshopPrep/WorkshopAssemblyRunner.cs`
- Test: `MarketMafioso.Tests/WorkshopPrep/WorkshopAssemblyRunnerTests.cs`

- [ ] **Step 1: Write failing pause/resume tests**

Add tests covering:

```csharp
runner.Pause();
Assert.Equal(WorkshopAssemblyRunnerState.Paused, runner.Progress.State);
Assert.True(runner.IsPaused);
Assert.False(runner.IsRunning);
```

and:

```csharp
runner.Resume();
Assert.NotEqual(WorkshopAssemblyRunnerState.Paused, runner.Progress.State);
Assert.True(runner.IsRunning);
```

Add a stop-after-pause test:

```csharp
runner.Pause();
runner.Stop();
Assert.Equal(WorkshopAssemblyRunnerState.Stopped, runner.Progress.State);
```

- [ ] **Step 2: Run tests to verify failure**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug -v minimal --filter "FullyQualifiedName~WorkshopAssemblyRunnerTests"
```

Expected: compile fails because `Paused`, `Pause`, `Resume`, and `IsPaused` do not exist.

- [ ] **Step 3: Add `Paused` state**

In `WorkshopAssemblyRunnerState`, add:

```csharp
Paused,
```

- [ ] **Step 4: Implement runner pause/resume**

Add to `WorkshopAssemblyRunner`:

```csharp
private WorkshopAssemblyRunnerState stateBeforePause;
```

Update properties:

```csharp
public bool IsPaused => Progress.State == WorkshopAssemblyRunnerState.Paused;
public bool IsRunning => Progress.State is not WorkshopAssemblyRunnerState.Idle
    and not WorkshopAssemblyRunnerState.Complete
    and not WorkshopAssemblyRunnerState.Stopped
    and not WorkshopAssemblyRunnerState.Failed
    and not WorkshopAssemblyRunnerState.Paused;
public bool HasActiveRun => IsRunning || IsPaused;
```

Add methods:

```csharp
public void Pause()
{
    if (!IsRunning)
        return;

    framework.Update -= OnFrameworkUpdate;
    stateBeforePause = Progress.State;
    SetState(WorkshopAssemblyRunnerState.Paused, "Workshop assembly paused.");
    diagnostics.Record("paused", "Workshop assembly paused by user.");
}

public WorkshopAssemblyActionResult Resume()
{
    if (!IsPaused)
        return new(false, "Workshop assembly is not paused.");

    var resumeState = stateBeforePause == WorkshopAssemblyRunnerState.Paused
        ? WorkshopAssemblyRunnerState.WaitingForFabricationStation
        : stateBeforePause;
    SetState(resumeState, "Workshop assembly resumed.");
    framework.Update += OnFrameworkUpdate;
    diagnostics.Record("resumed", "Workshop assembly resumed by user.");
    return new(true, "Workshop assembly resumed.");
}
```

Update `Stop()` guard:

```csharp
if (!IsRunning && !IsPaused)
    return;
```

- [ ] **Step 5: Run tests**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug -v minimal --filter "FullyQualifiedName~WorkshopAssemblyRunnerTests"
```

Expected: runner tests pass.

- [ ] **Step 6: Commit**

```powershell
git add MarketMafioso/WorkshopPrep/WorkshopAssemblyModels.cs MarketMafioso/WorkshopPrep/WorkshopAssemblyRunner.cs MarketMafioso.Tests/WorkshopPrep/WorkshopAssemblyRunnerTests.cs
git commit -m "feat: add workshop assembly pause and resume"
```

---

### Task 5: Add Frozen Queue UI and Pause Controls

**Files:**
- Modify: `MarketMafioso/Windows/MainWindow.cs`

- [ ] **Step 1: Add UI state fields**

Add private fields to `MainWindow`:

```csharp
private string frozenQueueNameInput = string.Empty;
private Guid? selectedFrozenQueueId;
private bool confirmNewWorkshopQueue;
private bool confirmLoadFrozenQueue;
private bool confirmDeleteFrozenQueue;
```

- [ ] **Step 2: Add queue toolbar above active queue table**

At the top of `DrawWorkshopPrepQueue`, after the section header, call:

```csharp
DrawFrozenQueueToolbar();
```

Implement:

```csharp
private void DrawFrozenQueueToolbar()
{
    var canEditQueue = !workshopAssemblyRunner.IsRunning && !workshopAssemblyRunner.IsPaused;
    var activeFrozen = config.ActiveFrozenWorkshopQueueId == null
        ? null
        : config.FrozenWorkshopQueues.FirstOrDefault(x => x.Id == config.ActiveFrozenWorkshopQueueId.Value);
    var label = activeFrozen == null
        ? "Unsaved Queue"
        : WorkshopQueueService.ActiveQueueMatchesFrozenQueue(config)
            ? $"Frozen: {activeFrozen.Name}"
            : $"Modified from: {activeFrozen.Name}";

    ImGui.TextColored(ColMuted, label);

    if (ImGuiUi.Button("New Queue", canEditQueue && config.WorkshopPrepQueue.Count > 0))
        confirmNewWorkshopQueue = true;

    ImGui.SameLine();
    ImGui.SetNextItemWidth(180);
    ImGui.InputText("##FrozenQueueName", ref frozenQueueNameInput, 64);

    ImGui.SameLine();
    if (ImGuiUi.Button("Freeze Current Queue", canEditQueue && config.WorkshopPrepQueue.Count > 0))
    {
        var result = WorkshopQueueService.FreezeCurrentQueue(config, frozenQueueNameInput, DateTime.UtcNow);
        if (result.Success)
        {
            config.Save();
            frozenQueueNameInput = string.Empty;
        }

        workshopStatus = result.Message;
    }

    ImGui.SameLine();
    if (ImGuiUi.Button("Load Frozen Queue", canEditQueue && config.FrozenWorkshopQueues.Count > 0))
        ImGui.OpenPopup("LoadFrozenWorkshopQueue");

    DrawLoadFrozenQueuePopup(canEditQueue);
}
```

- [ ] **Step 3: Add load popup**

Add:

```csharp
private void DrawLoadFrozenQueuePopup(bool canEditQueue)
{
    if (!ImGui.BeginPopup("LoadFrozenWorkshopQueue"))
        return;

    foreach (var frozen in config.FrozenWorkshopQueues.OrderByDescending(x => x.UpdatedAt))
    {
        var totalQuantity = frozen.Items.Sum(x => x.Quantity);
        if (ImGui.Selectable($"{frozen.Name} ({frozen.Items.Count} projects, {totalQuantity} total)"))
        {
            var result = WorkshopQueueService.LoadFrozenQueue(config, frozen.Id);
            if (result.Success)
                config.Save();

            workshopStatus = result.Message;
            ImGui.CloseCurrentPopup();
        }
    }

    ImGui.EndPopup();
}
```

- [ ] **Step 4: Update queue mutation paths**

After every manual active queue edit, call:

```csharp
WorkshopQueueService.MarkActiveQueueEdited(config);
config.Save();
```

Apply this in:

- `AddWorkshopProject`
- quantity edit in `DrawWorkshopQueueTable`
- remove button in `DrawWorkshopQueueTable`
- clear/new queue handling

- [ ] **Step 5: Update assembly controls**

In `DrawWorkshopPrepActions`, replace the running/idle block with:

```csharp
if (workshopAssemblyRunner.IsPaused)
{
    if (ImGui.Button("Resume Assembly"))
    {
        var result = workshopAssemblyRunner.Resume();
        workshopStatus = result.Message;
    }

    ImGui.SameLine();
    if (ImGui.Button("Stop Assembly"))
        workshopAssemblyRunner.Stop();
}
else if (workshopAssemblyRunner.IsRunning)
{
    if (ImGui.Button("Pause Assembly"))
        workshopAssemblyRunner.Pause();

    ImGui.SameLine();
    if (ImGui.Button("Stop Assembly"))
        workshopAssemblyRunner.Stop();
}
else
{
    if (ImGuiUi.Button("Start Native Assembly", hasPrepQueue))
        StartWorkshopAssembly(enableDiagnostics: false);

    ImGui.SameLine();
    if (ImGuiUi.Button("Start Assembly With Diagnostics", hasPrepQueue))
        StartWorkshopAssembly(enableDiagnostics: true);
}
```

- [ ] **Step 6: Build**

Run:

```powershell
dotnet build "MarketMafioso.sln" -c Debug
```

Expected: build succeeds.

- [ ] **Step 7: Commit**

```powershell
git add MarketMafioso/Windows/MainWindow.cs
git commit -m "feat: add frozen queue and pause controls"
```

---

### Task 6: Full Verification and Dev Plugin Deploy

**Files:**
- No source changes expected.

- [ ] **Step 1: Run plugin tests**

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug -v minimal
```

Expected: all tests pass.

- [ ] **Step 2: Verify formatting**

```powershell
dotnet format "MarketMafioso.sln" --verify-no-changes
```

Expected: exits 0.

- [ ] **Step 3: Verify diff whitespace**

```powershell
git diff --check
```

Expected: exits 0.

- [ ] **Step 4: Deploy active dev plugin**

```powershell
MarketMafioso/tools/Deploy-DevPlugin.ps1 -Configuration Debug
```

Expected output includes:

```text
Verified Dalamud target DLL: F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\MarketMafioso\MarketMafioso\bin\Release\MarketMafioso.dll
SHA256:
Reload MarketMafioso in Dalamud if it is already loaded.
```

- [ ] **Step 5: Manual in-game checks**

Run these checks with diagnostics enabled:

- Start a queue and pause while the station menu is visible. Resume and confirm assembly continues.
- Start a queue and pause during material contribution. Resume and confirm assembly continues.
- Stop after pause. Start again and confirm the runner reacquires live workshop UI from the active queue.
- Complete one product and confirm the active queue quantity decrements by one.
- Freeze a queue, load it, assemble one product, and confirm the frozen queue quantity is unchanged.

- [ ] **Step 6: Final commit if verification required changes**

If verification required fixes, commit them:

```powershell
git add MarketMafioso MarketMafioso.Tests docs
git commit -m "fix: stabilize frozen queue and pause workflow"
```

## Self-Review

Spec coverage:

- Pause/resume is covered by Task 4 and UI controls in Task 5.
- Stop remains terminal in Task 4.
- Frozen queue persistence is covered by Tasks 1, 2, and 5.
- Active queue decrementing is covered by Task 3.
- Verification and deploy are covered by Task 6.

Placeholder scan:

- No placeholder markers or deferred implementation notes remain.

Type consistency:

- The plan uses `WorkshopFrozenQueue`, `WorkshopQueueService`, `WorkshopQueueOperationResult`, `Paused`, `IsPaused`, `HasActiveRun`, `Pause()`, and `Resume()` consistently across tasks.
