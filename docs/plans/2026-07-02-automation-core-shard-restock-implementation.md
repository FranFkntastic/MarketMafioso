# Automation Core Shard Restock Implementation Plan

Status: recovered plan, carried forward from the archived automation-core worktree cleanup.

## Purpose

Workshop shard restock should move from one-off UI code inside `WorkshopRetainerRestockService` toward reusable automation-core helpers. The immediate goal is to make retainer selection, select-string matching, and loaded-inventory counting testable outside the workshop service so future restock automation can use the same primitives.

This plan preserves the intent of the archived worktree document whose untracked file was removed with the old worktree.

## Current Surviving Work

The active branch `work/automation-core-followup` contains one committed namespace-cleanup slice and an uncommitted implementation slice.

Committed slice:

- Moves market-board, travel, and external automation tests under `tests/MarketMafioso.Tests/Automation/*`.
- Keeps runtime behavior unchanged.

Uncommitted implementation slice:

- Adds `AutomationInventoryCounter` for summing an item across loaded inventory snapshots.
- Adds `SelectStringText` for normalized select-string entry matching.
- Adds `RetainerListDriver` for reading and selecting retainers from `RetainerList`.
- Refactors `WorkshopRetainerRestockService` to call the new helpers instead of owning the retainer-list and inventory-counting internals directly.
- Adds focused tests for the new helpers.

## Implementation Slices

### 1. Preserve Shared Select-String Matching

Create `MarketMafioso.Automation.Runtime.SelectStringText`.

Responsibilities:

- Strip control/private-use decoration characters.
- Trim trailing punctuation.
- Remove detail suffixes such as ` (22)`.
- Match entries by normalized prefix.
- Find the first matching entry index in a visible menu label list.

Consumers:

- `RetainerCommandMenuDriver`
- `RetainerContextMenuDriver`
- `RetainerUiAutomationText`

Verification:

- `SelectStringTextTests` should cover decorated entries, trailing periods, detail suffixes, positive matches, and negative matches.

### 2. Extract Retainer List Selection

Create `MarketMafioso.Automation.Retainers.RetainerListDriver`.

Responsibilities:

- Validate that `RetainerList` is ready and visible.
- Read visible retainer names and active/inactive state from addon values.
- Select a retainer by name using the existing callback shape.
- Return explicit `AutomationOperationResult` failures instead of silent fallback behavior.
- Format retainer-not-found diagnostics with the visible retainer list.

Consumers:

- `WorkshopRetainerRestockService.SelectRetainerFromList`

Verification:

- `RetainerListDriverTests` should cover diagnostic text for active/inactive visible retainers and empty lists.
- Pointer/addon callback behavior remains integration-level and should be verified in-game before broadening automation.

### 3. Extract Loaded Inventory Counting

Create `MarketMafioso.Automation.Inventory.AutomationInventoryCounter`.

Responsibilities:

- Count matching item quantity across loaded `AutomationInventoryContainerSnapshot` values.
- Ignore unloaded snapshots.
- Keep the counting logic independent from Dalamud pointers.

Consumers:

- `WorkshopRetainerRestockService.CountPlayerItem`, after loaded containers are scanned through `AutomationInventoryContainerScanner`.

Verification:

- `AutomationInventoryCounterTests` should cover multiple loaded bags, HQ/non-HQ slots, unrelated items, and unloaded containers.

### 4. Keep Workshop Restock as the Orchestrator

`WorkshopRetainerRestockService` should keep orchestration ownership:

- planning candidate retainers;
- waiting for the retainer list;
- asking the retainer-list driver to select the target retainer;
- selecting `Entrust or withdraw items`;
- opening item context menus;
- withdrawing stack quantities;
- counting player inventory after each action.

It should not own low-level reusable parsing/callback helpers when those helpers can be tested separately.

## Boundaries

Do not expand this slice into a full automation rewrite.

Do not make retainer selection optimistic. If the visible list is missing, the retainer is absent, or the expected menu entry cannot be found, fail with explicit diagnostics.

Do not assume all inventory containers are loaded. Loaded state remains part of the contract.

Do not touch Market Acquisition while finishing this slice unless a shared automation helper requires a namespace/reference update.

## Suggested Verification

Run focused automation tests first:

```powershell
dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "AutomationInventoryCounterTests|RetainerListDriverTests|SelectStringTextTests|RetainerCommandMenuDriverTests|RetainerContextMenuDriverTests|WorkshopRetainerRestockServiceTests" --no-restore
```

Then run the solution build:

```powershell
dotnet build .\MarketMafioso.sln --no-restore
```

Before claiming the branch is usable in game, deploy the dev plugin and test against a live retainer list:

```powershell
.\src\MarketMafioso\tools\Deploy-DevPlugin.ps1
```

## Branch Disposition

Keep `work/automation-core-followup`.

It contains unique committed namespace cleanup plus dirty implementation work that is still relevant. It is not prune-safe until the uncommitted helper extraction is either completed and merged or deliberately discarded.
