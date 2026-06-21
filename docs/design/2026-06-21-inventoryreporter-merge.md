# InventoryReporter Merge Plan

## Goal

Fold the useful InventoryReporter behavior into MarketMafioso so one plugin owns retainer inventory snapshots, active retainer sale listings, and market-board workflow data.

## Keep From InventoryReporter

- HTTP JSON export shape and configurable endpoint.
- Player inventory and retainer inventory scanning concepts.
- Retainer cache boundary checks and freshness expectations.
- Manual send command or equivalent explicit refresh action.

## Keep From MarketMafioso

- Active retainer sale-listing capture work.
- Runtime market snapshot model.
- GUI-first workflow centered around reviewable retainer and market data.
- Dev-plugin sync helper.

## Rework Instead Of Preserving

- Old MarketMafioso UI layout and command assumptions.
- Any capture path that only exists because of exploratory reference code.
- Duplicated inventory/retainer models once the unified data contract is clear.

## First Implementation Shape

1. Define a single snapshot model for character, retainers, inventory items, active sale listings, and optional market context.
2. Port InventoryReporter's scanner behavior into a MarketMafioso service.
3. Keep HTTP export behind an explicit configuration toggle and manual send action at first.
4. Rebuild the UI around inspectable snapshots before adding pricing or undercut decisions.

## Source Boundaries

`external/InventoryReporter` is merge reference source. Do not build or ship it directly from this repository.
