# Market Acquisition Multi-Item Roadmap

Design source: `docs/design/2026-06-28-market-acquisition-multi-item-batches.md`

Implementation plan: `docs/superpowers/plans/2026-06-28-market-acquisition-multi-item-batches.md`

Current sprint handoff: `docs/design/2026-06-29-market-acquisition-sprint-handoff.md`

## Goal

Promote Market Acquisition from single-item request execution to multi-item batch execution.

The dashboard should stage one purchase batch from its queued item lines. The plugin should claim one batch, prepare one combined route, and execute item subtasks per world with per-line status, progress, and audit records.

## Why This Is A New Roadmap

The original Market Acquisition roadmap proved the novel pieces:

- dashboard-to-plugin request pickup,
- advisory planning,
- Lifestream-assisted route execution,
- market-board search automation,
- live listing reads,
- guarded purchase execution,
- route progress reporting.

That roadmap answered whether the full loop could work. This roadmap changes the unit of work from one item to a durable multi-line batch. That touches storage, contracts, dashboard staging, plugin pickup, planning, route execution, progress, and diagnostics, so it deserves its own track.

## Principles

- `Stage Queue` creates one batch, not N independent requests.
- One batch contains one or more lines.
- A single-item acquisition is a one-line batch.
- The plugin may have only one active execution attempt.
- One line can under-procure or skip without failing the whole batch.
- Catastrophic or ambiguous route/purchase failures still stop the batch.
- Purchases remain authorized only by fresh live market-board rows.
- Completed and failed batches remain inspectable for audit and later preset reuse.

## Phase 0: Batch Model Alignment

Status: Complete

Objective: Lock vocabulary, migration behavior, and line semantics before code changes.

Exit criteria:

- Design doc defines batch, line, attempt, route stop, and item subtask.
- Old single-request fields have explicit one-line mapping.
- Route ordering and in-world item ordering are settled.
- Server `lineId` ownership and validation rules are defined.
- The old `/api/marketmafioso/...` route shape remains retired.

Evidence:

- `docs/design/2026-06-28-market-acquisition-multi-item-batches.md` defines the durable batch model and the single-line compatibility shape.
- Server, dashboard, and plugin DTOs now expose batch `lines` and line ids.

## Phase 1: Batch Contracts And Storage

Status: Mostly complete

Objective: Add batch and line DTOs/storage while preserving existing single-item data as one-line batches.

Exit criteria:

- Server can create, list, and claim multi-line batches. Done in first server slice via `/api/acquisition/batches`, `/api/acquisition/batches/pending`, and existing claim routes.
- Existing pending, claimed, accepted, and terminal single-item rows expose one-line batch views. Done for API reads via fallback line projection; physical historical backfill remains optional.
- Batch create is atomic across all lines. Done for new batch creates.
- Claim is atomic at the batch level. Done by preserving the existing request-level claim boundary.
- Idempotent create works for identical bodies and conflicts on body changes.
- Canonical hosted/local `/marketmafioso/api/acquisition/batches` routes pass tests. Done in `MarketAcquisitionRequestEndpointTests`.

Next work:

- Add line-aware lifecycle/progress/audit payloads before the plugin starts reporting per-line purchase outcomes.
- Migrate dashboard staging to submit one batch payload instead of relying on legacy one-request semantics.
- Migrate plugin pickup to call the batch pending endpoint and consume `lines`.

## Phase 2: Dashboard Batch Staging

Status: Mostly complete

Objective: Make the dashboard builder's local queue stage one batch.

Exit criteria:

- `Stage Queue` sends one batch payload containing all queued lines. Done in first dashboard slice.
- Dashboard shows one row per batch. Mostly done through existing request grid because one staged queue now produces one server row.
- Batch details show line status, thresholds, caps, and purchased quantity. Done in the dashboard request drawer with an explicit purchase-lines table.
- Terminal batches remain visible but are not active pickup candidates.
- Dashboard routing value mismatches such as `CurrentWorld` vs `CurrentWorldOnly` are normalized before submit. Done at batch submit.
- Dashboard request-board rows render all batch lines instead of the request-level first-line compatibility fields. Done; quantity and max-unit columns now show per-line values.

Next work:

- Confirm the builder's template/preset flow can restore multi-line batches once completed runs are reused as presets.
- Remove stale per-line routing fields from the dashboard UI model if they remain unused after plugin migration.

## Phase 3: Plugin Batch Pickup

Status: Mostly complete

Objective: Let the plugin fetch, claim, accept, persist, and reconcile one batch with many lines.

Exit criteria:

- Pending pickup shows batches with item counts and line summaries. Mostly done; pending/active rows identify multi-line batches and the claimed batch view shows line rows.
- Claiming returns all lines. Done at contract/model level.
- Accepted batch state survives plugin reload. Done for line summaries.
- Old single-line accepted state restores as a one-line batch. Done via persistence fallback.
- The plugin refuses to claim a different batch over an active accepted/running batch.
- Terminal server state clears local active route/line state.

Current caveat:

- Live validation still needs to prove terminal server-state reconciliation across reloads and failed/retried batches. The old "first subtask only" caveat is no longer current; Phase 5 now owns remaining multi-item execution validation.

## Phase 4: Combined Advisory Planning

Status: Mostly complete

Objective: Build one route from multiple line-level advisory candidate sets.

Exit criteria:

- Planner creates route stops with item subtasks carrying `lineId`, `itemId`, and line constraints. Done.
- Multiple lines on the same world share one world stop. Done and covered by planner tests.
- Current-world/current-data-center ordering still applies. Preserved through existing route sort after world grouping.
- Items inside a world process in dashboard queue order. Done in model ordering; full execution waits for Phase 5.
- Within each item line, cheapest confirmed safe listing wins. Done for advisory remote listings.
- A line with no supported remote listings becomes a line-level skip, not a batch failure. Done and covered by planner tests.

Current caveat:

- The route monitor can search/probe/purchase the active stop's item subtasks and can advance from one item to the next on the same world. Live validation should continue to focus on stale search-state regression, candidate reuse, opportunistic mid-flight expansion, and visible-cache exhaustion on very deep market-board results.

## Phase 5: Multi-Item World Execution

Status: Mostly complete

Objective: Execute all relevant item subtasks on a world before traveling.

Exit criteria:

- Route runner searches/selects/reads/purchases per item subtask. Done for the current runner: route stops track an active item-subtask cursor, and search/probe/purchase uses that active line.
- Re-read-after-purchase behavior remains intact.
- Listing window closure after the last listing is normal exhaustion. Existing behavior preserved for world completion; needs line-level confirmation in live testing.
- Item B cannot reuse stale live candidates from item A. Done for the current route runner: route advancement clears search submission, read results, live candidates, reconciliation, and purchase session state before monitoring the next same-world item.
- Non-catastrophic line exhaustion continues to the next line. Done in route session for no-safe-candidates and purchase-complete transitions on the same world.
- Catastrophic/ambiguous failures stop the batch.
- The market-board reader exposes listing-cache capacity and truncation diagnostics when the game reports more listings than the readable cache contains.
- The route session preserves visible-cache exhaustion as a line-level skip reason instead of presenting it as ordinary no-safe-stock.
- Planner diagnostics explain accepted/rejected listings, including price, HQ policy, world scope, gil cap, quantity cap, wrong item, and sweep-probe worlds.

Current caveat:

- Opportunistic subtasks can now be probed and reconciled against live market-board rows, but live validation still needs more end-to-end testing for mid-flight plan expansion and for items with more than 100 visible listings.
- The market-board listing reader now reports when the game exposes more listings than the readable `InfoProxyItemSearch` cache, and zero-purchase line advancement preserves that as `SkippedVisibleCacheExhausted`. This is truncation-aware behavior, not deeper-pagination support.

## Phase 6: Per-Line Progress And Audit

Status: Mostly complete

Objective: Make server and dashboard progress meaningful for batches.

Exit criteria:

- Attempt events can include `lineId`.
- Unknown or wrong-batch line ids fail explicitly.
- Idempotent line-event replay works.
- Purchase audit rows include `lineId`.
- Dashboard details show per-line status and purchase totals.

Current caveat:

- Batch status still follows explicit batch lifecycle events. Richer batch-status projection from all line statuses can be added later if we want partial/under-procured terminal states to appear as first-class statuses.

## Phase 7: Cleanup And Live Validation

Status: In progress

Objective: Remove stale single-request assumptions and prove the multi-item loop live.

Exit criteria:

- Dashboard cannot stage N independent requests from one queue.
- Plugin cannot silently overwrite accepted/running batch state.
- Single-line behavior still works through the new batch model.
- Two low-value items can be staged, claimed, planned, routed, and purchased in one batch. Needs final live-test confirmation after this feature track deploy.
- Roadmap status is updated with live-test notes.
- Terminal batches remain inspectable in the dashboard archive and can be reused as composer drafts through `Run again`.

Current caveat:

- This phase cannot be closed from source and unit tests alone. It needs at least one live multi-item batch that purchases or safely skips more than one item line across at least one shared-world stop.
- Deeper pagination is tracked separately in `docs/superpowers/plans/2026-06-29-market-board-deeper-pagination.md`; current code records truncation and request-id diagnostics but does not yet request additional market-board pages.

## Deferred

- Global batch gil cap.
- Server-side route-log indexing.
- Deeper market-board pagination beyond the visible game listing cache. Current source evidence shows `InfoProxyItemSearch` has a fixed 100-listing cache. `InfoProxyPageInterface` exposes `CurrentRequestId`, `NextRequestId`, and `RequestData()`, and its `AddPage(...)` path may dispatch additional pagination/fetch work internally, but the safe state transition is not yet proven. Follow `docs/superpowers/plans/2026-06-29-market-board-deeper-pagination.md` before automating this.
- Craft Architect quality planning integration.
- Native travel automation beyond Lifestream.
