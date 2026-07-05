# Client Acquisition Surface Reconciliation Plan

## Status

Draft proposal for review. This reconciles the scattered Market Acquisition, Quick Shop, Craft Architect Companion, and diagnostics surfaces into one client-native acquisition workbench while preserving automatic dashboard sync.

Related mockups:

- `mockups/acquisition-workbench-build.html`
- `mockups/acquisition-workbench-run.html`
- `mockups/acquisition-workbench-adjust.html`

## Problem

The current plugin surfaces were introduced in useful slices, but each slice added its own window, status language, item controls, route-scope controls, and next action. The result is hard to operate in game:

- Quick shopping, craft-cost appraisal, route preparation, guided execution, and diagnostics are split across top-level tabs and popouts.
- Single-item and multi-item acquisition are treated like different mental models even though they should both be route drafts with one or more lines.
- Craft Architect evidence is useful, but it is visually too close to route execution and can be mistaken for authoritative pricing.
- The dashboard sync contract is correct, but the user should not have to return to the dashboard to claim, accept, monitor, or recover a client-authored route.
- Recovery branches, especially resume, replan remaining, and restart after interruption, are not visible enough at the point where the user needs them.

## Product Principle

The plugin should expose one operational workbench:

> Build the acquisition locally, optionally appraise it with Craft Architect evidence, sync it automatically for dashboard monitoring, prepare a route, execute it, adjust it, and recover from interruptions without leaving the client.

The dashboard remains a passive monitor and audit surface for client-created routes. It must not be a required step in the client workflow.

## Proposed Information Architecture

### Main Window

The main `Market Acquisition` tab becomes a compact status and launcher:

- Active route summary.
- Last synced request.
- One primary action: `Open Acquisition Workbench`.
- Secondary links: dashboard, diagnostics folder, internal settings.

The main window should not contain the route builder, CA appraisal, claimed batch editor, advisory-plan table, guided route controls, and diagnostics all at once.

### Acquisition Workbench Popout

The workbench owns the full client-side workflow. It uses a compact phase strip in the bottom status bar rather than a left-side step rail:

1. `Build`
2. `Appraise`
3. `Prepare`
4. `Run`
5. `Review`

The phase strip is informational, not a navigation tax. The main panes should make the current state obvious without dedicating a full column to workflow labels.

### Diagnostics Drawer

Diagnostics become an attached drawer or secondary pane inside the workbench. The standalone diagnostics windows may remain for deep inspection, but the normal route runner should have inline diagnostics entry points:

- Latest market-board probe.
- Last reconciliation decision.
- Current blocker.
- Latest diagnostic printout path.
- Buttons for capture/probe only when meaningful.

## Workflow Model

### Step 1: Build Draft

The user starts in `Build`.

Route-wide fields:

- Target character and world inferred from current character scope.
- Region, defaulted from current world or saved setting.
- World policy:
  - `Recommended`
  - `All-world sweep`
- All-world sweep scope:
  - `Region`
  - `Current data center`
  - `Selected data centers`
- Recent-world policy:
  - `Smart skip`
  - `Full resweep`

Line fields:

- Item name autocomplete with inferred item ID.
- Quantity policy:
  - `Buy up to quantity`
  - `Buy all below threshold`
- Quantity or purchase cap:
  - `Buy up to quantity` treats it as the target quantity.
  - `Buy all below threshold` may leave it empty; when supplied, it is the maximum quantity the route may buy.
- HQ policy.
- Max unit price.
- Optional gil cap.
- Optional note or source badge.

Single-item acquisitions are drafts with one line. Multi-item acquisitions are drafts with multiple lines. There should be no separate single-item mode.

For uncapped `Buy all below threshold` lines, stock availability must not claim that enough stock exists. It should report depth instead: eligible units observed, listing count, and whether any under-threshold stock is present. Capped or target-quantity lines can report sufficiency against the requested quantity.

Branching points:

- No character scope: block submit, keep draft editable.
- Item search ambiguous: require explicit selection from autocomplete results.
- Missing max unit price: allow appraise, block route sync.
- Missing quantity: block `Buy up to quantity` route sync.
- Missing purchase cap: valid for `Buy all below threshold`; stock availability reports eligible depth instead of sufficiency.
- Mixed HQ policies: valid per line.
- Region mismatch with current world: warn, do not block.

### Step 2: Appraise Evidence

The user can appraise any line, several lines, or the entire draft.

Evidence sources:

- Workshop Host / CA hosted quote API.
- Last-good quote cache.
- Manual fallback only when enabled.
- Universalis stock check using the same route scope that the draft will use.

Rules:

- Craft cost is advisory.
- Max unit price remains user-owned.
- Applying quote values mutates the editable threshold field and invalidates stock availability.
- Quote application should use explicit actions such as `Use craft cost` or `Set threshold from quote`; percentage adjustment buttons are too arbitrary for the primary surface.
- Stock availability must be scoped the same way as route planning, or it must visibly say that it is broader than the planned route.
- Stock availability counts only listings that satisfy the current line constraints:
  - same item,
  - selected HQ policy,
  - positive quantity,
  - positive unit price,
  - unit price less than or equal to the line's max unit price,
  - inside the selected route scope.
- Stock availability compares eligible stock against the line's target quantity or purchase cap when one exists. For uncapped `Buy all below threshold`, it reports observed eligible depth and should not use words like `enough` or `short`.
- Stock availability should not treat above-threshold stock as available for the route.

### Observed Price Cache

Threshold changes should not require a fresh Universalis request when the item and route scope have not changed.

Add a client-side observed price cache for raw listing observations:

- Key: item ID, region/scope query target, and listing source.
- Value: raw listings, fetched timestamp, source freshness metadata, and request diagnostics.
- Threshold, quantity, HQ policy, and line enablement changes re-run local analysis against cached listings.
- Item, region, world policy, sweep scope, selected data centers, manual refresh, or cache expiry triggers a new fetch.
- The first implementation can be in-memory with a bounded LRU size and a short freshness TTL.
- The UI should expose whether availability came from cached observations or a fresh fetch.
- A manual `Refresh Stock` action bypasses the cache when the user wants newer Universalis uploads considered immediately.

Branching points:

- CA quote unavailable: keep route building usable and show source unavailable.
- Quote incomplete: show warnings, allow manual threshold.
- Market data stale or unavailable: keep quote visible, block stock-dependent claims.
- No under-threshold stock: allow threshold adjustment, all-world sweep, or route creation only if the user explicitly chooses a probe route.

### Step 3: Sync And Prepare

The user clicks `Sync Route` from the workbench.

The client performs:

1. Create server-backed batch with origin `ClientQuickShop`.
2. Claim using current plugin instance.
3. Accept locally.
4. Persist the accepted claim.
5. Show dashboard sync state.

The user then clicks `Prepare Plan`.

Preparation performs:

- Universalis fetches.
- World-catalog recent visit filtering.
- Re-check for fresher external uploads when useful.
- Plan construction.
- Diagnostics capture.

Branching points:

- Create succeeds, claim fails: show created request id and offer `Claim Synced Route`.
- Claim succeeds, accept fails: keep claim state and offer retry.
- Dashboard/server unavailable: keep local draft intact.
- No legal stock: return to Appraise with the failed evidence visible.
- Partial legal stock: allow run with partial plan, adjust thresholds, or restart planning from the current draft.
- Recently visited worlds skipped: show skipped count and make restart semantics explicit.

### Step 4: Run Route

The `Run` surface owns route execution:

- Route map with enough room for planned worlds, active world state, completed/probed markers, and per-world totals.
- Active world.
- Current line focus.
- Planned buys by line.
- Market-board read details as a secondary pane, not the central object.
- Purchases made.
- Dashboard progress sync.
- Pause, stop, resume, and restart planning.

Branching points:

- Market board closed: wait state with open-board prompt.
- No search item: show item and retry controls.
- Visible cache exhausted: continue, restart planning, or mark world probed.
- Purchase selection recoverable failure: retry, skip listing, or re-probe.
- Server reports route already failed/complete: reconcile local state and stop mutation.
- Route stopped: preserve request fulfillment progress and durable world/catalog observations for recovery.

### Step 5: Adjust, Recover, Review

Adjustment is not a failure mode. It is part of the workbench.

Available adjustments:

- Change threshold per line.
- Change purchase cap or target quantity per line.
- Disable a line.
- Switch Recommended to All-world sweep.
- Resume the current prepared stop list when route state is still valid.
- Replan remaining quantities when partial fulfillment should be preserved.
- Restart the plan from the edited draft when the route should be recalculated from scratch.

Review shows:

- Bought quantities by line.
- Spend by line.
- Worlds visited.
- Worlds skipped due to recent catalog.
- Failed or zero-purchase reasons.
- Dashboard sync status.
- Route completion/failure action.

Branching points:

- Complete all lines: mark route complete.
- Partial completion: stop, resume, replan remaining, or restart plan.
- User stops mid-route: resume the existing plan when still valid, or replan from remaining quantities when the current stop list is stale.
- User wants a new acquisition from the same item set: duplicate into new draft.

### Recovery Semantics

Recovery options are about route and request state, not about whether world/catalog knowledge is durable.

- `Resume Route` continues the current prepared stop list. It is useful when the interruption was temporary and the prepared route is still valid.
- `Replan Remaining` recomputes from unfilled request quantities while preserving route fulfillment progress: already purchased quantities, line spend, and dashboard purchase audit remain part of the request. It still uses durable world/catalog and observed-price cache data.
- `Restart Plan` recomputes from the current edited draft quantities. It does not use route-local partial fulfillment to reduce quantities, but it still uses durable world/catalog and observed-price cache data unless the user explicitly refreshes or clears those observations.

Durable learned state is not plan-bound:

- recently checked/probed worlds,
- observed listings and Universalis snapshots,
- purchase history,
- known exhausted or picked-over worlds,
- freshness timestamps.

Run-local fulfillment state is request-bound:

- bought quantity by line,
- spent gil by line,
- dashboard progress and purchase audit,
- current prepared stops,
- active stop state.

## Surface Mockup Details

### Build/Appraise Surface

File: `mockups/acquisition-workbench-build.html`

Purpose:

- Show one workbench where single and multi-line acquisition are the same flow.
- Put route settings, item lines, CA evidence, threshold controls, and stock availability into one coherent screen.
- Make dashboard sync passive and visible, not a separate action outside the client.

Primary controls:

- Add line.
- Duplicate line.
- Remove line.
- Quote selected line.
- Check stock.
- Sync Route.

### Prepare/Run Surface

File: `mockups/acquisition-workbench-run.html`

Purpose:

- Show route state after sync and plan preparation.
- Make the world route, active world, active line, market-board read details, purchase progress, and dashboard sync visible at once.
- Keep diagnostics reachable without switching windows.

Primary controls:

- Prepare Plan.
- Start Route.
- Pause.
- Stop.
- Restart Plan.
- Open Diagnostics Drawer.

### Adjust/Review Surface

File: `mockups/acquisition-workbench-adjust.html`

Purpose:

- Show the route after interruption or partial completion.
- Make resume, replan remaining, and restart plan semantics explicit without treating durable market knowledge as volatile plan memory.
- Show per-line adjustments before restarting without turning every possible adjustment into a separate branch choice.

Primary controls:

- Resume Route.
- Replan Remaining.
- Restart Plan.
- Apply Line Adjustments.
- Stop Route.
- Duplicate As New Draft.

## Component Reconciliation

### New Shared UI Units

- `AcquisitionWorkbenchWindow`
  - Owns command bar, route state summary, phase strip, and pane routing.
- `AcquisitionDraftPanel`
  - Owns route-wide settings and line grid.
- `AcquisitionLineEditor`
  - Owns add/edit/duplicate/remove for single and multi-line drafts.
- `ItemAutocompleteControl`
  - Shared item-name search and inferred item ID behavior.
- `RouteScopeSelector`
  - Shared region, world policy, sweep scope, and data-center selector.
- `CraftEvidencePanel`
  - Advisory quote display and threshold application actions.
- `StockAvailabilityPanel`
  - Scope-correct stock summary, per-line availability, and optional detail drilldown.
- `ObservedMarketSnapshotCache`
  - Stores raw listing observations so threshold and quantity changes can be reanalyzed without refetching Universalis.
- `AcquisitionPlanPanel`
  - Prepared plan summary, skipped worlds, and planning diagnostics.
- `RouteRunPanel`
  - World route, active route, market-board read details, purchase actions, and progress reporting.
- `RouteRecoveryPanel`
  - Adjust/recover/review workflow.
- `DiagnosticsDrawer`
  - Embedded normal-case diagnostics, with links to deep diagnostic windows.

### Existing Surface Changes

- `MarketAcquisitionQuickShopWindow`
  - Retire as a standalone primary surface after the workbench can build and sync drafts.
  - Reuse its draft validator and request builder.
- `CraftArchitectCompanionWindow`
  - Retire as a standalone primary surface after the workbench can quote/appraise draft lines.
  - Reuse its quote providers, appraisal service, and telemetry.
- `MainWindow.DrawMarketAcquisitionTab`
  - Reduce to status launcher and route summary.
- `MarketAcquisitionDiagnosticsWindow`
  - Keep for deep debugging, but make common diagnostics available inside the workbench drawer.

## State Ownership

### Local Draft State

Owned by the workbench until sync succeeds.

Invalidation rules:

- Changing item, route scope, sweep scope, or data centers invalidates the cached observation set for the affected line or draft.
- Changing quantity, HQ, threshold, or line enablement invalidates the stock availability analysis, but should reuse cached observations when the cache key still matches.
- Changing item or quantity invalidates matching craft quote unless the quote source explicitly supports the new request.
- Changing max unit price never changes craft quote; it only changes stock availability and route handoff.

### Server Route State

Owned by existing Market Acquisition lifecycle after sync.

The workbench reflects:

- Request id.
- Origin.
- Claim state.
- Accept state.
- Dashboard sync status.
- Progress reporting state.

### Route Runner State

Owned by the existing route runner, but exposed through the workbench:

- Running, paused, stopped, completed, failed.
- Active stop.
- Completed/probed stops.
- Active line progress.
- Purchase audit.
- Last diagnostics.

### Recovery State Boundary

The workbench must keep three state classes separate:

- Durable market/world state: world visit catalog, observed price cache, purchase history, and freshness timestamps.
- Request fulfillment state: bought quantities, spend, line progress, purchase audit, and dashboard lifecycle.
- Prepared plan state: current stop list and active stop execution state.

`Replan Remaining` consumes request fulfillment state to reduce target quantities. `Restart Plan` uses the edited draft quantities instead. Both may use durable market/world state.

## Dashboard Sync Contract

Client-created routes must be automatically visible to the dashboard.

The dashboard should receive:

- Origin `ClientQuickShop`.
- Draft line names and inferred item IDs.
- Route scope.
- Claim/accept status.
- Preparation and route progress.
- Line progress.
- Purchase audit.
- Completion/failure status.

The user should not need to claim or accept the route from the dashboard. If dashboard state diverges, the workbench should surface reconciliation actions inside the client.

## Accessibility And In-Game Ergonomics

- Use compact operational labels.
- Avoid explanatory wall text in windows.
- Use disabled states with adjacent cause text.
- Keep all critical state visible without horizontal scrolling at common plugin popout sizes.
- Prefer tables for line and world data.
- Keep action buttons in stable command bars.
- Do not hide destructive actions near primary actions without state confirmation.
- Use color as emphasis, not the only status signal.

## Implementation Slices

### Slice 1: Shared Controls And State Invalidation

- Extract item autocomplete.
- Extract route scope selector.
- Add draft/appraisal invalidation keys.
- Add tests proving stale stock/appraisal results are cleared after relevant input changes.
- Add observed price cache tests proving threshold and quantity changes reanalyze cached listings without refetching.

### Slice 2: Workbench Shell

- Add `AcquisitionWorkbenchWindow`.
- Move quick-shop draft editing into the workbench.
- Keep old Quick Shop launcher as a temporary link to the workbench.
- Main Market Acquisition tab becomes status and launcher only.

### Slice 3: Appraisal Integration

- Move CA quote/appraisal panels into the workbench.
- Make stock availability fetch scope match route scope.
- Add observed price cache and expose cached/fresh status in stock availability.
- Keep threshold user-owned.
- Support line-level quote application.

### Slice 4: Sync, Prepare, And Run Integration

- Wire create/claim/accept into the workbench.
- Surface plan preparation and route runner state.
- Embed common diagnostics drawer.

### Slice 5: Recovery And Review

- Add adjustment/recovery/review panel.
- Add `Resume Route`, `Replan Remaining`, and `Restart Plan` actions.
- Keep durable world/catalog and observed-price cache data separate from request fulfillment progress.
- Add duplicate-as-new-draft.

### Slice 6: Retire Scattered Surfaces

- Remove or demote standalone Quick Shop and CA Companion windows.
- Keep deep diagnostics windows as advanced/debug surfaces.
- Update private usage docs.

## Verification Plan

Automated tests:

- Single-line draft maps to one-line batch.
- Multi-line draft maps to multi-line batch.
- Item search requires resolved item ID.
- Changing route scope invalidates stock availability.
- Changing threshold reanalyzes cached observations without refetching when item and route scope are unchanged.
- CA quote never overwrites threshold without an explicit apply action.
- Stock availability counts only under-threshold listings that match route line constraints.
- Uncapped all-below-threshold lines report depth, not sufficiency.
- Sync creates, claims, accepts, and persists route state.
- Replan Remaining reduces planned quantities by request fulfillment progress.
- Restart Plan uses edited draft quantities rather than route-local partial fulfillment.
- Both Replan Remaining and Restart Plan may use durable world/catalog and observed-price cache data.

Manual verification:

- Build a one-item acquisition entirely in the client.
- Build a three-item acquisition entirely in the client.
- Apply CA quote to one line, manually set another threshold, and leave a third manual.
- Sync, prepare, run, stop, resume, replan remaining, and restart plan without using the dashboard.
- Confirm dashboard passively monitors the client-created route.
- Confirm stale stock availability is not shown after threshold/scope changes.

## Open Questions For Review

- Should line-level CA quotes be allowed for multiple lines in one request, or should the first implementation quote one selected line at a time?
- Should `Create probe route despite no known stock` exist in the first workbench slice, or remain a later advanced option?
