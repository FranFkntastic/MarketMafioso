# ECommons UI Automation Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Market Acquisition's fragile time-gated market-board automation with an ECommons-backed, condition-driven automation layer while keeping the plugin loadable and usable between every slice.

**Architecture:** ECommons should be added as a dependency, but isolated behind `MarketMafioso.UiAutomation` facades. Market Acquisition will migrate one behavior path at a time: bootstrap dependency, introduce task/predicate helpers, convert search, convert listing selection, convert confirmation/removal waiting, prove listing-cache freshness, then extract execution orchestration out of `MainWindow`.

**Tech Stack:** C# 12, `net8.0-windows`, Dalamud.NET.Sdk 15, ECommons `3.2.1.15`, xUnit tests, Dalamud dev-plugin deployment via `MarketMafioso/tools/Deploy-DevPlugin.ps1`.

---

## Current Execution Status

Last updated: 2026-06-30

- Slice 1 complete: ECommons is pinned, initialized on plugin startup, disposed during plugin shutdown, and deployed through the dev-plugin path.
- Slice 2 complete: `MarketMafioso.UiAutomation` now owns the ECommons task queue/readiness wrapper seam so market-acquisition code does not call ECommons directly for generic orchestration.
- Slice 3 complete: market-board item search uses the shared text-input helper and ECommons button-click helper, and only reports submitted search when an exact result, visible result, agent work, or actual search-button activation is observed.
- Slice 4 complete: listing selection now uses `MarketBoardListingListProbe` and treats a not-yet-clickable listing list as recoverable instead of terminal.
- Slice 5 complete: purchase confirmation now has explicit phases through `MarketBoardPurchaseSessionPhase`; confirmation submission and listing-removal proof are separate states.
- Slice 5.5 complete: listing-cache freshness is explicit before further orchestration refactor. The 2026-06-30 route after the freshness slice completed without incident, and commit `073372a` was pushed to `local-dev`, fast-forwarded into `main`, pushed, and deployed to the configured dev-plugin DLL.
- Slice 6 complete as a local-dev checkpoint: `MarketBoardAutomationController` owns purchase session/result state, the purchase monitor schedule, and the confirmation/listing-removal polling state machine. `MainWindow` still owns route orchestration, diagnostic snapshot recording, and route-level purchase counters.
- Slice 7 intentionally partial: obsolete arbitrary search success checks were removed where proven harmful, but watchdogs remain as failure boundaries. Do not remove remaining timing boundaries until live logs prove equivalent condition-based behavior.
- Slice 8 pending live validation: SimpleTweaks-enabled search, listing-list retry, multi-item same-world routes, and multi-world routes still need in-game confirmation.

## Verification Log

- `dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug` passed for the ECommons bootstrap.
- `dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketBoardItemSearchDriverTests" -v minimal` passed after search conversion.
- `dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~DalamudMarketBoardPurchaseAdapterTests" -v minimal` passed after listing-list readiness conversion.
- `dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketBoardPurchaseSessionTests" -v minimal` passed after purchase-session phase modeling.
- `dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketBoardAutomationControllerTests" -v minimal` passed after introducing the controller seam.
- `dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketBoardListingReaderTests|FullyQualifiedName~MarketAcquisitionLiveCandidatePlannerTests|FullyQualifiedName~MarketAcquisitionRouteDiagnosticsTests|FullyQualifiedName~MarketAcquisitionRouteRunnerTests" -v minimal` passed after listing-cache freshness classification and non-terminal route retry handling.
- `dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketBoardAutomationControllerTests" -v minimal` passed with 8/8 after moving purchase session/result state and monitor scheduling into `MarketBoardAutomationController`.
- `dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug` passed with 0 warnings / 0 errors after the controller state extraction.
- `MarketMafioso/tools/Deploy-DevPlugin.ps1` deployed the controller state extraction from `local-dev@073372a`; visible manifest `1.1.227.226`, target DLL SHA256 `00E2F81CC1B3490874CC2D6C77317BD4BA7E085E78E2107BDE407DA3731B4894`.
- `dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketBoardAutomationControllerTests" -v minimal` passed with 11/11 after moving confirmation/listing-removal polling into `MarketBoardAutomationController`.
- `dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug` passed with 0 warnings / 0 errors after controller-owned polling.
- `MarketMafioso/tools/Deploy-DevPlugin.ps1` deployed the controller-owned polling checkpoint from the pre-commit local-dev worktree; visible manifest `1.1.227.11306`, target DLL SHA256 `2C2AE8AD58A12919D273A904FFEE9CC63BC863C3D9450AD01741E2D294A32075`.
- Parallel test/build execution caused a known `DalamudPackager` artifact file lock. Run focused tests and builds sequentially for this repo.

---

## Stability Rules

- Each slice must compile independently.
- Each slice must leave Inventory Reporter and Workshop Logistics untouched.
- Until a new ECommons-backed behavior is proven live, keep the current path available through a small adapter boundary.
- Do not introduce broad market-acquisition rewrites inside the dependency bootstrap slice.
- Do not delete current diagnostics until the replacement diagnostics are visible in route logs.
- For live-tested plugin behavior, deploy with `MarketMafioso/tools/Deploy-DevPlugin.ps1` and report the visible manifest version plus target DLL hash.
- The existing dirty work in the current tree is part of the active market-acquisition patch lane. Do not revert it while executing this plan.

## Target File Structure

- Modify: `MarketMafioso/MarketMafioso.csproj`
  - Adds the pinned ECommons dependency.
- Modify: `MarketMafioso/Plugin.cs`
  - Initializes and disposes ECommons.
- Create: `MarketMafioso/UiAutomation/UiAutomationTaskQueue.cs`
  - Thin wrapper around `ECommons.Automation.NeoTaskManager.TaskManager`.
- Create: `MarketMafioso/UiAutomation/UiAutomationTaskResult.cs`
  - Small MMF-owned task result/status contract.
- Create: `MarketMafioso/UiAutomation/AddonStateReader.cs`
  - MMF-owned wrapper for addon lookup/readiness checks.
- Modify: `MarketMafioso/UiAutomation/AtkTextInputAutomation.cs`
  - Moves text-input callback/focus behavior onto ECommons helpers where useful.
- Create: `MarketMafioso/MarketAcquisition/MarketBoardAutomationController.cs`
  - Coordinates market-board search, listing readiness, purchase selection, and confirmation as named condition-based steps.
- Create: `MarketMafioso/MarketAcquisition/MarketBoardListingListProbe.cs`
  - Probes clickable market-board listing list components and returns rich diagnostics.
- Modify: `MarketMafioso/MarketAcquisition/MarketBoardItemSearchDriver.cs`
  - Converts search submission and item-result selection to condition-gated operations.
- Modify: `MarketMafioso/MarketAcquisition/DalamudMarketBoardPurchaseAdapter.cs`
  - Uses `MarketBoardListingListProbe` and waits for clickable rows before dispatching purchase selection.
- Modify: `MarketMafioso/MarketAcquisition/MarketBoardPurchaseSession.cs`
  - Converts confirmation/removal waiting into explicit step state.
- Modify: `MarketMafioso/MarketAcquisition/MarketBoardListingReader.cs`
  - Surfaces listing-cache freshness/generation state instead of treating active search item id as proof that rows are current.
- Modify: `MarketMafioso/MarketAcquisition/MarketBoardLiveListingModels.cs`
  - Adds a typed listing read state that route code can consume without string comparisons.
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionLiveCandidatePlanner.cs`
  - Plans only from fresh listing reads.
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionRouteRunner.cs`
  - Retries stale or switching listing reads instead of marking the line/world complete.
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionRouteDiagnostics.cs`
  - Records read-state and raw item-id mismatch evidence in route logs and observed-listing CSVs.
- Modify: `MarketMafioso/Windows/MainWindow.cs`
  - Removes low-level time-gated orchestration gradually; delegates to controller once equivalent behavior exists.
- Test: `MarketMafioso.Tests/MarketAcquisition/*`
  - Adds focused tests for step transitions, probe diagnostics, and timeout classification.

---

## Slice 1: Dependency Bootstrap With No Behavior Change

**Files:**
- Modify: `MarketMafioso/MarketMafioso.csproj`
- Modify: `MarketMafioso/Plugin.cs`

- [ ] **Step 1: Add ECommons package reference**

Add this package reference to `MarketMafioso/MarketMafioso.csproj`:

```xml
<PackageReference Include="ECommons" Version="3.2.1.15" />
```

Keep it in a normal `ItemGroup`. Do not add ECommons usage in market-acquisition code yet.

- [ ] **Step 2: Initialize ECommons in plugin startup**

In `MarketMafioso/Plugin.cs`, add:

```csharp
using ECommons;
```

In the `Plugin()` constructor, immediately after `Instance = this;`, initialize ECommons with reduced logging:

```csharp
ECommonsMain.ReducedLogging = true;
ECommonsMain.Init(PluginInterface, this);
```

- [ ] **Step 3: Dispose ECommons last**

In `Plugin.Dispose()`, after existing MMF cleanup has disposed windows/services, add:

```csharp
ECommonsMain.Dispose();
```

If ECommons disposal throws during early live testing, wrap it in a narrow `try/catch` that logs an error through `Log.Error`, but do not swallow initialization failure during startup.

- [ ] **Step 4: Verify bootstrap only**

Run:

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
```

Expected: build succeeds.

Run:

```powershell
MarketMafioso/tools/Deploy-DevPlugin.ps1
```

Expected: source and target hashes match, plugin loads, current Market Acquisition behavior is unchanged.

- [ ] **Step 5: Commit bootstrap**

Commit only the ECommons bootstrap files:

```powershell
git add MarketMafioso/MarketMafioso.csproj MarketMafioso/Plugin.cs
git commit -m "chore: bootstrap ECommons dependency"
```

---

## Slice 2: Add MMF-Owned Automation Facades

**Files:**
- Create: `MarketMafioso/UiAutomation/UiAutomationTaskResult.cs`
- Create: `MarketMafioso/UiAutomation/UiAutomationTaskQueue.cs`
- Create: `MarketMafioso/UiAutomation/AddonStateReader.cs`
- Modify: `MarketMafioso.Tests/MarketAcquisition/DalamudMarketBoardPurchaseAdapterTests.cs` only if helper tests need existing test fixtures

- [ ] **Step 1: Create task result model**

Create `MarketMafioso/UiAutomation/UiAutomationTaskResult.cs`:

```csharp
namespace MarketMafioso.UiAutomation;

public enum UiAutomationTaskOutcome
{
    Waiting,
    Complete,
    Abort,
}

public sealed record UiAutomationTaskResult(
    UiAutomationTaskOutcome Outcome,
    string Message,
    IReadOnlyDictionary<string, string>? Diagnostics = null)
{
    public static UiAutomationTaskResult Waiting(string message, IReadOnlyDictionary<string, string>? diagnostics = null) =>
        new(UiAutomationTaskOutcome.Waiting, message, diagnostics);

    public static UiAutomationTaskResult Complete(string message, IReadOnlyDictionary<string, string>? diagnostics = null) =>
        new(UiAutomationTaskOutcome.Complete, message, diagnostics);

    public static UiAutomationTaskResult Abort(string message, IReadOnlyDictionary<string, string>? diagnostics = null) =>
        new(UiAutomationTaskOutcome.Abort, message, diagnostics);
}
```

- [ ] **Step 2: Create ECommons task queue wrapper**

Create `MarketMafioso/UiAutomation/UiAutomationTaskQueue.cs`:

```csharp
using ECommons.Automation.NeoTaskManager;

namespace MarketMafioso.UiAutomation;

public sealed class UiAutomationTaskQueue : IDisposable
{
    private readonly TaskManager taskManager;

    public UiAutomationTaskQueue(int timeLimitMs = 15000)
    {
        taskManager = new TaskManager(new TaskManagerConfiguration(
            abortOnTimeout: true,
            abortOnError: true,
            showDebug: false,
            timeLimitMS: timeLimitMs,
            timeoutSilently: true));
    }

    public bool IsBusy => taskManager.IsBusy;

    public void Enqueue(string name, Func<UiAutomationTaskResult> step)
    {
        taskManager.Enqueue(
            () =>
            {
                var result = step();
                return result.Outcome switch
                {
                    UiAutomationTaskOutcome.Waiting => false,
                    UiAutomationTaskOutcome.Complete => true,
                    UiAutomationTaskOutcome.Abort => null,
                    _ => null,
                };
            },
            name);
    }

    public void Abort() => taskManager.Abort();

    public void Dispose() => taskManager.Dispose();
}
```

- [ ] **Step 3: Create addon state reader wrapper**

Create `MarketMafioso/UiAutomation/AddonStateReader.cs`:

```csharp
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using static ECommons.GenericHelpers;

namespace MarketMafioso.UiAutomation;

public sealed class AddonStateReader
{
    private readonly IGameGui gameGui;

    public AddonStateReader(IGameGui gameGui)
    {
        this.gameGui = gameGui;
    }

    public unsafe T* GetAddon<T>(string addonName) where T : unmanaged =>
        gameGui.GetAddonByName<T>(addonName, 1);

    public unsafe bool IsReady(AtkUnitBase* addon) =>
        addon != null && IsAddonReady(addon);
}
```

- [ ] **Step 4: Build facade slice**

Run:

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
```

Expected: build succeeds and behavior remains unchanged.

- [ ] **Step 5: Commit facade slice**

```powershell
git add MarketMafioso/UiAutomation/UiAutomationTaskResult.cs MarketMafioso/UiAutomation/UiAutomationTaskQueue.cs MarketMafioso/UiAutomation/AddonStateReader.cs
git commit -m "feat: add ECommons-backed UI automation facade"
```

---

## Slice 3: Convert Market-Board Search Behind Existing Public Contract

**Files:**
- Modify: `MarketMafioso/MarketAcquisition/MarketBoardItemSearchDriver.cs`
- Modify: `MarketMafioso.Tests/MarketAcquisition/MarketBoardItemSearchDriverTests.cs`
- Modify: `MarketMafioso/UiAutomation/AtkTextInputAutomation.cs`

- [ ] **Step 1: Preserve `MarketBoardItemSearchDriver.Search` and `Observe` method signatures**

Do not change call sites in `MainWindow` in this slice. `Search(uint itemId, string? itemName)` and `Observe(uint itemId, string? itemName)` must still return `MarketBoardItemSearchResult`.

- [ ] **Step 2: Replace fixed retry interpretation with predicate statuses**

In `MarketBoardItemSearchDriver`, keep the existing statuses:

- `MarketBoardNotOpen`
- `ModeReset`
- `SearchSent`
- `ItemResultsReady`
- `ItemOpenSent`
- `ListingsReady`
- `SearchSubmitFailed`

Change internals so `SearchSent` means the addon accepted the input/search event, not merely that MMF wrote text into a field. If ECommons/Atk focus evidence says the button remains disabled, return `SearchSubmitFailed` with diagnostics instead of `SearchSent`.

- [ ] **Step 3: Convert text submission helper to ECommons-compatible callback flow**

Use `AtkTextInputAutomation` as the only place that directly touches input focus, text, text-changed callbacks, enter callbacks, or receive-event click helpers. `MarketBoardItemSearchDriver` should call helper methods rather than manipulating input fields directly.

- [ ] **Step 4: Add focused tests for status classification**

Update `MarketMafioso.Tests/MarketAcquisition/MarketBoardItemSearchDriverTests.cs` with pure tests for helper methods already exposed as `internal static`:

```csharp
[Fact]
public void ShouldWaitForSubmittedSearch_WhenExactItemNotVisibleAndPushPending_ReturnsTrue()
{
    var result = MarketBoardItemSearchDriver.ShouldWaitForSubmittedSearch(
        searchMatchesSubmittedState: true,
        exactItemVisible: false,
        agentIsPartialSearching: false,
        agentIsItemPushPending: true,
        elapsedSinceSubmit: TimeSpan.FromMilliseconds(250),
        retryDelay: TimeSpan.FromSeconds(1));

    Assert.True(result);
}
```

Add companion tests for "retry delay elapsed" and "exact item visible".

- [ ] **Step 5: Verify search conversion**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketBoardItemSearchDriverTests" -v minimal
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
MarketMafioso/tools/Deploy-DevPlugin.ps1
```

Expected: focused tests pass, plugin builds, deploy succeeds. Live test should confirm SimpleTweaks-enabled search still triggers.

- [ ] **Step 6: Commit search slice after live confirmation**

```powershell
git add MarketMafioso/MarketAcquisition/MarketBoardItemSearchDriver.cs MarketMafioso/UiAutomation/AtkTextInputAutomation.cs MarketMafioso.Tests/MarketAcquisition/MarketBoardItemSearchDriverTests.cs
git commit -m "feat: drive market search through condition-aware UI automation"
```

---

## Slice 4: Convert Listing Selection Readiness

**Files:**
- Create: `MarketMafioso/MarketAcquisition/MarketBoardListingListProbe.cs`
- Modify: `MarketMafioso/MarketAcquisition/DalamudMarketBoardPurchaseAdapter.cs`
- Create or modify: `MarketMafioso.Tests/MarketAcquisition/DalamudMarketBoardPurchaseAdapterTests.cs`

- [ ] **Step 1: Extract list probing from purchase adapter**

Move `FindListingList`, `DescribeListingLists`, and list-candidate selection rules from `DalamudMarketBoardPurchaseAdapter` into `MarketBoardListingListProbe`.

The probe result must include:

```csharp
public sealed record MarketBoardListingListProbeResult(
    bool IsReady,
    uint? ComponentId,
    int VisibleItemCount,
    int RequestedRow,
    string Diagnostic);
```

- [ ] **Step 2: Define readiness explicitly**

`IsReady` is true only when:

- the listing addon is present,
- the addon is ready and visible,
- a candidate list component exists,
- the component has at least one visible/clickable item,
- the requested row can be selected or scrolled into range.

`InfoProxyItemSearch.ListingCount > 0` is not sufficient.

- [ ] **Step 3: Change purchase adapter failure status**

If info proxy has rows but the clickable list is not ready, return:

```text
Status: ListingListNotReady
Message: Market-board listing data is ready, but the clickable listing component is not ready yet.
```

This status is recoverable. It must not fail the route immediately.

- [ ] **Step 4: Teach MainWindow/route monitor to retry recoverable listing readiness**

In `MainWindow.BeginNextWorldPurchase`, treat `ListingListNotReady` like a wait state:

- record automation snapshot,
- set `nextGuidedRouteMonitorUtc` to a short retry,
- do not call `FailRoute`.

This is a transitional step. Slice 6 will move this out of `MainWindow`.

- [ ] **Step 5: Verify listing readiness slice**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~DalamudMarketBoardPurchaseAdapterTests" -v minimal
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
MarketMafioso/tools/Deploy-DevPlugin.ps1
```

Expected: recoverable list-not-ready results retry instead of killing route.

- [ ] **Step 6: Commit listing readiness slice after live confirmation**

```powershell
git add MarketMafioso/MarketAcquisition/MarketBoardListingListProbe.cs MarketMafioso/MarketAcquisition/DalamudMarketBoardPurchaseAdapter.cs MarketMafioso/Windows/MainWindow.cs MarketMafioso.Tests/MarketAcquisition/DalamudMarketBoardPurchaseAdapterTests.cs
git commit -m "feat: wait for clickable market listing readiness"
```

---

## Slice 5: Convert Purchase Confirmation and Removal Waiting

**Files:**
- Modify: `MarketMafioso/MarketAcquisition/MarketBoardPurchaseSession.cs`
- Modify: `MarketMafioso/MarketAcquisition/DalamudMarketBoardPurchaseAdapter.cs`
- Modify: `MarketMafioso.Tests/MarketAcquisition/MarketBoardPurchaseSessionTests.cs`
- Modify: `MarketMafioso/Windows/MainWindow.cs`

- [ ] **Step 1: Make purchase session statuses typed internally**

Add an enum in `MarketBoardPurchaseSession.cs`:

```csharp
public enum MarketBoardPurchaseSessionPhase
{
    WaitingForConfirmation,
    WaitingForListingRemoval,
    Completed,
    Failed,
}
```

Keep the existing string `Status` property for UI/API output during this slice, but derive it from the enum.

- [ ] **Step 2: Split confirmation waiting from listing-removal waiting**

`TryConfirmPendingPurchase` should only decide whether the confirmation prompt is present and valid. Listing-removal verification should be a separate predicate that compares current live listings against the candidate that was purchased.

- [ ] **Step 3: Use watchdogs only as failure boundaries**

Keep the current 15-second watchdogs as maximum bounds, but do not use them as normal step pacing. The monitor should re-check predicates on framework ticks or short retry intervals.

- [ ] **Step 4: Add tests for session phase transitions**

Update `MarketBoardPurchaseSessionTests` to assert:

- confirmation submitted moves to `WaitingForListingRemoval`,
- listing changed/removal observed moves to `Completed`,
- confirmation timeout moves to `Failed`,
- removal timeout moves to `Failed` with a diagnostic message naming the listing id.

- [ ] **Step 5: Verify confirmation/removal slice**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketBoardPurchaseSessionTests" -v minimal
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
MarketMafioso/tools/Deploy-DevPlugin.ps1
```

Expected: purchase confirmation and post-purchase listing removal still work in the live single-world purchase path.

- [ ] **Step 6: Commit confirmation/removal slice after live confirmation**

```powershell
git add MarketMafioso/MarketAcquisition/MarketBoardPurchaseSession.cs MarketMafioso/MarketAcquisition/DalamudMarketBoardPurchaseAdapter.cs MarketMafioso/Windows/MainWindow.cs MarketMafioso.Tests/MarketAcquisition/MarketBoardPurchaseSessionTests.cs
git commit -m "feat: model market purchase confirmation phases"
```

---

## Slice 6: Introduce MarketBoardAutomationController

Execution guard satisfied: Slice 5.5 is implemented, live-validated, committed, pushed, merged to `main`, and deployed. Continue controller extraction without reintroducing stale listing-cache ambiguity.

**Files:**
- Create: `MarketMafioso/MarketAcquisition/MarketBoardAutomationController.cs`
- Modify: `MarketMafioso/Windows/MainWindow.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketBoardAutomationControllerTests.cs`

- [x] **Step 1: Create controller shell**

Create `MarketBoardAutomationController` with explicit methods:

```csharp
public sealed class MarketBoardAutomationController : IDisposable
{
    public bool IsBusy { get; }
    public string Status { get; }
    public string Message { get; }

    public void StartSearchAndPurchase(MarketBoardPurchaseCandidate candidate);
    public void Abort();
    public void Dispose();
}
```

The first implementation may delegate to the existing search driver and purchase adapter. The value of this slice is moving orchestration ownership away from `MainWindow`, not changing behavior.

- [x] **Step 2: Move low-level purchase monitor fields out of MainWindow**

Move these from `MainWindow` into the controller where possible:

- `marketBoardPurchaseSession`
- `marketBoardPurchaseResult`
- `nextMarketBoardPurchaseMonitorUtc`
- purchase confirmation polling
- purchase listing-removal polling

Keep route-level counters in `MainWindow` for this slice if moving them would broaden the diff.

Progress: `marketBoardPurchaseSession`, `marketBoardPurchaseResult`, `nextMarketBoardPurchaseMonitorUtc`, confirmation polling, and listing-removal polling are now owned by `MarketBoardAutomationController`. `MainWindow` records diagnostics and applies route counters from the controller tick result.

- [x] **Step 3: Keep UI rendering stable**

`MainWindow` should still render the same market-acquisition status text, but read it from the controller.

- [x] **Step 4: Add controller tests**

Create tests for controller behavior using fake collaborators:

- returns busy after `StartSearchAndPurchase`,
- records recoverable wait when listing list is not ready,
- abort clears busy state,
- failed purchase selection exposes failure message.

Progress: controller tests cover purchase-session start, recoverable selection waits, abort, monitor failure state, clear, monitor scheduling, confirmation polling, listing-removal polling, and not polling before the scheduled monitor time.

- [x] **Step 5: Verify controller slice**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketBoardAutomationControllerTests" -v minimal
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
MarketMafioso/tools/Deploy-DevPlugin.ps1
```

Expected: UI still shows the same route/purchase information, but `MainWindow` no longer owns the low-level purchase session.

- [x] **Step 6: Commit controller slice**

Committed as `refactor: move market board purchase polling into controller`.

```powershell
git add MarketMafioso/MarketAcquisition/MarketBoardAutomationController.cs MarketMafioso/Windows/MainWindow.cs MarketMafioso.Tests/MarketAcquisition/MarketBoardAutomationControllerTests.cs
git commit -m "refactor: move market board automation into controller"
```

---

## Slice 5.5: Make Listing-Cache Freshness Explicit

Execution order: this slice was added after the original Slice 6 shell had already partially landed. Treat it as the next required implementation slice before any further controller extraction or cleanup.

**Files:**
- Modify: `MarketMafioso/MarketAcquisition/MarketBoardLiveListingModels.cs`
- Modify: `MarketMafioso/MarketAcquisition/MarketBoardListingReader.cs`
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionLiveCandidatePlanner.cs`
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionRouteRunner.cs`
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionGuidedRouteSession.cs`
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionRouteDiagnostics.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketBoardListingReaderTests.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionLiveCandidatePlannerTests.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteRunnerTests.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteDiagnosticsTests.cs`

This slice covers the captured `route-20260630-020624.log` failure shape: Malboro Darksteel Ore had active search item id `5121`, but readable listing rows still included raw Electrum Ingot item id `5066` from the previous item. The reader normalized those rows to the active item, the planner saw only above-threshold rows, and route progression treated `VisibleCacheExhausted` as successful line/world completion.

- [x] **Step 1: Add a typed listing read state**

In `MarketMafioso/MarketAcquisition/MarketBoardLiveListingModels.cs`, add:

```csharp
public enum MarketBoardListingReadState
{
    Unavailable,
    Loading,
    SwitchingItem,
    FreshPartial,
    FreshComplete,
}
```

Add these properties to `MarketBoardReadResult`:

```csharp
public MarketBoardListingReadState ReadState { get; init; }
public bool IsFresh =>
    ReadState is MarketBoardListingReadState.FreshPartial or MarketBoardListingReadState.FreshComplete;
public IReadOnlyDictionary<uint, int> RawItemIdMismatchCounts { get; init; } =
    new Dictionary<uint, int>();
```

Expected responsibility:

- `Status` remains for UI text and diagnostics.
- `ReadState` becomes the route/planner decision input.
- `RawItemIdMismatchCounts` preserves evidence for stale row diagnosis.

- [x] **Step 2: Write reader tests for stale mixed-row cache**

In `MarketMafioso.Tests/MarketAcquisition/MarketBoardListingReaderTests.cs`, add a test equivalent to:

```csharp
[Fact]
public void BuildReadResult_ReturnsSwitchingItem_WhenRowsContainPreviousItemEvidence()
{
    var listings = new[]
    {
        new MarketBoardLiveListing
        {
            ItemId = 5066,
            RawItemId = 5066,
            WorldName = "Malboro",
            ListingId = "5277656679361153",
            RetainerId = "33777097236802393",
            UnitPrice = 2000,
            Quantity = 99,
        },
        new MarketBoardLiveListing
        {
            ItemId = 5121,
            RawItemId = 5121,
            WorldName = "Malboro",
            ListingId = "2885119312730168",
            RetainerId = "33777097240228606",
            UnitPrice = 800,
            Quantity = 99,
        },
    };

    var result = MarketBoardListingReader.BuildReadResult(
        waitingForListings: false,
        itemId: 5121,
        currentWorld: "Malboro",
        listings,
        reportedListingCount: 73,
        listingCapacity: 100,
        currentRequestId: 12,
        nextRequestId: 13);

    Assert.Equal(MarketBoardListingReadState.SwitchingItem, result.ReadState);
    Assert.False(result.IsFresh);
    Assert.Equal("ListingCacheSwitching", result.Status);
    Assert.Equal(1, result.RawItemIdMismatchCounts[5066]);
    Assert.Empty(result.Listings);
}
```

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketBoardListingReaderTests.BuildReadResult_ReturnsSwitchingItem_WhenRowsContainPreviousItemEvidence" -v minimal
```

Expected: FAIL until `MarketBoardListingReader` stops normalizing stale rows into purchasable rows.

- [x] **Step 3: Implement freshness classification in the reader**

In `MarketBoardListingReader.BuildReadResult(...)`:

- compute raw item-id mismatch counts before normalization;
- if any real row has `RawItemId` or `ItemId` different from the active `itemId`, return:

```csharp
return new MarketBoardReadResult
{
    Status = "ListingCacheSwitching",
    Message = $"Market board listing cache is still switching to item {itemId}; raw row item ids included {FormatRawItemIdMismatchCounts(rawItemIdMismatchCounts)}.",
    ReadState = MarketBoardListingReadState.SwitchingItem,
    ItemId = itemId,
    WorldName = currentWorld,
    ReportedListingCount = effectiveReportedListingCount,
    ListingCapacity = effectiveListingCapacity,
    IsAtListingCapacity = isAtListingCapacity,
    IsListingCountTruncated = isListingCountTruncated,
    CurrentRequestId = currentRequestId,
    NextRequestId = nextRequestId,
    RawItemIdMismatchCounts = rawItemIdMismatchCounts,
    Listings = [],
};
```

When no stale evidence exists:

- `waitingForListings` with no rows returns `Loading`;
- missing addon/info proxy remains `Unavailable`;
- rows with `IsListingCountTruncated == true` return `FreshPartial`;
- rows with `IsListingCountTruncated == false` return `FreshComplete`.

Do not pass stale mismatched rows to the candidate planner.

- [x] **Step 4: Make candidate planning reject non-fresh reads**

In `MarketAcquisitionLiveCandidatePlanner`, before calling `BuildCandidatePlanCore` from overloads that accept `MarketBoardReadResult`, add:

```csharp
if (!readResult.IsFresh)
    throw new InvalidOperationException($"Market board listings are not fresh enough to plan purchases: {readResult.Status}.");
```

Add a focused test in `MarketAcquisitionLiveCandidatePlannerTests`:

```csharp
[Fact]
public void BuildCandidatePlan_RejectsSwitchingItemRead()
{
    var read = new MarketBoardReadResult
    {
        Status = "ListingCacheSwitching",
        ReadState = MarketBoardListingReadState.SwitchingItem,
        ItemId = 5121,
        WorldName = "Malboro",
    };

    var exception = Assert.Throws<InvalidOperationException>(() =>
        MarketAcquisitionLiveCandidatePlanner.BuildCandidatePlan(
            CreateRequest(itemId: 5121, maxUnitPrice: 720),
            CreatePlanForWorld("Malboro", itemId: 5121),
            CreateSubtask("Malboro", itemId: 5121),
            "Malboro",
            read));

    Assert.Contains("not fresh enough", exception.Message, StringComparison.OrdinalIgnoreCase);
}
```

- [x] **Step 5: Keep stale reads non-terminal in route progression**

In `MarketAcquisitionRouteRunner` where live reads become candidate plans:

- if `readResult.ReadState == MarketBoardListingReadState.SwitchingItem`, record diagnostics and retry the listing-read step;
- do not call `MarketAcquisitionLiveCandidatePlanner`;
- do not call `session.MarkActiveItem...`;
- do not advance the active route item or world;
- if the route watchdog expires while still switching, fail loudly with a message like:

```text
Market board listing cache did not become fresh for Darksteel Ore (5121) on Malboro.
```

In `MarketAcquisitionGuidedRouteSession`, preserve the existing `VisibleCacheExhausted` behavior only for fresh reads. Stale reads are not line outcomes.

- [x] **Step 6: Extend diagnostics and CSV evidence**

In `MarketAcquisitionRouteDiagnostics`:

- add route-log fields:
  - `listingReadState`;
  - `rawItemIdMismatchCounts`;
  - `readIsFresh`;
  - `readIsTerminalForProgression`;
- add observed-listings CSV columns:
  - `listingReadState`;
  - `rawItemIdMismatchCounts`;
  - `readIsFresh`.

For stale reads with no candidate rows, still emit a summary route-log event so live diagnostics have evidence even when no observed-listings CSV rows are written.

- [x] **Step 7: Run focused verification**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketBoardListingReaderTests|FullyQualifiedName~MarketAcquisitionLiveCandidatePlannerTests|FullyQualifiedName~MarketAcquisitionRouteRunnerTests|FullyQualifiedName~MarketAcquisitionRouteDiagnosticsTests" -v minimal
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
```

Expected:

- stale mixed-row reads are non-fresh;
- planner does not receive stale rows;
- route runner retries or fails loudly instead of silently skipping;
- existing fresh read tests still pass.

- [x] **Step 8: Deploy for live validation**

Run:

```powershell
MarketMafioso/tools/Deploy-DevPlugin.ps1
```

Expected:

- source and target hashes match;
- visible manifest version changes;
- a repeated Malboro-style item switch either waits until the row cache is fresh or fails as a listing freshness failure with raw item-id evidence.

- [x] **Step 9: Commit freshness slice after live confirmation**

Committed as `073372a Harden market acquisition listing freshness`, pushed to `local-dev`, fast-forwarded into `main`, pushed, and redeployed from `local-dev@073372a`.

```powershell
git add MarketMafioso/MarketAcquisition/MarketBoardLiveListingModels.cs `
        MarketMafioso/MarketAcquisition/MarketBoardListingReader.cs `
        MarketMafioso/MarketAcquisition/MarketAcquisitionLiveCandidatePlanner.cs `
        MarketMafioso/MarketAcquisition/MarketAcquisitionRouteRunner.cs `
        MarketMafioso/MarketAcquisition/MarketAcquisitionGuidedRouteSession.cs `
        MarketMafioso/MarketAcquisition/MarketAcquisitionRouteDiagnostics.cs `
        MarketMafioso.Tests/MarketAcquisition/MarketBoardListingReaderTests.cs `
        MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionLiveCandidatePlannerTests.cs `
        MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteRunnerTests.cs `
        MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteDiagnosticsTests.cs `
        docs/design/2026-06-29-market-acquisition-future-refactor.md `
        docs/superpowers/plans/2026-06-29-ecommons-ui-automation-migration.md
git commit -m "fix: require fresh market listing cache before planning"
```

---

## Slice 7: Remove Obsolete Time-Gated Search/Purchase Paths

**Files:**
- Modify: `MarketMafioso/MarketAcquisition/MarketBoardItemSearchDriver.cs`
- Modify: `MarketMafioso/MarketAcquisition/DalamudMarketBoardPurchaseAdapter.cs`
- Modify: `MarketMafioso/MarketAcquisition/MarketBoardPurchaseSession.cs`
- Modify: `MarketMafioso/Windows/MainWindow.cs`
- Modify: `docs/design/2026-06-29-market-acquisition-future-refactor.md`

- [ ] **Step 1: Remove dead retry constants**

Remove constants and fields that only exist for the legacy time-gated path, such as stale submitted-search retry fields that are no longer used by predicate gates.

- [ ] **Step 2: Preserve timeout diagnostics**

Do not remove watchdogs. Rename them to reflect their role as failure boundaries:

- `MarketBoardSearchWatchdog`
- `MarketBoardPurchaseConfirmationWatchdog`
- `MarketBoardPurchaseListingRemovalWatchdog`

- [ ] **Step 3: Update route diagnostics vocabulary**

Diagnostics should include:

- current automation task name,
- last predicate that returned waiting,
- addon readiness summary,
- info proxy item id/listing count,
- clickable listing list status,
- timeout boundary if failure occurred.

- [ ] **Step 4: Update future-refactor doc**

Mark the following as completed or partially completed in `docs/design/2026-06-29-market-acquisition-future-refactor.md`:

- reusable UI automation primitives,
- listing-list selection diagnostics,
- execution controller extraction if Slice 6 completed.

- [ ] **Step 5: Run broader verification**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug -v minimal
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
MarketMafioso/tools/Deploy-DevPlugin.ps1
```

Expected: plugin tests pass, plugin builds, deploy succeeds.

- [ ] **Step 6: Commit cleanup slice**

```powershell
git add MarketMafioso/MarketAcquisition MarketMafioso/Windows/MainWindow.cs docs/design/2026-06-29-market-acquisition-future-refactor.md
git commit -m "refactor: remove legacy market automation timing gates"
```

---

## Slice 8: Live Route Validation and Rollback Boundary

**Files:**
- Modify only if live diagnostics expose a specific bug.

- [ ] **Step 1: Run live single-item route**

Expected:

- route travels,
- search submits under SimpleTweaks-enabled and default paths,
- exact item result is selected,
- listings open,
- first safe listing is purchased,
- confirmation is accepted,
- listing removal is observed,
- route advances or completes.

- [ ] **Step 2: Run live multi-item same-world route**

Expected:

- first item buys or skips safely,
- market results close/reset,
- second item search submits,
- second item buys or skips safely,
- world summary includes both item lines.

- [ ] **Step 3: Run live multi-world route**

Expected:

- route stays within current data center before cross-DC travel when possible,
- route closes market-board windows before Lifestream travel,
- route resumes after arrival,
- per-world summary records purchases and Universalis freshness verification.

- [ ] **Step 4: Patch only evidence-backed failures**

If live test fails, read the route log and patch the smallest specific condition/predicate. Do not reintroduce arbitrary sleeps as primary synchronization.

- [ ] **Step 5: Final broad verification**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug -v minimal
dotnet build "MarketMafioso.sln" -c Debug
MarketMafioso/tools/Deploy-DevPlugin.ps1
```

Expected: tests pass, solution builds, deploy succeeds.

---

## Execution Notes

- Slice 1 is the only slice that should touch dependency/lifecycle setup.
- Slice 2 is the only slice that should introduce generic automation wrappers.
- Slices 3 through 5 should each be live-testable independently.
- Slice 5.5 is now the required next slice. It must land before further Slice 6 extraction so stale or mixed listing-cache reads cannot become controller-owned behavior.
- Slice 6 is the first larger refactor. Do not continue it until search, purchase behavior, and listing-cache freshness are stable under ECommons-backed predicates.
- Slice 7 is cleanup. Do not remove old diagnostics before Slice 8 produces at least one successful live route log.

## Self-Review

- Spec coverage: the plan covers dependency bootstrap, facade isolation, condition-gated search, clickable listing readiness, purchase confirmation/removal, controller extraction, cleanup, and live validation.
- Placeholder scan: no `TBD`, `TODO`, or intentionally vague implementation steps remain.
- Type consistency: new type names are consistent across slices: `UiAutomationTaskQueue`, `UiAutomationTaskResult`, `AddonStateReader`, `MarketBoardAutomationController`, and `MarketBoardListingListProbe`.
