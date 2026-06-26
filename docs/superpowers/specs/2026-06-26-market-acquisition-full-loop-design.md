# Market Acquisition Full Loop Design

## Goal

Ship the full Market Acquisition loop end to end: dashboard request, plugin pickup, advisory planning, guided travel, live market-board search, guarded purchases, progress reporting, and terminal audit.

The mechanisms have now been proven individually. The final design should stop treating the system as a probe and start treating it as a controlled execution pipeline.

## Locked Product Shape

Market Acquisition remains a private, self-hosted, plugin-owned buying assistant.

- The dashboard stages intent.
- The plugin explicitly picks up and accepts a request.
- The user provides local consent once by launching the accepted route.
- Remote market data creates an advisory route.
- Live in-game market board rows are the purchase authority.
- The runner does not require routine user input after launch.
- The plugin buys only live-confirmed safe listings.
- Unknown, ambiguous, or catastrophic states stop the runner and ask for intervention.

The first full loop targets one dashboard request for one item. Multi-item queue orchestration can compose later by running one completed request after another.

## Primary Loop

1. Dashboard creates one or more acquisition request rows.
2. Plugin fetches pending requests manually from `/mmf`.
3. User claims and accepts one request locally.
4. Plugin prepares a sparse advisory plan from current external market data.
5. Plugin route-sorts worlds from the player's current world and current data center outward.
6. User starts the guided run.
7. For each world:
   - Ensure blocking UI is clear before any Lifestream command.
   - Travel or approach the market board.
   - Open the market board if nearby, otherwise use `/li <world> mb` or `/li mb`.
   - Search the accepted item by name and select the exact item id from results.
   - Wait for visible live listings.
   - Build the safe candidate pool from the current live rows.
   - Buy safe candidates in live unit-price order.
   - Re-read live listings after each confirmed purchase.
   - Continue while the request rules still permit buying.
   - Mark the world complete, skipped, under-procured, or failed.
8. After each world, rerank remaining route stops using confirmed live outcome.
9. Finish as `Complete`, `UnderProcured`, `Cancelled`, `Stopped`, or `Failed`.
10. Report final lifecycle state to the server/dashboard.

## Quantity Semantics

New requests expose only two quantity modes.

- `TargetQuantity`: buy cheapest safe whole stacks until the target is satisfied or safe stock runs out. Harmless overage is allowed because market board stacks cannot be partially bought.
- `AllBelowThreshold`: buy every confirmed live listing at or below max unit price, optionally limited by a positive gil cap and/or an optional max quantity.

`Exact` and `UpTo` are removed quantity modes. They are not functionally distinct under whole-stack live purchasing and should not appear in new dashboard UI, request validation, plugin planning, or purchase execution code.

Max unit price is mandatory. Gil cap is optional; blank or zero means no total spend cap. A positive gil cap is a hard stop. Dashboard quantity input means required target quantity for `TargetQuantity`; when `AllBelowThreshold` is selected, the same control is disabled or relabeled to `Max quantity`, and blank or zero means no quantity cap.

## Planning Contract

The external plan is advisory and sparse.

Recommended mode should include only worlds with useful supporting data, not every world in the region. `AllWorldSweep` remains an explicit advanced mode.

World route ordering:

1. Current world first when present in the plan.
2. Other worlds on the current data center.
3. Other data centers grouped to minimize backtracking.
4. Within each group, cheaper and higher-yield worlds first.

After every live world result, the remaining route may be reranked. If a current-world live candidate is cheaper than planned candidates elsewhere and satisfies the request rules, the live candidate wins.

## Live Candidate Rules

A candidate may be purchased only when all facts are true from the current live read:

- Current world matches the active route stop.
- Current market-board search item id matches the request item id.
- Listing item id matches the request item id.
- HQ flag satisfies the HQ policy.
- Unit price is less than or equal to max unit price.
- Quantity is positive.
- Listing id and retainer id are present.
- Remaining gil cap, when positive, can cover the whole stack.
- The candidate is still present on a fresh read immediately before the purchase action.

Remote listing id, remote retainer name, and external data age are hints only. They are not purchase authority.

## Purchase Loop

The current `Buy First Safe Listing` mechanism becomes the primitive used by a batch loop.

For each world batch:

1. Build safe candidates from live listings.
2. Sort by unit price ascending, then total gil ascending, then listing identity for stable display.
3. Select the cheapest current candidate.
4. Re-read and revalidate that exact candidate.
5. Execute one purchase.
6. Wait for success evidence:
   - confirmation accepted,
   - guarded listing disappears from fresh live listing data,
   - or a classified game/UI failure appears.
7. Record a purchase audit row.
8. Re-read live listings and rebuild the candidate pool.
9. Continue until the world's request rule is satisfied, safe stock runs out, budget is exhausted, or a stop condition occurs.

After a purchase succeeds, the loop continues on a rebuilt live candidate pool when remaining candidates are still within configured limits. Favorable drift such as cheaper replacement stock and more below-threshold stock is accepted automatically. Worse listings are skipped. Ambiguous changes stop the route and request intervention.

## Success And Terminal States

Request terminal states:

- `Complete`: request target is satisfied, or `AllBelowThreshold` has no remaining safe confirmed stock across planned route.
- `UnderProcured`: route completed safely but target quantity was not fully met.
- `Cancelled`: dashboard or local user cancelled before a terminal outcome.
- `Stopped`: user stopped the local run.
- `Failed`: unknown, unsafe, or non-recoverable condition stopped execution.

World stop states:

- `Pending`
- `TravelCommandSent`
- `Arrived`
- `Searching`
- `Purchasing`
- `Complete`
- `Skipped`
- `UnderProcured`
- `Failed`

Purchase attempt states:

- `Planned`
- `Revalidated`
- `PurchaseSent`
- `ConfirmationAccepted`
- `Purchased`
- `SkippedPriceChanged`
- `SkippedListingMissing`
- `SkippedBudgetExceeded`
- `SkippedHqMismatch`
- `InventoryFull`
- `InsufficientGil`
- `AmbiguousUi`
- `Timeout`
- `UnknownFailure`

`UnknownFailure` always stops the route.

## Hardening Rules

### UI Automation

Every game action must have a visible precondition and postcondition.

- Do not send Lifestream commands while known blocking UI is open.
- Do not search if the market board item search addon is not visible.
- Do not probe listings until the exact item result was selected and the listing results addon is visible.
- Do not buy if current item, world, or listing identity is ambiguous.
- Do not continue after an unknown confirmation prompt.
- Every UI wait has a watchdog and writes a diagnostic event.

### Economic Safety

- Max unit price is always enforced from live rows.
- Positive gil cap is always enforced before each purchase.
- Whole-stack overage is allowed only within max unit price and optional gil cap.
- Surprise cheaper stock is allowed and preferred.
- Surprise more expensive stock is skipped or stops for intervention when the state cannot be classified safely.

### Recovery

- Dashboard `Cancel` clears pending/claimed requests that have not begun live purchase execution.
- Dashboard `Resend` is only for pickup recovery and does not duplicate requests.
- Once a route is `Purchasing`, dashboard cancellation should request a local stop; the plugin owns the actual stop point.
- Plugin reload restores accepted claim/request metadata but not transient UI automation progress.
- A reload during `Purchasing` must leave the server request in a recoverable state: either `Running` with stale progress that can be failed/cancelled by dashboard, or `Failed` if the plugin can report during disposal.

### Server Audit

The server stores lifecycle and purchase summary facts, not secrets or raw client memory.

Audit rows should include:

- request id,
- lifecycle state,
- world,
- item id,
- quantity,
- unit price,
- total gil,
- HQ flag,
- listing id,
- retainer id/name when available,
- action result,
- failure classification,
- timestamp,
- plugin version,
- diagnostic log path or correlation id when available.

Audit rows must not include API keys, CSRF tokens, auth headers, raw game memory dumps, or full plugin config.

## UI Contract

Dashboard:

- Creates request rows.
- Shows queue status live.
- Shows latest plugin progress.
- Cancels or resends pending/claimed-but-not-running requests.
- Does not remote-control active purchases.

Plugin:

- Owns local consent.
- Owns live validation.
- Owns route execution.
- Shows compact route status.
- Keeps verbose diagnostics in the diagnostics popout.
- Provides `Start`, `Pause`, `Stop`, and `Restart`.
- Shows launch controls and explicit intervention states, not routine purchase confirmations.

## Failure Taxonomy

Continue safely:

- Planned listing missing but other safe live listings exist.
- Cheaper replacement listing appears.
- More below-threshold stock appears.
- Target quantity met with harmless overage.
- Current world has no safe stock, but route has remaining worlds.

Skip listing:

- Price rose above threshold.
- HQ mismatch.
- Listing disappeared before purchase.
- Gil cap would be exceeded.
- Quantity is zero or unreadable.

Stop world batch:

- No safe live candidates remain.
- Inventory looks full.
- Insufficient gil.
- Search item is wrong.
- Current world is wrong.

Stop route:

- Unknown purchase result.
- Ambiguous market-board UI.
- Lifestream repeatedly cannot reach the target world.
- Plugin loses accepted request/claim identity.
- Server rejects lifecycle progress with auth, claim, or terminal-state errors.

## Implementation Direction

The implementation should preserve existing boundaries and extend them instead of replacing them.

- Extend `MarketBoardPurchaseExecutor` from one-shot to batch orchestration.
- Keep `DalamudMarketBoardPurchaseAdapter` as the only unsafe purchase adapter.
- Add pure models for batch results and audit records before touching UI automation.
- Add route-runner methods for purchase progress instead of embedding route state in `MainWindow`.
- Keep `MainWindow` as the UI coordinator, but move batch-loop decisions into focused MarketAcquisition classes.
- Add focused tests first for quantity semantics, candidate rebuilding, batch stop conditions, and route purchase progression.

## Final Target

The first shippable full loop is:

- one request,
- one item,
- multiple planned worlds,
- one launch consent for the accepted route,
- multiple purchases per world,
- live re-read after every purchase,
- route continues across worlds,
- terminal server report when complete/under-procured/failed/stopped.

Multi-item queue automation, Discord push notifications, automatic retainer deposit, native cross-data-center travel, and Craft Architect-quality hosted planning are later modules.
