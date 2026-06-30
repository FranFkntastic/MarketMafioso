# Market Acquisition Future Refactor Notes

Date: 2026-06-29

## Purpose

This document parks the structural review of the current Market Acquisition module so the current pass can stay focused on patching the live route runner until it can complete full plans reliably.

These notes are not blocking for the present alpha automation work. Treat them as the next cleanup lane once the current route/purchase machinery is stable enough to stop changing underneath us every live test.

## Current Shape

The Market Acquisition module now has several useful services:

- `MarketAcquisitionPlanner` builds advisory plans from remote market data.
- `MarketAcquisitionGuidedRouteSession` tracks planned world stops and line subtasks.
- `MarketAcquisitionRouteRunner` owns route lifecycle, diagnostics, freshness observations, and route state updates.
- `MarketBoardItemSearchDriver` owns market board item-search automation.
- `MarketBoardListingReader` reads live market board rows.
- `MarketBoardPurchaseExecutor`, `DalamudMarketBoardPurchaseAdapter`, and `MarketBoardPurchaseSession` handle guarded listing purchase attempts and confirmation verification.
- `UiAutomation/AtkTextInputAutomation.cs`, `UiAutomationTaskQueue`, and `AddonStateReader` are the start of a reusable low-level UI automation helper layer.
- `MarketBoardListingListProbe` owns the current clickable-list discovery path for market-board listing selection.
- `MarketBoardAutomationController` exists as a tested seam for purchase-session progress, but is not yet the owner of the full route execution loop.

This is much healthier than the original shape: planners, readers, sessions, and automation helpers are increasingly testable, and the input-capture diagnostics have become a practical way to debug live game UI behavior.

## Progress Since This Note Was Created

- Completed: ECommons is bootstrapped behind MMF-owned wrappers instead of being scattered directly through Market Acquisition.
- Completed: market-board item search now records whether an activation was actually sent, instead of treating an enabled button as proof that search happened.
- Completed: listing-list selection now reports clickable-list readiness and candidate component diagnostics, reducing the "row exists visually but code cannot click it" failure shape.
- Completed: purchase confirmation state distinguishes confirmation submission from proven listing removal.
- Partial: the execution-controller seam exists, but `MainWindow` still owns route orchestration, active route ticks, and most user-facing acquisition status.
- Pending: route diagnostics still need a single structured snapshot model that spans search, listing read, selection, confirmation, and post-purchase verification.

## Main Structural Problems

### MainWindow is still the execution orchestrator

`Windows/MainWindow.cs` owns too much of the Market Acquisition runtime:

- ImGui rendering.
- Dashboard request pickup and claim persistence.
- Route start/pause/stop/restart command handling.
- Route monitoring ticks.
- Market board approach/search/probe orchestration.
- Purchase start and purchase session monitoring.
- Active world and active line purchase counters.
- Dashboard route-progress reporting.
- User-facing acquisition status text.

This is the largest source of development friction. Small live edge-case fixes keep requiring edits in a huge UI file, which makes patches slower, riskier, and harder to test.

The new `MarketBoardAutomationController` should be treated as the first extraction seam, not a completed fix. The next refactor should move route tick decisions and purchase-session monitoring into a controller/service that `MainWindow` only commands and renders.

### Route and purchase state are stringly typed

Internal control flow still depends heavily on status strings such as:

- `Pending`
- `TravelCommandSent`
- `Arrived`
- `Purchasing`
- `Running`
- `Ready`
- `SearchSent`
- `ListingsReady`
- `WaitingForConfirmation`
- `WaitingForListingRemoval`
- `Completed`
- `Failed`

These strings are useful for diagnostics and server payloads, but they should not be the primary internal state machine representation. Recent fixes around purchase confirmation semantics are examples of how easy it is for string status naming to blur real state.

### RouteRunner is becoming the second god object

`MarketAcquisitionRouteRunner` is useful and well covered, but it now owns route lifecycle, diagnostics, freshness bookkeeping, search automation timeout classification, world summaries, line progress, and completion/failure transitions.

This is less urgent than `MainWindow`, but after extracting execution orchestration, the runner should be split into clearer units.

### UI automation primitives are still uneven

The new `UiAutomation` namespace is the right direction, but market-board automation is still spread across search driver, listing reader, purchase adapter, input capture, and route runner diagnostics.

Repeated addon/input patterns should move into reusable helpers only once they appear in more than one place. Avoid a premature framework, but keep extracting proven primitives.

### Unsafe market-board list probing needs richer diagnostics

`MarketBoardListingListProbe` now probes candidate list components and returns the selected component id, visible item count, requested row, and diagnostic summary. `DalamudMarketBoardPurchaseAdapter` still owns scrolling, selection, dispatching item events, and validating listing identity.

Recent failures around visible row counts and selected row identity show this area should keep moving toward a dedicated listing-list selection helper. The probe is a useful first step, but not the final shape.

## Refactor Opportunities

### 1. Introduce typed internal statuses

Create internal status types or constants for:

- Route state.
- Route stop state.
- Search result state.
- Listing read state.
- Purchase session state.
- Request lifecycle state.

The goal is not to remove string values from diagnostics or API payloads. The goal is to keep route decisions from relying on scattered string comparisons.

### 2. Extract a MarketAcquisitionExecutionController

Move the live execution loop out of `MainWindow`.

The controller should own:

- Route monitor tick.
- Market board approach/search/probe orchestration.
- Purchase session start and monitoring.
- Active world counters.
- Active line counters.
- World completion decisions.
- Calls to dashboard route-progress reporting through a callback or small reporter service.

`MainWindow` should eventually render state and send commands:

- Start.
- Start with diagnostics.
- Pause.
- Stop.
- Restart.
- Open diagnostics.

It should not directly decide what the next market-board automation step is.

### 3. Split route diagnostics and freshness tracking from RouteRunner

After the execution controller exists, move these out of `MarketAcquisitionRouteRunner`:

- Diagnostic log lifecycle.
- Automation snapshot recording.
- Universalis freshness observations.
- Per-world completion diagnostic summaries.

Likely target services:

- `MarketAcquisitionRouteDiagnosticsJournal`
- `UniversalisFreshnessTracker`

### 4. Grow UiAutomation carefully

Keep `AtkTextInputAutomation` and add helpers only when they are clearly reused:

- Text input focus/activate/set/submit.
- Addon readiness checks.
- List component probing.
- List row scrolling and selection.
- Confirmation prompt classification.

This should make future UI automation less bespoke without inventing a heavy framework.

### 5. Extract market-board listing-list selection diagnostics

Create a small helper around the current list-component probing in `DalamudMarketBoardPurchaseAdapter`.

The return value should include:

- Selected component id.
- Candidate component ids and visible counts.
- Requested absolute row index.
- Whether a scroll was attempted.
- Whether the row became visible after scroll.
- A clear failure reason.

This would make row-selection failures much easier to patch from logs.

## Recommended Order

Do not start this until the current route runner can complete full plans reliably.

When ready:

1. Add typed status constants/enums with minimal behavior changes.
2. Extract `MarketAcquisitionExecutionController` from `MainWindow`.
3. Add focused controller tests around route tick transitions and purchase-session outcomes.
4. Split diagnostics/freshness from `MarketAcquisitionRouteRunner`.
5. Extract reusable UI automation primitives only where current duplication proves the seam.

## Non-Goals For The Current Patch Lane

- Do not rewrite Market Acquisition from scratch.
- Do not block route completion fixes on this refactor.
- Do not move UI rendering out of `MainWindow` as part of the first extraction.
- Do not create a broad UI automation framework before the existing market-board automation paths stabilize.

## Working Principle

Patch the current live loop until it can get through full plans. Once it is boring enough to trust, extract the execution controller so future failures can be reproduced and tested without turning every small behavior fix into surgery inside `MainWindow`.
