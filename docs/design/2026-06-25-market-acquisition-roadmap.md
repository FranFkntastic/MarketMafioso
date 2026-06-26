# Market Acquisition Roadmap

## Goal

Build Market Acquisition as a private, self-hosted dashboard-to-plugin workflow where the browser dashboard stages a request, the plugin explicitly picks it up, and the plugin owns all in-game validation and execution.

This roadmap is intentionally investigation-heavy. The feature touches three areas with different risk profiles:

- Server-side request staging and audit.
- Plugin-side request pickup and local consent.
- In-game market board reading, travel, and purchase execution.

The safe order is to make the server and plugin speak first, then prove read-only market-board visibility, then add guided world-batch orchestration, and only then consider guarded purchases.

## Roadmap Principles

- Dashboard requests are high-level intent, not remote-control instructions.
- The plugin never constantly polls in the background.
- The plugin must explicitly pick up requests from `/mmf`.
- A dashboard request can suggest promising worlds, but the plugin must validate live market-board rows before purchase.
- Purchase decisions are price-led after live confirmation. The server plan is advisory; confirmed live listings read during the current run are the purchase authority.
- New dashboard requests expose only two quantity modes: `TargetQuantity` and `AllBelowThreshold`. Legacy `Exact` and `UpTo` values are accepted only as migration aliases for `TargetQuantity`.
- Recommended-world mode is the default and may consider the configured region. Full regional sweep is a separate explicit mode.
- Every phase must be useful and testable without assuming later automation exists.
- Any phase that touches game UI automation must have a read-only proof pass before mutation.
- Plugin pickup auth uses the same client API key as inventory ingest and machine-read report routes.
- Logical acquisition routes live under `{basePath}/acquisition/...`; hosted dev resolves that to `/api/marketmafioso/acquisition/...`.

## Live Execution Semantics

- `TargetQuantity` buys the cheapest confirmed safe live listings until the requested target is satisfied or safe stock runs out. Whole-stack overage is allowed.
- `AllBelowThreshold` buys every confirmed live listing at or below the max unit price, optionally bounded by a positive gil cap.
- Blank or zero gil cap means no total spend cap. Max unit price remains mandatory and is always a hard safety threshold.
- Lowest confirmed live unit price wins. If a newly discovered below-threshold listing is cheaper than listings on later planned worlds, buy the cheaper confirmed listing first.
- Favorable drift is a valid success path: cheaper prices, replacement listings, and newly available below-threshold stock should be folded into the candidate pool instead of treated as plan mismatch.
- Missing planned listings and worse prices are not automatically fatal. They become under-procurement or skipped listings unless no safe candidate remains or the UI state is ambiguous.
- Purchases may only be sent for confirmed live rows from the current market-board read. Unconfirmed remote listings are route hints only.

## Progress Snapshot

Current status after the 2026-06-25 local-dev pass:

| Phase | Status | Notes |
| --- | --- | --- |
| Phase 0: Baseline Alignment | Done | Design docs, dashboard/plugin boundary, client API key model, and self-hosted/private assumptions are established. |
| Phase 1: Server Request Lifecycle | Done | Server stores dashboard-created acquisition requests, supports pickup lifecycle, and uses the unified client API key for plugin routes. |
| Phase 2: Dashboard Request Creation Surface | Done | Dashboard has a Market Acquisition surface, item search/ID resolution, queue staging, diagnostic error display, optional gil cap, request list, and queue recovery actions to cancel or resend stranded requests. Quantity modes still need to be simplified from legacy `Exact`/`UpTo`/`AllBelowThreshold` UI to `TargetQuantity`/`AllBelowThreshold`. |
| Phase 3: Plugin Request Pickup UI | Done | `/mmf` has a Market Acquisition tab with one-shot fetch, claim, accept, reject, persisted active claim restore after plugin reload, local claim forget, and shared plugin-wide server/API-key settings. |
| Phase 4: Market Planning Dry Run | Done | Accepted requests can prepare a Universalis-backed advisory plan and display world/listing batches. Planner semantics now need to align with the two-mode quantity model and optional gil cap everywhere. |
| Phase 5: Live Market Board Read-Only Probe | Done for visible rows | In-game probe succeeded on current patch: item id, visible listing rows, listing id, retainer id/name, HQ flag, unit price, and quantity populated correctly. The `WaitingForListings` flag can remain set while visible rows exist, and the reader now treats populated rows as ready with a diagnostic note. Current-world live candidate evaluation is included in this phase because it only classifies the visible page after the read-only probe. Remaining risk moves to pagination/deeper listing-page behavior. No purchase path exists. |
| Phase 5.5: Current-World Live Candidate Evaluation | Done for visible rows | After `Read Live Listings`, the plugin validates item/world, builds a confirmed live candidate pool, sorts by live unit price, supports favorable drift, respects HQ/max-unit/gil-cap constraints, and reports would-buy/skip/under-procure outcomes without purchasing. Verbose tables live in a diagnostics popout so the main Market Acquisition tab stays operational. |
| Phase 6: Lifestream-Guided World-Batch Orchestration | Partially done | The plugin can start a volatile guided route from the prepared plan, show/copy/execute the next `/li <world> mb` command through Dalamud command dispatch, check whether the current world matches the active stop, and advance stops after a successful live probe/dry-run. Server progress reporting, pause/resume, and live re-ranking across remaining stops are still pending. |
| Phase 7: Purchase Mechanism Investigation | Not started | Blocks any real purchase executor. Must prove the purchase path, success/failure observation, and safe stop behavior with low-value current-world tests. |
| Phase 8: Guarded Purchase Execution | Blocked | Only starts if Phase 7 proves a safe purchase mechanism. |
| Phase 9: Travel Automation Spike | Deferred | Only needed if Lifestream cannot cover the required region/world routes. Until then, travel work is an integration/orchestration problem rather than native aetheryte automation. |
| Phase 10: Craft Architect Plan Integration | Deferred | Wait until the core loop is proven and a clean HTTP/JSON service boundary exists. |

## Phase 0: Baseline Alignment

### Objective

Lock the design, docs, and UI expectations before code work starts.

### Work

- Keep `docs/design/2026-06-25-market-acquisition-module.md` as the product/design source of truth.
- Keep `mockups/market-acquisition-initial-ui.html` as the plugin-window UI reference.
- Add this roadmap as the engineering sequence and investigation register.

### Baseline Decisions

- Implementation starts from the branch/worktree chosen by the maintainer for the coding pass. Before Phase 1 begins, that target must be written into the task notes and verified with `git status`.
- If the target branch already has SQLite support, reuse the existing storage/migration pattern. If it does not, Phase 1 introduces SQLite for acquisition request lifecycle data.
- Use the plugin-wide `MarketMafioso:ClientApiKey` for command pickup, inventory ingest, and machine-read report routes. Legacy split-key settings are migration aliases only.
- Acquisition scope is region-wide. Recommended mode filters that region down to worlds with supporting listings; all-world sweep is explicit and visually distinct.
- Product choices that affect module behavior are not silently converted into defaults. If a later phase exposes a real product fork, pause and ask the maintainer before locking it.

### Exit Criteria

- Design doc and roadmap agree on lifecycle names, request fields, and default world-selection behavior.
- Mockup reflects the current Dalamud/ImGui window style, not a server dashboard.

## Phase 1: Server Request Lifecycle

### Objective

Add a server-side request queue that can store, expire, claim, and audit dashboard-created acquisition requests without involving the game client yet.

### Proposed Files

- Create `MarketMafioso.Server/MarketAcquisitionRequestModels.cs`
- Create `MarketMafioso.Server/MarketAcquisitionRequestStore.cs`
- Modify `MarketMafioso.Server/Program.cs`
- Add tests in `MarketMafioso.Server.Tests/MarketAcquisitionRequestEndpointTests.cs`

### Capability

- Dashboard can create a request.
- Phase 1 creation is an authenticated JSON lifecycle endpoint; the browser dashboard form and CSRF workflow land in Phase 2.
- Server can return pending requests for a target character/world.
- Plugin-style caller can claim one request atomically.
- Claimed, expired, rejected, completed, and failed requests stop appearing as pending.
- Server records lifecycle timestamps and actor/source labels.

### Investigation Points

- Storage and atomic claim mechanism:
  - First implementation uses SQLite for acquisition requests, progress events, idempotency keys, and audit events.
  - Claims use a transaction that updates only `PendingPickup` rows that are not expired and not already claimed.
  - Request payload, current status, lifecycle timestamps, actor labels, claim metadata, and progress events must survive server restart.
- Auth:
  - Current hosted receiver uses one client API key for plugin-to-server traffic.
  - Command pickup uses the same configured key: `MarketMafioso:ClientApiKey`.
  - Hosted/API-key mode fails at startup if `MarketMafioso:ClientApiKey` is missing.
  - Dashboard Basic Auth can create browser-originated acquisition requests, but cannot claim, accept, reject, progress, complete, or fail plugin lifecycle.
- Path/base-path behavior:
  - All new endpoints are logical `/acquisition/...` routes under the existing base path.
  - Hosted dev path must be `/api/marketmafioso/acquisition/...`.
  - Tests must fail on accidental `/api/plugin/...` routes.
- Expiry cleanup:
  - Expired requests can be marked lazily on reads.
  - Pending requests expire after the configured pickup window.
  - Claimed requests get `claimExpiresAtUtc`, default 5 minutes after claim.
- Claim token:
  - Successful claim returns a server-generated `claimToken`.
  - Accept, reject, progress, complete, and fail require the matching claim token.
- Idempotency:
  - Dashboard create and plugin state-changing calls require an idempotency key.
  - Same key plus same body returns original result.
  - Same key plus different body returns conflict.
- Retention:
  - Acquisition retention is separate from inventory raw JSON retention.
  - Default cleanup: expired unclaimed requests after 24 hours.
  - Default terminal audit retention: 90 days or 5,000 terminal requests, whichever is hit first, configurable.
- Status codes:
  - Define deterministic responses for expired, already claimed, wrong scope, duplicate terminal update, invalid transition, stale claim token, and idempotency conflict.

### Exit Criteria

- Endpoint tests cover create, pending, claim, accept, wrong-character, stale claim token, idempotency replay, idempotency conflict, restart persistence, hosted base path, auth split, and accidental `/api/plugin/...` routes.
- Remaining Phase 1 lifecycle coverage still needed before calling the whole phase complete: reject, progress, complete, fail, expiry before claim, claim-expiry after claim, already-claimed response shape, invalid transition, duplicate terminal retry, concurrent claims, and retention pruning.
- Manual smoke can create and claim a request through the hosted base path.
- No plugin code is required for the phase to pass.

## Phase 2: Dashboard Request Creation Surface

### Objective

Add a small dashboard form for creating a market acquisition request, with clear pickup instructions.

### Proposed Files

- Modify `MarketMafioso.Server/Program.cs`
- Modify or create server-side HTML rendering helpers near the existing dashboard rendering.
- Add tests in `MarketMafioso.Server.Tests/MarketAcquisitionDashboardTests.cs`

### Capability

- Dashboard page exposes a `Market Acquisition` request form.
- User can enter item id/name, quantity mode (`TargetQuantity` or `AllBelowThreshold`), quantity when applicable, max unit price, optional gil cap, HQ policy, region, and world mode.
- Default world mode is `recommended`.
- `allWorldSweep` is visually and behaviorally distinct.
- After creation, dashboard shows:
  - Request summary.
  - Expiry countdown or expiry timestamp.
  - Instruction to open `/mmf` and click `Fetch Dashboard Requests`.

### Investigation Points

- Item search:
  - First pass can accept item id plus optional item name.
  - Full item search/autocomplete can wait.
- Dashboard auth:
  - The first private-server implementation uses the existing dashboard/read auth for request creation.
  - Browser-originated request creation and queue recovery actions require CSRF in addition to dashboard/read auth.
  - In deployments where dashboard auth is enforced outside the app, set `MarketMafioso:TrustExternalDashboardAuth=true`; otherwise browser form mutations fail closed in hosted/API-key mode.
  - API-key-only ingest clients cannot create dashboard requests.
- CSRF:
  - Existing delete routes use CSRF token patterns.
  - Reuse or extend that pattern for request creation and dashboard queue recovery actions.
- Ownership:
  - Account and creator fields come from authenticated dashboard/session context, not trusted JSON.
- Queue recovery:
  - Dashboard can cancel or resend stranded requests through CSRF-protected browser posts.
  - Resend clears stale claim ownership and returns the existing request to `PendingPickup`; it does not clone the request.
  - These actions are recovery controls for the queue, not a live purchase-run remote control surface.

### Exit Criteria

- A dashboard request can be created from the browser.
- The created request appears in pending pickup endpoint until expiry.
- Request creation fails closed when required fields are missing or invalid.
- Request creation requires CSRF and dashboard auth.
- Dashboard cancel/resend requires CSRF and dashboard auth.
- Client API key cannot create browser-originated dashboard requests.

## Phase 3: Plugin Request Pickup UI

### Objective

Add the in-game pickup tray without any market planning, game UI automation, or purchase behavior.

### Current Implementation Status

- `/mmf` has a `Market Acquisition` tab.
- The tab exposes a one-shot `Fetch Dashboard Requests` action. Shared server URL, dashboard URL, and client API key settings live in the plugin-wide `Settings` tab.
- Pickup scope uses the current character name plus the stable home-world identity already used by inventory reports.
- Matching requests render in a compact table and can be claimed explicitly.
- A claimed request can be accepted or rejected explicitly, and accepting only records local consent.
- Active claimed request and claim token state persist in plugin configuration so a plugin reload restores the claim card instead of stranding it server-side.
- The tab includes a local forget action for stale claims that were cancelled or resent from the dashboard.
- No timed polling, market-board reading, world travel, or purchase behavior exists in this phase.

### Proposed Files

- Create `MarketMafioso/MarketAcquisition/MarketAcquisitionRequestModels.cs`
- Create `MarketMafioso/MarketAcquisition/MarketAcquisitionRequestClient.cs`
- Create `MarketMafioso/MarketAcquisition/MarketAcquisitionPickupState.cs`
- Modify `MarketMafioso/Windows/MainWindow.cs`
- Add tests in `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRequestClientTests.cs`

### Capability

- `/mmf` gains a `Market Acquisition` tab.
- Tab shows dashboard URL, `Open Dashboard`, and `Fetch Dashboard Requests`.
- Fetch is one-shot manual fetch plus manual retry.
- No countdown or `Stop Fetching` button appears in Phase 3.
- Matching pending request candidates are shown in a compact table.
- User chooses one request to claim and review.
- Claimed request is shown in a compact pending-request table with accept/reject.
- User can accept or reject locally.
- Accepting does not start purchasing; it moves request into local `Accepted` state only.

### Investigation Points

- Character identity:
  - Current implementation uses `IPlayerState.CharacterName` and `IPlayerState.HomeWorld`, matching inventory report identity.
  - Later runner phases may separately track visited/current world for travel and live listing validation, but pickup identity should stay stable.
- Dashboard URL derivation:
  - Reuse existing endpoint classification and dashboard URL fallback.
- HTTP lifecycle:
  - First pass is one-shot request plus manual retry.
  - Add timed polling only if UX needs it after Phase 3.
- Configuration:
  - Use the plugin-wide client API key from the `Settings` tab.
- Multiple pending requests:
  - Show matching candidates and let user claim one.
  - Do not automatically claim an arbitrary request.
- Claimed but unaccepted:
  - Local UI state is volatile.
  - Plugin reports reject/abandoned when possible if window closes, plugin reloads, or character/world changes before acceptance.

### Exit Criteria

- Plugin can fetch matching dashboard request candidates in a test/fake server path.
- Plugin can claim one selected request and retain the claim token locally.
- Plugin can reject a request and server records it.
- Plugin can accept a request and server records it.
- No constant polling exists.

## Phase 4: Market Planning Dry Run

### Objective

Turn an accepted request into a data-supported world plan, without touching the game UI.

### Current Implementation Status

- Accepted requests can be prepared into a dry-run plan from the `Market Acquisition` tab.
- The first plan source reads Universalis current listings from `/api/v2/{region}/{itemId}?listings=...`.
- The parser requires the live listing fields used by the planner: world name/id, listing id, retainer id/name, unit price, quantity, HQ flag, and review timestamp.
- The planner filters by HQ policy, max unit price, optional max total gil, quantity mode, and world mode.
- Recommended mode includes only worlds with listings under the configured threshold; it does not blindly include every world in the region.
- Selected mode currently fails explicitly before remote planning because the request payload does not yet carry a selected-world list.
- All-world sweep is allowed but labeled distinctly in the dry-run plan; richer sweep-specific routing waits for later runner phases.
- Plans preserve individual listing rows because stack quantities and prices can differ.
- Planning assumes whole-stack purchases. If fulfilling a request requires buying beyond the requested quantity, the batch is marked as an overage.
- The generated plan is an advisory route, not the final purchase authority. Live confirmed listings can replace, reorder, or expand the advisory plan when they satisfy the request rules.
- Remote listing ids remain advisory until the read-only market-board probe proves live row identity.

### Proposed Files

- Create `MarketMafioso/MarketAcquisition/MarketAcquisitionPlanModels.cs`
- Create `MarketMafioso/MarketAcquisition/UniversalisMarketAcquisitionPlanSource.cs`
- Create `MarketMafioso/MarketAcquisition/MarketAcquisitionPlanner.cs`
- Add tests in `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionPlannerTests.cs`

### Capability

- Accepted request can be prepared into planned world batches.
- Default `recommended` mode emits only worlds with supporting listings under threshold.
- `selected` mode limits to selected worlds.
- `currentWorldOnly` mode limits to current world.
- `allWorldSweep` is separate and explicit.
- Planner respects HQ policy, quantity mode, max unit price, optional max total gil, and requested quantity when the request uses `TargetQuantity`.

### Investigation Points

- Universalis API shape:
  - Confirmed live current-listings shape on 2026-06-25 against `https://universalis.app/api/v2/North-America/2?listings=5`.
  - Current response includes `pricePerUnit`, `quantity`, `worldName`, `worldID`, `hq`, `listingID`, `retainerID`, `retainerName`, and `lastReviewTime`.
  - Confirm behavior for DC-level item fetches and world upload times.
  - Confirm rate limits and chunk sizes for single-item and future multi-item requests.
- Craft Architect parity:
  - First pass uses simple threshold filtering with stable sorting by unit price, listing age, and retainer name.
  - CA-like anti-gouging, mode-price filtering, and route scoring are later planner enhancements after the dashboard-to-plugin workflow is proven.
- Item identity:
  - First pass uses the dashboard-provided item name when present and item id as the durable identity.
  - Lumina id-to-name resolution can improve display later.
- Remote listing identity:
  - Remote listing ids are advisory unless a live probe proves exact mapping to current in-game rows for the current patch.

### Exit Criteria

- Planner tests prove recommended mode does not blindly include every world.
- UI shows compact planned world batches after `Prepare`.
- No in-game market board interaction exists yet.

## Phase 5: Live Market Board Read-Only Probe

### Objective

Read and reconcile live market board listings from the game without sending any purchase requests.

### Current Implementation Status

- Added live-listing reconciliation models and a strict reconciler.
- Added a read-only `InfoProxyItemSearch`/`AddonItemSearchResult` probe behind `Read Live Listings` in the `Market Acquisition` tab.
- Reconciliation requires the prepared plan item id to match the current market-board search item id.
- Reconciliation requires the current market-board world to match a world batch in the prepared plan.
- Planned listings are advisory rows. Live rows become executable only after item id, world, HQ policy, unit price threshold, listing id, retainer id, quantity, and purchase preconditions are read from the current market board state.
- Missing planned listings, cheaper replacement listings, extra below-threshold stock, and changed quantities are classified explicitly. Favorable changes can produce executable candidates; unsafe or ambiguous changes block the affected candidate.
- Current-world live candidate evaluation is treated as Phase 5.5, not full Phase 6, because it only works on the current visible market-board page and does not advance through world batches.
- The probe reads `SearchItemId`, `ListingCount`, `WaitingForListings`, and current `Listings` only.
- Live proof on 2026-06-25 confirmed visible listing rows populate even when `WaitingForListings` remains true. When rows are visible, the reader treats the result as ready and includes a diagnostic message that the waiting flag was still set.
- No callback, packet, purchase request, world-travel automation, market-board search automation, or row selection exists yet.
- Local FFXIVClientStructs XML exposes `AddonItemSearchResult`, `AgentItemSearch`, and `InfoProxyItemSearch.Listings`; visible-row field interpretation has been proven for the current patch.

### Implemented Files

- `MarketMafioso/MarketAcquisition/MarketBoardListingReader.cs`
- `MarketMafioso/MarketAcquisition/MarketBoardLiveListingModels.cs`
- `MarketMafioso/MarketAcquisition/MarketBoardListingReconciler.cs`
- `MarketMafioso.Tests/MarketAcquisition/MarketBoardListingReconcilerTests.cs`
- `MarketMafioso.Tests/MarketAcquisition/MarketBoardListingReaderTests.cs`
- `MarketMafioso/Windows/MainWindow.cs` exposes the `Read Live Listings` probe and compact summary.
- `MarketMafioso/Windows/MarketAcquisitionDiagnosticsWindow.cs` exposes verbose reconciliation and live dry-run tables.

### Remaining Work

- Confirm pagination and deeper listing-page behavior when more listings exist than the currently visible result set.
- Confirm no-listings behavior on an item/world with no stock.
- Keep strict reconciliation as diagnostics and use the Phase 5.5 candidate-pool builder for favorable drift and would-buy decisions.

### Capability

- Plugin can detect whether market board search result UI is open.
- Plugin can identify current market board item search.
- Plugin can read visible/live listing data.
- Plugin can reconcile live listings against the accepted request and planned world batch.
- Plugin can show read-only validation results.

### Investigation Points

- FFXIVClientStructs:
  - Local Dalamud dev hooks expose `AgentItemSearch`, `AddonItemSearchResult`, and `InfoProxyItemSearch.Listings`.
  - Compile-time reflection confirmed `InfoProxyItemSearch` exposes `SearchItemId`, `ListingCount`, `WaitingForListings`, and `Listings`; `MarketBoardListing` exposes listing id, retainer id, item id, unit price, quantity, and HQ flag.
  - In-game test still needs to confirm those fields are populated as expected for current market-board search results.
  - Retainer name is not present on `MarketBoardListing`; live matching uses listing id plus retainer id.
  - Confirm listing count/page behavior.
  - Confirm how to identify the active item search reliably.
- Addon state:
  - Determine whether `AddonItemSearchResult` is enough for visible state and row selection.
  - Determine how to detect "listings are loading" vs "no listings".
- Listing identity:
  - Determine whether live listing id and retainer id are accessible and stable.
  - Probe must prove exact remote Universalis listing id to live-row matching for the current patch; otherwise remote ids remain advisory hints only.
  - Document the live row identity tuple and every case where it becomes invalid.
- Diagnostics:
  - Decide exact dump fields needed to debug read failures without exposing secrets.
- Version drift:
  - Probe output records game version, Dalamud API level, Dalamud.NET.Sdk version, and FFXIVClientStructs version.
  - If the current patch/version has not been probed, live reading may run in diagnostics-only mode and purchase execution remains unavailable.

### Exit Criteria

- Read-only probe can be run on a low-risk common item.
- Probe demonstrates current item id, listing count, row identity, HQ flag, unit price, quantity, retainer id, listing id, town/world fields, pagination behavior, and loading/no-listings distinction.
- Probe produces live listing rows and validation status.
- No purchase request path is called.
- Unknown UI state produces a clear diagnostic.
- Populated visible rows override a stale `WaitingForListings` flag and produce a ready result with a diagnostic note.

## Phase 5.5: Current-World Live Candidate Evaluation

### Objective

Build a confirmed buy/skip candidate pool from the current visible market-board page after the read-only probe, still without purchasing.

### Implemented Files

- `MarketMafioso/MarketAcquisition/MarketAcquisitionLiveDryRunModels.cs`
- `MarketMafioso/MarketAcquisition/MarketAcquisitionLiveDryRunPlanner.cs`
- `MarketMafioso/MarketAcquisition/MarketAcquisitionLiveDryRunPresenter.cs`
- `MarketMafioso/Windows/MarketAcquisitionDiagnosticsWindow.cs`
- `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionLiveDryRunPlannerTests.cs`
- `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionLiveDryRunPresenterTests.cs`

### Capability

- Candidate evaluation runs only after a successful visible-page live market-board read.
- Candidate evaluation validates item id and current world against the accepted request and prepared plan.
- Candidate evaluation builds a confirmed candidate pool from live listings at or below max unit price.
- Candidate evaluation sorts confirmed candidates by unit price before any future dry-run or purchase action.
- Candidate evaluation records favorable drift such as cheaper listings, replacement listings, and more below-threshold stock.
- Candidate evaluation reports per-listing `WouldBuy` or `Skipped` rows with explicit reasons such as above threshold, HQ mismatch, gil cap exceeded, or target already satisfied.
- Candidate evaluation reports aggregate `Ready`, `UnderProcured`, or `NoSafeListings`.
- The main plugin tab shows a compact summary; verbose reconciliation and candidate rows live in the diagnostics popout.

### Exit Criteria

- Current-world dry-run can classify the current visible page without purchase calls.
- Main Market Acquisition tab remains compact.
- Diagnostics popout contains verbose reconciliation and candidate tables.

## Phase 6: Lifestream-Guided World-Batch Orchestration

### Objective

Add a local runner that walks through planned world batches by delegating travel to Lifestream and re-running live probe/candidate evaluation at each destination, still without purchasing.

### Proposed Files

### Implemented Files

- `MarketMafioso/MarketAcquisition/MarketAcquisitionGuidedRouteSession.cs`
- `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionGuidedRouteSessionTests.cs`
- `MarketMafioso/Windows/MainWindow.cs` exposes the first guided route controls.

### Proposed Later Files

- `MarketMafioso/MarketAcquisition/MarketAcquisitionRunner.cs`
- `MarketMafioso/MarketAcquisition/WorldTravel/LifestreamWorldTravelDriver.cs`
- `MarketMafioso/MarketAcquisition/MarketAcquisitionDiagnostics.cs`
- `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRunnerTests.cs`

### Capability

- First slice moves through planned worlds in prepared-plan order.
- First slice guides Lifestream travel by showing, copying, and executing `/li <world> mb` through Dalamud command dispatch.
- If command dispatch returns unhandled, the route remains on the active stop and reports that Lifestream did not handle the command.
- For each world, runner waits until current world matches expected world.
- Runner waits for market board/listings to be available.
- Runner reuses Phase 5.5 candidate evaluation at each destination.
- Later slices re-rank remaining world stops if a live-confirmed cheaper candidate changes the best path.
- Runner shows one world-batch confirmation.
- `Dry Run Batch` records what would be bought and advances.

### Investigation Points

- Lifestream integration:
  - Confirm the command shape for same-data-center and cross-data-center market board travel.
  - Live-test whether `/li <world> mb` issued through `ICommandManager.ProcessCommand` behaves identically to typing the command.
  - Confirm failure messages for inaccessible, congested, or temporarily unavailable worlds.
- Current world detection:
  - Confirm stable source for current world after world travel.
  - Confirm timing after travel completes.
- Confirmation surface:
  - Use inline confirmation for low-risk actions.
  - Reserve modal popup for high-risk world-batch purchase confirmation.
  - Ensure confirmation is invalidated if live listings change.
- Runner persistence:
  - Decide whether active run state is volatile only.
  - Recommended: volatile only; dashboard request audit stores progress/results.

### Exit Criteria

- User can dry-run a full multi-world plan with Lifestream-assisted travel.
- Runner can pause, stop, fail, and complete with clear status.
- Server receives progress/failure/completion reports.
- Confirmation invalidates if current world, current search item, confirmed candidate identity, confirmed candidate price, confirmed candidate quantity, or remaining configured budget changes before batch action.

### Current Slice Exit Criteria

- User can start a volatile guided route from a ready plan.
- Plugin shows, copies, and can execute `/li <world> mb` for the active planned world.
- If Dalamud reports the command was not handled, the plugin reports that immediately and does not advance the route.
- Plugin can check current world against the active stop.
- Successful live probe/dry-run records current stop quantities and advances to the next stop.
- No purchase path exists.

## Phase 7: Purchase Mechanism Investigation

### Objective

Prove or reject a safe way to execute a purchase programmatically from a live validated listing.

This is an investigation phase, not a shipping phase.

### Proposed Files

- Create a private/local diagnostic branch or guarded experimental class under `MarketMafioso/MarketAcquisition/Experimental/`.
- Add a writeup under `docs/design/2026-06-25-market-acquisition-purchase-probe.md`.

### Investigation Points

- Purchase path:
  - Probe must prove or reject whether `InfoProxyItemSearch.SetLastPurchasedItem(...)` plus `SendPurchaseRequestPacket()` works in current Dalamud.
  - Probe must document required field population for listing id, retainer id, container index, item id, quantity, unit price, HQ, tax, and town id.
  - Probe must document whether purchase quantity is fixed by listing stack quantity.
- Purchase response:
  - Confirm how success/failure is observed.
  - Confirm response error ids or visible UI messages.
  - Confirm how listing disappearance or price changes report.
- Safety:
  - Confirm re-reading live listing immediately before purchase is possible.
  - Confirm no purchase can be sent while wrong item/world/search is active.
  - Confirm inventory-full and insufficient-gil behavior.
- Test economics:
  - Use only low-value items and strict gil caps.
  - Prefer one purchase from current world only.
- Failure taxonomy:
  - Classify each purchase outcome as purchased, skipped unsafe listing, skipped missing listing, under-procured, inventory full, insufficient gil, terminal, retryable after manual refresh, or unknown.
  - Unknown defaults to terminal.

### Exit Criteria

- Written probe result explains whether guarded purchase execution is feasible.
- If feasible, exact required fields, preconditions, postconditions, success signal, failure signal, and safe stop behavior are documented.
- If not feasible, roadmap stops at dry-run/guided mode until a safer path exists.
- No purchase code may be merged behind a runtime flag until this probe passes.

## Phase 8: Guarded Purchase Execution

### Objective

Add live purchases behind strict gates if Phase 7 proves the mechanism.

### Proposed Files

- Create `MarketMafioso/MarketAcquisition/MarketBoardPurchaseExecutor.cs`
- Create `MarketMafioso/MarketAcquisition/MarketBoardPurchaseResult.cs`
- Add tests in `MarketMafioso.Tests/MarketAcquisition/MarketBoardPurchaseExecutorTests.cs`

### Capability

- Purchase execution is disabled unless the user explicitly enables it.
- Plugin confirms once per world batch.
- Before each purchase send, plugin revalidates item id, world, listing id, retainer id, HQ flag, unit price, quantity, and remaining gil cap when configured.
- `TargetQuantity` executes cheapest confirmed live candidates until the target is satisfied, safe stock runs out, or a safety stop occurs.
- `AllBelowThreshold` executes every confirmed live candidate at or below max unit price, optionally bounded by a configured gil cap.
- Confirmed replacement listings below threshold are valid purchase candidates.
- Runner stops on unknown purchase result.
- Server receives progress and final audit state.

### Investigation Points

- Setting/kill switch:
  - Guarded purchase execution has its own config flag.
  - Default false.
- Retry policy:
  - Decide which failures are safe to continue.
  - Recommended first pass: no automatic retries after any purchase error.
- Audit detail:
  - Store request id, item id, HQ flag, unit price, quantity, world, retainer/listing identity facts, validation timestamp, action result, and failure classification.
  - Do not store secrets, auth material, raw plugin config, raw game memory dumps, or unnecessary live game diagnostics.

### Exit Criteria

- One low-value current-world purchase succeeds under max unit price.
- Multi-listing single-world batch succeeds after one confirmation.
- Unsafe or ambiguous mismatch stops the affected candidate or batch according to the failure taxonomy. Favorable live drift can continue.
- No purchase path exists without local plugin acceptance and world-batch confirmation.
- Unknown purchase response stops the runner.
- Execution audit records enough listing identity and validation facts to explain why a purchase was attempted, without storing secrets or raw unstable client dumps by default.

## Phase 9: Native Travel Automation Spike

### Objective

Investigate native regional world travel automation only if Lifestream-assisted travel cannot cover the required route.

### Proposed Files

- Create a probe writeup under `docs/design/2026-06-25-market-acquisition-world-travel-probe.md`.
- Add code only after the probe establishes stable UI states.

### Investigation Points

- Addon flow:
  - Identify aetheryte/world travel addon names and menu entries.
  - Confirm visible preconditions and postconditions for every action.
- Travel restrictions:
  - Detect inaccessible, congested, or temporarily unavailable worlds.
  - Confirm how failures present in UI.
- State timing:
  - Determine how to wait through loading and confirm current world after arrival.
- Version drift:
  - World travel probe records game version, Dalamud API level, Dalamud.NET.Sdk version, and FFXIVClientStructs version.
- Scope:
  - Region-wide acquisition is the target.
  - Same-data-center and cross-data-center travel may land as separate sub-slices, but both require probe evidence before automation is enabled.

### Exit Criteria

- If safe and still needed, add an `AetheryteWorldTravelDriver` plan.
- If unsafe, unstable, or unnecessary because Lifestream covers the route, keep Lifestream/manual travel as the supported mode.
- Automated travel remains blocked until the probe documents visible preconditions, action steps, postconditions, timeout behavior, and all known failure surfaces.

## Phase 10: Craft Architect Plan Integration

### Objective

Replace or augment the simple local planner with Craft Architect's richer market analysis when a clean service boundary exists.

### Proposed Files

- Server side:
  - Add a CA-compatible planning endpoint or shared planning service.
- Plugin side:
  - Add `ServerDashboardAcquisitionPlanSource` support if not already present.

### Investigation Points

- Current CA web app appears to run market analysis client-side with IndexedDB.
- Later integration must pick one explicit service boundary before coding: MarketMafioso.Server endpoint, CA backend endpoint, or shared library. The first implementation does not depend on this decision.
- Avoid direct plugin dependency on CA assemblies.
- Preserve self-hosted DIY package behavior.

### Exit Criteria

- Dashboard-created requests can include ranked world batches from CA-quality planning.
- Plugin still treats server plan as advisory and revalidates live listings.

## Cross-Cutting Risks

### Security

- Dashboard request creation and plugin pickup must use different capabilities.
- Commands must expire and be single-use.
- Never accept arbitrary code or raw UI instructions.
- Unauthorized or wrong-scope responses must not reveal request existence, character, world, item, or gil cap.
- Audit records must not contain secrets, auth headers, CSRF tokens, raw plugin config, or unnecessary live game diagnostics.

### Game UI Drift

- Market board and world travel structures can break with FFXIV/Dalamud updates.
- Keep read-only probes and diagnostics easy to run after updates.
- After any FFXIV patch or Dalamud struct update, rerun the read-only probe before enabling dry-run reconciliation and rerun the purchase probe before enabling purchase execution.

### Economic Risk

- Max unit price is mandatory. Max total gil is optional; when configured it is a hard spend stop.
- Purchase execution remains disabled until explicitly enabled.
- Start purchase-mechanism testing with low-value current-world tests only; this is a safety smoke test, not the final acquisition scope.

### UX Risk

- The plugin window must stay small and ImGui-native.
- Dashboard owns rich planning; plugin owns consent and state.

## Recommended Execution Order

1. Phase 1: Server Request Lifecycle.
2. Phase 2: Dashboard Request Creation Surface.
3. Phase 3: Plugin Request Pickup UI.
4. Phase 4: Market Planning Dry Run.
5. Phase 5: Live Market Board Read-Only Probe.
6. Phase 5.5: Current-World Live Candidate Evaluation.
7. Phase 6: Lifestream-Guided World-Batch Orchestration.
8. Phase 7: Purchase Mechanism Investigation.
9. Phase 8 only if Phase 7 passes.
10. Phase 9 is deferred unless Lifestream cannot cover required routes.
11. Phase 10 can wait until the core loop is proven.

The first milestone worth deploying to the VPS is Phase 3: dashboard request creation plus plugin pickup. That proves the novel server-to-plugin workflow without touching game automation.
