# Craft Architect Watched Inventory Companion Design

## Context

InventoryReporter is a Dalamud API 15 plugin that can read player inventory containers, cache retainers when their inventory windows close, and POST a JSON report to a configured URL. FFXIV Craft Architect is a separate planning utility with a Blazor WebAssembly app, shared Core services, and procurement workflows based on active procurement demand.

The first integration should be opt-in and focused. The goal is not to add a broad inventory browser to FFXIV Craft Architect. The useful feature is a watched item overlay: when a user starts a craft plan, they can choose specific materials or outputs to watch and see how many they currently own while they craft, gather, buy, or retrieve items from retainers.

## Decision

Keep InventoryReporter as a separate companion project. Do not merge Dalamud dependencies into FFXIV Craft Architect.

The two projects should collaborate through a versioned inventory snapshot contract and a local bridge/import boundary:

1. InventoryReporter captures live inventory and retainer snapshots in game.
2. A local companion bridge receives automatic POSTs from the plugin.
3. FFXIV Craft Architect Web imports or polls the bridge for the latest snapshot.
4. Craft Architect Core compares that snapshot against an explicit watched item list.

The first implementation should be a hybrid of automatic local posting and manual import:

- Automatic path: plugin POSTs to a localhost bridge endpoint.
- Manual fallback: Craft Architect accepts pasted or file-loaded snapshot JSON using the same contract.

This keeps the automatic path ergonomic while preserving a debug path when the bridge is not running.

## Non-Goals

- No full inventory viewer in the first feature.
- No hosted remote inventory storage in the first feature.
- No silent mutation of procurement quantities by default.
- No direct browser inbound HTTP endpoint. The Blazor WebAssembly app cannot listen for plugin POSTs by itself, especially when hosted as a static site.
- No Craft Architect dependency on Dalamud packages.

## Architecture

### InventoryReporter Plugin

Responsibilities:

- Scan configured player inventory sources.
- Cache retainer inventory when retainer windows close.
- POST a complete snapshot to the configured bridge URL.
- Expose manual send from `/invreport send`.
- Keep all game-process and Dalamud-specific logic inside the plugin repo.

Required improvements before Craft Architect consumes it:

- Add a `schemaVersion` field.
- Add a `snapshotId` field.
- Add `capturedAtUtc`.
- Include character identity: character name, home world, and optional content ID if safe and appropriate.
- Preserve item location/source details instead of only aggregated bags.
- Correctly report HQ state. The current scanner always emits `isHQ = false`.
- Include retainer cache freshness per retainer.

### Local Bridge

Responsibilities:

- Listen on localhost for InventoryReporter POSTs.
- Validate snapshot schema and basic payload size.
- Store the latest snapshot per character/world.
- Serve the latest snapshot to Craft Architect Web.
- Support CORS only for known local Craft Architect origins.

Recommended first shape:

- A tiny local .NET process, separate from both apps.
- Endpoints:
  - `POST /inventory/snapshots`
  - `GET /inventory/snapshots/latest`
  - `GET /health`
- Default bind: `http://127.0.0.1:37145`.

The bridge can later move into a Craft Architect desktop companion, tray app, or optional hosted service, but it should begin as a small local adapter.

### FFXIV Craft Architect Core

Add Core-only models and services so both Web and any future WPF path can share the behavior.

Suggested models:

- `InventorySnapshot`
- `InventorySnapshotItem`
- `InventoryItemLocation`
- `WatchedInventoryItem`
- `WatchedInventoryCoverage`

Suggested service:

- `InventoryCoverageService`

Responsibilities:

- Normalize inventory snapshot items by item ID and HQ state.
- Compare normalized inventory against a watched item list.
- Report owned quantity, required quantity, remaining quantity, and stale data warnings.

The watched item list should be explicit. It can be seeded from active procurement items, but it should not automatically include every material forever.

### FFXIV Craft Architect Web

Responsibilities:

- Let the user opt in to inventory companion features.
- Allow importing a snapshot manually from JSON.
- Allow connecting to the local bridge and polling for latest snapshot.
- Let the user choose watched items.
- Show watched coverage without turning the procurement screen into an inventory dump.

Recommended first UI behavior:

1. Add an optional "Inventory Watch" panel or tab.
2. Let users seed watched items from the current active procurement list.
3. Let users add/remove watched items manually.
4. Display only watched rows:
   - item name
   - needed quantity when tied to active procurement
   - owned quantity
   - remaining quantity
   - source summary such as "Inventory 12, Retainer 8"
   - snapshot age
5. Keep "deduct owned inventory from procurement" as a separate explicit toggle or later feature.

## Data Contract Draft

```json
{
  "schemaVersion": 1,
  "snapshotId": "2026-06-12T03:20:00Z:Example:Adamantoise",
  "capturedAtUtc": "2026-06-12T03:20:00Z",
  "character": {
    "name": "Example Name",
    "homeWorld": "Adamantoise"
  },
  "items": [
    {
      "itemId": 36224,
      "itemName": "Immutable Solution",
      "quantity": 12,
      "isHq": false,
      "locationType": "Inventory",
      "locationName": "Inventory1"
    },
    {
      "itemId": 36224,
      "itemName": "Immutable Solution",
      "quantity": 8,
      "isHq": false,
      "locationType": "Retainer",
      "locationName": "Retainer Name",
      "retainerLastUpdatedUtc": "2026-06-12T03:18:10Z"
    }
  ]
}
```

Craft Architect should treat `itemId`, `quantity`, `isHq`, and source freshness as authoritative. Item names are helpful for diagnostics but should not be required for matching.

## Watched Item Flow

1. User opens a Craft Architect plan.
2. User opts into Inventory Watch.
3. User clicks "Seed from active procurement" or manually adds items.
4. InventoryReporter sends snapshots to the bridge automatically.
5. Craft Architect Web polls or refreshes from the bridge.
6. `InventoryCoverageService` computes watched coverage.
7. UI displays only watched items and warnings.

If the bridge is offline, the UI should explain the missing connection and offer manual JSON import. It should not fail the recipe planner, market analysis, or procurement workflows.

## Error Handling

- Invalid snapshot JSON: reject with a clear validation error.
- Unknown schema version: reject and show upgrade guidance.
- Bridge unreachable: show a non-blocking warning and keep the last imported snapshot.
- Character/world mismatch: show a warning and require user confirmation before applying the snapshot.
- Stale retainer cache: show source-level age warnings.
- Oversized payload: reject at the bridge with a documented size limit.

## Privacy And Safety

Inventory data is personal gameplay data. The first implementation should default to localhost only.

Recommended defaults:

- Bridge binds to `127.0.0.1`, not all interfaces.
- No remote server URL is preconfigured except localhost.
- API key support remains optional for local use.
- Craft Architect stores only the latest imported snapshot unless the user explicitly saves history.
- The user can clear imported inventory data from Craft Architect.

## Testing Strategy

InventoryReporter:

- Unit-test payload normalization if scanner logic is factored away from Dalamud pointer access.
- Add a regression test or isolated helper proof that HQ detection is not hard-coded false.

Bridge:

- Test valid snapshot POST and latest snapshot GET.
- Test invalid schema rejection.
- Test CORS origin behavior.
- Test per-character latest snapshot selection.

Craft Architect Core:

- Test watched coverage for exact quantity matches.
- Test partial owned quantities.
- Test HQ and NQ separation.
- Test stale retainer warnings.
- Test active procurement seed list creation.

Craft Architect Web:

- Test manual JSON import state updates.
- Test bridge-unavailable UI state.
- Test watched panel only renders watched items.
- Test procurement quantities remain unchanged unless the explicit deduction toggle is enabled.

## Suggested Milestones

1. Define shared snapshot contract and manual import in Craft Architect.
2. Add watched item Core coverage service and focused tests.
3. Add Web Inventory Watch panel using manual JSON import.
4. Fix InventoryReporter payload gaps: schema version, location details, HQ state, freshness fields.
5. Add local bridge and automatic POST/poll loop.
6. Add optional "deduct watched owned inventory from procurement" once coverage behavior is trusted.

## Open Questions

- Should watched items be stored per plan, per browser profile, or globally?
- Should output items be watchable alongside materials?
- Should retainer counts be included by default when stale, or displayed separately until refreshed?
- Should the bridge live in its own repo, the InventoryReporter repo, or the FFXIV Craft Architect repo?
