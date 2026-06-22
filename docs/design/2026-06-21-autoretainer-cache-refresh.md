# AutoRetainer Retainer Cache Refresh

## Goal

Add a manual full retainer cache refresh action for Inventory Reporter that uses AutoRetainer's existing retainer iteration flow. A full refresh should update every available retainer cache entry and send one inventory report after the batch finishes.

## User Flow

- Open a summoning bell and reach the retainer list.
- Press `Refresh Retainer Cache` from MarketMafioso's Inventory Reporter tab, or press the MarketMafioso refresh button added to AutoRetainer's retainer-list overlay.
- AutoRetainer cycles through available retainers.
- MarketMafioso opens the retainer inventory during each postprocess step, lets the existing retainer cache hook snapshot the inventory when the inventory window closes, then returns control to AutoRetainer.
- MarketMafioso sends one report after all expected retainers are processed.
- Normal AutoRetainer runs also piggyback the same postprocess hook to keep individual retainer cache entries fresh.

## Integration Boundary

MarketMafioso should not bundle or reference `AutoRetainerAPI.dll`. The required AutoRetainer IPC surface is small and stable enough to wrap locally:

- `AutoRetainer.Init`
- `AutoRetainer.OnRetainerListTaskButtonsDraw`
- `AutoRetainer.OnRetainerListCustomTask`
- `AutoRetainer.OnRetainerAdditionalTask`
- `AutoRetainer.RequestPostprocess`
- `AutoRetainer.OnRetainerReadyForPostprocess`
- `AutoRetainer.FinishPostprocessRequest`

The integration remains optional. If AutoRetainer is not installed or not ready, existing manual retainer cache behavior continues to work.

## Implementation Shape

Add `AutoRetainerRefreshService`:

- Subscribe to AutoRetainer retainer-list button draw and postprocess IPC events.
- Expose `IsAvailable`, `IsRefreshing`, `CanStartRefresh`, `ProcessedRetainers`, `ExpectedRetainers`, and `LastStatus` for UI.
- Add an overlay button that starts MarketMafioso's custom AutoRetainer task.
- Let the Inventory Reporter tab call the same `StartFullRefresh()` method.
- Count expected retainers from `RetainerManager.GetRetainerCount()` at batch start.
- Wait for the retainer command menu and the localized `Entrust or withdraw items` entry before selecting it.

Extend `RetainerCacheManager`:

- Add batch mode so retainer close still updates cache, but skips `AutoSendOnRetainerClose`.
- Send once after the service confirms all expected retainers have completed.

Update `MainWindow`:

- Add `Refresh Retainer Cache` to Inventory Reporter actions.
- Disable it unless AutoRetainer is available and the retainer list context is present.
- Show a compact status line while refresh is running or unavailable.

## Risks

- AutoRetainer's custom task trigger only makes sense while the retainer list is open.
- Retainer menu ordering can vary, so MarketMafioso should select `Entrust or withdraw items` by localized text instead of a fixed index.
- AutoRetainer can fire postprocess before the retainer command menu is ready, so MarketMafioso must wait for that menu state instead of acting on the next framework tick.
- AutoRetainer requires postprocess callers to return the UI to the same retainer-menu state before calling finish.

## Retainer UI Automation Rules

Project-level UI automation rules live in [ui-automation-rules.md](ui-automation-rules.md). The rules below are the AutoRetainer-specific application of those defaults.

- Prefer AutoRetainer's normal scheduler postprocess hook when it is available. It runs after AutoRetainer has handled ventures and routine retainer work, so it is naturally closer to the stable retainer command menu.
- Treat the manual full refresh as a strict UI state machine, not as a chain of hopeful clicks.
- Every automated action needs one visible precondition and one visible postcondition:
  - Select `Entrust or withdraw items` only when the retainer command `SelectString` is visible and that localized entry exists.
  - Close the inventory only when `InventoryRetainerLarge` or `InventoryRetainer` is visible.
  - Finish AutoRetainer postprocess only after the retainer inventory window has closed.
- Diagnostic failures should include the tracked retainer addons and visible `SelectString` entries. This keeps future fixes tied to the actual screen state instead of adding blind delay.
- If a full refresh fails, stop requesting more postprocess callbacks until the user starts another manual refresh. Piggyback refreshes can continue during ordinary AutoRetainer runs.

## ItemFinderModule Notes

`ItemFinderModule` is not the primary path for Inventory Reporter snapshots. It exposes retainer item id/count arrays and can report whether a retainer was summoned in the current session, but it does not represent the full live retainer inventory shape that MarketMafioso needs for item detail, slots, gil, crystals, and market entries.

Keep the direct retainer inventory scan as the source of truth. `ItemFinderModule` may become useful later for lightweight aggregate totals or freshness indicators, but it should not replace opening the retainer inventory when the goal is a full cache refresh.
