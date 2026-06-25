# Workshop Saved Jobs Browser and ETA Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Redesign the frozen queue browser as a Saved Jobs browser and add estimated assembly completion time for each saved job.

**Architecture:** Keep existing persisted configuration/model names (`FrozenWorkshopQueues`, `WorkshopFrozenQueue`) for compatibility, but change user-facing labels to Saved Jobs. Add a pure estimator that consumes the same catalog/queue data as native assembly, with catalog-provided contribution step counts so estimates are based on UI automation work units rather than raw material quantity. Rebuild the browser layout as a left list plus right inspector, matching the Workshop Prep workbench skeleton.

**Tech Stack:** C# 12, Dalamud ImGui, Lumina Excel sheets, xUnit tests, existing `WorkshopPrep` services.

---

## File Structure

- Modify: `MarketMafioso/WorkshopPrep/WorkshopPrepModels.cs`
  - Add estimated assembly metadata to `WorkshopProjectDefinition` and `WorkshopAssemblyQueueEntry`.
- Modify: `MarketMafioso/WorkshopPrep/WorkshopProjectCatalog.cs`
  - Compute per-project contribution step count and phase count from `CompanyCraftSequence.CompanyCraftPart` / `CompanyCraftProcess`.
- Modify: `MarketMafioso/WorkshopPrep/WorkshopAssemblyPlanBuilder.cs`
  - Carry estimate metadata into `WorkshopAssemblyQueueEntry`.
- Modify: `MarketMafioso/WorkshopPrep/WorkshopAssemblyTiming.cs`
  - Add reusable timing constants for estimation.
- Create: `MarketMafioso/WorkshopPrep/WorkshopAssemblyEstimator.cs`
  - Pure ETA calculator and formatter.
- Create: `MarketMafioso.Tests/WorkshopPrep/WorkshopAssemblyEstimatorTests.cs`
  - Unit coverage for project counts, contribution step math, phase/final/retrieval overhead, and duration formatting.
- Modify: `MarketMafioso.Tests/WorkshopPrep/WorkshopAssemblyPlanBuilderTests.cs`
  - Update test fixture construction to verify estimate metadata survives plan building.
- Modify: `MarketMafioso/Windows/WorkshopFrozenQueueBrowserWindow.cs`
  - Rename user-facing copy to Saved Jobs, increase window size, replace form pile with split browser/inspector layout, and add ETA column/metric.
- Modify: `MarketMafioso/Windows/MainWindow.cs`
  - Rename main controls from frozen queue to saved job where visible, without changing persisted field names.
- Optional modify: `mockups/workshop-saved-jobs-browser.html`
  - Keep mockup text aligned if implementation copy drifts.

## ETA Calculation Design

The estimator must not use total required material quantity as the primary duration driver. A project requiring 336 of an item may represent 48 repeated "contribute 7" confirmations, and those confirmation cycles are what cost time.

Calculation inputs:

- `TotalProjects`: `sum(queue item quantity)`.
- `ContributionSteps`: per project, `sum(process.Value.SetsRequired[index])` for each nonzero supply item in every nonzero company craft part, multiplied by queued quantity.
- `PhaseAdvancePrompts`: per project, `max(0, phase count - 1)`, multiplied by queued quantity.
- `FinalConstructionPrompts`: one per project instance.
- `ProductRetrievalPrompts`: one per project instance.
- `CutsceneSkips`: one per project instance. This is intentionally modeled even when cutscenes are skipped, because the runner still spends time detecting and dismissing them.
- `ProjectOpenOperations`: one per project instance.

Initial timing constants should live in `WorkshopAssemblyTiming`, not inside the UI:

```csharp
public static readonly TimeSpan EstimatedProjectOpen = TimeSpan.FromSeconds(2);
public static readonly TimeSpan EstimatedContributionStep = PostContributionLockout + TimeSpan.FromMilliseconds(650);
public static readonly TimeSpan EstimatedPhaseAdvance = TimeSpan.FromSeconds(2);
public static readonly TimeSpan EstimatedFinalConstruction = TimeSpan.FromSeconds(3);
public static readonly TimeSpan EstimatedCutsceneSkip = TimeSpan.FromSeconds(4);
public static readonly TimeSpan EstimatedProductRetrieval = TimeSpan.FromSeconds(2);
```

Formula:

```csharp
duration =
    (TotalProjects * EstimatedProjectOpen) +
    (ContributionSteps * EstimatedContributionStep) +
    (PhaseAdvancePrompts * EstimatedPhaseAdvance) +
    (FinalConstructionPrompts * EstimatedFinalConstruction) +
    (CutsceneSkips * EstimatedCutsceneSkip) +
    (ProductRetrievalPrompts * EstimatedProductRetrieval);
```

Known limitation for first pass:

- This estimates a full run from queue state. It does not inspect the live workshop UI to subtract already-contributed materials from a partially completed project. If we later want live ETA while the runner is active, use `WorkshopAssemblyProgress` plus observed UI progress to produce a remaining-time estimate.

## Export Label Guardrail

Superseded on 2026-06-25: Craft Architect export is now native `.craftplan` JSON and the visible action should be `Copy Craft Architect Plan`. This section originally preserved the older Teamcraft text bridge and should not be used for new UI work.

### Task 1: Add Estimate Metadata to Project and Plan Models

**Files:**
- Modify: `MarketMafioso/WorkshopPrep/WorkshopPrepModels.cs`
- Modify: `MarketMafioso/WorkshopPrep/WorkshopAssemblyModels.cs`
- Modify: `MarketMafioso/WorkshopPrep/WorkshopAssemblyPlanBuilder.cs`
- Modify: `MarketMafioso.Tests/WorkshopPrep/WorkshopAssemblyPlanBuilderTests.cs`

- [ ] **Step 1: Update the plan builder test fixture first**

In `MarketMafioso.Tests/WorkshopPrep/WorkshopAssemblyPlanBuilderTests.cs`, change the helper so tests can set phase and contribution metadata:

```csharp
private static WorkshopProjectDefinition BuildProject(
    uint workshopItemId,
    string name,
    uint materialItemId,
    int materialQuantity,
    int contributionSteps = 1,
    int phaseCount = 1)
{
    return new WorkshopProjectDefinition(
        workshopItemId,
        workshopItemId + 1000,
        name,
        0,
        [new WorkshopMaterialRequirement(materialItemId, "Material", 0, materialQuantity)],
        CategoryId: 0,
        TypeId: 0,
        EstimatedContributionSteps: contributionSteps,
        EstimatedPhaseCount: phaseCount);
}
```

Add a test:

```csharp
[Fact]
public void Build_carries_estimate_metadata_into_entries()
{
    var queue = new[] { new WorkshopPrepQueueItem { WorkshopItemId = 10, Quantity = 2 } };

    var plan = WorkshopAssemblyPlanBuilder.Build(
        queue,
        [BuildProject(10, "Project A", 100, 3, contributionSteps: 11, phaseCount: 3)]);

    var entry = Assert.Single(plan.Entries);
    Assert.Equal(11, entry.EstimatedContributionSteps);
    Assert.Equal(3, entry.EstimatedPhaseCount);
}
```

- [ ] **Step 2: Run the focused test and verify it fails**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~WorkshopAssemblyPlanBuilderTests"
```

Expected: compile fails because `EstimatedContributionSteps` and `EstimatedPhaseCount` do not exist yet.

- [ ] **Step 3: Add model fields**

In `MarketMafioso/WorkshopPrep/WorkshopPrepModels.cs`, change `WorkshopProjectDefinition` to:

```csharp
public sealed record WorkshopProjectDefinition(
    uint WorkshopItemId,
    uint ResultItemId,
    string Name,
    ushort IconId,
    IReadOnlyList<WorkshopMaterialRequirement> Materials,
    uint CategoryId = 0,
    uint TypeId = 0,
    int EstimatedContributionSteps = 0,
    int EstimatedPhaseCount = 1);
```

In `MarketMafioso/WorkshopPrep/WorkshopAssemblyModels.cs`, change `WorkshopAssemblyQueueEntry` to:

```csharp
public sealed record WorkshopAssemblyQueueEntry(
    uint WorkshopItemId,
    uint ResultItemId,
    uint CategoryId,
    uint TypeId,
    string ProjectName,
    int Quantity,
    IReadOnlyList<WorkshopMaterialRequirement> Materials,
    int EstimatedContributionSteps,
    int EstimatedPhaseCount);
```

- [ ] **Step 4: Carry metadata through the plan builder**

In `MarketMafioso/WorkshopPrep/WorkshopAssemblyPlanBuilder.cs`, update the `entries.Add` call:

```csharp
entries.Add(new WorkshopAssemblyQueueEntry(
    project.WorkshopItemId,
    project.ResultItemId,
    project.CategoryId,
    project.TypeId,
    project.Name,
    queueItem.Quantity,
    project.Materials,
    project.EstimatedContributionSteps,
    project.EstimatedPhaseCount));
```

- [ ] **Step 5: Run focused plan builder tests**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~WorkshopAssemblyPlanBuilderTests"
```

Expected: all `WorkshopAssemblyPlanBuilderTests` pass.

- [ ] **Step 6: Commit**

```powershell
git add "MarketMafioso/WorkshopPrep/WorkshopPrepModels.cs" "MarketMafioso/WorkshopPrep/WorkshopAssemblyModels.cs" "MarketMafioso/WorkshopPrep/WorkshopAssemblyPlanBuilder.cs" "MarketMafioso.Tests/WorkshopPrep/WorkshopAssemblyPlanBuilderTests.cs"
git commit -m "feat: carry workshop assembly estimate metadata"
```

### Task 2: Compute Contribution Step Metadata from Lumina Catalog Data

**Files:**
- Modify: `MarketMafioso/WorkshopPrep/WorkshopProjectCatalog.cs`

- [ ] **Step 1: Replace one-pass material extraction with local process rows**

Inside `BuildProject`, before material aggregation, create process rows:

```csharp
var processRows = sequence.CompanyCraftPart
    .Where(part => part.RowId != 0)
    .SelectMany(part => part.Value.CompanyCraftProcess)
    .SelectMany(process => Enumerable.Range(0, process.Value.SupplyItem.Count)
        .Select(index => new
        {
            SupplyItemId = process.Value.SupplyItem[index].RowId,
            SetQuantity = process.Value.SetQuantity[index],
            SetsRequired = process.Value.SetsRequired[index],
        }))
    .Where(x => x.SupplyItemId > 0 &&
                x.SetQuantity > 0 &&
                x.SetsRequired > 0 &&
                supplyItems.ContainsKey(x.SupplyItemId))
    .ToList();
```

Then build materials from `processRows`:

```csharp
var materials = processRows
    .Select(x =>
    {
        var item = supplyItems[x.SupplyItemId];
        return new WorkshopMaterialRequirement(
            item.RowId,
            item.Name.ToString(),
            item.Icon,
            x.SetQuantity * x.SetsRequired);
    })
    .GroupBy(x => x.ItemId)
    .Select(x =>
    {
        var first = x.First();
        return first with { Quantity = x.Sum(y => y.Quantity) };
    })
    .OrderBy(x => x.ItemName)
    .ToList();
```

- [ ] **Step 2: Compute phase and contribution counts**

Still inside `BuildProject`, add:

```csharp
var phaseCount = sequence.CompanyCraftPart.Count(part => part.RowId != 0);
var contributionSteps = processRows.Sum(x => x.SetsRequired);
```

When constructing `WorkshopProjectDefinition`, pass:

```csharp
return new WorkshopProjectDefinition(
    sequence.RowId,
    sequence.ResultItem.RowId,
    sequence.ResultItem.Value.Name.ToString(),
    sequence.ResultItem.Value.Icon,
    materials,
    sequence.CompanyCraftDraftCategory.RowId,
    sequence.CompanyCraftType.RowId,
    contributionSteps,
    Math.Max(1, phaseCount));
```

- [ ] **Step 3: Build plugin project**

Run:

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
```

Expected: build succeeds. This change uses Lumina-generated field types, so compile is the first meaningful verification.

- [ ] **Step 4: Commit**

```powershell
git add "MarketMafioso/WorkshopPrep/WorkshopProjectCatalog.cs"
git commit -m "feat: derive workshop contribution step counts"
```

### Task 3: Implement Pure ETA Estimator

**Files:**
- Modify: `MarketMafioso/WorkshopPrep/WorkshopAssemblyTiming.cs`
- Create: `MarketMafioso/WorkshopPrep/WorkshopAssemblyEstimator.cs`
- Create: `MarketMafioso.Tests/WorkshopPrep/WorkshopAssemblyEstimatorTests.cs`

- [ ] **Step 1: Write failing estimator tests**

Create `MarketMafioso.Tests/WorkshopPrep/WorkshopAssemblyEstimatorTests.cs`:

```csharp
using MarketMafioso.WorkshopPrep;

namespace MarketMafioso.Tests.WorkshopPrep;

public sealed class WorkshopAssemblyEstimatorTests
{
    [Fact]
    public void Estimate_counts_projects_contributions_and_phase_overhead()
    {
        var plan = new WorkshopAssemblyPlan(
            [
                new WorkshopAssemblyQueueEntry(
                    10,
                    1010,
                    0,
                    0,
                    "Project A",
                    2,
                    [],
                    EstimatedContributionSteps: 5,
                    EstimatedPhaseCount: 3),
            ],
            []);

        var estimate = WorkshopAssemblyEstimator.Estimate(plan);

        Assert.Equal(2, estimate.TotalProjects);
        Assert.Equal(10, estimate.ContributionSteps);
        Assert.Equal(4, estimate.PhaseAdvancePrompts);
        Assert.Equal(2, estimate.FinalConstructionPrompts);
        Assert.Equal(2, estimate.ProductRetrievalPrompts);
        Assert.Equal(2, estimate.CutsceneSkips);
        Assert.True(estimate.Duration > TimeSpan.Zero);
    }

    [Theory]
    [InlineData(0, "0m")]
    [InlineData(59, "<1m")]
    [InlineData(60, "~1m")]
    [InlineData(3599, "~60m")]
    [InlineData(3600, "~1h")]
    [InlineData(5400, "~1h 30m")]
    public void FormatDuration_uses_compact_human_readable_text(int seconds, string expected)
    {
        Assert.Equal(expected, WorkshopAssemblyEstimator.FormatDuration(TimeSpan.FromSeconds(seconds)));
    }
}
```

- [ ] **Step 2: Run focused tests and verify they fail**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~WorkshopAssemblyEstimatorTests"
```

Expected: compile fails because `WorkshopAssemblyEstimator` does not exist.

- [ ] **Step 3: Add timing constants**

In `MarketMafioso/WorkshopPrep/WorkshopAssemblyTiming.cs`, append:

```csharp
public static readonly TimeSpan EstimatedProjectOpen = TimeSpan.FromSeconds(2);
public static readonly TimeSpan EstimatedContributionStep = PostContributionLockout + TimeSpan.FromMilliseconds(650);
public static readonly TimeSpan EstimatedPhaseAdvance = TimeSpan.FromSeconds(2);
public static readonly TimeSpan EstimatedFinalConstruction = TimeSpan.FromSeconds(3);
public static readonly TimeSpan EstimatedCutsceneSkip = TimeSpan.FromSeconds(4);
public static readonly TimeSpan EstimatedProductRetrieval = TimeSpan.FromSeconds(2);
```

- [ ] **Step 4: Create estimator implementation**

Create `MarketMafioso/WorkshopPrep/WorkshopAssemblyEstimator.cs`:

```csharp
using System;
using System.Linq;

namespace MarketMafioso.WorkshopPrep;

public sealed record WorkshopAssemblyEstimate(
    TimeSpan Duration,
    int TotalProjects,
    int ContributionSteps,
    int PhaseAdvancePrompts,
    int FinalConstructionPrompts,
    int ProductRetrievalPrompts,
    int CutsceneSkips);

public static class WorkshopAssemblyEstimator
{
    public static WorkshopAssemblyEstimate Estimate(WorkshopAssemblyPlan plan)
    {
        var totalProjects = plan.Entries.Sum(x => x.Quantity);
        var contributionSteps = plan.Entries.Sum(x => x.Quantity * x.EstimatedContributionSteps);
        var phaseAdvancePrompts = plan.Entries.Sum(x => x.Quantity * Math.Max(0, x.EstimatedPhaseCount - 1));
        var finalConstructionPrompts = totalProjects;
        var productRetrievalPrompts = totalProjects;
        var cutsceneSkips = totalProjects;

        var duration =
            (totalProjects * WorkshopAssemblyTiming.EstimatedProjectOpen) +
            (contributionSteps * WorkshopAssemblyTiming.EstimatedContributionStep) +
            (phaseAdvancePrompts * WorkshopAssemblyTiming.EstimatedPhaseAdvance) +
            (finalConstructionPrompts * WorkshopAssemblyTiming.EstimatedFinalConstruction) +
            (cutsceneSkips * WorkshopAssemblyTiming.EstimatedCutsceneSkip) +
            (productRetrievalPrompts * WorkshopAssemblyTiming.EstimatedProductRetrieval);

        return new WorkshopAssemblyEstimate(
            duration,
            totalProjects,
            contributionSteps,
            phaseAdvancePrompts,
            finalConstructionPrompts,
            productRetrievalPrompts,
            cutsceneSkips);
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return "0m";

        if (duration < TimeSpan.FromMinutes(1))
            return "<1m";

        var totalMinutes = (int)Math.Ceiling(duration.TotalMinutes);
        if (totalMinutes < 60)
            return $"~{totalMinutes}m";

        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        return minutes == 0
            ? $"~{hours}h"
            : $"~{hours}h {minutes}m";
    }
}
```

- [ ] **Step 5: Run estimator tests**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~WorkshopAssemblyEstimatorTests"
```

Expected: all `WorkshopAssemblyEstimatorTests` pass.

- [ ] **Step 6: Commit**

```powershell
git add "MarketMafioso/WorkshopPrep/WorkshopAssemblyTiming.cs" "MarketMafioso/WorkshopPrep/WorkshopAssemblyEstimator.cs" "MarketMafioso.Tests/WorkshopPrep/WorkshopAssemblyEstimatorTests.cs"
git commit -m "feat: estimate workshop saved job completion time"
```

### Task 4: Redesign Browser Surface as Saved Jobs

**Files:**
- Modify: `MarketMafioso/Windows/WorkshopFrozenQueueBrowserWindow.cs`

- [ ] **Step 1: Rename user-facing window title and resize**

Change the constructor base title and size constraints:

```csharp
: base("Saved Jobs##MarketMafiosoFrozenQueueBrowser", ImGuiWindowFlags.None)
```

```csharp
SizeConstraints = new WindowSizeConstraints
{
    MinimumSize = new Vector2(900, 560),
    MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
};
```

Keep the hidden ID suffix unchanged to avoid losing ImGui window state unnecessarily.

- [ ] **Step 2: Add selected-job estimate helper**

Add this private helper:

```csharp
private WorkshopAssemblyEstimate? TryEstimateQueue(WorkshopFrozenQueue queue)
{
    try
    {
        var plan = WorkshopAssemblyPlanBuilder.Build(queue.Items, workshopCatalog.GetProjects());
        return WorkshopAssemblyEstimator.Estimate(plan);
    }
    catch (InvalidOperationException)
    {
        return null;
    }
}
```

This intentionally treats unknown/stale project IDs as unavailable estimates instead of inventing fallback data.

- [ ] **Step 3: Replace `DrawSearchAndCreate` with a top command strip**

Rewrite `DrawSearchAndCreate`:

```csharp
private void DrawSearchAndCreate()
{
    ImGuiUi.SectionHeaderWithActions(
        "Workshop Saved Jobs",
        ColHeader,
        () =>
        {
            if (ImGui.Button("Close"))
                IsOpen = false;
        },
        actionWidth: 70);

    var saveWidth = 160f;
    var nameWidth = Math.Max(220f, (ImGui.GetContentRegionAvail().X - saveWidth) * 0.45f);
    ImGui.SetNextItemWidth(nameWidth);
    ImGui.InputText("New saved job name##workshopSavedJobNewName", ref newQueueNameInput, 128);
    ImGui.SameLine();
    if (ImGuiUi.Button("Save Current As New", actions.CanEditQueue && config.WorkshopPrepQueue.Count > 0))
        actions.NewFromCurrent(newQueueNameInput);

    ImGui.SetNextItemWidth(-1);
    ImGui.InputText("Search##workshopSavedJobSearch", ref search, 256);
}
```

- [ ] **Step 4: Change list table columns**

In `DrawQueueTable`, rename visible text and add ETA:

```csharp
var etaWidth = Math.Max(ImGui.CalcTextSize("~1h 30m").X, ImGui.CalcTextSize("ETA").X) + 24;
if (ImGui.BeginTable("WorkshopFrozenQueueBrowserTable", 5, flags, new Vector2(0, 260)))
{
    ImGui.TableSetupColumn("Saved Job", ImGuiTableColumnFlags.WidthFixed, nameWidth);
    ImGui.TableSetupColumn("Projects", ImGuiTableColumnFlags.WidthFixed, projectWidth);
    ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, stateWidth);
    ImGui.TableSetupColumn("ETA", ImGuiTableColumnFlags.WidthFixed, etaWidth);
    ImGui.TableSetupColumn("Updated", ImGuiTableColumnFlags.WidthFixed, updatedWidth);
    ImGui.TableHeadersRow();
```

Inside the row, after status:

```csharp
ImGui.TableNextColumn();
var estimate = TryEstimateQueue(queue);
ImGui.TextUnformatted(estimate == null
    ? "-"
    : WorkshopAssemblyEstimator.FormatDuration(estimate.Duration));
```

Update empty state text to:

```csharp
ImGui.TextColored(ColMuted, "No saved jobs match this search.");
```

- [ ] **Step 5: Convert selected job details into inspector flow**

In `DrawSelectedQueue`, change empty text to:

```csharp
ImGui.TextColored(ColMuted, "Select a saved job to preview its projects.");
```

After the selected job title/state, draw ETA summary:

```csharp
var estimate = TryEstimateQueue(queue);
if (estimate != null)
{
    ImGui.TextColored(ColMuted, $"Estimated time: {WorkshopAssemblyEstimator.FormatDuration(estimate.Duration)}");
    ImGui.SameLine();
    ImGui.TextColored(ColMuted, $"Projects: {estimate.TotalProjects}");
    ImGui.SameLine();
    ImGui.TextColored(ColMuted, $"Contribution steps: {estimate.ContributionSteps}");
}
else
{
    ImGui.TextColored(ColMuted, "Estimated time: unavailable");
}
```

Keep the preview table, but rename the action labels in `DrawQueueActions`:

```csharp
ImGui.InputText("Name##workshopSavedJobRename", ref renameInput, 128);
...
ImGui.InputText("Duplicate as##workshopSavedJobDuplicate", ref duplicateNameInput, 128);
```

- [ ] **Step 6: Run plugin build**

Run:

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
```

Expected: build succeeds.

- [ ] **Step 7: Commit**

```powershell
git add "MarketMafioso/Windows/WorkshopFrozenQueueBrowserWindow.cs"
git commit -m "feat: redesign workshop saved jobs browser"
```

### Task 5: Rename Main Workshop Prep Visible Copy

**Files:**
- Modify: `MarketMafioso/Windows/MainWindow.cs`

- [ ] **Step 1: Change active queue labels**

In `DrawFrozenQueueToolbar`, change:

```csharp
? "Active queue: unsaved"
: WorkshopQueueService.ActiveQueueMatchesFrozenQueue(config)
    ? $"Active saved job: {activeFrozenQueue.Name}"
    : $"Active saved job: {activeFrozenQueue.Name} (modified)";
```

- [ ] **Step 2: Change load combo text**

In `DrawFrozenQueueLoadCombo`, change preview fallback to:

```csharp
: "Load saved job...";
```

and the `BeginCombo` fallback to use the same text. Keep method names unchanged.

- [ ] **Step 3: Change manage button and confirmations**

Change button text:

```csharp
if (ImGui.Button("Manage Saved Jobs"))
    FrozenQueueBrowser.IsOpen = true;
```

Superseded on 2026-06-25: if this task touches the nearby export helper text, keep Craft Architect described as a native `.craftplan` JSON target:

```csharp
ImGui.TextColored(ColMuted, "Handoff contains VIWI and future queue targets. Export contains Artisan JSON and Craft Architect .craftplan JSON.");
```

Change confirmation messages:

```csharp
ImGui.TextColored(ColMuted, "Load saved job? Unsaved active queue changes will be discarded.");
```

```csharp
if (ImGuiUi.Button("Confirm Load Saved Job", canEditQueue && selectedFrozenQueueId != null))
```

```csharp
if (ImGui.Button("Cancel Load Saved Job"))
```

- [ ] **Step 4: Build**

Run:

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
```

Expected: build succeeds.

- [ ] **Step 5: Commit**

```powershell
git add "MarketMafioso/Windows/MainWindow.cs"
git commit -m "chore: rename frozen queue UI to saved jobs"
```

### Task 6: Verification and Deployment

**Files:**
- No source edits unless verification finds an issue.

- [ ] **Step 1: Run focused tests**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~WorkshopAssemblyEstimatorTests|FullyQualifiedName~WorkshopAssemblyPlanBuilderTests|FullyQualifiedName~WorkshopQueueServiceTests"
```

Expected: all selected tests pass.

- [ ] **Step 2: Run plugin tests**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug -v minimal
```

Expected: all plugin tests pass.

- [ ] **Step 3: Verify formatting**

Run:

```powershell
dotnet format "MarketMafioso.sln" --verify-no-changes
```

Expected: no formatting changes required.

- [ ] **Step 4: Deploy dev plugin**

Run:

```powershell
& "MarketMafioso/tools/Deploy-DevPlugin.ps1"
```

Expected: script reports the configured Dalamud target DLL was built/refreshed and prints the active target path.

- [ ] **Step 5: In-game visual check**

Open `/mmf`, go to Workshop Prep, click `Manage Saved Jobs`, and verify:

- Window title says `Saved Jobs`.
- Search and `Save Current As New` live in a clean top command strip.
- Saved job table has columns `Saved Job`, `Projects`, `Status`, `ETA`, `Updated`.
- Selecting a saved job shows preview, rename, duplicate, `Load`, `Overwrite With Current`, and `Delete`.
- ETA text is compact (`<1m`, `~42m`, `~1h 18m`) and does not resize columns awkwardly.

- [ ] **Step 6: Commit verification fixes if needed**

If verification required source fixes:

```powershell
git add <changed-files>
git commit -m "fix: polish workshop saved jobs browser"
```

---

## Self-Review

- Spec coverage: The plan covers Saved Jobs naming, browser layout redesign, and ETA display in both list and inspector.
- ETA coverage: The plan derives contribution steps from `SetsRequired`, models phase/final/retrieval/cutscene overhead, and centralizes constants in `WorkshopAssemblyTiming`.
- Export copy: Superseded on 2026-06-25; Craft Architect export is now native `.craftplan` JSON, not a plain-text import manifest.
- Compatibility: Persisted config names and JSON keys remain unchanged.
- Risk: Exact ETA still depends on runtime behavior and game latency. The initial constants are deliberately explicit and testable so they can later be tuned from diagnostic logs.
