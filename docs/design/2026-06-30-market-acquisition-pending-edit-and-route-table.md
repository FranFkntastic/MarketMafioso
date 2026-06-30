# Market Acquisition Pending Edit And Route Table Pass

## Goal

Make the dashboard request workflow forgiving enough for real use, and make the in-game guided route table explain multi-item route state instead of flattening useful detail away.

This pass bundles two closely related UX fixes:

- Dashboard-side pending batch modification: add lines to an unclaimed staged batch, allow arbitrary pickup expiry, and prove staged batches survive reload.
- Plugin-side guided world route display: replace the current flattened route table with expandable per-world rows that expose per-item planned and discovered state.

## Current Problems

### Pending Request Editing

The dashboard can stage a multi-line acquisition batch, but once staged it behaves like a sealed request. If the user forgets one item, the practical recovery path is cancel and recreate. That is too punishing for a dashboard-driven workflow.

The current pickup expiry control is also too rigid. The dashboard offers a short preset list while the server store currently clamps batch pickup expiry to a narrow maximum. This makes longer intentional pickup windows look configurable while not necessarily being honored consistently.

Staged requests should be durable. A staged request is server state, not transient browser state. Browser reload, dashboard reconnect, and server process restart should not lose pending batches before expiry.

### Guided Route Table

The current in-game guided world route table is too compressed for multi-item routes:

- `Items` is only a count, so the user cannot see which items apply to a world.
- `Live Candidate` is useful as debug state, but too noisy for day-to-day routing.
- `Live` is semantically closer to `Discovered`, but it is currently flattened across all items.
- Opportunistic checks make the item count less meaningful because a world can produce discoveries for items beyond the strict route stop expectation.
- The table does not show planned/expected versus live/discovered quantity and gil per item.

The table should answer, at a glance:

- Which world is next?
- Which items are planned on that world?
- Which items have been discovered live on that world?
- Which item is currently being purchased?
- Which item was skipped, exhausted, or completed?

## Design Decisions

### Pending Batches Are Mutable Only Before Claim

Only `PendingPickup` batches may be modified from the dashboard.

Once a plugin claims a batch, the plugin owns a local copy of the route input. Mutating the server-side batch after claim would create a hidden split-brain state where the dashboard and plugin disagree about what should be executed.

Allowed:

- Append lines to a `PendingPickup` batch.
- Coalesce exact duplicate lines in a `PendingPickup` batch.
- Extend pickup expiry for a `PendingPickup` batch.

Rejected:

- Editing `Claimed`, `AcceptedInPlugin`, `Running`, `Complete`, `Failed`, `Rejected`, `Expired`, or `Cancelled` batches.
- Shortening expiry by accidental merge.
- Rewriting historical lifecycle or attempt events.

For non-pending batches, the dashboard should offer template reuse or clone-and-stage behavior instead of in-place mutation.

### Mutable Line Truth Lives In Batch Lines

Do not rewrite the original acquisition request `payload_json` during append operations. That payload is part of create-time idempotency behavior.

After creation, line truth should come from `acquisition_batch_lines`, lifecycle events, and attempt/purchase audit state. The original payload remains useful as a creation snapshot.

### Exact Duplicate Lines Coalesce

When appending to a pending batch, exact duplicates should merge instead of producing confusing duplicate buy lines.

Coalesce when all of these match:

- item id
- quantity mode
- HQ policy
- max unit price
- gil cap

For `TargetQuantity`, add target quantities.

For `AllBelowThreshold`, add max quantities only when both sides are capped. If either side is uncapped, keep the resulting line uncapped.

Keep separate lines when any rule differs. Same item with different max price or HQ policy is a distinct intent.

### Optimistic Concurrency

Add a request `Revision` value and require dashboard mutations to send `expectedRevision`.

If the request is claimed, expired, cancelled, or otherwise changed between render and mutation, the server returns `409 Conflict` with a specific error code. The dashboard refreshes and explains that the batch changed before the merge could be applied.

This is intentionally simple. It prevents silent lost updates without introducing a larger collaboration model.

### Pickup Expiry Is Custom But Bounded

Replace fixed expiry-only dropdown behavior with custom duration support.

Recommended UI:

- quick choices: `5m`, `15m`, `30m`, `1h`
- custom numeric value
- unit selector: minutes or hours

Server bounds:

- minimum: `MarketMafioso:AcquisitionMinimumExpirySeconds`
- maximum: `MarketMafioso:AcquisitionMaximumExpirySeconds`
- default maximum: 24 hours

The dashboard and server should share the same effective bounds semantically. The server remains authoritative.

When appending lines to a pending batch, preserve the existing expiry unless the new requested pickup window would extend it. Do not shorten an existing pending batch accidentally.

### Staged Request Persistence

Pending acquisition batches must persist through:

- dashboard reload
- browser reconnect
- server process restart

Persistence source of truth:

- `acquisition_requests`
- `acquisition_batch_lines`
- request lifecycle events

The dashboard may keep a local unstaged composer queue later, but that is not part of this slice. This slice is about already-staged server state.

### Expandable Guided Route Rows

The guided route table should keep one row per world by default, then expand to show per-item detail.

Default world row columns:

- World
- Data Center
- Status
- Planned
- Discovered
- Purchasing
- Result

Recommended labels:

- `Planned`: total planned quantity and gil across all items for the world.
- `Discovered`: live quantity and gil discovered across all checked items for the world.
- `Purchasing`: the active item name and current purchase summary, or `-`.
- `Result`: compact route state such as `Ready`, `Purchasing`, `Complete`, `No safe listings`, `Skipped`, or `Failed`.

Expanded row detail should show one line per item on that world:

- Item
- Planned quantity / gil
- Discovered quantity / gil
- Bought quantity / gil
- Status
- Latest note

`Live Candidate` should move to diagnostics or be represented as a compact status in the expanded detail. It should not remain a primary table column.

### Expansion Behavior

The UI should support:

- Click or small disclosure arrow to expand a world row.
- Auto-expand the current active world while the route is running.
- Keep manually expanded rows open until route reset or window close.
- Collapse completed worlds by default, unless they failed or were manually opened.

If ImGui table nesting becomes too busy, the first implementation can draw expanded details as indented rows directly under the world row rather than as a nested table.

## Implementation Plan

### Task 1: Server Pending Batch Append Contract

Files:

- Modify `MarketMafioso.Server/MarketAcquisitionModels.cs`
- Modify `MarketMafioso.Server/MarketAcquisitionRequestStore.cs`
- Modify `MarketMafioso.Server/Program.cs`
- Test `MarketMafioso.Server.Tests/MarketAcquisitionRequestEndpointTests.cs`
- Test `MarketMafioso.Server.Tests/MarketAcquisitionRequestStoreTests.cs`

Steps:

- Add `Revision` to `MarketAcquisitionRequestView` and `MarketAcquisitionClaimView`.
- Add `MarketAcquisitionBatchAppendLinesRequest` with `ExpectedRevision`, `ExpiresInSeconds`, and `Lines`.
- Add `AppendLinesAsync` to the store.
- Reject append unless status is `PendingPickup`.
- Coalesce exact duplicate lines.
- Increment request revision.
- Insert a lifecycle event such as `lines_appended`.
- Add `POST /api/acquisition/batches/{id}/lines`.
- Add endpoint tests for success, claimed rejection, stale revision rejection, duplicate coalescing, and returned line count.

### Task 2: Server Expiry Bounds And Persistence Tests

Files:

- Modify `MarketMafioso.Server/MarketAcquisitionRequestStore.cs`
- Modify `MarketMafioso.Server/Program.cs`
- Test `MarketMafioso.Server.Tests/DashboardSettingsEndpointTests.cs`
- Test `MarketMafioso.Server.Tests/MarketAcquisitionRequestStoreTests.cs`

Steps:

- Replace hardcoded `300` second create/resend clamps with configurable maximum expiry.
- Keep minimum expiry configurable through existing minimum setting.
- Add `MarketMafioso:AcquisitionMaximumExpirySeconds`, defaulting to 86400.
- Align dashboard settings validation with the same effective bounds.
- Add tests proving custom expiry over 5 minutes is honored.
- Add tests proving pending batches and appended lines survive store recreation.
- Add tests proving expiry happens only after the configured expiry.

### Task 3: Dashboard Merge And Custom Expiry UI

Files:

- Modify `MarketMafioso.Dashboard/Models/DashboardModels.cs`
- Modify `MarketMafioso.Dashboard/Services/DashboardApiClient.cs`
- Modify `MarketMafioso.Dashboard/Services/AcquisitionDashboardState.cs`
- Modify `MarketMafioso.Dashboard/Components/Acquisition/RequestBuilder.razor`
- Modify `MarketMafioso.Dashboard/Components/Acquisition/ServerRequestGrid.razor`
- Modify `MarketMafioso.Dashboard/Components/Acquisition/RequestDetailsDrawer.razor`

Steps:

- Add dashboard model types for append-line requests and revised request views.
- Add API client method for `POST api/acquisition/batches/{id}/lines`.
- Add state method `AppendToPendingBatchAsync`.
- Detect eligible pending batches matching character, world, region, routing, sweep scope, and sweep data centers.
- Add `Add to Pending Batch` action when the local queue has lines and an eligible pending batch exists.
- If several eligible pending batches exist, require the user to pick one.
- Replace fixed pickup expiry dropdown with preset/custom duration controls.
- On append success, clear local queue, refresh requests, select the updated batch, and show a concise success message.
- On stale/claimed conflict, refresh requests and show a clear non-scary warning.

### Task 4: Guided Route Row Model

Files:

- Modify or create route display model under `MarketMafioso/MarketAcquisition/`
- Modify `MarketMafioso/Windows/MainWindow.cs`
- Test `MarketMafioso.Tests/MarketAcquisition/`

Steps:

- Add a route display summary model with one row per world.
- Add per-item child rows containing planned, discovered, bought, status, and latest note.
- Populate the model from the existing route runner state without changing route execution behavior.
- Preserve diagnostic-only values for logs/diagnostics, but keep them out of the primary table unless expanded.
- Add unit tests for multi-item world summary aggregation.

### Task 5: Guided Route Table Rendering

Files:

- Modify `MarketMafioso/Windows/MainWindow.cs`
- Optionally create `MarketMafioso/Windows/GuidedRouteTableRenderer.cs` if the rendering block becomes too large.

Steps:

- Replace the current flattened guided route table columns with the world row columns.
- Add disclosure controls for expandable rows.
- Auto-expand the active world.
- Render per-item detail rows indented under the world row.
- Rename `Live` to `Discovered`.
- Move `Live Candidate` to diagnostics or expanded row latest-note text.
- Keep column sizing dense enough for Dalamud window use.

### Task 6: Verification

Commands:

- `dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug -v minimal`
- `dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug -v minimal --filter "FullyQualifiedName~MarketAcquisition"`
- `dotnet build "MarketMafioso.sln" -c Debug`

Manual checks:

- Stage a one-line batch, reload dashboard, confirm it persists.
- Add a second line to the pending batch, refresh dashboard, confirm both lines render.
- Fetch from plugin after merge, confirm plugin sees both lines.
- Start a multi-item route and confirm the active world auto-expands.
- Confirm planned versus discovered values are visible per item.
- Confirm claimed/running batches reject dashboard merge attempts with a clear message.

## Non-Goals

- Editing claimed or running batches in place.
- Persisting the unstaged local composer queue.
- Reworking the route runner execution model.
- Changing purchase safety behavior.
- Replacing diagnostics tables in this pass.

## Open Follow-Ups

- Add full pending-batch edit/remove/reorder support if append-only merge proves insufficient.
- Add clone-from-terminal request presets after the pending mutation path is stable.
- Consider moving route table rendering into a dedicated ImGui component if `MainWindow` continues to grow.
