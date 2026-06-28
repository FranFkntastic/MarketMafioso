# Market Acquisition Multi-Item Batches

## Purpose

Market Acquisition can stage multiple dashboard rows, but the plugin currently accepts and executes one acquisition request at a time. That makes multi-item workflows awkward and unsafe: accepting another request can overwrite local state, progress reporting is tied to a single item, and a route cannot naturally buy several useful items while already standing at the correct market board.

This document defines the next model: a dashboard queue stages one multi-line acquisition batch, the plugin claims one batch, and the plugin executes one combined route with per-line status.

## Goals

- Make multi-item purchase queues first-class instead of a collection of unrelated single-item requests.
- Preserve the existing one-active-execution-attempt invariant in the plugin.
- Let one route buy multiple items on the same world before traveling again.
- Keep request/batch identity stable across retries while giving each execution attempt its own child identity.
- Track per-item outcomes so one item can under-procure without failing the whole batch.
- Keep live market-board rows authoritative for purchase decisions.
- Keep dashboard staging ergonomic: the existing queue builder should stage one batch from its queued lines.

## Non-Goals

- Multiple simultaneous active acquisition attempts in one plugin instance.
- Server-owned UI automation.
- Treating remote market data as purchase authority.
- Global batch-level gil budgeting in the first implementation.
- Automatic retainer deposits or inventory-space optimization.
- Preset/archive reuse beyond keeping terminal batches inspectable.

## Core Concepts

### Acquisition Batch

An acquisition batch is the durable dashboard-created intent.

It answers:

- Which character should execute the purchase?
- Which region and routing mode are requested?
- Which item lines are included?
- Has a plugin claimed the batch?
- What is the current server-visible lifecycle state?

A single-item acquisition is represented as a batch with one line.

The batch is the user-facing identity. Retrying the same batch creates a new execution attempt under the same batch id; it does not create a new top-level acquisition object.

### Acquisition Line

An acquisition line is one item target inside a batch.

It answers:

- Which item should be bought?
- What max unit price, optional max quantity, optional gil cap, and HQ policy apply?
- How much has been purchased?
- Is the line complete, under-procured, skipped, or failed?

First implementation keeps gil caps per line. A global batch cap can be added later if it becomes useful.

Legacy single-item fields map directly onto one batch line:

- `quantity` maps to `targetQuantity` for `TargetQuantity`.
- `quantity` maps to `maxQuantity` for `AllBelowThreshold`, where blank or zero means no max quantity.
- `maxTotalGil` maps to the line's `gilCap`.
- request-level item id, item name, item kind, max unit price, and HQ policy move to the line.

### Execution Attempt

An execution attempt is one plugin-side try to fulfill one claimed batch.

The existing run-model rules still apply:

- route start or restart creates a new `attemptId`,
- event sequence is scoped to that attempt,
- stale attempts are classified instead of producing repeated generic conflicts,
- one plugin instance may have at most one active acquisition attempt.

### Route Stop

A route stop is one world visit inside an attempt.

For multi-item batches, a route stop owns a list of item subtasks. The plugin should buy every safe live candidate for relevant lines on that world before traveling again, unless a catastrophic or ambiguous condition stops the route.

## Lifecycle Model

### Batch Lifecycle

```text
PendingPickup
  -> Claimed
  -> AcceptedInPlugin
  -> Running
  -> Complete
   | PartialComplete
   | UnderProcured
   | Cancelled
   | Stopped
   | Failed
```

`PartialComplete` means at least one line reached a useful terminal state while at least one other line did not fully complete. It is not automatically a failure.

### Line Lifecycle

```text
Pending
  -> Planning
  -> Ready
  -> Running
  -> Complete
   | UnderProcured
   | SkippedNoRemoteCandidates
   | SkippedNoLiveStock
   | SkippedPriceDrift
   | Failed
```

Line failure should not stop the batch unless the failure class is catastrophic or ambiguous for the whole route.

### Route Stop Item Subtask Lifecycle

```text
Pending
  -> SearchingItem
  -> ListingsReady
  -> Purchasing
  -> Complete
   | Exhausted
   | Skipped
   | Failed
```

The runner should be able to leave one item subtask and continue to the next item on the same world when the listing window closes, safe stock runs out, or the line has reached its target.

## Quantity Semantics

The batch model keeps the two current quantity modes:

- `TargetQuantity`: buy cheapest confirmed safe live listings until the target is satisfied or safe stock runs out. Whole-stack overage is allowed.
- `AllBelowThreshold`: buy every confirmed safe live listing at or below max unit price, optionally bounded by max quantity and/or per-line gil cap.

`Exact` and `UpTo` remain out of the model.

For `AllBelowThreshold`, dashboard wording should show `All safe stock` or `All safe stock, max N` rather than implying the plugin will stop at an exact requested quantity.

## Route Planning

The planner should build one combined route from all line candidates.

1. Build advisory candidates per line from remote market data.
2. Filter by line max unit price, HQ policy, optional max quantity, and optional line gil cap.
3. Group candidates by world.
4. Order worlds from current world outward:
   - current world first when it has viable candidates,
   - same data center next,
   - other data centers after that,
   - economic ordering inside those locality groups.
5. Inside a world, process item lines in dashboard queue order for predictability.
6. Inside a line, process live listings by confirmed live value:
   - cheaper confirmed live candidates for that item win,
   - favorable live drift is accepted,
   - newly discovered safe stock for that item can be folded into the current world's work.

Lowest confirmed safe live unit price remains the purchase authority within each item line. Cross-item unit prices are not comparable, so the runner should not reorder unrelated items by raw gil value.

## Execution Behavior

The plugin claims one batch, prepares one combined plan, and runs one route.

At each world:

1. Confirm the current world.
2. Open or approach the market board.
3. For each planned item line with candidates on that world:
   - search the item,
   - select the exact item result by item id,
   - read visible listings,
   - build live candidates,
   - buy safe candidates until the line/world subtask is complete, exhausted, or stopped.
4. Re-read after purchases as the current loop already does.
5. If the listing window closes after the last listing, treat it as normal exhaustion and continue to the next item/world.
6. Close market-board windows before travel.

Expected non-fatal outcomes:

- planned listing missing,
- listing already bought by someone else,
- price drift above threshold for one listing,
- no live stock for one line on this world,
- one line under-procured while other lines can continue.

Catastrophic or route-stopping outcomes:

- inventory full,
- insufficient gil,
- ambiguous purchase result,
- wrong item selected,
- wrong world after travel,
- market-board automation cannot establish a valid searchable state,
- repeated UI timeout with no classified recovery.

## Server Data Model Sketch

The existing acquisition request table should evolve into a batch table or be treated as a batch table in code.

### `acquisition_batches`

Suggested fields:

- `batch_id`
- `target_character_name`
- `target_home_world`
- `region`
- `world_mode`
- `status`
- `claim_token_hash`
- `claimed_by_plugin_instance_id`
- `created_at_utc`
- `updated_at_utc`
- `expires_at_utc`
- latest attempt projection fields

If keeping the existing table name during migration is simpler, the product language should still call it a batch.

### `acquisition_batch_lines`

Suggested fields:

- `line_id`
- `batch_id`
- `item_id`
- `item_name`
- `item_kind`
- `quantity_mode`
- `target_quantity`
- `max_quantity`
- `max_unit_price`
- `gil_cap`
- `hq_policy`
- `status`
- `purchased_quantity`
- `spent_gil`
- `latest_message`
- `created_at_utc`
- `updated_at_utc`

### Attempt, Event, And Purchase Rows

Existing attempt/event hardening should be retained and extended to include `lineId`:

- attempt events include `batchId`, `attemptId`, optional `lineId`, optional `routeStopId`, world, phase, and sequence,
- purchase attempts include `batchId`, `lineId`, `attemptId`, listing identity, world, quantity, price, and result.

Line ids are server-owned. The server should reject progress or purchase events that reference an unknown line id or a line id that belongs to another batch. Idempotent replay of the same line event should remain safe; reusing an idempotency key or event sequence for a different line payload should be a real conflict.

## API Shape

Canonical machine endpoints remain under `/marketmafioso/api/...`.

Recommended shape:

```text
POST /marketmafioso/api/acquisition/batches
GET  /marketmafioso/api/acquisition/batches
GET  /marketmafioso/api/acquisition/batches/pending
POST /marketmafioso/api/acquisition/batches/{batchId}/claim
POST /marketmafioso/api/acquisition/batches/{batchId}/accept
POST /marketmafioso/api/acquisition/batches/{batchId}/reject
POST /marketmafioso/api/acquisition/batches/{batchId}/progress
POST /marketmafioso/api/acquisition/batches/{batchId}/complete
POST /marketmafioso/api/acquisition/batches/{batchId}/fail
GET  /marketmafioso/api/acquisition/batches/{batchId}/events
```

Short-term compatibility inside the codebase may reuse request DTO names while implementation is being moved. Public/dashboard language should move to batch/line terminology in the same pass.

The old `/api/marketmafioso/...` route shape remains retired. The batch migration should add hosted/local tests proving canonical `/marketmafioso/api/acquisition/batches...` paths work and the old namespace does not come back by accident.

## Dashboard UX

The dashboard request builder already has the right shape: it queues lines locally, then stages them.

Changes:

- `Stage Queue` creates one batch with all queued lines.
- The request table shows one row per batch.
- A batch row summarizes:
  - item count,
  - target character,
  - routing mode,
  - status,
  - latest route/purchase message,
  - terminal summary.
- A details drawer or page shows:
  - line table,
  - route stops,
  - purchases/skips,
  - diagnostics/timeline.

Completed and failed batches should remain inspectable. Later, a terminal batch can become a preset source through `Run again`, but that is not required for the foundation.

## Plugin UX

The plugin should show one claimed batch.

Main surface:

- batch status,
- line summary table,
- combined advisory plan,
- compact route controls,
- latest current-world/current-item action,
- latest purchase or stop classification.

Diagnostics window:

- route events,
- input capture,
- market-board listing reads,
- per-line candidate decisions,
- purchase attempts.

The plugin should not expose multiple accepted independent requests in the main surface.

## Migration Strategy

1. Add batch/line DTOs and storage while single-line requests still work as one-line batches.
2. Add a schema migration/backfill path for existing single-item rows:
   - pending rows become one-line batches,
   - claimed/accepted rows keep their claim state,
   - terminal rows remain inspectable,
   - idempotency and attempt/event rows continue to reference the same durable top-level id.
3. Change dashboard `Stage Queue` to send one batch payload.
4. Change plugin pickup to fetch and claim batches.
5. Change planning to aggregate all lines into one route.
6. Change route execution to iterate item subtasks at each world.
7. Add per-line and per-purchase audit projection.
8. Remove old single-request assumptions from UI labels and tests.

## Invariants

- One dashboard `Stage Queue` action creates one batch.
- A batch has one or more lines.
- A single-item request is a one-line batch.
- One plugin instance has at most one active attempt.
- A route restart creates a new attempt id under the same batch id.
- Progress events can identify a batch, attempt, line, route stop, and world.
- Purchases are tied to fresh live market-board rows.
- One failed line does not automatically fail the whole batch.
- Catastrophic/ambiguous UI or economic failures stop the batch.

## Open Follow-Up Decisions

- Should terminal batch history get a separate archive view before `Run again` exists?
- Should a future global batch gil cap exist? Recommended: defer until per-line behavior is stable.
- How much purchase audit history should be retained compared to the existing diagnostic event cap?
