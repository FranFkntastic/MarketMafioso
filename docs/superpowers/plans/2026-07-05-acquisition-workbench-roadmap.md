# Acquisition Workbench Implementation Roadmap

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this roadmap task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the scattered Market Acquisition, Quick Shop, Craft Architect Companion, and common diagnostics surfaces with one client-native Acquisition Workbench that supports single-line and multi-line acquisitions end to end.

**Architecture:** Build the workbench as a new popout that composes existing Market Acquisition and Craft Architect Companion services before retiring older primary popouts. Keep durable market/world knowledge separate from request fulfillment progress and prepared route state. Preserve automatic dashboard sync for client-authored routes.

**Tech Stack:** C#/.NET, Dalamud ImGui, MarketMafioso plugin services, existing Market Acquisition server APIs, existing xUnit-style `MarketMafioso.Tests`.

---

## Source Design

- Spec: `docs/design/2026-07-05-client-acquisition-surface-reconciliation-plan.md`
- Build/Appraise mockup: `mockups/acquisition-workbench-build.html`
- Run mockup: `mockups/acquisition-workbench-run.html`
- Adjust/Recover mockup: `mockups/acquisition-workbench-adjust.html`

## Current Anchors

- Quick-shop draft and sync:
  - `src/MarketMafioso/MarketAcquisition/MarketAcquisitionQuickShopDraft.cs`
  - `src/MarketMafioso/MarketAcquisition/MarketAcquisitionQuickShopDraftValidator.cs`
  - `src/MarketMafioso/MarketAcquisition/MarketAcquisitionQuickShopRequestBuilder.cs`
  - `src/MarketMafioso/MarketAcquisition/MarketAcquisitionQuickShopWorkflow.cs`
  - `src/MarketMafioso/Windows/MarketAcquisitionQuickShopWindow.cs`
- Craft appraisal:
  - `src/MarketMafioso/CraftArchitectCompanion/CraftArchitectCompanionModels.cs`
  - `src/MarketMafioso/CraftArchitectCompanion/CraftArchitectMarketAppraisalService.cs`
  - `src/MarketMafioso/CraftArchitectCompanion/CraftAppraisalPanelPresenter.cs`
  - `src/MarketMafioso/Windows/CraftArchitectCompanionWindow.cs`
- Planning and route execution:
  - `src/MarketMafioso/MarketAcquisition/UniversalisMarketAcquisitionPlanSource.cs`
  - `src/MarketMafioso/MarketAcquisition/MarketAcquisitionPlanner.cs`
  - `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRouteRunner.cs`
  - `src/MarketMafioso/MarketAcquisition/MarketAcquisitionPlanRepreparer.cs`
  - `src/MarketMafioso/Windows/MainWindow.cs`
- Diagnostics:
  - `src/MarketMafioso/Windows/MarketAcquisitionDiagnosticsWindow.cs`
  - `src/MarketMafioso/Windows/AutomationDiagnosticsWindow.cs`

## State Model To Preserve

- Durable market/world state:
  - world visit catalog,
  - observed Universalis listing snapshots,
  - purchase history,
  - known exhausted or picked-over worlds,
  - freshness timestamps.
- Request fulfillment state:
  - bought quantity by line,
  - spent gil by line,
  - dashboard line progress,
  - purchase audit,
  - request lifecycle.
- Prepared route state:
  - current stop list,
  - active stop,
  - route runner state,
  - current diagnostic session.

`Replan Remaining` consumes request fulfillment state to reduce remaining quantities. `Restart Plan` uses the current edited draft quantities. Both may use durable market/world state.

## Phase 1: Stock Availability And Observed Listing Cache

**Purpose:** Make threshold adjustment cheap and correct before building more UI around it.

**Files:**

- Create: `src/MarketMafioso/MarketAcquisition/ObservedMarketSnapshotCache.cs`
- Create: `src/MarketMafioso/MarketAcquisition/StockAvailabilityService.cs`
- Test: `tests/MarketMafioso.Tests/MarketAcquisition/ObservedMarketSnapshotCacheTests.cs`
- Test: `tests/MarketMafioso.Tests/MarketAcquisition/StockAvailabilityServiceTests.cs`
- Modify as needed: `src/MarketMafioso/CraftArchitectCompanion/CraftArchitectMarketAppraisalService.cs`

**Tasks:**

- [ ] Add an in-memory, bounded observed-listing cache keyed by item ID plus route-scope query target.
- [ ] Store raw `MarketAcquisitionListing` rows, fetch timestamp, source freshness metadata, and diagnostics summary.
- [ ] Add cache hit, miss, expiry, manual refresh, and bounded eviction tests.
- [ ] Add stock-availability analysis that filters by item, HQ policy, positive quantity, positive unit price, threshold, and route scope.
- [ ] Add tests proving uncapped all-below-threshold lines report depth rather than sufficiency.
- [ ] Add tests proving capped/target lines report enough, partial, none, or invalid.
- [ ] Add tests proving threshold and quantity changes reanalyze cached rows without refetching.

**Verification commands:**

- `dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "ObservedMarketSnapshotCacheTests|StockAvailabilityServiceTests"`

## Phase 2: Shared Workbench Controls

**Purpose:** Stop duplicating item search, route scope, and line editing between Quick Shop and CA Companion.

**Files:**

- Create: `src/MarketMafioso/Windows/AcquisitionWorkbench/ItemAutocompleteControl.cs`
- Create: `src/MarketMafioso/Windows/AcquisitionWorkbench/RouteScopeSelector.cs`
- Create: `src/MarketMafioso/Windows/AcquisitionWorkbench/AcquisitionLineEditor.cs`
- Create: `src/MarketMafioso/Windows/AcquisitionWorkbench/AcquisitionDraftPanel.cs`
- Test presenter logic where possible under: `tests/MarketMafioso.Tests/MarketAcquisition/`
- Read and reuse behavior from:
  - `src/MarketMafioso/Windows/MarketAcquisitionQuickShopWindow.cs`
  - `src/MarketMafioso/Windows/CraftArchitectCompanionWindow.cs`

**Tasks:**

- [ ] Extract item name autocomplete with inferred item ID behavior.
- [ ] Extract route scope selector for region, recommended/all-world policy, sweep scope, and data centers.
- [ ] Move single-line and multi-line quick-shop editing into reusable draft controls.
- [ ] Preserve uncapped `Buy all below threshold` behavior.
- [ ] Preserve validation rules from `MarketAcquisitionQuickShopDraftValidator`.
- [ ] Add focused tests for validation and line model transitions.

**Verification commands:**

- `dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "MarketAcquisitionQuickShopDraftValidatorTests|MarketAcquisitionQuickShopRequestBuilderTests"`
- `dotnet build .\MarketMafioso.sln`

## Phase 3: Acquisition Workbench Shell

**Purpose:** Introduce the new popout without deleting the existing windows.

**Files:**

- Create: `src/MarketMafioso/Windows/AcquisitionWorkbenchWindow.cs`
- Create folder: `src/MarketMafioso/Windows/AcquisitionWorkbench/`
- Modify: `src/MarketMafioso/Plugin.cs`
- Modify: `src/MarketMafioso/Windows/MainWindow.cs`

**Tasks:**

- [ ] Register `AcquisitionWorkbenchWindow` in the window system.
- [ ] Add a compact `Open Acquisition Workbench` launcher to the Market Acquisition tab.
- [ ] Keep the old Quick Shop and CA Companion windows available while the workbench is built.
- [ ] Add workbench header metrics for target, route state, lines, stock, and dashboard sync.
- [ ] Add bottom phase strip instead of a left-side workflow rail.
- [ ] Add build/appraise/run/recover pane routing from internal workbench state.

**Verification commands:**

- `dotnet build .\MarketMafioso.sln`
- Manual plugin check: open Market Acquisition tab, open workbench, resize small and wide.

## Phase 4: Build And Appraise Pane

**Purpose:** Make the first useful workbench state support client-only single-item and multi-item route construction.

**Files:**

- Create: `src/MarketMafioso/Windows/AcquisitionWorkbench/CraftEvidencePanel.cs`
- Create: `src/MarketMafioso/Windows/AcquisitionWorkbench/StockAvailabilityPanel.cs`
- Modify: `src/MarketMafioso/Windows/AcquisitionWorkbenchWindow.cs`
- Modify as needed: `src/MarketMafioso/CraftArchitectCompanion/CraftAppraisalPanelPresenter.cs`
- Modify as needed: `src/MarketMafioso/CraftArchitectCompanion/CraftArchitectQuickShopDraftBuilder.cs`

**Tasks:**

- [ ] Add draft route settings and line table to the build pane.
- [ ] Add Craft Architect evidence for the selected line.
- [ ] Keep craft cost advisory and require explicit threshold application.
- [ ] Remove percentage adjustment actions from the implementation.
- [ ] Add `Check Stock` and `Refresh Stock` actions.
- [ ] Show stock availability as depth for uncapped all-below-threshold lines.
- [ ] Show stock availability as sufficiency for capped/target lines.
- [ ] Show whether stock came from cache or fresh Universalis fetch.

**Verification commands:**

- `dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "CraftArchitectMarketAppraisalServiceTests|CraftAppraisalPanelPresenterTests|StockAvailabilityServiceTests"`
- Manual plugin check: add one uncapped all-below-threshold line and one target/capped line; verify wording differs.

## Phase 5: Sync, Prepare, And Run Pane

**Purpose:** Let the user create, claim, accept, prepare, and run a route entirely inside the workbench.

**Files:**

- Modify: `src/MarketMafioso/Windows/AcquisitionWorkbenchWindow.cs`
- Modify: `src/MarketMafioso/MarketAcquisition/MarketAcquisitionQuickShopWorkflow.cs`
- Modify: `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRouteTablePresenter.cs`
- Modify as needed: `src/MarketMafioso/Windows/MainWindow.cs`

**Tasks:**

- [ ] Wire `Sync Route` to existing create, claim, accept workflow.
- [ ] Persist accepted claim exactly as the existing Market Acquisition tab does.
- [ ] Add `Prepare Plan` inside the workbench.
- [ ] Move route table presentation into a central workbench pane with enough room for world state, planned quantity, lines, and result.
- [ ] Move market-board read details into a side pane named `Market Board Read`.
- [ ] Surface dashboard sync state in the header/status area.
- [ ] Keep deep diagnostics windows accessible but not required for normal operation.

**Verification commands:**

- `dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "MarketAcquisitionQuickShopWorkflowTests|MarketAcquisitionRouteTablePresenterTests|MarketAcquisitionRouteRunnerTests"`
- Manual plugin check: create a two-line route, confirm it syncs and can be prepared without dashboard interaction.

## Phase 6: Recovery Semantics

**Purpose:** Replace ambiguous legacy recovery controls with resume, replan remaining, and restart plan.

**Files:**

- Create: `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRecoveryService.cs`
- Modify: `src/MarketMafioso/MarketAcquisition/MarketAcquisitionPlanRepreparer.cs`
- Modify: `src/MarketMafioso/MarketAcquisition/MarketAcquisitionRouteRunner.cs`
- Modify: `src/MarketMafioso/Windows/AcquisitionWorkbenchWindow.cs`
- Test: `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRecoveryServiceTests.cs`
- Extend:
  - `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionPlanRepreparerTests.cs`
  - `tests/MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteRunnerTests.cs`

**Tasks:**

- [ ] Add `Resume Route` action that continues the current prepared stop list.
- [ ] Add `Replan Remaining` action that reduces planned quantities by request fulfillment progress.
- [ ] Add `Restart Plan` action that uses edited draft quantities instead of route-local partial fulfillment.
- [ ] Keep durable world/catalog and observed-price cache data available to both replanning modes.
- [ ] Rename legacy route recovery button text to the new semantics.
- [ ] Add tests proving `Replan Remaining` preserves purchase audit and reduces target quantities.
- [ ] Add tests proving `Restart Plan` ignores route-local partial fulfillment but still allows durable cache/catalog input.

**Verification commands:**

- `dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "MarketAcquisitionRecoveryServiceTests|MarketAcquisitionPlanRepreparerTests|MarketAcquisitionRouteRunnerTests"`

## Phase 7: Retire Scattered Primary Surfaces

**Purpose:** Finish the reconciliation after the workbench covers the full workflow.

**Files:**

- Modify: `src/MarketMafioso/Windows/MainWindow.cs`
- Modify: `src/MarketMafioso/Plugin.cs`
- Modify or remove primary entry points for:
  - `src/MarketMafioso/Windows/MarketAcquisitionQuickShopWindow.cs`
  - `src/MarketMafioso/Windows/CraftArchitectCompanionWindow.cs`
- Update private docs:
  - `.docs/private/market-acquisition-usage.md`

**Tasks:**

- [ ] Reduce the Market Acquisition tab to active status, route summary, workbench launcher, dashboard link, and diagnostics link.
- [ ] Demote old Quick Shop and CA Companion windows to temporary/debug-only access or remove their launcher paths.
- [ ] Keep Market Acquisition Diagnostics as advanced diagnostics.
- [ ] Update private usage docs to describe the workbench as the primary client workflow.
- [ ] Confirm no dashboard claim/accept step is required for a client-created route.

**Verification commands:**

- `dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj`
- `dotnet build .\MarketMafioso.sln`
- `dotnet format .\MarketMafioso.sln --verify-no-changes`
- `.\src\MarketMafioso\tools\Deploy-DevPlugin.ps1`

## Manual End-To-End Acceptance

- [ ] Build a one-line acquisition entirely in the client.
- [ ] Build a multi-line acquisition entirely in the client.
- [ ] Use uncapped all-below-threshold and confirm stock availability reports depth, not sufficiency.
- [ ] Use capped/target quantity and confirm stock availability reports enough/partial/none.
- [ ] Adjust threshold and confirm cached listings are reanalyzed without a fresh Universalis fetch.
- [ ] Refresh stock and confirm Universalis is fetched again.
- [ ] Sync, claim, accept, prepare, and start without requiring dashboard handoff for the client-authored route.
- [ ] Stop route and resume the same prepared stops.
- [ ] Stop route and replan remaining quantities while preserving request fulfillment progress.
- [ ] Stop route and restart plan from edited draft quantities.
- [ ] Confirm dashboard automatically receives and monitors route lifecycle, line progress, purchases, completion, and failure while dashboard-authored route creation remains intact.

## Rollout Notes

- Keep the old windows until Phase 7 so in-game testing has a fallback.
- Keep all new surfaces behind the existing Market Acquisition/internal feature gate.
- Prefer focused tests per phase before full-suite verification.
- Deploy to the dev plugin only after the workbench can be opened and closed safely in game.
