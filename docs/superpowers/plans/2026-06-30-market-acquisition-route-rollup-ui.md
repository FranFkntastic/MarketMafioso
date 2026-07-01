# Market Acquisition Route Rollup UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the in-plugin Market Acquisition guided route view useful for normal operation, not just debugging. Collapsed world rows should summarize multi-item progress clearly, expanded rows should expose item-level planned/discovered/bought data, and completed runs should produce a compact post-run rollup that explains what happened, what was skipped, what was opportunistically found, and which diagnostics need attention.

**Architecture:** Keep the current route runner functional while extracting display/accounting semantics into small presenter/model helpers. `MarketAcquisitionGuidedRouteSession` remains the route-state source. `MarketAcquisitionRouteRunner` remains the executor for this pass. `MainWindow` should consume presenter view models instead of building all strings inline.

**Tech Stack:** C# 12, Dalamud ImGui, existing `MarketMafioso.MarketAcquisition` models, focused xUnit tests under `MarketMafioso.Tests/MarketAcquisition`.

---

## Current State

The route table is already expandable, but the collapsed row is still shaped like a debug table:

- `Items` is a count or short list that becomes less meaningful with opportunistic checks.
- `Planned`, `Discovered`, and `Bought` flatten all lines into world-level totals, which is useful for volume but poor for multi-item decisions.
- `Live candidate` is operationally useful but too debug-heavy for day-to-day route monitoring.
- Expanded item rows exist, but they inherit the parent table columns, so the row semantics are visually misaligned.
- Post-run diagnostics currently surface only warning count and latest world completion, not a complete route rollup.

Recent CSV diagnostics also showed why the table should not be the only truth surface:

- A completed world can have purchased, skipped, and opportunistic line outcomes at the same time.
- Observed market volume and bought stock are distinct concepts.
- Purchase records and observed-listing records need to be easy to compare after a run.
- Universalis freshness warnings are useful, but they should be summarized instead of dominating normal route output.

## Design Target

### Collapsed World Row

Use one row per world with operational summaries:

| Column | Purpose |
| --- | --- |
| World | Expander plus world name. |
| Data Center | Static route grouping context. |
| Route Lines | Human-readable item summary, for example `Raw Larimar, Topaz +3`, with planned/opportunistic counts in muted text. |
| State | Aggregate world state: `Pending`, `Traveling`, `Buying`, `Partial`, `Complete`, `Blocked`. |
| Intent | Planned route intent: total planned items/gil and count of planned lines. |
| Result | Purchased items/gil and completion ratio. |
| Notes | Short outcome summary: skipped lines, opportunistic finds, no-safe-stock, warnings. |

Do not show raw `LiveCandidateStatus` in collapsed rows. It belongs in diagnostics or expanded item details.

### Expanded World Rows

Expanded rows should use a formatting/header row so the child data reads as its own subtable:

| Item | Source | Item State | Planned | Discovered | Bought | Notes |
| --- | --- | --- | --- | --- | --- | --- |

Source should distinguish:

- `Planned`: this item/world was in the server advisory route.
- `Opportunistic`: this item was checked because the route was already on that world.

Discovered should represent live market-board observations for that item on that world, not the whole world's flattened market volume.

Bought should represent actual purchase results for that item on that world.

### Post-Run Rollup

After route completion or failure, show a compact rollup above or below the route table:

- Total bought quantity and gil.
- Planned vs opportunistic purchased quantity/gil.
- Completed worlds, skipped worlds, failed worlds.
- Completed lines, skipped/no-stock lines, failed lines.
- Top purchased items by gil.
- Universalis freshness summary: confirmed, unavailable, unconfirmed, warnings.
- Diagnostic artifact paths when diagnostics were enabled.

The rollup should be concise in the main window and defer details to diagnostics.

## Implementation Tasks

### 1. Add Route Table Presenter Models

- [x] Add `MarketMafioso/MarketAcquisition/MarketAcquisitionRouteTablePresenter.cs`.
- [x] Define immutable presenter records:
  - `MarketAcquisitionRouteStopRow`
  - `MarketAcquisitionRouteLineRow`
  - `MarketAcquisitionRouteStopAggregate`
- [x] Presenter input should be `IReadOnlyList<MarketAcquisitionGuidedRouteStop>`.
- [x] Presenter output should contain ready-to-render text and semantic state, not ImGui calls.
- [x] Keep formatting culture-invariant where the result is logged or tested; UI can use normal numeric grouping through current helper methods.

Presenter rules:

- [x] Collapsed `RouteLines` text shows up to two item names plus `+N`, but also exposes planned/opportunistic counts for tooltip or muted suffix.
- [x] Stop aggregate state is:
  - `Blocked` if stop status is failed or any line failed.
  - `Buying` if stop status is `Purchasing`.
  - `Traveling` if stop status is `TravelCommandSent`.
  - `Complete` if all lines are terminal and no line failed.
  - `Partial` if at least one line bought stock and at least one line skipped.
  - Otherwise current stop status or `Pending`.
- [x] Intent uses planned quantities/gil only.
- [x] Result uses purchased quantities/gil only.
- [x] Notes mention skipped lines, opportunistic wins, warnings, or `No safe stock` without exposing internal enum noise.

### 2. Add Route Table Presenter Tests

- [x] Add `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteTablePresenterTests.cs`.
- [x] Cover a single-item planned world.
- [x] Cover a multi-item world with one planned item and one opportunistic item.
- [x] Cover a partial world: one completed line, one skipped line.
- [x] Cover collapsed item summary truncation.
- [x] Cover that discovered values are per-line in expanded rows and not only flattened world totals.

Focused command:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisitionRouteTablePresenterTests" -v minimal
```

### 3. Fix World Completion Accounting Before Repainting

The table should not make bad accounting look nicer.

- [x] Audit `MarketAcquisitionGuidedRouteSession.CompleteActiveStop`.
- [x] Ensure completed stop totals are derived from `LineStates` or explicitly accumulated stop-scoped purchases, not from stale active-world counters.
- [ ] Add tests in `MarketAcquisitionGuidedRouteSessionTests` for two-world routes where:
  - World A purchases stock.
  - World B purchases different stock.
  - World B summary does not include World A purchases.
- [ ] Add a multi-item same-world test where line-level totals sum to the world summary.

Focused command:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisitionGuidedRouteSessionTests" -v minimal
```

### 4. Add Post-Run Rollup Model

- [x] Add `MarketMafioso/MarketAcquisition/MarketAcquisitionRouteRunSummary.cs`.
- [x] Build summary from route stops and diagnostic warning state.
- [x] Include:
  - `PurchasedQuantity`
  - `SpentGil`
  - `PlannedPurchasedQuantity`
  - `PlannedSpentGil`
  - `OpportunisticPurchasedQuantity`
  - `OpportunisticSpentGil`
  - `CompletedWorldCount`
  - `PartialWorldCount`
  - `FailedWorldCount`
  - `CompletedLineCount`
  - `SkippedLineCount`
  - `FailedLineCount`
  - `TopItemsBySpentGil`
  - diagnostic warning count and artifact paths
- [x] Expose `MarketAcquisitionRouteRunner.LastRunSummary` after completion/failure.
- [x] Keep route summary generation pure and testable.

### 5. Add Post-Run Rollup Tests

- [x] Add `MarketAcquisitionRouteRunSummaryTests`.
- [x] Cover completed route totals.
- [x] Cover planned vs opportunistic purchase totals.
- [x] Cover partial/failed line counts.
- [x] Cover top-item ordering by spent gil.
- [x] Cover diagnostic artifact paths appearing only when available.

Focused command:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisitionRouteRunSummaryTests" -v minimal
```

### 6. Improve Purchase/Observed Diagnostic Joinability

Do not overbuild a database here; make the CSVs easier to reconcile.

- [x] Add `itemId`, `unitPrice`, and `sourceCandidateStatus` to purchase records if not already available at the purchase callsite.
- [ ] Add a purchase-time observed row with a clear decision such as `PurchasedFromFreshRead` when a purchase is made from a fresh listing read that was not present in the earlier candidate plan CSV row.
- [x] Ensure every successful purchase can be traced by `requestId`, `world`, `lineId`, `listingId`, and `retainerId`.
- [x] Update `MarketAcquisitionRouteDiagnosticsTests` for headers and a representative row.

Focused command:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisitionRouteDiagnosticsTests" -v minimal
```

### 7. Replace MainWindow Route Table Rendering

- [x] In `MarketMafioso/Windows/MainWindow.cs`, change `DrawGuidedRouteStops` to consume presenter rows.
- [x] Change collapsed table columns to:
  - `World`
  - `Data Center`
  - `Route Lines`
  - `State`
  - `Intent`
  - `Result`
  - `Notes`
- [x] Render expanded child rows with their own header/formatting row:
  - `Item`
  - `Source`
  - `Item State`
  - `Planned`
  - `Discovered`
  - `Bought`
  - `Notes`
- [x] Preserve row expand/collapse behavior.
- [x] Keep columns resizable.
- [x] Use color only for semantic emphasis: state text, source tags, warning notes.
- [x] Avoid adding new custom window controls.

### 8. Render Post-Run Rollup In MainWindow

- [x] Replace the warning-count-only `DrawPostRunDiagnosticSummary` with a compact rollup when `LastRunSummary` exists.
- [x] Keep the current warning line if no full summary is available yet.
- [x] Show diagnostics path and CSV paths only as muted detail, ideally behind the existing diagnostics button or short line.
- [x] Do not spam the main window with all warnings; the main surface should say what happened and where to inspect details.

### 9. Visual Polish

- [ ] Keep the warm MarketMafioso palette used by the dashboard mockup direction.
- [ ] Apply color to make state legible without turning the table into a rainbow:
  - Green: complete/purchased.
  - Blue: active/buying.
  - Yellow: skipped/no safe stock/warnings.
  - Red: failed/blocking.
  - Muted: planned-only or unavailable data.
- [ ] Prefer concise text over dense debug labels.
- [ ] Keep the expanded row indentation modest; nested rows should read like line items, not a second unrelated table.

### 10. Verification

Run focused tests first:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisitionRouteTablePresenterTests|FullyQualifiedName~MarketAcquisitionRouteRunSummaryTests|FullyQualifiedName~MarketAcquisitionGuidedRouteSessionTests|FullyQualifiedName~MarketAcquisitionRouteDiagnosticsTests" -v minimal
```

Then build:

```powershell
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
```

Deploy for live review:

```powershell
powershell -ExecutionPolicy Bypass -File "MarketMafioso/tools/Deploy-DevPlugin.ps1"
```

Live smoke checks:

- [ ] `/mmf` opens.
- [ ] Market Acquisition tab loads.
- [ ] A prepared multi-item plan shows useful collapsed world rows.
- [ ] Expanding a world shows item-level planned/discovered/bought values.
- [ ] Completed run shows a post-run rollup.
- [ ] Diagnostics button/path still works.

## Commit Strategy

- [ ] Commit this plan separately.
- [ ] Commit presenter/accounting model changes with tests.
- [ ] Commit diagnostics CSV joinability changes with tests.
- [ ] Commit ImGui table rendering and post-run rollup UI.
- [ ] Deploy after the UI commit for live testing.

## Non-Goals

- Do not replace the route runner in this pass.
- Do not redesign the full Market Acquisition tab layout.
- Do not introduce a persistent route history database.
- Do not remove diagnostics CSVs.
- Do not hide all debug states; move them out of the primary collapsed table.

## Follow-Up Candidates

- Route history browser using completed runs as presets.
- Exportable post-run report.
- Dashboard-side route rollup matching the plugin summary.
- Further extraction of route execution out of `MainWindow`.
- Event-driven pagination/purchase confirmation refinements as continued live use reveals them.
