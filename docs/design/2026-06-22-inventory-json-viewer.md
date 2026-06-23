# Inventory JSON Viewer Implementation Plan

## Goal

Build a first-pass inventory snapshot viewer into the local MarketMafioso server panel. The viewer should parse stored inventory JSON into readable player-inventory and retainer-inventory sections while keeping the parsing and presentation layers easy to extract later.

This is not a market/undercut feature. It stays within the current inventory reporting baseline: receive snapshots, store them locally, and make their contents easier to inspect.

## Current State

- The plugin builds an `InventoryReport` from live player inventory plus cached retainer inventory.
- `HttpReporter` sends that report as JSON to the configured server URL.
- The local server receives `POST /inventory` and `POST /api/inventory`.
- `InventoryReportStore` wraps each report as a `StoredInventoryReport`, writes it to `MarketMafioso.Server/data/reports/`, and stores a summary beside the raw report.
- The dashboard at `/` lists stored snapshots.
- The detail page at `/reports/{id}` currently shows summary metadata and raw formatted JSON.
- Raw API access remains available at `/api/reports/{id}`.

## Product Shape

The first viewer should make one stored snapshot readable without trying to become a full inventory management tool.

The detail page should show:

- Snapshot header: character, world, received time, report timestamp.
- Summary totals: player stacks/items, retainer stacks/items, retainer count.
- Player inventory grouped by bag.
- Retainer inventory grouped by retainer, then bag.
- Item rows with item name, item id, quantity, HQ, and condition.
- Raw JSON still available for debugging.

Search, sorting, item aggregation, and export are useful future steps, but they should not be required for the first slice.

## Recommended Architecture

Keep three separate layers on the server side.

### Stored Report

Continue using `StoredInventoryReport` as the persistence shape. Do not rewrite existing stored JSON files for this slice.

### Parsed Viewer Model

Add an internal server-side model such as `InventorySnapshotView`. This model should be built from `StoredInventoryReport` and should contain only display-ready inventory structure:

- Snapshot identity and timestamps.
- Character and world.
- Summary totals.
- Player sections.
- Retainer sections.
- A flattened item list if it naturally falls out of the builder, but do not force search/sort behavior into the first pass.

The viewer model should be independent of HTML so it can be returned from an API endpoint and eventually reused by an extracted viewer.

### Rendering

Update `/reports/{id}` to render from `InventorySnapshotView` instead of directly serializing the stored report.

The HTML can remain server-rendered for now. Keep it plain and local-tool focused: dense tables, compact sections, clear empty states, no decorative layout.

## Server API Plan

Add:

```text
GET /api/reports/{id}/view
```

This endpoint should return the parsed `InventorySnapshotView`.

Keep:

```text
GET /api/reports/{id}
```

This remains the raw stored report endpoint.

This split gives us an extraction bridge: a future standalone viewer can consume `/api/reports/{id}/view` without knowing about `StoredInventoryReport` persistence details.

## Plugin Contract Plan

The plugin should remain responsible for reporting facts, not presentation.

For this slice, avoid reshaping inventory data in the plugin unless needed. The current item fields are enough for a useful viewer:

- `itemId`
- `itemName`
- `quantity`
- `isHQ`
- `condition`

Add lightweight metadata to harden the JSON contract:

- `schemaVersion`
- `sourcePlugin`
- `pluginVersion`
- `generatedAtUtc`

The server should expose metadata in the parsed view header. Existing stored snapshots without metadata should still be viewable and should display unknown metadata values, but newly emitted snapshots should use the canonical metadata going forward.

## First Implementation Slice

1. Add server viewer models:
   - `InventorySnapshotView`
   - `InventorySectionView`
   - `InventoryBagView`
   - `InventoryItemView`

2. Add an `InventorySnapshotViewBuilder`:
   - Input: `StoredInventoryReport`.
   - Output: `InventorySnapshotView`.
   - Compute totals from the actual item rows.
   - Preserve empty player/retainer sections as explicit empty states.

3. Add `/api/reports/{id}/view`:
   - Return `404` when the stored report does not exist.
   - Return the parsed viewer model otherwise.

4. Update `/reports/{id}`:
   - Header and summary cards.
   - Player inventory table grouped by bag.
   - Retainer sections grouped by retainer and bag.
   - Link to raw JSON.
   - Optionally keep a collapsed or lower-page raw JSON block.

5. Add tests:
   - Builder turns sample JSON into expected player and retainer sections.
   - Builder computes totals from rows.
   - Empty inventory produces explicit empty sections.
   - `/api/reports/{id}/view` returns parsed data for a stored report.

6. Run verification:
   - `dotnet build "MarketMafioso.sln" -c Debug`
   - `dotnet format "MarketMafioso.sln" --verify-no-changes`
   - `dotnet test "MarketMafioso.sln" -v minimal`

## Testing Notes

The current solution has no dedicated test project. This feature is a good point to add a small server test project because the parsing layer is pure server logic and does not depend on Dalamud.

Prefer tests around the view builder before rendering polish. HTML rendering can be smoke-tested with the existing sample JSON and the local server.

## Risks and Boundaries

- Retainer freshness remains a plugin/cache limitation. The viewer should show what the snapshot contains, not imply it is live game state.
- Do not introduce market board, undercut, or pricing concepts in this slice.
- Do not make the plugin responsible for UI grouping or sorting.
- Avoid a frontend framework for now. Server-rendered HTML is enough until the viewer requirements prove otherwise.
- Keep raw JSON access visible because it is the best diagnostic view when the parsed display looks wrong.

## Review Questions

- Should metadata hardening be included in the first slice, or should the first slice stay server-only?
- Should raw JSON remain inline on `/reports/{id}`, or should it only be linked via `/api/reports/{id}`?
- Should item rows be grouped exactly by source bag first, or should the first viewer also include an aggregate item summary?
