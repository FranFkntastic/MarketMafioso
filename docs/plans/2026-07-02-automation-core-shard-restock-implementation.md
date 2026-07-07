# Automation Core Shard Restock Status

Status: backlog reference, not an active implementation plan.

This note preserves the useful part of the pruned automation-core shard restock branch: retainer restock automation should keep moving low-level UI and inventory helpers out of `WorkshopRetainerRestockService` when the next refactor slice is worth doing.

## Current State

- Some shared automation helpers now exist under `src/MarketMafioso/Automation/`.
- `WorkshopRetainerRestockService` still owns important retainer-list selection, command-menu, loaded-inventory, and withdrawal orchestration behavior.
- The original follow-up branch is gone, so this document should not be treated as an implementation-ready branch plan.

## Still Useful

Future restock automation work should consider extracting:

- retainer-list selection and readiness checks;
- select-string text normalization;
- command-menu and context-menu actions;
- loaded player-inventory counting;
- live retainer inventory scanning.

The goal is still sound: keep workflow sequencing in `WorkshopRetainerRestockService`, but make the low-level retainer UI primitives reusable by standalone Restock and future automation flows.

## Archive Linkage

The longer branch triage is retained in `docs/plans/2026-07-06-pruned-branch-revisit-todo.md`.
