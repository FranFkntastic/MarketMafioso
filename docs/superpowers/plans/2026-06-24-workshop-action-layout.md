# Workshop Action Layout Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Workshop Prep actions scale by moving controls to the object they affect, adding one-click queue save semantics, and leaving the bottom area for active assembly workflow state.

**Architecture:** Keep queue persistence in `WorkshopQueueService`; keep ImGui helper logic in `ImGuiUi`; keep Workshop Prep orchestration in `MainWindow`. The UI should use object-local controls: queue controls in the Prep Queue section, material controls in the Materials section, export/handoff actions in popup menus, and assembly controls/status in a dedicated workflow area.

**Tech Stack:** C# 12, Dalamud ImGui bindings, xUnit, `dotnet test`, `dotnet format`, `MarketMafioso/tools/Deploy-DevPlugin.ps1`.

---

## File Structure

- Modify `MarketMafioso/WorkshopPrep/WorkshopQueueService.cs`
  - Add `SaveActiveQueue` so the UI can save a queue whether or not it is already linked to a frozen queue.
- Modify `MarketMafioso.Tests/WorkshopPrep/WorkshopQueueServiceTests.cs`
  - Cover save-as-new and save-existing behavior.
- Modify `MarketMafioso/Windows/ImGuiUi.cs`
  - Add small reusable helpers for section headings and popup menu items.
- Modify `MarketMafioso/Windows/MainWindow.cs`
  - Move controls into object-local section headers and replace `DrawWorkshopPrepActions` with an assembly workflow area.
- No production dependency on `mockups/workshop-prep-actions-redesign.html`.
  - It remains ignored scratch design material.

---

### Task 1: Queue Save Without Prior Freeze

**Files:**
- Modify: `MarketMafioso/WorkshopPrep/WorkshopQueueService.cs`
- Modify: `MarketMafioso.Tests/WorkshopPrep/WorkshopQueueServiceTests.cs`

- [ ] **Step 1: Add failing tests for save semantics**

Append these tests to `MarketMafioso.Tests/WorkshopPrep/WorkshopQueueServiceTests.cs`:

```csharp
[Fact]
public void SaveActiveQueue_creates_frozen_queue_when_active_queue_is_unsaved()
{
    var now = new DateTime(2026, 6, 24, 16, 0, 0, DateTimeKind.Utc);
    var config = new Configuration
    {
        WorkshopPrepQueue =
        [
            new WorkshopPrepQueueItem { WorkshopItemId = 531, Quantity = 2 },
        ],
    };

    var result = WorkshopQueueService.SaveActiveQueue(config, "Bridge batch", now);

    Assert.True(result.Success);
    Assert.Single(config.FrozenWorkshopQueues);
    Assert.Equal("Bridge batch", config.FrozenWorkshopQueues[0].Name);
    Assert.Equal(config.FrozenWorkshopQueues[0].Id, config.ActiveFrozenWorkshopQueueId);
    Assert.Equal(now, config.FrozenWorkshopQueues[0].CreatedAt);
    Assert.Equal(now, config.FrozenWorkshopQueues[0].UpdatedAt);
    Assert.NotSame(config.WorkshopPrepQueue[0], config.FrozenWorkshopQueues[0].Items[0]);
}

[Fact]
public void SaveActiveQueue_overwrites_active_frozen_queue_when_linked()
{
    var frozenId = Guid.NewGuid();
    var now = new DateTime(2026, 6, 24, 16, 30, 0, DateTimeKind.Utc);
    var config = new Configuration
    {
        ActiveFrozenWorkshopQueueId = frozenId,
        WorkshopPrepQueue =
        [
            new WorkshopPrepQueueItem { WorkshopItemId = 532, Quantity = 4 },
        ],
        FrozenWorkshopQueues =
        [
            new WorkshopFrozenQueue
            {
                Id = frozenId,
                Name = "Bridge batch",
                CreatedAt = now.AddDays(-1),
                UpdatedAt = now.AddDays(-1),
                Items =
                [
                    new WorkshopPrepQueueItem { WorkshopItemId = 531, Quantity = 1 },
                ],
            },
        ],
    };

    var result = WorkshopQueueService.SaveActiveQueue(config, "Ignored because linked", now);

    Assert.True(result.Success);
    Assert.Single(config.FrozenWorkshopQueues);
    Assert.Equal("Bridge batch", config.FrozenWorkshopQueues[0].Name);
    Assert.Equal(532u, config.FrozenWorkshopQueues[0].Items[0].WorkshopItemId);
    Assert.Equal(4, config.FrozenWorkshopQueues[0].Items[0].Quantity);
    Assert.Equal(now, config.FrozenWorkshopQueues[0].UpdatedAt);
}

[Fact]
public void SaveActiveQueue_requires_name_for_unsaved_queue()
{
    var config = new Configuration
    {
        WorkshopPrepQueue =
        [
            new WorkshopPrepQueueItem { WorkshopItemId = 531, Quantity = 2 },
        ],
    };

    var result = WorkshopQueueService.SaveActiveQueue(config, " ", DateTime.UtcNow);

    Assert.False(result.Success);
    Assert.Empty(config.FrozenWorkshopQueues);
    Assert.Null(config.ActiveFrozenWorkshopQueueId);
}
```

- [ ] **Step 2: Run tests to verify failure**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug -v minimal --filter "FullyQualifiedName~WorkshopQueueServiceTests.SaveActiveQueue"
```

Expected: compile failure because `WorkshopQueueService.SaveActiveQueue` does not exist.

- [ ] **Step 3: Implement `SaveActiveQueue`**

Add this method to `MarketMafioso/WorkshopPrep/WorkshopQueueService.cs` after `FreezeCurrentQueue`:

```csharp
public static WorkshopQueueOperationResult SaveActiveQueue(Configuration config, string name, DateTime nowUtc)
{
    if (config.ActiveFrozenWorkshopQueueId is { } queueId)
        return OverwriteFrozenQueue(config, queueId, nowUtc);

    return FreezeCurrentQueue(config, name, nowUtc);
}
```

- [ ] **Step 4: Run focused queue tests**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug -v minimal --filter "FullyQualifiedName~WorkshopQueueServiceTests"
```

Expected: all `WorkshopQueueServiceTests` pass.

---

### Task 2: Add Small Reusable ImGui Helpers

**Files:**
- Modify: `MarketMafioso/Windows/ImGuiUi.cs`

- [ ] **Step 1: Add compact section header helper**

Modify `MarketMafioso/Windows/ImGuiUi.cs` by adding:

```csharp
public static void SectionHeaderWithActions(string text, Vector4 color, Action drawActions)
{
    ImGui.TextColored(color, text);
    ImGui.SameLine();

    var contentWidth = ImGui.GetContentRegionAvail().X;
    var cursorY = ImGui.GetCursorPosY();
    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, contentWidth - 1));
    ImGui.SetCursorPosY(cursorY);
    drawActions();

    ImGui.Separator();
}
```

If this cursor math behaves poorly in-game, replace it during implementation with the simpler approach:

```csharp
public static void SectionHeaderWithActions(string text, Vector4 color, Action drawActions)
{
    ImGui.TextColored(color, text);
    ImGui.SameLine();
    drawActions();
    ImGui.Separator();
}
```

- [ ] **Step 2: Add menu item helper**

Add this helper to `MarketMafioso/Windows/ImGuiUi.cs`:

```csharp
public static bool MenuItem(string label, bool enabled)
{
    if (!enabled)
        ImGui.BeginDisabled();

    var clicked = ImGui.MenuItem(label);

    if (!enabled)
        ImGui.EndDisabled();

    return clicked;
}
```

- [ ] **Step 3: Build plugin**

Run:

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
```

Expected: build succeeds.

---

### Task 3: Move Workshop Controls To Object-Local Locations

**Files:**
- Modify: `MarketMafioso/Windows/MainWindow.cs`

- [ ] **Step 1: Replace the Prep Queue header**

In `DrawWorkshopPrepQueue`, replace:

```csharp
ImGuiUi.SectionHeader("Prep Queue", ColHeader);
DrawFrozenQueueToolbar();
ImGui.Spacing();
```

with:

```csharp
ImGuiUi.SectionHeaderWithActions("Prep Queue", ColHeader, DrawWorkshopQueueHeaderActions);
DrawFrozenQueueToolbar();
ImGui.Spacing();
```

- [ ] **Step 2: Add queue header action menus**

Add these methods near `DrawFrozenQueueToolbar`:

```csharp
private void DrawWorkshopQueueHeaderActions()
{
    var hasPrepQueue = config.WorkshopPrepQueue.Count > 0;
    var canEditQueue = !workshopAssemblyRunner.HasActiveRun;

    if (ImGui.Button("Handoff"))
        ImGui.OpenPopup("WorkshopQueueHandoffMenu");

    if (ImGui.BeginPopup("WorkshopQueueHandoffMenu"))
    {
        if (ImGuiUi.MenuItem("Send to VIWI", hasPrepQueue && canEditQueue))
            confirmViwiClear = true;

        ImGui.EndPopup();
    }

    ImGui.SameLine();
    if (ImGui.Button("Export"))
        ImGui.OpenPopup("WorkshopQueueExportMenu");

    if (ImGui.BeginPopup("WorkshopQueueExportMenu"))
    {
        if (ImGuiUi.MenuItem("Copy Artisan Manifest", hasPrepQueue))
            CopyWorkshopArtisanManifest();

        if (ImGuiUi.MenuItem("Copy Craft Architect Import", hasPrepQueue))
            CopyWorkshopCraftArchitectManifest();

        ImGui.EndPopup();
    }
}
```

- [ ] **Step 3: Rename and simplify frozen queue buttons**

In `DrawFrozenQueueToolbar`:

- Change `Freeze Current Queue` to `Save Queue`.
- Change `Save Frozen Queue` to `Save As...`.
- Use `WorkshopQueueService.SaveActiveQueue` for `Save Queue`.
- Keep `Save As...` using `FreezeCurrentQueue`.

The resulting button block should be:

```csharp
if (ImGuiUi.Button("Save Queue", canEditQueue && config.WorkshopPrepQueue.Count > 0))
    ApplyFrozenQueueResult(WorkshopQueueService.SaveActiveQueue(config, frozenQueueNameInput, DateTime.UtcNow), clearName: config.ActiveFrozenWorkshopQueueId == null);

ImGui.SameLine();
if (ImGuiUi.Button("Save As...", canEditQueue && config.WorkshopPrepQueue.Count > 0))
    ApplyFrozenQueueResult(WorkshopQueueService.FreezeCurrentQueue(config, frozenQueueNameInput, DateTime.UtcNow), clearName: true);
```

- [ ] **Step 4: Move material buttons to the Materials header**

Replace the first line in `DrawWorkshopMaterialSummary`:

```csharp
ImGuiUi.SectionHeader("Materials", ColHeader);
```

with:

```csharp
ImGuiUi.SectionHeaderWithActions("Materials", ColHeader, DrawWorkshopMaterialHeaderActions);
```

Add:

```csharp
private void DrawWorkshopMaterialHeaderActions()
{
    var canRefreshRetainers = autoRetainerRefresh.CanStartRefresh &&
                              !autoRetainerRefresh.IsRefreshing &&
                              !autoRetainerRefresh.IsStartQueued;

    if (ImGuiUi.Button("Refresh Retainer Cache", canRefreshRetainers))
        autoRetainerRefresh.StartFullRefresh();

    ImGui.SameLine();
    if (ImGuiUi.Button("Restock From Retainers", !workshopRetainerRestock.IsRunning))
        _ = workshopRetainerRestock.StartAsync(GetWorkshopAvailability());

    ImGui.SameLine();
    if (ImGui.Button("Columns"))
        ImGui.OpenPopup("WorkshopMaterialColumnsMenu");

    if (ImGui.BeginPopup("WorkshopMaterialColumnsMenu"))
    {
        ImGui.TextColored(ColMuted, "Use table header context menu to hide columns.");
        ImGui.EndPopup();
    }
}
```

- [ ] **Step 5: Replace bottom actions with assembly workflow**

Rename `DrawWorkshopPrepActions` to `DrawWorkshopAssemblyWorkflow` and remove:

- retainer refresh/restock buttons
- VIWI send button
- clear queue button
- manifest copy buttons

The method body should be:

```csharp
private void DrawWorkshopAssemblyWorkflow()
{
    ImGuiUi.SectionHeader("Assembly Workflow", ColHeader);

    var hasPrepQueue = config.WorkshopPrepQueue.Count > 0;
    if (workshopAssemblyRunner.IsPaused)
    {
        if (ImGui.Button("Resume"))
            workshopStatus = workshopAssemblyRunner.Resume().Message;

        ImGui.SameLine();
        if (ImGui.Button("Stop"))
        {
            workshopAssemblyRunner.Stop();
            workshopStatus = "Workshop assembly stopped.";
        }
    }
    else if (workshopAssemblyRunner.IsRunning)
    {
        if (ImGui.Button("Pause"))
            workshopStatus = workshopAssemblyRunner.Pause().Message;

        ImGui.SameLine();
        if (ImGui.Button("Stop"))
        {
            workshopAssemblyRunner.Stop();
            workshopStatus = "Workshop assembly stopped.";
        }
    }
    else
    {
        if (ImGuiUi.Button("Start Assembly", hasPrepQueue))
            StartWorkshopAssembly(enableDiagnostics: false);

        ImGui.SameLine();
        if (ImGuiUi.Button("Start With Diagnostics", hasPrepQueue))
            StartWorkshopAssembly(enableDiagnostics: true);
    }

    ImGui.Spacing();
    ImGui.TextColored(GetWorkshopStatusColor(), workshopStatus);
    ImGui.TextColored(workshopRetainerRestock.IsRunning ? ColHeader : ColMuted, workshopRetainerRestock.LastStatus);

    var progress = workshopAssemblyRunner.Progress;
    ImGui.TextColored(workshopAssemblyRunner.HasActiveRun ? ColHeader : ColMuted, progress.Message);
    if (progress.TotalProjects > 0)
        ImGui.TextColored(ColMuted, $"Assembly progress: {progress.CompletedProjects}/{progress.TotalProjects}");

    DrawWorkshopQueueConfirmations();
}
```

Update `DrawWorkshopPrepTab` to call:

```csharp
DrawWorkshopAssemblyWorkflow();
```

- [ ] **Step 6: Keep queue destructive confirmations near workflow status**

Extract the existing VIWI clear confirmation and clear queue action into:

```csharp
private void DrawWorkshopQueueConfirmations()
{
    var hasPrepQueue = config.WorkshopPrepQueue.Count > 0;
    var canEditQueue = !workshopAssemblyRunner.HasActiveRun;

    if (config.WorkshopPrepQueue.Count == 0)
        confirmViwiClear = false;

    if (!confirmViwiClear)
        return;

    ImGui.TextColored(ColMuted, "This will clear VIWI Workshoppa's queue and send the MarketMafioso prep queue.");

    if (ImGuiUi.Button("Confirm VIWI Queue Sync", hasPrepQueue && canEditQueue))
    {
        var result = viwiWorkshoppaIpc.SendQueue(config.WorkshopPrepQueue, clearExisting: true);
        workshopStatus = result.Message;
        confirmViwiClear = false;
    }

    ImGui.SameLine();
    if (ImGui.Button("Cancel VIWI Queue Sync"))
        confirmViwiClear = false;
}
```

Keep `Clear Prep Queue` inside the queue toolbar or manage popup only if it still has a clear placement. If it remains in the bottom workflow, rename it to `Clear Queue` and place it beside `New Queue`.

- [ ] **Step 7: Build**

Run:

```powershell
dotnet build "MarketMafioso.sln" -c Debug
```

Expected: build succeeds.

---

### Task 4: Verification And Dev Plugin Deploy

**Files:**
- No source edits expected unless verification finds a defect.

- [ ] **Step 1: Run plugin tests**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug -v minimal
```

Expected: all plugin tests pass.

- [ ] **Step 2: Run solution tests**

Run:

```powershell
dotnet test "MarketMafioso.sln" -c Debug -v minimal
```

Expected: plugin and server tests pass.

- [ ] **Step 3: Verify formatting**

Run:

```powershell
dotnet format "MarketMafioso.sln" --verify-no-changes
```

Expected: exit code 0.

- [ ] **Step 4: Check whitespace**

Run:

```powershell
git diff --check
```

Expected: no whitespace errors. CRLF conversion warnings are acceptable on this repo.

- [ ] **Step 5: Deploy to configured Dalamud target**

Run:

```powershell
& "MarketMafioso/tools/Deploy-DevPlugin.ps1"
```

Expected: script reports:

```text
Verified Dalamud target DLL: F:\Everything (HDD)\Misc\Gooseworks (Projects)\FFXIV-Development\MarketMafioso\MarketMafioso\bin\Release\MarketMafioso.dll
SHA256: <hash>
```

- [ ] **Step 6: Manual UI smoke test**

Reload MarketMafioso in Dalamud and verify:

- `Save Queue` creates a frozen queue when no frozen queue is active.
- `Save Queue` overwrites the active frozen queue when one is loaded.
- `Save As...` creates a separate frozen queue.
- `Handoff > Send to VIWI` opens the existing confirmation.
- `Export` menu copies Artisan and Craft Architect manifests.
- Material refresh/restock buttons appear in the Materials header.
- Bottom workflow only shows assembly controls, status, diagnostics, and progress.

---

## Self-Review

- Spec coverage: covers save-without-freeze, object-local queue/material controls, compact handoff/export menus, and assembly-only workflow area.
- Placeholder scan: no TBD/TODO placeholders remain.
- Type consistency: uses existing `WorkshopQueueService`, `Configuration`, `WorkshopFrozenQueue`, `MainWindow`, and `ImGuiUi` names.
