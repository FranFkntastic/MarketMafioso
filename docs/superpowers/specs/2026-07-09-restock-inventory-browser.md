# Restock Inventory Browser

## Problem

The standalone Restock tab has the right underlying automation, but the plan-building experience is still too manual. Adding a restock item currently searches the full Lumina item catalog, so it suggests items that are not present in any accessible inventory source. The user has to know the item name first, then separately infer whether any cached retainer can actually satisfy it.

The target is an inventory-first restock workflow:

- Browse items this character can actually access.
- See player-held and retainer-held stock before adding a plan row.
- Add only stock-backed items to the plan.
- Require the user to explicitly enter the desired quantity for each item.
- Keep the retainer withdrawal automation and existing plan/run model underneath.

## Design Principles

1. Stock evidence comes first.
   The Restock add flow starts from accessible stock, not a global item catalog. If an item has no accessible stock, it should not appear in the browser.

2. Desired quantity is always explicit.
   The plugin must not infer or default the desired player quantity from available stock. A row can only be added to the saved plan after the user enters a positive desired quantity.

3. Accessible does not mean withdrawable.
   Player inventory, crystals, saddlebag, armoury, and equipped items can count as already accessible. Retainers are the only v1 automation source for withdrawals.

4. Keep ownership boundaries strict.
   Retainer stock must be owner-scoped to the current character and home world. Legacy unscoped cache entries and other-character storage are excluded once current owner scope is available.

5. Extract the browser from MainWindow.
   Restock should gain a dedicated presenter/window component instead of adding more state and rendering logic directly to `MainWindow`.

## Stock Scope

The v1 browser includes stock from:

- Current character bags.
- Crystals when `IncludeCrystals` is enabled.
- Saddlebag when `IncludeSaddlebag` is enabled.
- Armoury when `IncludeArmoury` is enabled.
- Equipped items when `IncludeEquipped` is enabled.
- Cached retainers whose owner character and owner home world match the current player scope.

The v1 browser excludes:

- FC chest.
- Other characters and other-character retainers.
- Market listings.
- Legacy unscoped retainer cache entries when owner scope is available.
- Any source that would require item transfer between alts.

The internal source model should leave room for a future directly accessible `FcChest` source type, but no FC chest scanning or UI is part of this pass.

## Target Layout

The Restock tab becomes a two-pane inventory browser with a saved plan queue.

Left pane: Accessible Stock Browser

- Search box filters stock-backed item rows.
- Source filters for player-held and retainer-held stock.
- Stock rows show item name, total accessible quantity, player quantity, retainer quantity, cache age, and compact source details.
- Rows with no retainer quantity remain visible if the item is already accessible on the player, but they will show that no withdrawal is needed or possible.

Right pane: Plan Queue

- Shows saved `RetainerRestockPlanItem` rows.
- Quantity entry is explicit and required.
- Adding from the stock browser stages the item and focuses the desired quantity input.
- Saving the staged item creates or updates the matching plan row.
- Existing plan rows keep enabled, desired quantity, note, and remove controls.
- Preview columns continue to show player quantity, need, retainer coverage, missing quantity, status, and candidates.

Run controls stay below the browser:

- Refresh Retainer Cache.
- Restock From Retainers.
- Last automation status.
- Current owner-scope/cache freshness summary.

## Component Boundaries

Add a dedicated Restock browser surface instead of growing `MainWindow`.

Expected components:

- `RetainerRestockStockCatalog`
  Builds stock-backed rows from player inventory counts, configuration toggles, cached retainers, and owner scope.

- `RetainerRestockStockRow`
  Represents one item in the browser, including source totals, retainer candidates, and cache age.

- `RetainerRestockBrowserState`
  Holds search text, selected stock row, staged desired quantity text, source filters, and selected plan row.

- `RetainerRestockBrowserPanel`
  Renders the two-pane browser, validates explicit desired quantity, mutates `config.RetainerRestockPlanItems`, and delegates plan preview/run data back to existing planner services.

`MainWindow` should own orchestration only:

- Create the browser state/panel.
- Pass current stock inputs and plan outputs.
- Continue to own top-level tab layout and run controls unless extraction makes a smaller boundary obvious during implementation.

## Required Behavior

- The add/search browser never uses the full Lumina item catalog as its primary source.
- Browser rows are derived from positive accessible stock.
- Duplicate item names are still disambiguated by item id when necessary.
- The user cannot add a new row without entering a positive desired quantity.
- Re-adding an existing item updates that plan row only after explicit confirmation through the same quantity input.
- Retainer candidates are owner-scoped and sorted by available quantity, freshness, then retainer name, matching the current planner behavior.
- Existing retainer restock automation consumes the same `RetainerRestockPlanLine` data it uses today.
- A player-only item can be added to the plan for tracking, but the run preview must make clear there are no retainer candidates to withdraw.

## Pain Points And Decisions To Revisit

- Player inventory toggles are doing double duty: they control Inventory Reporter payloads and now Restock accessibility. If that feels surprising in live use, Restock may need its own source toggles.
- Cache freshness becomes more visible in an inventory browser. If stale cache causes bad decisions, add stronger stale-row warnings or default filters later.
- Player-only rows may be useful for planning but not for automation. If they clutter the browser, add a filter for "retainer-withdrawable only."
- FC chest should fit the source model later, but only if it is directly accessible by the current character.
- The first implementation should avoid broad inventory tooling beyond Restock, but the extracted stock catalog should be reusable when future inventory-based tools arrive.

## Visual Acceptance

The live UI is acceptable when:

- The first Restock viewport resembles the inventory-first mockup: stock browser on one side, plan queue on the other.
- Search results only show items with accessible stock.
- Stock rows show meaningful player/retainer/source detail without requiring the user to open another panel.
- Adding a row requires explicit desired quantity entry.
- The preview and run controls remain familiar and still use the existing restock engine.
- `MainWindow` is smaller or at least not materially larger because the browser logic lives in extracted Restock components.

## Verification

- Add focused tests for stock catalog construction, owner-scoped retainer filtering, source totals, search filtering, and explicit quantity validation.
- Run focused RetainerRestock tests.
- Build the plugin in Debug.
- Deploy the dev plugin.
- Visually inspect the Restock tab in game against the mockup.
