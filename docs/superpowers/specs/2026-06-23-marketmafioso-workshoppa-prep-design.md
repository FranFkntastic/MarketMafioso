# MarketMafioso Workshoppa Prep Design

## Goal

Add a MarketMafioso-owned workshop preparation module that helps the player prepare Free Company workshop project queues before handing them to VIWI Workshoppa. MarketMafioso is the source of truth for the prep queue, material demand, retainer availability, and retainer withdrawal flow. VIWI remains the execution tool for workshop automation.

## Product Boundary

MarketMafioso is the quartermaster. It can decide what materials are needed, where those materials are stored, and retrieve missing materials from retainers. It can optionally send the prepared queue to VIWI Workshoppa through VIWI's public IPC.

Workshoppa remains the foreman. MarketMafioso must not start workshop crafting, interact with fabrication stations, advance phases, discontinue projects, run Grindstone leveling, or track Workshoppa's active project state.

This avoids building a parallel Workshoppa module while still giving MarketMafioso a useful inventory-driven companion feature.

## Existing Source Evidence

MarketMafioso already owns the right foundation:

- `Configuration.RetainerCache` stores cached retainer inventories by retainer id.
- `RetainerCacheManager` snapshots retainer inventory on `InventoryRetainerLarge` / `InventoryRetainer` close.
- `AutoRetainerRefreshService` can ask AutoRetainer to cycle retainers and refresh the cache.
- `InventoryScanner` reads player inventory and current retainer inventory containers.
- `docs/design/ui-automation-rules.md` already requires concrete UI-state checks before retainer automation actions.
- Current retainer cache data is aggregate-oriented. `InventoryScanner.ScanCurrentRetainer()` merges `RetainerPage1..7` into one `RetainerInventory` bag by item id, and `CachedItem` does not store page or slot identity.

VIWI Workshoppa currently exposes a narrow public IPC surface:

- `VIWI.Workshoppa.AddQueueItem`, accepting `workshopItemId` and `quantity`.
- `VIWI.Workshoppa.ClearQueue`.

These names were verified from VIWI source on 2026-06-23 at commit `b03d625`, in `VIWI.Core/IPC/WorkshoppaIPC.cs`. The implementation plan must re-verify the current source or installed plugin before relying on them.

Because that IPC does not expose VIWI's current queue or material demand, MarketMafioso should not try to read VIWI queue state. The MarketMafioso prep queue is local and explicit. MarketMafioso must not add assembly references to VIWI, use service-provider lookups for VIWI internals, reflect into VIWI objects, or call non-public VIWI IPC for this feature.

## User Flow

1. The user opens `/mmf` and switches to a new `Workshop Prep` tab.
2. The user adds workshop projects and quantities to the MarketMafioso prep queue.
3. MarketMafioso computes all direct company-craft turn-in materials needed for that queue.
4. MarketMafioso compares material demand against current player inventory and cached retainer inventory.
5. If cache data is missing or stale, the user can refresh retainer cache with the existing AutoRetainer-powered refresh.
6. The user presses `Restock Materials From Retainers`.
7. MarketMafioso withdraws missing direct turn-in materials from retainers.
8. After restock, the user may press `Send Queue To VIWI` to clear and repopulate VIWI Workshoppa's queue through public IPC.
9. The user starts Workshoppa from VIWI when ready.

## Scope

In scope:

- A MarketMafioso-owned workshop prep queue.
- Adding/removing company workshop projects and quantities.
- Computing direct workshop material totals from Lumina company-craft sheets.
- Showing player inventory, retainer cache, total available, required amount, and shortage per material.
- Refreshing retainer cache through the existing AutoRetainer refresh path.
- Withdrawing missing direct workshop turn-in materials from retainers.
- Optional explicit queue handoff to VIWI via public IPC.
- Phase-specific status and errors for withdrawal automation.

Out of scope:

- Reading VIWI's internal queue.
- Reflecting into VIWI internals.
- Automating fabrication station interactions.
- Starting or pausing Workshoppa.
- Tracking VIWI's current project or current phase.
- Supporting recursive crafted sub-material restock in the first version.
- Market board buying, vendor buying, crafting subcomponents, or gathering support.

## Product Guidance Update

The repository guidance says to persist only plugin configuration/user options and cached retainer inventory between sessions. A saved workshop prep queue is intentional user configuration for this feature, not active Workshoppa execution state. It belongs in MarketMafioso config because it represents the user's desired preparation list and lets them return to a material-prep workflow across game sessions.

The implementation must not persist transient withdrawal progress, active retainer UI state, selected VIWI state, or inferred Workshoppa project state.

## Architecture

### Workshop Project Catalog

Add a service that loads company workshop project definitions from Lumina:

- Read `CompanyCraftSequence` for workshop project rows.
- Resolve project display name, result item id, category, type, and icon.
- Walk each part/process/phase to direct `CompanyCraftSupplyItem` requirements.
- Aggregate each material by item id for a queued project quantity.

This should be independent from UI and retainer automation so it can be tested with a thin deterministic model later if needed.

### Prep Queue

Add persisted configuration for MarketMafioso's prep queue:

- `WorkshopPrepQueue`: list of `WorkshopPrepQueueItem`.
- Each item stores `WorkshopItemId` and `Quantity`.
- The queue belongs to MarketMafioso even if it later gets sent to VIWI.

Do not persist active withdrawal state. If a retrieval run is interrupted, the user reruns the operation after the UI returns to a known state.

### Inventory Availability

Add a material availability calculator:

- Player inventory count comes from live player inventory containers.
- Retainer count comes from `Configuration.RetainerCache`.
- Shortage is `required - playerInventory`.
- Retainers are only used to cover the shortage, not to satisfy already-held inventory.
- Cache entries should display `LastUpdated` so stale data is obvious.
- The cache can choose candidate retainers and expected quantities, but it cannot identify live page/slot targets because the current cache merges retainer pages by item id.

The first version should treat all direct turn-in materials as NQ count totals, matching the current scanner's aggregated item-id cache behavior. If HQ awareness becomes necessary, it should be a separate scoped change because the current scanner groups by item id and does not preserve HQ as a separate aggregate key.

### Retainer Withdrawal Runner

Add a manual restock runner that uses MarketMafioso's cache to build a withdrawal plan:

- Refuse to start if another refresh/restock run is active.
- Refuse to start if there are no queued projects.
- Refuse to start if required retainer cache data is missing for all shortages.
- Prefer refreshing cache first when cache is empty or old.
- Require the retainer list or AutoRetainer context needed for stable retainer iteration.
- Open `Entrust or withdraw items` only after the localized command entry is visible.
- Open a specific retainer only when the retainer list is actionable.
- After opening each candidate retainer's live inventory, scan the live `RetainerPage1..7` containers to find current page/slot targets for requested item ids.
- If the live retainer inventory differs from cache, use the live inventory as authoritative for that retainer and update the withdrawal plan in memory.
- Retrieve the minimum needed quantity from each live stack.
- After each withdrawal, decrement the remaining shortage in memory.
- Close each retainer and return the UI to a stable retainer menu/list state before continuing.

The runner should follow the existing `docs/design/ui-automation-rules.md` principles: every action needs a visible precondition and a visible postcondition, and failures must include visible UI state where possible.

### Withdrawal State Machine And Abort Contract

The withdrawal runner should be a small named state machine, not an open-ended click script. Required states:

- `Idle`: no run active; UI buttons are enabled.
- `Planning`: compute shortages from current player inventory and retainer cache.
- `WaitingForRetainerList`: wait for the retainer list or supported AutoRetainer handoff context.
- `OpeningRetainer`: select one candidate retainer and confirm that the intended retainer session or command menu is active.
- `OpeningInventory`: select localized `Entrust or withdraw items` and confirm retainer inventory is visible.
- `WithdrawingItems`: scan live retainer pages, retrieve planned quantities, and update remaining shortages.
- `ClosingRetainer`: close retainer inventory and return to a stable retainer menu/list state.
- `Complete`: all possible shortages were withdrawn or no candidate retainers remain.
- `Failed`: automation stopped and the status tells the user what recovery action is required.

The runner must stop with a clear status when:

- Player inventory appears full or cannot accept another retrieved item stack.
- The item was moved or removed since cache refresh and no live stack exists on the opened retainer.
- The wrong retainer appears to be open.
- The numeric quantity popup or context menu does not expose the required retrieve action.
- AutoRetainer postprocess lock release fails.
- The user closes the retainer window, changes context, changes zones, logs out, or otherwise interrupts a run.

Abort recovery is manual. MarketMafioso does not roll back retrieved items. On failure it should leave the remaining shortage list visible, return any external postprocess lock it owns when possible, and tell the user to close retainer UI or rerun after returning to the retainer list.

### VIWI Handoff

Add a small optional IPC wrapper:

- Detect whether `VIWI.Workshoppa.AddQueueItem` and `VIWI.Workshoppa.ClearQueue` can be invoked.
- `Send Queue To VIWI` should be an explicit user action.
- Clearing VIWI's queue should require an explicit confirmation in the MarketMafioso UI.
- After clear, invoke `AddQueueItem` for each MarketMafioso prep queue item.
- If VIWI is unavailable or a call fails, show a clear status and leave the MarketMafioso queue unchanged.

MarketMafioso should not assume VIWI's queue matches unless the current handoff completed successfully.

## UI

Add a `Workshop Prep` tab to the main MarketMafioso window.

The tab should include:

- A project search/add control.
- Queue rows with project name, quantity, and remove control.
- A material summary table with required, player inventory, retainer cache, total available, and shortage.
- A retainer-cache status summary.
- Actions:
  - `Refresh Retainer Cache`
  - `Restock Materials From Retainers`
  - `Send Queue To VIWI`
  - `Clear Prep Queue`

Keep the UI operational and compact. Avoid turning this into a full Workshoppa dashboard.

## Error Handling

Use explicit failures instead of fallback behavior:

- If VIWI IPC is unavailable, report that queue handoff is unavailable.
- If AutoRetainer is unavailable, report that refresh/restock automation is unavailable.
- If retainer cache is empty, tell the user to refresh cache first.
- If a required item is not found in cached retainers, report the item and remaining shortage.
- If a cached retainer no longer has the expected live item stack, report the item, retainer, and remaining shortage.
- If player inventory is full or cannot accept the withdrawal, stop before continuing to another retainer.
- If an expected addon/menu/item cannot be found while automating, stop and include tracked UI state.
- If withdrawal partially succeeds before failing, report completed items and remaining shortages.

Do not silently substitute clipboard import, stale cache, or VIWI reflection when the required state is missing.

## Testing And Verification

Add focused tests where practical:

- Project/material aggregation maps a queued project quantity to expected direct material totals.
- Availability calculation subtracts player inventory first and uses retainer cache only for shortages.
- VIWI handoff wrapper leaves the MarketMafioso queue unchanged on failed calls.

Manual in-game verification is required for the withdrawal runner:

- Build Debug and sync the dev plugin.
- Open `/mmf`.
- Build a small prep queue with known retainer-held materials.
- Refresh retainer cache.
- Run `Restock Materials From Retainers`.
- Confirm inventory changed by the expected quantities.
- Confirm VIWI queue handoff adds the same projects and quantities when requested.

## Settled Decisions

- First version should use direct workshop turn-in materials only.
- Recursive crafted-submaterial planning should wait until direct restock is reliable.
- MarketMafioso should own the queue; VIWI queue handoff is an optional explicit sync.
