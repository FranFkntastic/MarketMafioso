# Pruned Branch Revisit / Todo

This document archives the remaining unique branch work before pruning the branches and their worktrees. The branches were reviewed on 2026-07-06 after `main` and `local-dev` were aligned at `30f69d8`.

## Summary

The surviving unique branches fell into two buckets:

- old inventory-browser/dashboard branches from the pre-`src/` layout;
- an automation-core branch with active shard-restock helper work.

The old inventory branches should not be merged directly. If their ideas are still wanted, port them intentionally into the current `src/MarketMafioso.Server` and Blazor dashboard shape.

The automation-core branch had relevant dirty implementation work. Its intent is preserved here and in `docs/plans/2026-07-02-automation-core-shard-restock-implementation.md`.

## `test/inventory-browser-vps-auth-fix`

Tip: `6d4ea28 Fix dashboard auth on inventory ingest`

Remote: `origin/test/inventory-browser-vps-auth-fix`

Original purpose:

- Fix dashboard basic-auth middleware so plugin inventory ingest routes use API-key auth instead of dashboard browser auth.
- Add a regression test showing `POST /inventory` with `X-Api-Key` succeeds when API-key auth is enabled.

Touched old-layout files:

- `MarketMafioso.Server/Auth/DashboardBasicAuthMiddleware.cs`
- `MarketMafioso.Server.Tests/DashboardAccountAuthTests.cs`

Current-main status:

- Current `main` has moved to `src/MarketMafioso.Server/*`.
- Dashboard auth is now session-oriented, and plugin inventory ingest routes are explicitly separated in `DashboardSessionAuthMiddleware`.
- The old branch should be treated as superseded reference, not as a merge candidate.

Todo if revisiting:

- Verify current tests still cover unauthenticated dashboard access versus API-key inventory ingest.
- If a gap exists, add the regression under current `tests/MarketMafioso.Server.Tests/InventoryReportViewEndpointTests.cs` or the current auth test suite rather than resurrecting the old middleware branch.

Disposition:

- Prune branch and remote.

## `test/inventory-browser-scopes`

Tip: `c805e9b Add inventory browser table interactions`

Remote: `origin/test/inventory-browser-scopes`

Included commits:

- `6d4ea28 Fix dashboard auth on inventory ingest`
- `0e5e297 Improve inventory browser scopes and item types`
- `c805e9b Add inventory browser table interactions`

Original purpose:

- Add inventory browser scope/item-type improvements.
- Add browser-table interactions: client-side live search, sortable columns, resizable columns, and visible-row counts.
- Add configurable display timezone support for receiver-rendered HTML.
- Add inventory payload/type fields and migration/test coverage in the old server layout.

Touched old-layout areas:

- `MarketMafioso.Server/Program.cs`
- `MarketMafioso.Server/InventoryBrowserViewBuilder.cs`
- `MarketMafioso.Server/InventoryBrowserModels.cs`
- `MarketMafioso.Server/Sqlite/SqliteSchemaMigrator.cs`
- `MarketMafioso/InventoryPayload.cs`
- `MarketMafioso/InventoryScanner.cs`
- `.github/workflows/deploy-vps-marketmafioso-dev.yml`
- old docs under `docs/hosted-receiver.md` and `docs/local-backend.md`

Current-main status:

- Current `main` has a newer `src/` layout and a dashboard API/client split.
- Inventory browser routes now include API endpoints such as `/api/inventory/browser` and the dashboard client consumes them.
- The old server-rendered table JavaScript should not be merged as-is.

Todo if revisiting:

- Decide whether the current Blazor inventory browser still needs any of these UX affordances:
  - live client-side search against already-loaded rows;
  - sortable columns;
  - resizable columns;
  - explicit visible-row count;
  - display timezone setting.
- If yes, implement those in the current dashboard components/services rather than the old `Program.cs` HTML renderer.
- Re-check whether item type and scope metadata are already represented in current `InventoryBrowserView` models before adding schema fields.

Disposition:

- Prune branch and remote.

## `work/automation-core-followup`

Tip: `8a5ee15 Align automation core test namespaces`

Remote: none.

Committed branch purpose:

- Move market-board, travel, and external automation tests into `tests/MarketMafioso.Tests/Automation/*`.
- This was mostly namespace/test organization cleanup.

Dirty implementation work found before pruning:

- Modified:
  - `src/MarketMafioso/Automation/Retainers/RetainerCommandMenuDriver.cs`
  - `src/MarketMafioso/Automation/Retainers/RetainerContextMenuDriver.cs`
  - `src/MarketMafioso/Automation/Retainers/RetainerUiAutomationText.cs`
  - `src/MarketMafioso/WorkshopPrep/WorkshopRetainerRestockService.cs`
- Added:
  - `src/MarketMafioso/Automation/Inventory/AutomationInventoryCounter.cs`
  - `src/MarketMafioso/Automation/Retainers/RetainerListDriver.cs`
  - `src/MarketMafioso/Automation/Runtime/SelectStringText.cs`
  - `tests/MarketMafioso.Tests/Automation/Inventory/AutomationInventoryCounterTests.cs`
  - `tests/MarketMafioso.Tests/Automation/Retainers/RetainerListDriverTests.cs`
  - `tests/MarketMafioso.Tests/Automation/Runtime/SelectStringTextTests.cs`

Original purpose:

- Extract low-level retainer-list selection, select-string text normalization, and loaded-inventory counting out of `WorkshopRetainerRestockService`.
- Keep workshop restock as the orchestrator while reusable automation helpers own parsing, matching, and inventory-counting primitives.

Important implementation details worth preserving:

- `SelectStringText` normalized select-string entries by removing control/private-use characters, trimming trailing periods, and stripping suffix details like ` (22)`.
- `RetainerCommandMenuDriver` and `RetainerContextMenuDriver` were changed to call `SelectStringText`.
- `RetainerListDriver` read `RetainerList` entries from addon values using:
  - first value index `3`;
  - value stride `10`;
  - maximum retainers `10`;
  - active flag offset `8`.
- `RetainerListDriver` selected a retainer by firing callback values equivalent to command `2` plus the selected retainer index.
- `RetainerListDriver.BuildRetainerNotFoundMessage` included visible active/inactive retainer names.
- `AutomationInventoryCounter.CountItem` summed matching item quantities across loaded `AutomationInventoryContainerSnapshot` values and ignored unloaded snapshots.
- `WorkshopRetainerRestockService.CountPlayerItem` was changed to scan loaded player inventory containers through `AutomationInventoryContainerScanner`, then call `AutomationInventoryCounter.CountItem`.

Todo if revisiting:

- Recreate the helper extraction against current `main`.
- Keep the helper tests from the dirty branch:
  - `AutomationInventoryCounterTests`
  - `RetainerListDriverTests`
  - `SelectStringTextTests`
- Run focused automation tests before building:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "AutomationInventoryCounterTests|RetainerListDriverTests|SelectStringTextTests|RetainerCommandMenuDriverTests|RetainerContextMenuDriverTests|WorkshopRetainerRestockServiceTests" --no-restore
```

- Live-test against an actual retainer list before trusting retainer selection callbacks.

Disposition:

- Prune branch and dirty worktree after this archive doc is committed.

