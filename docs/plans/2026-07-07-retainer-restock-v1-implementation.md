# Retainer Restock V1 Status

Status: V1 implemented on `local-dev`; retained as a compact maintainer note.

The first standalone Restock tab has landed. It lets the user build a local retainer restock plan, preview cached retainer coverage, and run guarded retainer withdrawal through the existing workshop retainer automation path.

## Landed Shape

- `MainWindow` owns the top-level `Restock` tab.
- `Configuration.RetainerRestockPlanItems` stores editable restock rows.
- `RetainerRestockPlanner` builds preview lines from desired quantity, player inventory, scoped retainer cache, and current owner scope.
- `RetainerRestockCompletionSummary` formats complete, partial, skipped, and failed outcomes.
- `WorkshopRetainerRestockService.StartRestockAsync(...)` is the generic restock entry point.
- Workshop Logistics still calls the workshop adapter path for workshop material shortages.

## Ownership Boundary

Restock plans are storage-retrieval intent. They should not silently become market acquisition routes, craft plans, or workshop-only actions.

Retainer cache entries are scoped by current character and home world when that owner scope is available. Downstream report, dashboard, and restock views should preserve that owner metadata.

## V2 Reference

The broader V2 design remains local/private under `docs/design/2026-07-07-retainer-restock-plans.md`.

Potential future work includes saved restock profiles, inventory-assisted item discovery, broader cache refresh support, and explicit Restock-then-Acquire handoff flows.
