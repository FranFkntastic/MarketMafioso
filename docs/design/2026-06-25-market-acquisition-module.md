# Market Acquisition Module Design

Roadmap: `docs/design/2026-06-25-market-acquisition-roadmap.md`

## Goal

Add a client-side MarketMafioso module that helps buy market board stock for a requested item below a configured price threshold.

The first version should be semi-automatic:

- The user creates an acquisition request in the browser dashboard.
- The server stores that request for short-lived plugin pickup.
- The plugin fetches pending dashboard requests only when the user explicitly starts pickup from `/mmf`.
- MarketMafioso can guide or automate travel between eligible worlds.
- The user confirms once per world batch before purchases begin on that world.
- MarketMafioso does not ask before every individual purchase in a confirmed batch.

The important boundary: the browser dashboard can stage intent, but the plugin owns pickup, local acceptance, live in-game validation, and execution. Remote market data can propose a route, but live in-game market board listings are the authority before a purchase is sent.

## Product Boundary

This is an explicitly requested new module. It is separate from the old market-board and undercut experiments that otherwise remain out of scope.

This module should be named around acquisition rather than resale or undercutting. Suggested labels:

- UI tab: `Market Acquisition`
- Service area: `MarketAcquisition`
- Runner: `MarketAcquisitionRunner`

The feature is a buying assistant for user-requested procurement. It is not a selling, undercutting, arbitrage, or unattended botting system.

## Dashboard Request Pickup Model

The server should not push commands to the plugin, and the plugin should not constantly poll in the background.

Use a short-lived, plugin-initiated pickup flow:

1. User creates a request in the browser dashboard.
2. Server stores it as `PendingPickup`.
3. Dashboard shows a short expiry and tells the user to open MarketMafioso in-game.
4. User opens `/mmf`, selects `Market Acquisition`, and clicks `Fetch Dashboard Requests`.
5. Plugin performs a one-shot fetch for pending requests. Timed polling can be added later, but Phase 3 uses manual retry.
6. Plugin shows matching request candidates locally.
7. User chooses one candidate to claim and review.
8. User accepts or rejects the claimed request in-game.
9. Accepted requests move into local runner state.

This should feel like pairing a device: the dashboard stages the request, then the plugin deliberately reaches out to collect it.

## Dashboard Item Resolution

The dashboard item field should not require the user to know exact item IDs. Item identity should come from the shared XIV Data Gateway described in `docs/design/2026-06-25-shared-xiv-data-gateway.md`.

The acquisition dashboard should use a Craft Architect-style selector:

- Search by item name.
- Select an item result with metadata.
- Resolve the hidden `itemId` from that selection.
- Allow numeric item ID entry only when it resolves through the same gateway path.
- Reject unresolved free text before staging a request.

The dashboard should also grow a Plan Builder-like queue. The queue can contain multiple resolved acquisition rows, but the first storage and plugin pickup contract remains one acquisition request per item. `Stage Queue` creates one normal request per queued row.

Request lifecycle:

- `PendingPickup`
- `Claimed`
- `AcceptedInPlugin`
- `Running`
- `Complete`
- `Failed`
- `Rejected`
- `Cancelled`
- `Expired`

Commands are single-use. A request that is claimed or expired should not be returned to another pickup call.

Legal transitions:

- `PendingPickup -> Claimed`
- `PendingPickup -> Rejected`
- `PendingPickup -> Cancelled`
- `PendingPickup -> Expired`
- `Claimed -> AcceptedInPlugin`
- `Claimed -> Rejected`
- `Claimed -> Expired`
- `AcceptedInPlugin -> Running`
- `AcceptedInPlugin -> Cancelled`
- `Running -> Complete`
- `Running -> Failed`
- `Running -> Cancelled`

Terminal states are immutable. Duplicate retries of the same terminal update may return the original result when the idempotency key and body match, but they must not change the stored terminal state.

## Current Evidence

MarketMafioso already has useful automation patterns:

- `WorkshopAssemblyRunner` uses an explicit framework-tick state machine with pause, resume, stop, diagnostics, timeout handling, and staged progress.
- `WorkshopAssemblyUiAutomation` validates visible addon state before acting and records descriptive failures.
- `WorkshopRetainerRestockService` treats UI automation as an async workflow with bounded waits and explicit failure results.
- `docs/design/ui-automation-rules.md` already establishes the right rule set: every action needs a visible precondition and a visible postcondition.

Craft Architect has market-analysis and shopping-plan logic that is close to what this module needs:

- `MarketShoppingService` calculates world recommendations, split purchases, travel-aware route scoring, and fraud/gouging filtering.
- `DetailedShoppingPlan`, `WorldShoppingSummary`, `SplitWorldPurchase`, and `ShoppingListingEntry` provide a useful DTO shape for a world-batched purchase plan.
- `UniversalisService` already fetches `https://universalis.app/api/v2/{worldOrDc}/{itemIds}`, plus world and data-center metadata.
- The hosted Craft Architect web app appears to run market analysis in the WebAssembly client using IndexedDB-backed market cache services. I did not find a hosted market-plan API endpoint in the web server during this pass.

Dalamud and FFXIVClientStructs expose enough market board state to design a safe live-validation layer:

- `AgentItemSearch` / `InfoProxyItemSearch` track the active market board item search, result pages, selected listings, listing count, and purchase response path.
- `MarketBoardListing` includes listing id, retainer id, item id, quantity, unit price, total tax, HQ flag, town id, container index, materia, and stain fields.
- Dalamud network structures also model current offerings and purchase request fields, including listing id, retainer id, item id, quantity, unit price, HQ, tax, and city id.

## Non-Goals For The First Version

- No constant background command polling.
- No server-pushed control channel.
- No per-purchase confirmation prompts after a world batch is confirmed.
- No unattended cross-world purchasing from stale remote data.
- No blind sweep of every world in a region in the default mode.
- No selling, undercutting, repricing, flipping, or retainer-sale automation.
- No hidden packet-only purchase flow before a read-only market board probe proves that live listing capture is correct.
- No automated cross-data-center travel before a separate spike proves the UI flow is safe and stable.
- No direct dependency from the plugin on Craft Architect assemblies. The plugin should consume a small HTTP/JSON plan contract.

## User Workflow

1. User opens the browser dashboard and creates a market acquisition request:
   - Item name or item id.
   - Maximum unit price.
   - Quantity mode: target quantity or all listings below threshold.
   - HQ policy: NQ only, HQ only, or either.
   - World source: recommended worlds from market data, selected worlds, current world only, or explicit all-world sweep mode.
   - Optional total gil cap. Blank or zero means no total spend cap; max unit price remains mandatory.
2. Dashboard stores the request and shows pickup instructions.
3. User opens `/mmf` in-game and selects `Market Acquisition`.
4. User clicks `Fetch Dashboard Requests`.
5. Plugin shows matching pending request candidates for the current character/world.
6. User selects one request to claim.
7. Plugin claims the request and displays a compact summary.
8. User accepts or rejects the request locally.
9. If accepted, MarketMafioso enters local run preparation.
10. For each world:
   - MarketMafioso gets the user to that world, either by guided travel or supported travel automation.
   - MarketMafioso waits for the user to open the market board, or opens/searches it if that path is implemented safely.
   - MarketMafioso loads live listings for the item.
   - MarketMafioso builds a confirmed candidate pool from live listings at or below the max unit price.
   - The current read-only slice can already dry-run that candidate pool for the current visible market-board result set and report would-buy/skip rows without purchasing.
   - MarketMafioso presents one world-batch confirmation.
   - After confirmation, MarketMafioso purchases still-valid confirmed listings in lowest-price order until the target quantity, all-below-threshold rule, optional gil cap, inventory state, or a safety stop ends the batch.
11. The run ends as complete, paused, stopped, cancelled, or failed with diagnostics.

## Command Contract

The dashboard request should be a typed high-level intent, not arbitrary plugin instructions.

Example:

```json
{
  "commandId": "7f3659b9-ef77-4d96-9266-cf8a0b1b5c43",
  "type": "market_acquisition.request",
  "schemaVersion": 1,
  "targetCharacter": "Wei Ning",
  "targetWorld": "Siren",
  "createdAtUtc": "2026-06-25T20:10:00Z",
  "expiresAtUtc": "2026-06-25T20:11:30Z",
  "payload": {
    "itemId": 5057,
    "itemName": "Darksteel Nugget",
    "maxUnitPrice": 430,
    "maxTotalGil": 160000,
    "quantityMode": "upTo",
    "quantity": 400,
    "hqPolicy": "either",
    "worldMode": "recommended",
    "dataCenter": "Crystal"
  }
}
```

The server can create and store this request from the dashboard. The plugin can claim it only through the plugin pickup endpoint. The dashboard should not be able to mark a request as accepted, running, or complete on behalf of the plugin.

All Market Acquisition routes are mounted under the existing receiver base path. In hosted dev, that means `/api/marketmafioso/acquisition/...`. The logical routes below are relative to that base path.

Endpoint shape:

- `POST /acquisition/requests` creates a dashboard request.
- `GET /acquisition/requests/pending?character=Wei%20Ning&world=Siren` returns pending pickup candidates for the authenticated plugin pickup capability. This is read-only and does not reserve a request.
- `POST /acquisition/requests/{id}/claim` atomically claims one request for a plugin instance and returns a server-generated `claimToken`.
- `POST /acquisition/requests/{id}/accept` records local plugin acceptance. Requires the matching `claimToken`.
- `POST /acquisition/requests/{id}/reject` records local plugin rejection. Requires the matching `claimToken`.
- `POST /acquisition/requests/{id}/progress` records runner state. Requires the matching `claimToken`.
- `POST /acquisition/requests/{id}/complete` records completion. Requires the matching `claimToken`.
- `POST /acquisition/requests/{id}/fail` records failure. Requires the matching `claimToken`.

The request, claim, progress, and result records are persisted for audit/debugging. Ownership fields such as account, creator, and actor labels are derived from authentication/session context; client-provided ownership fields are ignored if they conflict with authenticated context.

Every state-changing endpoint accepts an idempotency key. Replayed calls with the same key and same body return the original result. Replayed calls with the same key and a different body return conflict.

Claim must be atomic. The server may claim a request only when all conditions are true in one transaction:

- Current state is `PendingPickup`.
- `expiresAtUtc` is greater than server `UtcNow`.
- Request account, target character, and target world match the authenticated plugin pickup scope.
- No `claimedAtUtc`, `claimedBy`, or `claimToken` value has already been recorded.

On successful claim, the server records `claimedAtUtc`, `claimedBy`, `claimToken`, and `claimExpiresAtUtc`. `expiresAtUtc` is pickup expiry. `claimExpiresAtUtc` is local review expiry; first implementation should use 5 minutes. Claimed requests that are not accepted or rejected before `claimExpiresAtUtc` become `Expired` and are never returned as pending again.

## Batch Confirmation Contract

The confirmation prompt should be world-scoped, not purchase-scoped.

It should display:

- World name and data center.
- Item name and id.
- HQ policy.
- Remote data source and data age.
- Live market board refresh age if available.
- Planned quantity.
- Number of listings to buy.
- Cheapest and most expensive unit price in the batch.
- Estimated subtotal, tax if known, and maximum possible gil spend.
- Remaining run budget.

The confirmation should be invalidated and re-shown if the live listing set changes before purchases begin.

## Data Source And Plan Boundary

Define a provider boundary that can exist on either the server or plugin side without leaking Craft Architect internals:

```csharp
public interface IMarketAcquisitionPlanSource
{
    Task<MarketAcquisitionPlan> BuildPlanAsync(
        MarketAcquisitionPlanRequest request,
        CancellationToken cancellationToken);
}
```

Suggested request fields:

- `ItemId`
- `ItemName`
- `DataCenter`
- `WorldMode`
- `CandidateWorlds`
- `MaxUnitPrice`
- `QuantityMode`
- `RequestedQuantity`
- `HqPolicy`
- `MaxTotalGil`
- `AllowSplitWorld`

Suggested response shape:

- `PlanId`
- `SourceName`
- `FetchedAtUtc`
- `ItemId`
- `ItemName`
- `DataCenter`
- `WorldBatches`
- `RejectedWorlds`
- `Warnings`

Each `WorldBatch` should include:

- `WorldId`
- `WorldName`
- `DataCenter`
- `MarketDataAge`
- `PlannedQuantity`
- `EstimatedTotalGil`
- `Listings`

Each planned listing should include:

- `RemoteListingId` if available.
- `RetainerName`
- `Quantity`
- `NeededFromStack`
- `PricePerUnit`
- `IsHq`
- `LastReviewTimeUtc`

The first provider can be either:

- `ServerDashboardAcquisitionPlanSource`: the dashboard/server builds the request payload and optionally includes a precomputed world plan.
- `UniversalisMarketAcquisitionPlanSource`: calls Universalis directly and ports the minimum necessary Craft Architect filtering logic.
- `CraftArchitectMarketAcquisitionPlanSource`: calls a new Craft Architect-compatible HTTP endpoint once we expose one.

The plugin should not care which provider produced the plan. In the dashboard pickup model, the plugin can start from the claimed request and either consume a server-provided plan or locally refresh/rebuild a plan before execution.

## World Selection And Route Shape

Default operation should not mean "visit every world in the region."

The normal route should be data-supported and sparse:

- Start from worlds that have current listings at or below the requested threshold.
- Prefer worlds with enough quantity to matter.
- Prefer fresher market data.
- Prefer lower total cost after stack quantity and gil cap are considered.
- Exclude worlds where the only supporting data is stale, overpriced, inaccessible, or too thin to justify travel.

The dashboard/server may use region-wide market data to discover candidates, but the resulting request should send the plugin a ranked list of worlds of promise. The plugin should travel only to planned world batches unless the user explicitly chooses a broader operational mode.

Supported modes:

- `recommended`: default. Visit only ranked worlds in the configured region with supporting data.
- `selected`: user manually chooses worlds in the dashboard.
- `currentWorldOnly`: do not travel; only use the current world.
- `allWorldSweep`: explicit advanced mode. Visit every accessible world in the configured region, even when the price data is weak or absent.

`allWorldSweep` should be visually distinct and should require an explicit dashboard-side choice. It is useful as an exploration mode, not as the default procurement path.

## Craft Architect Integration Options

### Option A: Direct Universalis Provider First

MarketMafioso fetches Universalis data directly and applies a small local planner:

- Filter listing item id, HQ policy, and max unit price.
- Sort by unit price, then listing age, then retainer name for stable display.
- Group by world.
- Rank worlds by data-supported promise; do not emit every world by default.
- Stop each world batch at requested quantity or total budget.
- Display data age prominently.

This is fastest and avoids making the plugin depend on the state of the Craft Architect hosted app.

### Option B: Add A Small CA-Compatible Market Plan Endpoint

Expose an endpoint from a backend service that accepts the acquisition request and returns the DTO above.

This preserves CA's richer market trimming, travel scoring, and route planning without coupling MarketMafioso to CA binaries. It also makes future self-hosted users able to choose whether they want CA-backed planning or direct Universalis planning.

### Recommendation

Use a hybrid of Option A and the pickup model for the first implementation slice:

- Dashboard creates the request.
- Plugin picks it up on demand.
- Plugin can locally refresh/rebuild the simple Universalis plan before running.
- DTOs stay shaped so a richer Craft Architect plan endpoint can replace the local planner later.

This is less elegant than using all of Craft Architect immediately, but it avoids an extra server integration dependency while we are still proving the in-game acquisition workflow.

## Authentication And Information-Flow Safety

Self-hosting keeps the trust boundary small, but the request flow still needs to prevent malicious parties from tapping into inventory, request, or execution traffic.

Rules:

- All hosted traffic uses HTTPS.
- Local loopback HTTP is allowed only for local development.
- Market Acquisition uses two separate capabilities:
  - Dashboard Basic Auth may view the dashboard and create browser-originated acquisition requests.
  - Client API key auth is used by the plugin for inventory ingest, machine-read report routes, pending pickup, claim, accept, reject, progress, complete, and fail endpoints.
- Client API key auth uses the plugin-wide configured secret: `MarketMafioso:ClientApiKey`.
- Hosted/API-key mode fails at startup if `MarketMafioso:ClientApiKey` is missing.
- Dashboard session/auth can create requests, but cannot claim or execute them.
- Endpoint authorization is fixed:
  - `POST /acquisition/requests` as a non-browser JSON lifecycle endpoint requires dashboard auth.
  - Browser-originated request creation requires dashboard/read auth and CSRF. Hosted deployments must set `MarketMafioso:TrustExternalDashboardAuth=true` only when the dashboard is protected by an external layer such as Caddy Basic Auth.
  - Dashboard queue recovery actions require dashboard/read auth and CSRF.
  - Dashboard `Cancel` marks a request cancelled and clears stale claim ownership.
  - Dashboard `Resend` clears stale claim ownership and returns the existing request to `PendingPickup`; it does not create a duplicate row.
  - `GET /acquisition/requests/pending`, `POST /acquisition/requests/{id}/claim`, and all plugin lifecycle mutation routes require client API key auth.
- Browser-originated mutation routes require the server's CSRF token pattern. API keys are not CSRF tokens and must not be embedded in dashboard forms or JavaScript.
- Pickup requests are scoped to account, character, and world.
- Requests expire quickly, preferably 30-90 seconds for `PendingPickup`.
- Commands are single-claim and single-use.
- Secrets are never placed in URLs.
- Secrets are generated outside dashboard HTML, are redacted from logs and diagnostics, and are never included in audit records.
- Request payloads are typed and versioned.
- Request payloads require hard bounds: positive quantity when the selected quantity mode uses a target, positive max unit price, optional non-negative gil cap, allowed enum values, supported schema version, and server-capped expiry.
- No arbitrary code, script, low-level UI selector, or raw click instruction is accepted.
- Server records who created the request, when the plugin claimed it, and the plugin-reported final result.
- Pending and claim endpoints fail closed and avoid enumeration. Unauthorized or wrong-scope callers receive generic unauthorized or not-found responses that do not reveal whether a request id, character, world, item, or gil cap exists.
- Dashboard cancel/resend actions are queue recovery controls. They may recover a stranded claimed request before real purchase execution exists, but they must not become a remote control surface for an active in-game runner.
- Pickup, claim, and lifecycle mutation endpoints should have conservative rate limits and request-size limits.
- Audit records include lifecycle event timestamps, request id, schema version, actor type, non-secret actor label, source route, previous state, next state, and failure reason when applicable. Audit records must not store auth headers, API keys, CSRF tokens, raw plugin config, or unnecessary live game diagnostic dumps.
- Acquisition request/audit retention is separate from inventory snapshot and raw JSON retention.

## Live Market Board Reconciliation

Before every purchase send, MarketMafioso must re-read the current in-game listing and validate:

- Item id matches the requested item.
- HQ flag matches the selected policy.
- Unit price is less than or equal to the threshold.
- Quantity is positive.
- Listing id and retainer id still match the live row being targeted.
- Total spend stays under the remaining budget.
- The user is on the expected world.
- The current market board search is for the expected item.

If any validation fails, skip that listing or stop the batch with a visible diagnostic. Do not substitute another listing without re-running batch reconciliation.

Remote listing ids should be treated as hints only. They may be stale, missing, or absent depending on source. The live in-game listing row is the purchase authority.

Implementation must not match or purchase by remote listing id unless a live probe proves that id maps exactly to the current in-game row for the current patch.

## Runner State Machine

Suggested states:

- `Idle`
- `AwaitingDashboardRequest`
- `FetchingDashboardRequests`
- `RequestClaimed`
- `AwaitingLocalRequestAcceptance`
- `Planning`
- `PlanReady`
- `AwaitingRunStart`
- `PreparingWorldBatch`
- `AwaitingWorldTravel`
- `WaitingForExpectedWorld`
- `WaitingForMarketBoard`
- `SearchingMarketBoardItem`
- `WaitingForLiveListings`
- `ReconcilingLiveListings`
- `AwaitingBatchConfirmation`
- `PurchasingBatch`
- `WaitingForPurchaseResult`
- `WorldBatchComplete`
- `Paused`
- `Stopped`
- `Failed`
- `Complete`

The runner should use the existing framework-tick/state-machine style rather than a linear click script.

Every state should have:

- A timeout or explicit external wait reason.
- A diagnostic snapshot.
- A clear user-facing status line.
- A cancellation path.

## World Travel Boundary

The end-state feature should be able to move through world stops efficiently, but MarketMafioso should not hand-roll native world-travel UI automation while Lifestream can cover the route. The first implementation should treat travel as a Lifestream-assisted orchestration boundary.

Travel targets should come from accepted world batches, not from every world in a region. If a request uses `recommended`, the plugin should only guide or automate travel to those recommended worlds. If a request uses `allWorldSweep`, the UI should make that broader mode obvious before execution.

Recommended first slice:

- Support Lifestream-guided travel:
  - Show the next destination.
  - Issue or present the expected `/li <world> mb` command.
  - Wait until the current world matches the batch world.
  - Continue automatically once the world is detected.
  - Retry the read-only market-board probe after arrival until visible listings are ready or a diagnostic failure is reported.
- Keep an internal `IWorldTravelDriver` boundary:
  - `LifestreamWorldTravelDriver`
  - `ManualWorldTravelDriver` as fallback
  - Future `AetheryteWorldTravelDriver`
  - Future `DataCenterTravelDriver`

This lets us build the purchase planner and live market-board validation without mixing in native aetheryte UI-navigation work on day one.

## Purchase Execution Boundary

Use an adapter boundary around any purchase action:

```csharp
public interface IMarketBoardPurchaseExecutor
{
    MarketBoardPurchaseReadiness InspectReadiness(MarketAcquisitionLiveListing listing);
    Task<MarketBoardPurchaseResult> PurchaseAsync(
        MarketAcquisitionLiveListing listing,
        CancellationToken cancellationToken);
}
```

First implementation mode should include a read-only dry run:

- Load the live item search.
- Capture listings.
- Reconcile against threshold and plan.
- Show exactly what would be bought.
- Do not send purchases.

Only after that proves stable should `PurchaseAsync` send purchase requests.

## UI Shape

Add a `Market Acquisition` tab to the existing `/mmf` window. This tab is not the primary control panel. The browser dashboard owns request creation and richer planning. The plugin tab is a local pickup, consent, and execution surface.

The HTML mockup is a layout reference only. The implemented plugin UI must use the existing `MainWindow` ImGui pattern: native tab item, `TextColored` header, `TextWrapped` summary, `ImGuiUi.SectionHeader`, `SameLine` button rows, and `BeginTable` for compact data. Do not add a custom titlebar, persistent side panel, card grid, or CSS-style mini-panel layout. Use the existing module name `Workshop Prep` everywhere; add `Market Acquisition` without renaming existing modules.

Primary controls:

- Dashboard URL display and `Open Dashboard`.
- `Fetch Dashboard Requests`, active only when the server URL/auth state is valid.
- Phase 3 pickup is one-shot manual fetch plus manual retry. Do not show a countdown or `Stop Fetching` button until timed polling is implemented.
- Pending request summary.
- `Accept Request` and `Reject` buttons.
- Local runner controls after acceptance: `Prepare`, `Dry Run`, `Pause`, `Stop`.

The tab should match the existing ImGui style shown by Inventory Reporter and Workshop Prep:

- Text header and short summary.
- Section headers with separators.
- Full-width text fields where useful.
- Compact buttons.
- Compact `BeginTable` tables only when a request or run is present.
- No persistent sidebars, dashboard panes, sticky browser-grid behavior, or dense external-control layout.

Recommended sections:

### Dashboard Pickup

- Dashboard URL.
- `Open Dashboard`.
- `Fetch Dashboard Requests`.
- Status line: idle, fetching, request found, expired, auth failed, no requests.
- `Fetch Dashboard Requests` is enabled only when the configured receiver URL can produce a dashboard URL, the acquisition pickup endpoint can be derived, and the client API key is present. Invalid URL, missing secret, and unsupported endpoint must show separate status text.
- If multiple pending requests match the current character/world, show them in a compact table and let the user claim one. Do not automatically claim an arbitrary request unless the server returns exactly one candidate and the user has enabled that behavior later.

### Pending Request

Shown only when a request is claimed but not yet accepted or rejected.

Compact table:

- Item
- Quantity
- Max unit
- Gil cap
- Scope
- Expires

Actions:

- `Accept Request`
- `Reject`

Before acceptance, show character/world, item id/name, quantity mode, HQ policy, max unit price, max total gil if present, world mode, request age, expiry, and source. Disable `Accept Request` if max unit price, item id, quantity mode, or target character/world is missing or mismatched.

Claimed but unaccepted requests persist enough local state to survive plugin reload: request summary, claim token, and lifecycle idempotency keys. If the dashboard cancels or resends the request while the plugin still has stale local claim state, the plugin can forget the local claim and fetch again. `Accept Request` must not start planning, travel, market-board reads, dry-run execution, or purchases. It only records local consent and moves the request into the local accepted state.

### Local Runner

Shown once a request is accepted.

Compact summary:

- Current state.
- Action required.
- Current world.
- Expected world.
- Item/search status.
- Planned quantity.
- Remaining quantity.
- Estimated spend.
- Remaining cap.
- Last validation result.
- Last server progress result.

Use action-specific labels instead of ambiguous `Dry Run` where possible: `Prepare Plan`, `Read Market Board`, `Dry Run Batch`, and `Start Guided Run`. Disabled controls must have adjacent muted text explaining the missing prerequisite.

Optional world table:

- World
- Listings
- Quantity
- Estimated total
- Status

### Diagnostics

Small text block, not a separate pane:

- Last server response.
- Last runner state.
- Last validation failure.
- Diagnostic dump path if one exists.

### Confirmation UI

There are two separate confirmations:

- Local request acceptance confirms intent only.
- World-batch confirmation authorizes the next batch after live listing reconciliation.

World-batch confirmation must be an explicit ImGui confirmation block or popup that lists world, item, HQ policy, quantity rule, confirmed listing count, cheapest and highest unit price, estimated spend, remaining gil cap when configured, market data age, live refresh time, and exact stop conditions. The primary button should read `Confirm This World Batch`.

If live listings, current world, current search item, price, quantity, or remaining budget change after confirmation but before the first purchase, immediately invalidate the confirmation, disable the batch action, and show `Live listings changed; reconcile again before continuing.`

Prefer inline confirmation for low-risk actions to match the existing VIWI queue sync pattern. Reserve modal popups for high-risk world-batch purchase confirmation, and keep them compact enough for the current `MainWindow` minimum size.

### Error States

Every failed pickup, plan, live-read, travel, confirmation, and purchase state must produce one visible user-facing status line and one diagnostic detail string. Never show only `Failed`; include the state name and the specific failed precondition or response.

Known safe stops are item mismatch, world mismatch, market board not open, wrong search item, listing disappeared, price above threshold, budget exceeded, insufficient gil, inventory full, ambiguous listing selection, expired request, auth failed, and unknown purchase result. Unknown states stop the runner by default.

## Safety Rules

- Require a max unit price before live purchasing is enabled.
- Treat max total gil as optional. Blank or zero means no total cap; a positive value is a hard spend stop.
- Default routes must contain only planned worlds with supporting market data.
- Never buy a listing whose live unit price exceeds the threshold.
- Never exceed the configured total gil cap when one is set.
- Treat the server/dashboard plan as advisory. Live in-game listings are authoritative at purchase time.
- Purchase only from a confirmed candidate pool built from live market board rows read during the current run.
- The first confirmed-candidate implementation is read-only: after `Read Live Listings`, the plugin sorts visible live rows by unit price and reports `WouldBuy`/`Skipped` decisions without selecting rows or sending purchases.
- Lowest confirmed live unit price wins. If a newly discovered below-threshold listing is cheaper than listings on later planned worlds, buy the cheaper confirmed listing first.
- Remove the old `Exact` and `UpTo` quantity semantics from new requests. `TargetQuantity` replaces both and buys safe whole stacks until the target is satisfied or safe stock runs out. Harmless whole-stack overage is allowed.
- `AllBelowThreshold` buys every confirmed live listing at or below the max unit price, optionally bounded by a configured gil cap.
- Never keep purchasing after a purchase response error unless the error is explicitly classified as safe to continue.
- Stop on item mismatch, world mismatch, market board search mismatch, insufficient gil, inventory full, unknown confirmation prompt, or ambiguous listing selection.
- If live listings differ from the confirmed batch before the first purchase, invalidate the confirmation and rebuild the confirmed candidate pool.
- If live listings differ after a successful purchase, re-read and continue only with still-valid listings under the original batch confirmation.

## Testing Plan

Unit tests:

- Dashboard request expiry and lifecycle transitions.
- Single-claim behavior.
- Character/world scoping.
- Plugin pickup rejects expired or already-claimed requests.
- Planner filters by price threshold.
- Planner respects HQ policy.
- Planner respects requested quantity.
- Planner respects max total gil when configured.
- Planner groups listings into world batches.
- Planner excludes non-promising worlds by default.
- Planner supports explicit all-world sweep mode separately from recommended mode.
- Batch confirmation invalidates when live listings change.
- Live reconciliation rejects item id, HQ, world, quantity, and price mismatches.
- Purchase loop stops at budget, quantity, or unsafe purchase result.

Integration-style tests with fakes:

- Fake dashboard request source.
- Fake plan source.
- Fake current-world service.
- Fake market board listing reader.
- Fake purchase executor.
- Runner state transitions for happy path, skipped listing, price changed, listing disappeared, user pause, user stop, and timeout.

Manual verification:

- Create a dashboard request and pick it up in-game.
- Confirm expired requests do not appear after the pickup window.
- Confirm wrong-character requests do not appear.
- Confirm recommended mode only includes worlds with supporting listings under the threshold.
- Confirm all-world sweep is visually and behaviorally distinct from recommended mode.
- Dry-run live listing capture on a common item.
- Dry-run on a mixed HQ/NQ item.
- Dry-run after listings change externally.
- One-item low-value purchase test with a strict gil cap.
- Multi-listing world-batch purchase test with one confirmation.

## Implementation Slices

### Slice 1: Dashboard Request Pickup And Plugin UI

- Add `MarketAcquisition` service/model folder.
- Add request lifecycle DTOs.
- Add server endpoints for creating, claiming, accepting, rejecting, and expiring requests.
- Add `Market Acquisition` tab.
- Add manual `Fetch Dashboard Requests` flow.
- Display pending request summary and accept/reject controls.
- No game UI automation and no purchases.

### Slice 2: Planner And UI Dry Run

- Add acquisition plan DTOs.
- Add Universalis-backed plan source.
- Build or refresh a plan from an accepted request.
- Display compact world batch summary.
- No game UI automation and no purchases.

### Slice 3: Live Market Board Read-Only Probe

- Add market board listing reader.
- Detect current world.
- Load or wait for live market board search.
- Reconcile live rows against planned rows.
- Show live validation status in the UI.
- Produce diagnostics.
- Current patch proof succeeded for visible rows. `WaitingForListings` can remain true while rows are visible, so populated rows are treated as ready with a diagnostic note.

### Slice 3.5: Current-World Live Candidate Evaluation

- Build confirmed candidates from the current visible live market-board result set.
- Sort candidates by live unit price.
- Include cheaper replacement listings and extra below-threshold stock as favorable drift.
- Respect HQ policy, max unit price, and optional gil cap.
- Report aggregate `Ready`, `UnderProcured`, or `NoSafeListings`.
- Show a compact summary in the main plugin UI.
- Put per-row `WouldBuy` or `Skipped` decisions in a diagnostics popout.
- No row selection, purchase calls, travel automation, or server lifecycle completion.

### Slice 4: Lifestream-Guided World Batch Runner

- Add runner state machine.
- Add Lifestream-assisted travel driver, expected first as `/li <world> mb`.
- First implementation shows, copies, and executes `/li <world> mb` through Dalamud command dispatch.
- If command dispatch reports no handler, keep the active stop unchanged and show a clear status.
- Detect arrival/current world automatically before probing each destination.
- Retry the read-only live listing probe after arrival so the user does not need to press `Read Live Listings` manually after Lifestream opens the market board.
- Add batch confirmation UI.
- Add dry-run batch execution.
- Keep purchase executor disabled by default.

### Slice 5: Guarded Purchase Executor

- Add purchase executor behind an explicit setting.
- Revalidate before every purchase send.
- Confirm once per world batch.
- Stop on unsafe or unknown response.
- Add tests for every stop condition.

### Slice 6: Native Regional Travel Automation Spike

- Investigate same-data-center and cross-data-center world travel UI automation only if Lifestream cannot cover required routes.
- Add `AetheryteWorldTravelDriver` only after preconditions and postconditions are documented.
- Keep Lifestream/manual travel available.

## Locked Defaults For First Implementation

- Acquisition scope is region-wide by default. Recommended mode may consider all worlds in the configured FFXIV region, but it emits only worlds with supporting listings under the threshold.
- Cross-data-center automated travel remains gated behind the regional travel probe. Until that probe passes, region-wide plans can still be executed through guided/manual travel.
- Quantity mode starts with `TargetQuantity` and `AllBelowThreshold`. The old `Exact` and `UpTo` aliases should be removed from new dashboard requests because they are not functionally unique under live, whole-stack purchase execution.
- HQ policy defaults to `Either`, with explicit `NQ only` and `HQ only` options available in the dashboard request form.
- Purchase execution requires live in-game listing validation. Fresh external market upload age is displayed and used for planning confidence, but it is not the final purchase authority.
- First planning uses direct Universalis-backed threshold filtering. A Craft Architect-quality planning endpoint is a later integration, not a dependency for the first implementation.

## Remaining Investigation Gates

- Live market board probe must prove the current patch's listing fields, loading/no-listing states, row identity tuple, and pagination behavior before dry-run reconciliation is accepted.
- Purchase probe must prove the purchase packet path, success/failure observation, listing-disappeared behavior, price-change behavior, inventory-full behavior, and insufficient-gil behavior before any purchase executor can merge.
- World travel probe must prove visible preconditions, action steps, postconditions, timeout behavior, and failure surfaces before automated travel can merge.
