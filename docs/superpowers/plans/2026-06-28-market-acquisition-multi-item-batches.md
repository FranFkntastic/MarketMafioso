# Market Acquisition Multi-Item Batch Implementation Plan

> **For agentic workers:** Implement this plan task-by-task with focused tests. Do not reintroduce the retired `/api/marketmafioso/...` route shape. Canonical dashboard routes live under `/marketmafioso/`; machine endpoints live under `/marketmafioso/api/...`.

**Goal:** Replace the current single-item acquisition request assumption with first-class multi-item batches. Dashboard `Stage Queue` should create one durable batch with one or more lines, the plugin should claim one batch, and the route runner should execute one combined route with per-line progress.

**Design source:** `docs/design/2026-06-28-market-acquisition-multi-item-batches.md`

**Related foundation:** `docs/design/2026-06-27-market-acquisition-run-model.md`

## Current Problem

The dashboard can stage several acquisition rows, but the server and plugin treat them as separate requests. The plugin accepts one request blindly, and accepting another can overwrite the previous local state. The route runner, progress reporting, live candidate planner, and purchase loop all assume one accepted item.

The desired behavior is a single combined purchase queue:

- one dashboard staging action,
- one claimed plugin-side unit of work,
- one route,
- multiple item lines,
- per-line outcomes.

## Implementation Strategy

Use an incremental migration:

1. Introduce batch/line contracts and storage while preserving one-line batch behavior.
2. Add explicit schema migration/backfill from existing single-item request rows.
3. Change the dashboard builder so queued rows stage one batch.
4. Change plugin pickup and local state to accept one batch.
5. Change planning to produce one combined advisory route.
6. Change route execution to process item subtasks per world.
7. Add per-line status/progress/audit visibility.
8. Remove old single-request assumptions once tests and UI have moved.

## Task 1: Add Batch And Line Contracts

**Files:**

- Create or modify `MarketMafioso.Server/MarketAcquisitionBatchModels.cs`
- Modify `MarketMafioso.Server/MarketAcquisitionModels.cs`
- Create or modify `MarketMafioso/MarketAcquisition/MarketAcquisitionBatchModels.cs`
- Modify dashboard API client models under `MarketMafioso.Dashboard`
- Tests:
  - `MarketMafioso.Server.Tests/MarketAcquisitionRequestEndpointTests.cs`
  - `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRequestClientTests.cs`

### Steps

- [ ] Add server DTOs:
  - `MarketAcquisitionBatchCreateRequest`
  - `MarketAcquisitionBatchLineCreateRequest`
  - `MarketAcquisitionBatchView`
  - `MarketAcquisitionBatchLineView`
  - `MarketAcquisitionBatchClaimView`
- [ ] Include line fields:
  - `lineId`
  - `itemId`
  - `itemName`
  - `itemKind`
  - `quantityMode`
  - `targetQuantity`
  - `maxQuantity`
  - `maxUnitPrice`
  - `gilCap`
  - `hqPolicy`
  - `status`
  - `purchasedQuantity`
  - `spentGil`
  - `latestMessage`
- [ ] Treat existing single-item request DTOs as one-line compatibility only while migration is underway.
- [ ] Define the old-to-new field mapping:
  - old request `quantity` -> line `targetQuantity` for `TargetQuantity`.
  - old request `quantity` -> line `maxQuantity` for `AllBelowThreshold`, with blank/zero meaning uncapped.
  - old request `maxTotalGil` -> line `gilCap`.
  - old request item fields -> line item fields.
- [ ] Add plugin-side mirrored DTOs for pickup, claim, accept/reject, progress, and planning.
- [ ] Add dashboard-side mirrored DTOs for create/list/detail.

### Focused Verification

```powershell
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisition" -v minimal
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisitionRequestClientTests" -v minimal
```

## Task 2: Add Server Storage And Endpoints For Batches

**Files:**

- Modify `MarketMafioso.Server/MarketAcquisitionRequestStore.cs`
- Modify `MarketMafioso.Server/Program.cs`
- Tests: `MarketMafioso.Server.Tests/MarketAcquisitionRequestEndpointTests.cs`

### Steps

- [ ] Add SQLite migration for `acquisition_batch_lines`.
- [ ] Keep the existing acquisition request table as the physical batch table for this migration.
  - Product/service language should call these batches.
  - Avoid a table copy unless the current table shape blocks implementation.
- [ ] Add explicit backfill logic in `MarketAcquisitionRequestStore` for existing DBs:
  - existing pending single-item rows get one line,
  - existing claimed/accepted rows get one line and keep claim metadata,
  - existing terminal rows get one line and remain inspectable,
  - existing idempotency rows and attempt/event rows continue to reference the same top-level id.
- [ ] Add `POST /marketmafioso/api/acquisition/batches`.
- [ ] Add `GET /marketmafioso/api/acquisition/batches`.
- [ ] Add pending/claim/accept/reject/progress/complete/fail paths under `/marketmafioso/api/acquisition/batches/{batchId}`.
- [ ] Make create atomic: either batch and all lines are persisted, or none are.
- [ ] Make claim atomic at the batch level.
- [ ] Ensure all lifecycle authorization uses the existing client API key and claim token model.
- [ ] Ensure dashboard mutations use cookie-backed auth and the existing dashboard auth model.
- [ ] Update server auth route classification for the new `/acquisition/batches` endpoints.
- [ ] Add local and hosted canonical path tests for `/marketmafioso/api/acquisition/batches`.
- [ ] Ensure old `/api/marketmafioso/...` routes are not restored.

### Tests To Add

- [ ] Creating a batch with two lines returns one `batchId` and two `lineId`s.
- [ ] Creating a batch with zero lines fails.
- [ ] Creating a batch with an invalid line fails the whole request.
- [ ] Pending pickup returns batches with line arrays.
- [ ] Claiming a batch returns all lines.
- [ ] A claimed batch stops appearing as pending.
- [ ] Single-line batch still works.
- [ ] Idempotent create returns the same batch.
- [ ] Same idempotency key with different body returns conflict.
- [ ] Existing single-item pending row is visible as a one-line batch after migration.
- [ ] Existing claimed/accepted row preserves claim state and line data after migration.
- [ ] Existing terminal row remains visible/inspectable after migration.
- [ ] Dashboard cookie auth can create/list browser batch data.
- [ ] Client API key can claim/accept/progress batch data.
- [ ] Client API key cannot perform browser-only dashboard mutations.
- [ ] Retired `/api/marketmafioso/...` routes still return not found.

### Focused Verification

```powershell
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug --filter "FullyQualifiedName~Batch|FullyQualifiedName~MarketAcquisition" -v minimal
```

## Task 3: Change Dashboard Stage Queue To Create One Batch

**Files:**

- Modify `MarketMafioso.Dashboard/Components/Acquisition/RequestBuilder.razor`
- Modify acquisition dashboard API client/service files
- Modify acquisition request grid/detail components

### Steps

- [ ] Change `_queue.Select(ToCreateRequest)` staging into one batch create payload.
- [ ] Preserve the current local queue builder UX.
- [ ] Normalize dashboard routing values before sending them to the API.
  - Existing UI value `CurrentWorld` must map to the planner/server value used by the backend, currently `CurrentWorldOnly`.
- [ ] Rename visible labels from request rows to batch/line language where useful.
- [ ] Keep the main table one row per batch.
- [ ] Add a batch detail expansion or drawer if an existing component already supports it.
- [ ] Show per-line summary:
  - item,
  - mode,
  - max unit,
  - cap,
  - purchased quantity,
  - status.
- [ ] Keep completed and failed batches visible for inspection; do not make terminal rows active pickup candidates.

### Focused Verification

```powershell
dotnet build "MarketMafioso.Dashboard/MarketMafioso.Dashboard.csproj" -c Debug
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug --filter "FullyQualifiedName~Dashboard|FullyQualifiedName~Batch" -v minimal
```

## Task 4: Change Plugin Pickup State To One Batch

**Files:**

- Modify `MarketMafioso/MarketAcquisition/MarketAcquisitionRequestClient.cs`
- Modify pickup state/config models
- Modify `MarketMafioso/Windows/MainWindow.cs`
- Tests:
  - `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRequestClientTests.cs`
  - focused pickup/persistence tests if present

### Steps

- [ ] Fetch pending batches instead of individual requests.
- [ ] Render pending batches with item-count and first few line names.
- [ ] Claim one selected batch.
- [ ] Persist accepted batch and line metadata across plugin reload.
- [ ] Ensure claiming a new batch cannot silently overwrite an accepted/running batch.
- [ ] Reconcile restored local state against the server:
  - old single-line accepted state restores as a one-line batch,
  - multi-line accepted state restores with all lines,
  - terminal server state clears active route/line state,
  - active local batch blocks claiming a different batch.
- [ ] Rename local fields only where doing so reduces confusion; avoid unnecessary mechanical churn in hot files.
- [ ] Keep one active attempt invariant.

### Tests To Add

- [ ] Client deserializes a pending batch with multiple lines.
- [ ] Client claims a batch and receives all lines.
- [ ] Local accepted state preserves all lines.
- [ ] Attempt start requires an accepted batch, not just a single item.
- [ ] Reload restores a multi-line accepted batch.
- [ ] Reload restores old single-line accepted state as a one-line batch.
- [ ] Claiming over an active accepted/running batch is rejected.
- [ ] Terminal server state clears local active line/route state.

### Focused Verification

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisitionRequestClientTests|FullyQualifiedName~MarketAcquisition" -v minimal
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
```

## Task 5: Build Combined Advisory Planning

**Files:**

- Modify `MarketMafioso/MarketAcquisition/MarketAcquisitionPlanner*`
- Modify `MarketMafioso/MarketAcquisition/MarketAcquisitionGuidedRouteSession.cs`
- Modify `MarketMafioso/MarketAcquisition/MarketAcquisitionRouteRunner.cs`
- Tests:
  - planner tests
  - route session tests

### Steps

- [ ] Produce remote candidates per line.
- [ ] Filter candidates using each line's max unit, HQ policy, optional max quantity, and optional gil cap.
- [ ] Group candidates by world.
- [ ] Add explicit route-stop/item-subtask models carrying:
  - `lineId`,
  - `itemId`,
  - `itemName`,
  - per-line remaining quantity,
  - per-line remaining gil cap,
  - advisory candidate listings for that line on that world.
- [ ] Route current world first if viable, then same data center, then other data centers, preserving economic ordering inside locality groups.
- [ ] Process item subtasks inside a world in dashboard queue order.
  - Do not compare raw gil prices across unrelated item lines.
  - Within one item line, continue to buy cheapest confirmed safe live listings first.
- [ ] Ensure route summaries show total planned quantities/gil plus line breakdowns.
- [ ] Preserve existing single-item planning behavior as a one-line batch.

### Tests To Add

- [ ] Two lines on the same world create one world stop with two item subtasks.
- [ ] Two lines across different data centers order current-world/current-DC candidates first.
- [ ] A line with no supported remote listings becomes a line-level skip, not a batch failure.
- [ ] `AllBelowThreshold` line with blank max quantity plans all safe stock.
- [ ] Single-line batch route matches current single-request route behavior.
- [ ] Live candidate planning validates the active line's item id and does not reject the batch because another line has a different item id.
- [ ] Dashboard route value `CurrentWorld` is normalized to the backend/planner value.

### Focused Verification

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~Planner|FullyQualifiedName~GuidedRouteSession|FullyQualifiedName~RouteRunner" -v minimal
```

## Task 6: Execute Item Subtasks At Each World

**Files:**

- Modify `MarketMafioso/MarketAcquisition/MarketAcquisitionRouteRunner.cs`
- Modify `MarketMafioso/MarketAcquisition/MarketBoardPurchaseExecutor.cs`
- Modify live candidate planner/presenter files as needed
- Modify diagnostics window if needed
- Tests:
  - route runner tests
  - purchase executor tests
  - live candidate planner tests

### Steps

- [ ] At a world stop, iterate item subtasks instead of assuming one accepted item.
- [ ] For each item subtask:
  - search item,
  - select exact item by item id,
  - read listings,
  - evaluate live candidates for that line,
  - purchase safe candidates until complete/exhausted/stopped.
- [ ] Treat listing window closure after last listing as normal and continue to next item subtask.
- [ ] Re-read listings after each purchase.
- [ ] Update line purchased quantity/spent gil locally.
- [ ] Continue other lines after non-catastrophic line exhaustion.
- [ ] Stop whole batch on catastrophic/ambiguous states.
- [ ] Reset item-search/listing addon assumptions between item subtasks so stale listing state from the previous item cannot be reused.

### Tests To Add

- [ ] A completed first item subtask advances to the second item on the same world.
- [ ] No live stock for one line skips that line and continues another line.
- [ ] Listing window closing after last listing advances instead of failing the route.
- [ ] Wrong item result stops the route.
- [ ] Inventory full/insufficient gil stops the route.
- [ ] Single-line behavior remains unchanged.
- [ ] After finishing item A on a world, item B search cannot accidentally reuse item A live candidates.

### Focused Verification

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~RouteRunner|FullyQualifiedName~Purchase|FullyQualifiedName~LiveCandidate" -v minimal
dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug
```

## Task 7: Add Per-Line Progress, Audit, And Dashboard Projection

**Files:**

- Modify server attempt/progress models
- Modify `MarketMafioso.Server/MarketAcquisitionRequestStore.cs`
- Modify dashboard request grid/details components
- Modify plugin route progress reporter
- Tests:
  - server endpoint tests
  - plugin progress reporter tests

### Steps

- [ ] Include optional `lineId` in attempt events.
- [ ] Add line status updates to progress/complete/fail event payloads.
- [ ] Add purchase audit rows or extend existing purchase attempt rows with `lineId`.
- [ ] Validate `lineId` on the server:
  - unknown line ids are rejected,
  - line ids belonging to another batch are rejected,
  - idempotent replay of the same line event is accepted,
  - same idempotency key or sequence with a different line payload is a real conflict.
- [ ] Project batch status from line statuses and attempt terminal state.
- [ ] Project per-line status to the dashboard details view.
- [ ] Keep main dashboard table compact.
- [ ] Add diagnostics filters by `batchId`, `attemptId`, and `lineId` if the diagnostics view already has a place for it.

### Tests To Add

- [ ] Progress event can update one line without changing another line.
- [ ] Batch becomes `PartialComplete` when one line completes and one line under-procures.
- [ ] Batch becomes `Complete` when all lines complete.
- [ ] Purchase audit stores `lineId`.
- [ ] Dashboard API returns per-line status.
- [ ] Progress for an unknown line id fails explicitly.
- [ ] Progress for a line id from another batch fails explicitly.
- [ ] Idempotent replay of the same line event returns the same result.
- [ ] Stale attempt line events after a newer attempt are classified and do not poison the latest projection.

### Focused Verification

```powershell
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug --filter "FullyQualifiedName~Batch|FullyQualifiedName~Line|FullyQualifiedName~Attempt" -v minimal
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisitionRouteProgressReporterTests" -v minimal
```

## Task 8: Cleanup Old Single-Request Assumptions

**Files:**

- Search scope: `MarketMafioso/MarketAcquisition`, `MarketMafioso/Windows/MainWindow.cs`, `MarketMafioso.Server`, `MarketMafioso.Dashboard`
- Docs:
  - `docs/design/2026-06-25-market-acquisition-roadmap.md`
  - `docs/design/2026-06-27-market-acquisition-run-model.md` if terminology needs a note

### Steps

- [ ] Search for stale labels and field assumptions:
  - `AcceptedRequest`
  - `RequestId`
  - `ItemId`
  - `ItemName`
  - `TargetQuantity`
  - `CurrentRequest`
- [ ] Rename only when the old name causes real ambiguity.
- [ ] Remove old code paths that can stage multiple independent requests from one dashboard queue.
- [ ] Ensure no plugin path can accept a second batch over an active accepted/running batch.
- [ ] Update roadmap progress and mark multi-item batches as the active acquisition track.

### Verification

```powershell
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisition" -v minimal
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisition" -v minimal
dotnet build "MarketMafioso.sln" -c Debug
dotnet format "MarketMafioso.sln" --verify-no-changes
```

## Manual Smoke Test

After implementation and focused automated tests:

1. Deploy server dev:
   ```powershell
   .\MarketMafioso\tools\Deploy-ServerDev.ps1 -Ref main -TimeoutSeconds 900
   ```
2. Deploy dev plugin:
   ```powershell
   & "MarketMafioso/tools/Deploy-DevPlugin.ps1"
   ```
3. In the dashboard, queue two low-value items and stage once.
4. Confirm the dashboard shows one batch with two lines.
5. In `/mmf`, fetch and claim the batch.
6. Confirm the plugin shows one claimed batch with both lines.
7. Prepare plan and confirm route groups by world with item subtasks.
8. Start route with cheap safety thresholds.
9. Confirm one world stop can process both items before travel.
10. Confirm dashboard line statuses update independently.

## Plan Review Checklist

- [ ] Does any task allow multiple active plugin attempts? It should not.
- [ ] Does any task preserve the old `Stage Queue` creates N independent requests behavior? It should not.
- [ ] Is every route/purchase event able to identify batch, attempt, and line where relevant?
- [ ] Can one line fail or under-procure without incorrectly failing the whole batch?
- [ ] Do server tests prove atomic multi-line create and claim?
- [ ] Do plugin tests prove single-line behavior still works as a one-line batch?
- [ ] Are route changes tested at the world-stop/item-subtask boundary?
- [ ] Are dashboard changes scoped to batch rows plus line details, not a massive UX rewrite?
