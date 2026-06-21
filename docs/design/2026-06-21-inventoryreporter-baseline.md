# InventoryReporter Baseline

## Goal

Make MarketMafioso start from InventoryReporter behavior while positioning the plugin as a broader integrated toolbox. The active plugin should own player inventory scanning, retainer inventory caching, and optional HTTP JSON export before market-specific tools are rebuilt.

## Current Primary Features

- Scan player inventory bags.
- Optionally include armoury chest, crystals, equipped gear, and saddlebag.
- Cache current retainer inventory when a retainer inventory window closes.
- Include cached retainers in the outbound report.
- Send reports manually with `/mmf send`.
- Optionally auto-send on retainer window close or on a timer.
- Preview the last JSON payload in the settings UI.

## Explicitly Scrapped From The Old MarketMafioso Baseline

- Active retainer sale-listing capture.
- In-plugin market-board query cycles.
- Runtime market snapshot tables.
- Old overlay-driven market review UI.

## Future MarketMafioso Direction

MarketMafioso should become a home for small, practical features that are useful but too narrow for standalone plugins. Rebuild market tooling on top of the inventory reporting baseline once the inventory snapshot model is clean. Market tools should use the inventory/reporting data as their foundation rather than preserving the earlier exploratory capture code.

## Source Boundaries

The active implementation lives in `MarketMafioso/`. The old source directories were intentionally removed after porting the inventory reporting baseline.
