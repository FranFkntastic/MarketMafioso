# Workshop Material Manifest Export

## Goal

Add Workshop Prep actions that export the current missing material demand for use outside MarketMafioso:

- Artisan: copy Artisan's native crafting-list JSON for craftable missing materials.
- Craft Architect: copy text in Craft Architect's current Teamcraft-style import format so Craft Architect can import the material demand immediately and analyze it with its own craft/buy/vendor logic.

MarketMafioso remains the in-game source of truth for the workshop prep queue, player inventory, and retainer cache. It should not try to reproduce Craft Architect's procurement planner.

## Source Data

The export source is `WorkshopMaterialAvailability` built from the active Workshop Prep queue:

- `Required`: total material quantity for queued workshop projects.
- `PlayerInventory`: quantity currently held by the player.
- `RetainerCache`: quantity known from cached retainers.
- `Shortage`: quantity missing from player inventory.
- `TotalMissing`: quantity not present in player inventory or retainer cache.
- `CandidateRetainers`: retainers that currently hold the item.

Default export quantity should be `Shortage`, labeled in UI as `Inventory Missing`. This answers "what do I still need available to the player to proceed." A later selector can expose `Total Missing` for "what do I not own anywhere."

## Exported Plan Naming

Exported names should be descriptive without requiring user input.

Recommended format:

`Workshop Materials - <project summary> - <quantity mode> - <yyyy-MM-dd HHmm>`

Examples:

- `Workshop Materials - Shark-class Pressure Hull x16 - Inventory Missing - 2026-06-23 1715`
- `Workshop Materials - Shark-class Pressure Hull x16 + 2 more - Total Missing - 2026-06-23 1715`
- `Workshop Materials - 4 projects - Inventory Missing - 2026-06-23 1715`

The project summary should prefer the first queued project name and quantity. If multiple project types are queued, append `+ N more` while keeping the name short enough for clipboard/import UIs.

## Artisan Export

Artisan's native export/import format is its `NewCraftingList` JSON. It is imported by copying JSON to the clipboard and using Artisan's `Import List From Clipboard (Artisan Export)` action.

Important boundary: Artisan list entries are recipe IDs, not item IDs. MarketMafioso must resolve each missing material item ID to a standard craft recipe before it can include that item.

Implementation shape:

- Add an Artisan DTO matching `NewCraftingList`, `ListItem`, and `ListItemOptions`.
- Resolve recipes from Lumina `Recipe` rows where `ItemResult.RowId == material.ItemId`.
- Prefer the lowest recipe row ID when multiple exact result recipes exist, unless local evidence suggests Artisan expects a different default.
- Convert missing item quantity to craft count using recipe yield: `ceil(quantity / AmountResult)`.
- Set `NQOnly = true` and `Skipping = false` for exported material crafts.
- Populate `ExpandedList` by repeating each recipe ID by craft count, matching Artisan's exported list behavior.
- Skip materials without a standard craft recipe and report them in status text.

This export is best-effort by nature. Gathered, vendor-only, currency, crystal, and other non-crafted materials should not make Artisan export fail if at least one recipe was exported.

## Craft Architect Export

Craft Architect should receive demand, not a pre-decided procurement plan. Craft Architect is better positioned to decide whether an item should be crafted, bought from market, bought from vendor, or handled some other way.

Export Teamcraft-style text with a descriptive first line followed by an `Items:` section. Craft Architect's current Teamcraft importer ignores the descriptive first line and imports the `Items:` section as project/root item demand.

Example:

```text
Workshop Materials - Shark-class Pressure Hull x16 - Inventory Missing - 2026-06-23 1715
Items:
288x Cobalt Ingot
144x Cedar Lumber
```

This is intentionally a dumb compatibility format. It does not carry item IDs or retainer context, but it works with Craft Architect today and lets Craft Architect rebuild the plan using its normal item lookup and procurement workflow.

## Stretch Goal: Rich Craft Architect Manifest

A future Craft Architect importer can support a verbose versioned MarketMafioso manifest directly. That richer format would be additive; it should not replace or break existing native plan, Teamcraft text, or Artisan imports.

Proposed JSON shape:

```json
{
  "schema": "marketmafioso.workshop-material-manifest",
  "schemaVersion": 1,
  "source": {
    "plugin": "MarketMafioso",
    "exportedAtUtc": "2026-06-23T21:15:00Z"
  },
  "name": "Workshop Materials - Shark-class Pressure Hull x16 - Inventory Missing - 2026-06-23 1715",
  "quantityMode": "InventoryMissing",
  "queue": [
    {
      "workshopItemId": 123,
      "resultItemId": 456,
      "name": "Shark-class Pressure Hull",
      "quantity": 16
    }
  ],
  "materials": [
    {
      "itemId": 5378,
      "name": "Cobalt Ingot",
      "iconId": 12345,
      "exportQuantity": 288,
      "required": 288,
      "playerInventory": 0,
      "retainerCache": 75539,
      "inventoryMissing": 288,
      "totalMissing": 0,
      "candidateRetainers": [
        {
          "name": "Taffy-swordsman",
          "quantity": 75539,
          "lastUpdatedUtc": "2026-06-23T20:40:00Z"
        }
      ]
    }
  ]
}
```

Craft Architect could import `materials` as project/root items using `itemId`, `name`, `iconId`, and `exportQuantity`, then run its normal plan build and procurement analysis. The extra inventory and retainer fields would remain advisory context for future UI, diagnostics, or smarter import defaults.

## UI

Add compact buttons to Workshop Prep actions:

- `Copy Artisan Manifest`
- `Copy Craft Architect Manifest`

Keep status text explicit:

- `Copied Craft Architect import text: 24 materials.`
- `Copied Artisan manifest: 12 recipes. Skipped 7 non-craftable materials: Earth Cluster, Darksteel Ore, ...`
- `No missing workshop materials to export.`

Do not add a large export wizard for the first pass. If quantity mode selection is added, use a compact combo near the export buttons.

## Error Handling

- Empty queue: return an info result and do not touch the clipboard.
- No missing materials for selected quantity mode: return an info result and do not touch the clipboard.
- Artisan has no craftable materials: return a warning and do not copy an empty list.
- Craft Architect export should fail only for invalid source state, because the Teamcraft-style text path does not need recipe resolution.
- All results should include counts and a short skipped-item summary.

## Tests

Add focused tests around a pure export service:

- Builds descriptive names from one project, several projects, and generic fallbacks.
- Filters material quantities by `InventoryMissing`.
- Supports `TotalMissing` if the quantity mode selector is included.
- Emits Craft Architect-compatible Teamcraft-style text with a descriptive first line and an `Items:` section.
- Resolves Artisan recipes and applies recipe yield to craft counts.
- Reports Artisan skipped materials without failing the whole export.
- Returns clean info/warning results for empty queue and zero exportable materials.

## Open Questions

- Whether Craft Architect should later accept the rich MarketMafioso manifest directly.
- Whether Artisan export should prefer a class/job-specific recipe when multiple recipes produce the same item. The first pass should keep the simple exact-result rule unless testing finds mismatches.
