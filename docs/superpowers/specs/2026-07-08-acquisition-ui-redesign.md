# Acquisition UI Redesign

## Problem

The current Market Acquisition tab landed the mechanics of the request builder but missed the intended product shape. It renders as a thin vertical form above the old execution sections. The result is sparse, disorienting, and state-confusing: a restored accepted request can appear in the claimed batch section while the builder still reports a new empty local draft.

The target is a dense, organized acquisition board where every visible inch is part of the workflow:

- Build and edit the persistent request.
- Keep the accepted request, advisory plan, and guided route handler visible.
- Preserve the existing route handler.
- Make request edits, pricing evidence, plan staleness, and route readiness understandable from one screen.

## Design Principles

1. The request is the spine.
   The builder must always reflect the active claimed/accepted request when one exists. It must not show an unrelated empty draft beside an accepted claimed batch.

2. Lines are the primary object.
   Acquisition lines should dominate the builder surface. Item entry is a compact editor for the selected line, not the main visual mass of the page.

3. Evidence belongs to the selected line.
   Craft quote evidence, stock context, threshold edits, and appraisal controls belong near the selected line because they explain the buy rule for that line.

4. Lifecycle controls belong to their lifecycle.
   Request actions stay with the builder. Plan actions stay with the plan. Route actions stay with the existing route handler.

5. Dense does not mean cluttered.
   Use tables, child regions, fixed-width fields, status strips, and grouped action rows. Avoid full-width single-field bands and large blank spaces.

## Target Layout

The Market Acquisition tab becomes a two-pane board.

Left pane: Request Builder

- Status strip with target, sync state, line count, and stale-plan signal.
- Compact route scope row.
- Compact line editor row: item search, mode, HQ, quantity, max unit, gil cap, add/update/new.
- Acquisition line table with selected-row behavior.
- Selected-line inspector with craft quote, stock evidence, and local edit notes.
- Request action row: sync/update, refresh, adopt remote, clear/duplicate.

Right pane: Accepted Request, Plan, Guided Route

- Claimed batch summary and line table.
- Claim actions only when claim status supports them.
- Advisory plan section with stale-plan warning and prepare action.
- Existing guided world route controls and stop table.

Request Pickup is demoted. It should be compact and conditional:

- Show fetch controls only when no request is claimed or when explicitly useful for troubleshooting.
- Do not sit as a major section between builder and execution.

## Required Behavior

- On startup, if a claimed acquisition request is restored from config, the request builder adopts it unless it already contains unsynced edits for the same remote request.
- Claiming or accepting a request still adopts it into the builder.
- Syncing a new builder request creates, claims, accepts, and then leaves the accepted request visible in both builder and execution panes.
- Updating a synced request replaces the remote request through the revision-guarded endpoint and clears the prepared plan.
- Editing any builder line after plan preparation marks the plan stale and blocks route start.
- Refreshing a synced request adopts remote changes automatically only when there are no local edits; otherwise it stages the remote change for explicit adoption.
- The old popout workbench and Quick Shop language remain removed.

## Implementation Scope

This redesign is primarily ImGui UI structure and state coherence. It should not introduce a new acquisition engine, a new route handler, or new server contracts.

Expected files:

- `src/MarketMafioso/Windows/MainWindow.cs`
- `src/MarketMafioso/Windows/MarketAcquisitionRequestBuilder/MarketAcquisitionRequestBuilderPanel.cs`
- Focused request-builder tests if behavior is moved into testable helpers.

## Visual Acceptance

The live UI is acceptable when:

- The first viewport is visually closer to the dense mockup than the current thin stacked form.
- Builder and claimed batch agree about the active request.
- Acquisition lines are visible without scrolling past a tall form.
- Selected-line evidence is visible near the line table.
- Request, plan, and route actions are grouped by lifecycle.
- The old route handler remains visible and familiar.
- Empty states are compact and useful rather than large blank sections.

## Verification

- Build the plugin in Debug.
- Run focused Market Acquisition/request-builder tests.
- Deploy the dev plugin.
- Visually inspect the Market Acquisition tab in game and compare against the mockup and the failed screenshot.
