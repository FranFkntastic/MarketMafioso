# Inventory Browser Semantics and Retainer Scope Design

## Goal

Tighten the Inventory Reporter data model and dashboard so the default inventory browser is useful for real item lookup, without mixing unrelated retainer state into normal item totals.

This pass should make four concepts explicit:

- Inventory items.
- Gil held by owners, especially retainers.
- Retainer market listings.
- Filterable inventory scopes and table columns.

## Current State

- `InventoryScanner` scans `RetainerPage1` through `RetainerPage7`, `RetainerGil`, and `RetainerMarket` through the same retainer item path.
- The server persists owners, bags, and item stacks, but does not distinguish normal retainer inventory from gil or market listings.
- The inventory browser aggregates item rows by item id and shows owner/bag locations inline under each item.
- Retainers appear only as item owners, not as first-class scopes in the sidebar.
- Column headers are clickable-looking labels, but there is no separate filter affordance beside each label.

## Product Semantics

### Inventory Items

Normal item rows should represent physical inventory stacks only:

- Player bags.
- Equipped gear, if included.
- Armoury chest, if included.
- Crystals, if included.
- Saddlebag, if included.
- Retainer inventory pages.

Gil must not be counted as an inventory item. Retainer market listings must not be mixed into normal inventory rows.

### Gil

Gil should be stored as owner-level money, not as an item stack.

For this pass:

- Add `Gil` to retainer payload and cached retainer state.
- Store retainer gil on `inventory_owners`, nullable for non-retainers and older snapshots.
- Show retainer gil in the sidebar Retainers subsection.
- Do not include gil in item totals, stack counts, search results, HQ counts, or owner matched counts.

Player gil can be added later if we deliberately choose to scan and report it; this pass is about separating retainer gil from item rows.

### Retainer Market Listings

Market listings are retainer state, but they are not regular inventory. They should be displayed deliberately separately.

For this pass:

- Add a separate retainer market-listing collection to the payload model and cached retainer state.
- Store market listings separately from `inventory_items`, either in a dedicated table or in an explicit item-scope table keyed as `retainer_market`.
- Exclude market listings from the default inventory item table.
- Add a retainer detail subsection or diagnostic-style panel that lists market listings for each retainer.
- Show individual listings instead of aggregating by item, because unit prices may differ across listings for the same item.
- Show listing item name/id, quantity, HQ, item type, unit price, total price, listing identity/order, and listing data age when available.

Pricing, sale history, buyer data, and market-board comparisons stay out of scope unless the client can source them reliably.

## Dashboard Scope Model

The inventory browser default remains character-scoped:

1. Default to the latest snapshot for the selected character.
2. Show all normal inventory items for that character across player inventory and retainers.
3. Preserve the account-wide character selector.

Add a Retainers subsection under inventory scopes:

- One row per retainer.
- Show retainer name.
- Show normal inventory row or stack count.
- Show retainer gil.
- Show market-listing count.
- Show the age of the player inventory and each retainer inventory snapshot.
- Selecting a retainer filters the main inventory table to that retainer's normal inventory only.
- Market listings remain visually separate from normal inventory, even when a retainer is selected.

The sidebar should make it obvious whether the table is showing the whole character inventory or one retainer scope.

## Item Type

Item type should be carried through the whole path:

- Client payload: `ItemSlot.ItemType`.
- Cached retainer item state.
- Server payload model.
- SQLite item rows.
- Inventory browser view model.
- Table display and filtering.

The type string should be optional for backwards compatibility with older payloads and older snapshots. Missing type displays as blank or `Unknown`, but should not break ingestion.

## Table UX

The main display remains a dense table.

Changes:

- Add clearer vertical column separators in header and body cells.
- Preserve column names as plain labels for future sorting and accessibility behavior.
- Add a separate dropdown/filter button next to each filterable column label.
- The button, not the label text, opens filter UI.
- Search should update as the user types with a modest debounce.
- Columns should remain sortable and resizable. Default column widths should fit the available table width and scale automatically as the browser window changes.
- Column resizing should adjust each column's percentage share of the table, not lock columns to absolute pixel widths.
- Header resize grips and main-body vertical separators should both act as resize handles.

Initial filterable columns:

- Item.
- Type.
- Total.
- HQ.
- Where.

The icon column remains present but hidden by default.

## Storage Shape

Prefer explicit fields over implicit bag-name conventions.

Recommended schema additions:

```sql
ALTER TABLE inventory_owners ADD COLUMN gil INTEGER NULL;
ALTER TABLE inventory_items ADD COLUMN item_type TEXT NULL;

CREATE TABLE retainer_market_listings (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    owner_id INTEGER NOT NULL REFERENCES inventory_owners(id) ON DELETE CASCADE,
    item_id INTEGER NOT NULL,
    item_name TEXT NULL,
    item_type TEXT NULL,
    quantity INTEGER NOT NULL,
    is_hq INTEGER NOT NULL,
    condition REAL NOT NULL,
    sort_order INTEGER NOT NULL
);
```

The migrator should be idempotent. Because SQLite cannot add the same column twice, the migration code should check existing table columns before issuing `ALTER TABLE`.

Older snapshots should remain readable. If an older snapshot has a `RetainerGil` or `RetainerMarket` bag, the view builder should avoid treating those rows as normal inventory and should classify them through compatibility code where possible.

## Capture Flow

Client-side retainer scan should split containers by intent:

- Retainer inventory pages go to normal retainer bags.
- `RetainerGil` becomes `RetainerReport.Gil`.
- `RetainerMarket` becomes `RetainerReport.MarketListings`.

The retainer cache should persist the same split so auto-send and later reports do not re-flatten market/gil state into normal bags.

## Testing

Add focused coverage for:

- Retainer gil does not appear as an inventory item and does not affect item totals.
- Retainer gil round-trips through SQLite.
- Retainer market listings do not appear in default item aggregation.
- Retainer market listings render in their separate section.
- Item type round-trips from payload to SQLite to inventory browser.
- Sidebar retainer scopes display gil and listing counts.
- Sidebar player and retainer scopes display last snapshot age.
- Header filter affordances render separately from column labels.
- Main table columns render proportional widths and body separators expose resize handles.

Run at minimum:

```powershell
dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug -v minimal
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug -v minimal
```

Plugin changes should be deployed with `MarketMafioso/tools/Deploy-DevPlugin.ps1` before in-game validation.
