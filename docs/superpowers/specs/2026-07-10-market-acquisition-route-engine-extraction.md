# Market Acquisition Route Engine Extraction

## Problem

The Market Acquisition UI is now split into panels, but the fragile part still lives in `MainWindow`: the guided route loop, live listing probe, visible-listing continuation, guarded purchase pass, purchase confirmation monitor, world/line purchase counters, purchase audits, line progress, route progress, and several pieces of transient state.

`MarketAcquisitionRouteRunner` is useful, but it is not the whole engine. It owns route session state, route diagnostics, stop transitions, listing-read watchdogs, and Universalis freshness diagnostics. `MainWindow` still acts as the conductor that decides what to do each framework tick. That makes route behavior easy to disturb when UI code changes and hard to characterize with tests.

The next refactor should extract that conductor into a headless MMF-local route engine before any Franthropy migration.

## Goals

- Move route execution orchestration out of `MainWindow` without changing behavior.
- Make route ticking, probing, listing continuation, purchase execution, purchase monitoring, and progress/audit emission testable without ImGui.
- Preserve all existing route diagnostics, observed-listing CSV, purchase-record CSV, world visit catalog, progress reporting, and dashboard lifecycle semantics.
- Keep `MainWindow` as composition and presentation: panels call engine commands and render engine snapshots.
- Shape new contracts so later Franthropy extraction can lift reusable purchase automation cleanly.
- Avoid moving MMF-specific dashboard/request semantics into Franthropy.

## Non-Goals

- Do not move the full Market Acquisition route engine to Franthropy in this slice.
- Do not change the server/dashboard API contract.
- Do not redesign the Market Acquisition UI.
- Do not rewrite `MarketAcquisitionRouteRunner` from scratch.
- Do not change buy rules, max-price semantics, HQ policy, gil caps, quantity modes, or incomplete-listing behavior.
- Do not hide failures behind fallback behavior. Missing state should remain explicit and diagnostic.

## Approaches Considered

### Approach A: MMF-Local Route Engine First

Extract a `MarketAcquisitionRouteEngine` in the plugin project. It coordinates existing route, listing, purchase, reporting, and visit-catalog services through narrow interfaces. Franthropy candidates are identified after behavior is stable.

This is the recommended approach. It reduces immediate fragility without forcing reusable-library decisions while the engine boundary is still being discovered.

### Approach B: Direct Franthropy Extraction

Move purchase automation and route orchestration into Franthropy immediately.

This is too large for the current state. Purchase primitives are likely reusable, but MMF route semantics include dashboard claims, server progress transitions, Universalis freshness verification, world visit catalog policy, and request-specific line status behavior. Moving too much too early would either pollute Franthropy or create a second brittle adapter layer.

### Approach C: Continue Small Helper Extraction

Keep extracting helper methods and presenters from `MainWindow`.

This has reached diminishing returns. The remaining fragility is not presentation shape; it is orchestration state. More helper extraction would make the code look cleaner without making route behavior much safer.

## Target Architecture

```text
MainWindow
  - owns ImGui tab composition
  - owns dashboard request fetching, claiming, accepting, rejecting, and request-builder sync for now
  - delegates route lifecycle commands to MarketAcquisitionRouteEngine
  - renders route/purchase/probe snapshots through existing panels/windows

MarketAcquisitionRouteEngine
  - owns route execution lifecycle and per-frame tick decisions
  - owns active read/probe/purchase state
  - owns world and line purchase counters
  - invokes route runner state transitions
  - emits local diagnostics, route progress, line progress, and purchase audit events
  - exposes immutable snapshot data for UI and diagnostics windows

MarketAcquisitionRouteRunner
  - continues to own route session state, route stop transitions, route diagnostics package, listing-read watchdog state, run summary, and Universalis freshness diagnostics

Adapters / Ports
  - current world and character scope
  - command sending
  - market-board approach
  - addon close/preflight/scroll operations
  - listing search and read
  - guarded purchase selection/confirmation
  - dashboard progress/audit client
  - world visit catalog recorder
  - logging and clock
```

The engine should be headless. It can know about Market Acquisition models and automation service contracts, but it should not call ImGui or draw UI.

## Engine Responsibilities

### Route Lifecycle

The engine owns start, pause, resume, stop, restart, reprepare, and reset operations for guided routes.

It should reset the same state currently reset in `MainWindow`:

- current market-board read result
- listing reconciliation
- live candidate plan
- listing accumulator
- purchase automation controller
- active world purchase quantity/gil
- active line purchase quantity/gil
- active world and active line ids
- guided route probe-running flag
- next monitor deadline
- route progress session version
- route progress nonce
- report sequence
- last route progress report key

Start/restart/reprepare still require a prepared `MarketAcquisitionPlan` and a reportable accepted/claimed dashboard request.

### Per-Frame Route Tick

The engine owns the current `MonitorGuidedRoute()` behavior:

- wait while a dashboard request action is busy
- wait while a probe is already running
- throttle by `nextGuidedRouteMonitorUtc`
- handle pending world stops
- close market-board windows before travel when the runner requires it
- check travel-blocking addons before movement commands
- send Lifestream travel commands through an injected command sender
- detect current-world mismatch
- approach/open market board
- submit market-board item search
- begin live-listing probe
- start the next purchase pass when a stop enters `Purchasing`
- always emit route progress after meaningful state changes

Ticking should return a small result object that says whether work occurred and when the next tick should be scheduled. The UI does not need to interpret every internal decision.

### Live Listing Probe

The engine owns the current `ProbeLiveMarketBoardCore()` behavior:

- read current market-board listings through an injected listing reader
- merge reads through `MarketBoardListingReadAccumulator`
- reconcile live listings against the prepared plan
- build a live candidate plan when the read is fresh enough
- record route probe completion when the route is at an arrived stop
- record world visit catalog observations for probes
- request deeper visible listing rows when coverage is incomplete and continuation is available
- keep route status and acquisition status explicit when listings are stale, loading, switched, partial, or complete

Visible-listing continuation must remain first-class. The engine should preserve the current rule: incomplete coverage can request deeper listing rows; no-candidate with incomplete coverage should not be treated as normal no-stock.

### Purchase Pass

The engine owns the current `BeginNextWorldPurchase()` behavior:

- validate that the current world matches the active route stop
- start/reset active world and line counters
- emit line progress when a line starts
- read and merge fresh listings
- build the live candidate plan with existing purchased/spent totals
- request deeper listings when the visible cache is incomplete
- execute the first guarded candidate through `MarketBoardPurchaseExecutor`
- record purchase-selection automation snapshots
- classify no-candidate behavior:
  - incomplete listing coverage fails the route
  - complete coverage completes the world/item batch
- keep recoverable purchase-selection states retryable
- fail the route on fatal selection states
- schedule purchase confirmation monitoring after a selection is sent

The first extraction should keep `MarketBoardPurchaseExecutor`, `MarketBoardPurchasePlanner`, `MarketBoardAutomationController`, and `MarketBoardPurchaseSession` intact. Those are Franthropy candidates later.

### Purchase Monitor

The engine owns the current `MonitorMarketBoardPurchase()` behavior:

- poll purchase confirmation state through `MarketBoardAutomationController`
- submit confirmation through the injected purchase adapter
- read fresh listings after confirmation submission
- record confirmation and listing-removal automation snapshots
- mark completed purchases
- increment active world and line counters
- record purchase audits locally and remotely
- clear market-board automation state after completion
- either complete the world batch or begin the next purchase pass
- fail the route when a purchase session ends inactive without completion

### Reporting

The engine owns route progress, purchase audit, and line progress emission because those are route execution side effects, not UI responsibilities.

It should use an injected reporter/client boundary that wraps:

- `ReportAttemptProgressAsync`
- `FailAttemptAsync`
- `CompleteAttemptAsync`
- `PostPurchaseAuditAsync`
- `PostLineProgressAsync`

The first extraction can adapt the existing `MarketAcquisitionRequestClient` directly. It should not change server payloads.

Conflict reconciliation currently in `TryHandleRouteProgressConflict(...)` should move out of `MainWindow` with reporting state because it mutates the accepted claim and local claim persistence. Introduce a dedicated MMF-local `MarketAcquisitionClaimLifecycleController` and inject it into the engine/reporting boundary. Do not leave conflict handling in `MainWindow` while the engine owns route reporting.

### World Visit Catalog

Probe and purchase visit recording should move with the engine because those records are route execution evidence.

The catalog boundary should expose intent-oriented methods such as:

- `RecordProbeVisit(...)`
- `RecordPurchaseVisit(...)`

The adapter can continue to use `MarketAcquisitionWorldVisitCatalog`, `ResolveCatalogDataCenter(...)`, pruning, and `config.Save()`.

## Engine Snapshot

The engine should expose immutable snapshot data for UI and diagnostics:

```text
MarketAcquisitionRouteEngineSnapshot
  StatusMessage
  VisibleAcquisitionStatus
  IsRouteActive
  IsProbeRunning
  MarketBoardReadResult?
  MarketBoardListingReconciliation?
  MarketAcquisitionLiveCandidatePlan?
  MarketBoardPurchaseSession?
  MarketBoardPurchaseResult?
  ActiveWorldPurchasedQuantity
  ActiveWorldSpentGil
  ActiveLinePurchasedQuantity
  ActiveLineSpentGil
  LastDiagnosticFilePath
  LastObservedListingsCsvPath
  LastPurchaseRecordsCsvPath
  LastRunSummary
  LatestWorldCompletionSummary
```

Existing panels and diagnostics windows should read from this snapshot instead of reaching into many `MainWindow` fields.

## Adapter Boundaries

Use narrow MMF-local interfaces in the first pass. These are not yet public Franthropy APIs.

### Route Context

Provides current game scope:

- `bool IsCurrentWorldAvailable`
- `string GetCurrentWorldName()`
- `bool TryGetCharacterScope(out string characterName, out string homeWorld)`

The concrete adapter wraps `IPlayerState`.

### Command and UI Automation

Provides:

- `bool ProcessCommand(string command)`
- `bool TryCloseMarketBoardWindows()`
- `bool IsAddonOpen(string addonName)`
- `bool TryScrollMarketBoardListingsToRow(int requestedRow, out string message)`
- `AutomationTravelPreflightResult CheckTravelPreflight()`

The concrete adapter can initially hold the unsafe addon pointer code currently in `MainWindow`.

### Market Board IO

Provides:

- `MarketBoardApproachResult OpenOrApproachMarketBoard()`
- `MarketBoardItemSearchResult SearchItem(uint itemId, string itemName)`
- `MarketBoardReadResult ReadCurrentListings(string currentWorld)`
- `MarketBoardInputCapture CaptureInputState()`

Concrete adapters can wrap the existing `MarketBoardApproachService`, `MarketBoardItemSearchDriver`, `MarketBoardListingReader`, and `MarketBoardInputCaptureReader`.

### Purchase IO

Provides:

- `MarketBoardPurchaseResult ExecuteFirstCandidate(MarketAcquisitionLiveCandidatePlan candidatePlan, MarketBoardReadResult freshRead)`
- `MarketBoardPurchaseResult TryConfirmPendingPurchase(MarketBoardPurchaseCandidate candidate)`

Concrete adapters wrap `MarketBoardPurchaseExecutor` and `DalamudMarketBoardPurchaseAdapter`.

### Progress and Audit Reporting

Provides route progress, purchase audit, and line progress methods using current request/claim context. It should own idempotency key construction and route progress conflict reconciliation or delegate conflict reconciliation to a claim lifecycle controller.

### Route Evidence

Provides world visit catalog recording and saving. This keeps `config.Save()` out of the engine core except through an explicit persistence boundary.

## Franthropy Path

After the MMF-local engine is stable, reusable candidates can move to Franthropy in this order:

1. Market-board live listing and purchase identity contracts.
2. Purchase revalidation and candidate selection.
3. Purchase session state machine and monitor tick model.
4. Automation snapshot result shapes, if they prove useful across projects.
5. Low-level listing-read accumulation, only if another project needs deeper visible-listing handling.

These should not move in the first route-engine extraction unless they must move to make the local engine testable.

MMF should keep:

- acquisition request lifecycle
- dashboard claim/accept/reject semantics
- route stop policy
- Universalis freshness diagnostics
- world visit catalog policy
- route progress and purchase audit server contracts
- UI panel presentation

## Test Strategy

Add characterization tests before moving behavior.

### Route Tick Tests

- Pending stop on wrong world sends a travel command only when travel preflight passes.
- Pending stop waits and records blocked UI when a travel-blocking addon is open.
- Market-board close-required state calls close-market-board UI and waits before travel.
- Arrived stop approaches/opens the market board before item search.
- Arrived stop searches the active item and waits when search is still in progress.

### Listing Probe Tests

- Stale listing read records pending state and does not build a candidate plan.
- Fresh complete listing read builds a candidate plan and records a probe visit.
- Fresh partial listing read with incomplete coverage requests a deeper row.
- Failed deeper-row scroll records pending state instead of completing the probe.
- `NoSearchItem` clears search submission for the next tick.

### Purchase Pass Tests

- Purchase pass refuses to run on the wrong world.
- New active line resets line counters and emits line progress.
- No candidate with incomplete listing coverage fails the route.
- No candidate with complete listing coverage completes the batch.
- Recoverable selection state schedules a retry instead of failing.
- Fatal selection state fails the route.

### Purchase Monitor Tests

- Confirmation submission records an automation snapshot.
- Listing removal completion increments world and line counters.
- Completed purchase emits local purchase audit and remote purchase audit.
- Completed purchase with no listings completes the world batch.
- Completed purchase with remaining listings begins the next purchase pass.
- Inactive failed purchase session fails the route.

### Reporting Tests

- Route progress report is skipped for unreportable local route states.
- Route progress report is skipped for unreportable server request statuses.
- Duplicate route progress messages are coalesced by report key.
- Conflict response with source `Complete` clears local claim.
- Conflict response with source `Failed` preserves claim status and blocks route reporting.

## Implementation Slices

### Slice 1: Characterization Harness

Create fake adapters and tests around the current behavior by extracting only enough callable seams to exercise the route decisions. This slice should not change production behavior.

### Slice 2: Engine State Container

Introduce `MarketAcquisitionRouteEngineState` and `MarketAcquisitionRouteEngineSnapshot`. Move route execution fields from `MainWindow` into the state container while `MainWindow` still calls existing methods.

### Slice 3: Engine Lifecycle Commands

Move start, pause, resume, stop, restart, reprepare, and reset into `MarketAcquisitionRouteEngine`. `MainWindow` callbacks delegate to the engine. Verify route controls still render and command behavior is unchanged.

### Slice 4: Probe and Listing Continuation

Move live listing read, reconciliation, candidate-plan build, and deeper-row continuation into the engine. Keep `MarketAcquisitionDiagnosticsWindow` working through snapshot delegates.

### Slice 5: Purchase Pass and Monitor

Move `BeginNextWorldPurchase`, `MonitorMarketBoardPurchase`, purchase snapshots, purchase counters, and batch completion into the engine.

### Slice 6: Reporting and Visit Catalog

Move route progress, line progress, purchase audit reporting, conflict handling, and visit catalog recording behind explicit reporting/evidence ports.

### Slice 7: MainWindow Cleanup

Remove obsolete route fields and helper methods from `MainWindow`. It should retain tab composition, request fetching/claiming/preparation, UI-only formatting, and engine wiring.

### Slice 8: Franthropy Candidate Review

Create a follow-up note listing which local interfaces and types are ready to move to Franthropy and which stayed MMF-specific because they still encode route/dashboard policy.

## Acceptance Criteria

- `MainWindow` no longer owns route tick, probe, purchase pass, purchase monitor, route progress, line progress, purchase audit, or world visit recording logic.
- Market Acquisition UI looks and behaves the same in game.
- Existing route diagnostics package contents remain compatible, including `route.log`, `observed-listings.csv`, and `purchase-records.csv`.
- Incomplete visible listing coverage remains distinct from normal no-stock/no-candidate behavior.
- Route progress and purchase audits still reach the dashboard using the existing API contracts.
- Existing Market Acquisition tests pass.
- New engine characterization tests cover the known fragile route/purchase/listing continuation paths.
- Dev-plugin deploy succeeds and the installed DLL hash is verified.

## Risks and Guardrails

- The engine must not swallow failures. Explicit missing current world, missing claim, stale listings, unavailable addons, and invalid route statuses should remain visible.
- The engine should not become a new god object with UI, dashboard pickup, and request builder ownership. Keep request fetching/claiming/preparation out of this slice.
- Do not move Franthropy candidates until the MMF-local boundary survives tests.
- Keep route diagnostic CSV field names stable unless a test and a documented migration justify a change.
- Run build/tests serially because this repo has had noisy shared-output locks when build/test steps overlap.

## Open Follow-Up

After this spec is approved, write a detailed implementation plan. The implementation plan should be task-by-task, test-first, and should avoid one giant move-everything commit. The first execution task should add or adapt tests before production behavior moves.
