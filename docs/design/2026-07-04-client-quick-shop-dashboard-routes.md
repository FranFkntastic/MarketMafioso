# Client Quick Shop Dashboard Routes

## Decision

Build a lightweight client-native route constructor that still creates a server-backed Market Acquisition batch. The plugin should make quick shopping faster to start, but execution should remain the same monitored acquisition route that the dashboard already understands.

The quick-shop panel is a shortcut for authoring a request, not a separate local-only workflow. After creation, the request is claimed and accepted by the creating plugin instance, then prepared and run through the existing guided route, progress reporting, purchase audit, reprepare, and world-visit catalog paths.

## Goals

- Let the plugin create quick market acquisition routes without using the full dashboard form.
- Support multi-item routes from the first slice.
- Keep dashboard monitoring, lifecycle status, timeline, line progress, purchase audit, cancel/resend behavior, and recent-request views intact.
- Reuse existing route planning and execution code instead of introducing a parallel "local route" model.
- Keep the client form denser than a dashboard workflow, but still safe: explicit item, quantity, HQ policy, price cap, and route scope per route.
- Preserve dashboard-created acquisition behavior exactly.

## Non-Goals

- Do not build a separate local-only route lifecycle.
- Do not replace the dashboard acquisition creator.
- Do not add inventory-aware item suggestions in this slice.
- Do not build a full catalog browser as part of quick shop.
- Do not require new route execution semantics. Live market-board validation remains authoritative.
- Do not add selected-world routing unless the selected-world payload already exists by the time this slice starts.

## Product Shape

Add a "Quick Shop" section to the Market Acquisition tab. The user can draft a route with shared route settings and one or more item lines:

- Shared route settings:
  - Target character/world from current player state.
  - Region, defaulting from receiver/dashboard settings or current world region when available.
  - World mode: Recommended or All-world sweep.
  - Sweep scope and data centers when all-world sweep is selected.
  - Expiration, using a longer quick-shop default than dashboard pickup because the creating client immediately claims it.

- Line grid:
  - Item id.
  - Item name, optional but persisted when supplied or resolved.
  - Quantity mode: Target quantity or all below threshold.
  - Target quantity or max quantity, depending on mode.
  - HQ policy.
  - Max unit price.
  - Optional line gil cap.
  - Add, remove, duplicate, and clear rows.

The main action is `Create monitored route`. It posts the batch to the receiver, claims it for the current plugin instance, accepts it, and leaves the normal `Prepare Plan` and `Start Route` controls available. The first slice does not add `Create and prepare`; keeping those actions separate makes server creation failures, planning failures, and route failures easier to diagnose.

## Server Contract

Prefer the existing acquisition batch endpoints:

- `POST /api/acquisition/batches`
- `POST /api/acquisition/requests/{id}/claim`
- `POST /api/acquisition/requests/{id}/accept`
- existing progress, line progress, purchase audit, complete, and fail endpoints

Add optional route-origin metadata to the create payload and persisted view:

```csharp
public string Origin { get; init; } = "DashboardCreated";
public string? CreatedByPluginInstanceId { get; init; }
```

Supported `Origin` values:

- `DashboardCreated`
- `ClientQuickShop`

The origin is display and filtering metadata only. It must not change route lifecycle rules. If older clients omit it, the server treats the request as `DashboardCreated`.

The plugin should send a stable idempotency key for create, such as:

```text
{PluginInstanceId}:quick-shop:{DraftId}:{DraftRevision}
```

If a create request is replayed, the server returns the existing batch. If the idempotency key conflicts with a different payload, the plugin surfaces the server conflict and does not guess.

## Client Flow

1. User edits the quick-shop draft in the plugin.
2. Plugin validates local fields:
   - current character/world is available
   - at least one line
   - item ids are non-zero
   - price caps are non-zero
   - target/max quantities match quantity mode
   - HQ policy and route mode are known
3. Plugin posts `MarketAcquisitionBatchCreateRequest` with origin `ClientQuickShop`.
4. Plugin immediately claims the returned request using current character/world and `PluginInstanceId`.
5. Plugin immediately accepts the claim using a generated accept idempotency key.
6. Plugin stores the accepted claim through existing claim persistence.
7. Existing plan preparation fetches Universalis, applies recent-world catalog policy, and builds the plan.
8. Existing guided route execution reports progress, line progress, purchase audits, completion, and failure to the dashboard.

This means quick-shop routes appear in dashboard acquisition monitoring as normal acquisition requests, with a visible `ClientQuickShop` origin marker.

## Dashboard Behavior

The dashboard should show quick-shop batches in the same acquisition list and timeline as normal dashboard-created batches. The visible difference should be small:

- Show an origin badge such as `Quick Shop` or `Dashboard`.
- Keep existing timeline and purchase audit UI.
- Allow cancel/resend controls where the current lifecycle already permits them.

The dashboard should not need a separate quick-shop page for the first slice.

## Plugin Architecture

Add a focused quick-shop draft model under `MarketAcquisition`:

- `MarketAcquisitionQuickShopDraft`
- `MarketAcquisitionQuickShopLineDraft`
- `MarketAcquisitionQuickShopDraftValidator`
- `MarketAcquisitionQuickShopRequestBuilder`

Extend `MarketAcquisitionRequestClient` with:

- `CreateBatchAsync(...)`
- helper request DTOs if the plugin-side models are not already present

Keep MainWindow responsible for ImGui rendering and orchestration only. The builder owns conversion from draft to server create request, and the validator owns user-facing validation messages.

The successful create/claim/accept result should populate the same fields used by dashboard pickup:

- `claimedAcquisitionRequest`
- `claimedAcceptIdempotencyKey`
- `claimedRejectIdempotencyKey`
- persisted claim config
- route/plan state reset

No route runner changes should be needed for the happy path.

## Data Model Notes

The server already models batch lines. Quick shop should use that shape directly:

- `MarketAcquisitionBatchCreateRequest.Lines`
- `MarketAcquisitionBatchLineCreateRequest`

For a single-line quick shop, still create a batch with one line. Avoid using the older single-request endpoint so quick shop has one consistent path and multi-item routes do not need a mode switch.

Primary request fields should mirror the first line for compatibility with existing views that still display a primary item. The store already has fallback/primary-line behavior; quick shop should not invent a second primary-item source.

## Error Handling

- Missing API key: show the existing receiver/API-key error and do not create a local-only route.
- Create conflict: show an idempotency conflict and keep the draft intact.
- Claim/accept failure after create: keep the created request id visible and offer `Claim existing quick route` or let normal pickup fetch it.
- Current character/world unavailable: block create.
- Server unavailable: leave the draft intact and show the HTTP error.
- Unsupported world mode or region: block locally before posting.
- If the create succeeds but the later auto-claim fails due to another plugin claiming it, surface the server conflict rather than reclaiming or resending automatically.

## Testing Plan

Server tests:

- Creating a batch with `Origin = ClientQuickShop` persists and returns the origin.
- Omitting origin preserves `DashboardCreated`.
- Dashboard list/timeline includes quick-shop batches.
- Existing create/claim/accept/progress/purchase tests still pass.

Plugin tests:

- Draft validator rejects missing current scope, empty lines, zero item id, zero price cap, invalid quantity mode, and invalid HQ policy.
- Request builder maps multi-line drafts into `MarketAcquisitionBatchCreateRequest`.
- Request client posts create batch to `/api/acquisition/batches` with `X-Api-Key`.
- Quick-shop orchestration create -> claim -> accept persists the claim and resets route state.

Manual verification:

- Create a two-item quick shop route from the plugin.
- Confirm it appears in dashboard acquisition monitoring with a Quick Shop badge.
- Prepare and start the route from the plugin.
- Confirm dashboard updates progress, line progress, purchases, and completion.
- Reload plugin and confirm the accepted quick-shop claim restores like a dashboard-created claim.

## Implementation Slices

1. Server origin metadata:
   - Add optional origin fields to create requests and views.
   - Persist/round-trip origin with migration-safe defaults.
   - Add dashboard badge display.

2. Client request creation:
   - Add plugin DTOs/client method for batch create.
   - Add quick-shop draft model, validator, and request builder.
   - Add focused tests.

3. Plugin Quick Shop UI:
   - Add the draft panel and multi-line editor.
   - Implement create -> claim -> accept orchestration.
   - Reuse existing plan and route UI.

4. End-to-end verification:
   - Server tests.
   - Plugin tests.
   - Dev-plugin deploy.
   - Manual dashboard monitor smoke.

## First-Slice Decisions

- Item name resolution is manual in the first slice. The user can type an item id and optional item name; an item lookup button can be added later against the existing XIV item search endpoint.
- `ClientDraftName` is not part of the first slice. Add it when quick-shop drafts can be saved and reused.
- `Create and prepare` is not part of the first slice. The plugin creates, claims, and accepts the monitored route, then the existing Prepare Plan button remains the next action.
