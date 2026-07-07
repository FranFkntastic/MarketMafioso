# Retainer Restock V1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a standalone `Restock` top-level tab that builds a manual retainer restock plan, previews cached retainer coverage, and runs guarded retainer withdrawal through the existing automation path.

**Architecture:** Introduce a generic `MarketMafioso.RetainerRestock` namespace for plan rows, preview lines, planning, status text, and workshop adaptation. Keep low-level retainer UI automation in the existing workshop runner for this slice, but add a generic `StartRestockAsync(...)` entrypoint so Workshop Logistics and the new tab share one execution path.

**Tech Stack:** C#/.NET, Dalamud ImGui, existing inventory scanner and retainer cache configuration, xUnit.

---

### Task 1: Generic Planner And Summaries

**Files:**
- Create: `src/MarketMafioso/RetainerRestock/RetainerRestockModels.cs`
- Create: `src/MarketMafioso/RetainerRestock/RetainerRestockPlanner.cs`
- Create: `src/MarketMafioso/RetainerRestock/RetainerRestockCompletionSummary.cs`
- Test: `tests/MarketMafioso.Tests/RetainerRestock/RetainerRestockPlannerTests.cs`
- Test: `tests/MarketMafioso.Tests/RetainerRestock/RetainerRestockCompletionSummaryTests.cs`

- [ ] Write failing tests for desired-minus-player quantity, disabled rows, candidate ranking, missing quantity, and completion summary text.
- [ ] Run the focused tests and verify they fail because the new namespace does not exist.
- [ ] Implement the generic models, planner, and completion summary.
- [ ] Run the focused tests and verify they pass.

### Task 2: Generic Runner Entry Point

**Files:**
- Modify: `src/MarketMafioso/WorkshopPrep/WorkshopRetainerRestockService.cs`
- Test: `tests/MarketMafioso.Tests/WorkshopPrep/WorkshopRetainerRestockCompletionTests.cs`

- [ ] Write a failing test proving generic completion summaries use non-workshop wording.
- [ ] Add `StartRestockAsync(IReadOnlyList<RetainerRestockPlanLine>)` and keep `StartAsync(IReadOnlyList<WorkshopMaterialAvailability>)` as a workshop adapter.
- [ ] Update run status text so generic runs say `Retainer restock` while workshop runs keep current workshop wording.
- [ ] Run focused retainer restock tests and verify they pass.

### Task 3: Persist Active Manual Rows

**Files:**
- Modify: `src/MarketMafioso/Configuration.cs`

- [ ] Add `List<RetainerRestockPlanItem> RetainerRestockPlanItems`.
- [ ] Use the existing plugin config persistence path; no migration is needed because the property defaults to an empty list.

### Task 4: Top-Level Restock Tab

**Files:**
- Modify: `src/MarketMafioso/Windows/MainWindow.cs`

- [ ] Add `using MarketMafioso.RetainerRestock`.
- [ ] Load item autocomplete options from the existing Acquisition Workbench item catalog helper.
- [ ] Add the `Restock` tab between `Market Acquisition`/`Diagnostics` and `Settings`.
- [ ] Draw plan editor, preview table, cache controls, and run controls.
- [ ] Wire `Restock From Retainers` to `workshopRetainerRestock.StartRestockAsync(GetRetainerRestockPlan().Lines)`.

### Task 5: Verification

**Files:**
- No source changes unless verification exposes failures.

- [ ] Run focused tests: `dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --filter "RetainerRestock|WorkshopRetainerRestock" --no-restore`
- [ ] Run plugin tests if focused tests pass: `dotnet test .\tests\MarketMafioso.Tests\MarketMafioso.Tests.csproj --no-restore`
- [ ] Run format check if tests pass: `dotnet format .\MarketMafioso.sln --verify-no-changes`

### Task 6: Owner-Scoped Retainer Cache And Upstream Payloads

**Files:**
- Modify: `src/MarketMafioso/Configuration.cs`
- Modify: `src/MarketMafioso/RetainerCacheManager.cs`
- Modify: `src/MarketMafioso/RetainerRestock/RetainerRestockModels.cs`
- Modify: `src/MarketMafioso/RetainerRestock/RetainerRestockPlanner.cs`
- Modify: `src/MarketMafioso/WorkshopPrep/WorkshopMaterialAvailabilityService.cs`
- Modify: `src/MarketMafioso/HttpReporter.cs`
- Modify: `src/MarketMafioso/InventoryPayload.cs`
- Modify: `src/MarketMafioso.Server/Models.cs`
- Modify: `src/MarketMafioso.Server/InventorySnapshotView*.cs`
- Modify: `src/MarketMafioso.Server/InventoryBrowser*.cs`
- Modify: `src/MarketMafioso.Dashboard/Models/DashboardModels.cs`
- Modify: `src/MarketMafioso.Dashboard/Pages/Inventory.razor`
- Test: `tests/MarketMafioso.Tests/RetainerRestock/RetainerRestockPlannerTests.cs`
- Test: `tests/MarketMafioso.Tests/WorkshopPrep/WorkshopMaterialAvailabilityServiceTests.cs`
- Test: `tests/MarketMafioso.Tests/HttpReporterRetainerScopeTests.cs`
- Test: `tests/MarketMafioso.Server.Tests/InventorySnapshotViewBuilderTests.cs`
- Test: `tests/MarketMafioso.Server.Tests/InventoryReportViewEndpointTests.cs`
- Test: `tests/MarketMafioso.Server.Tests/InventoryReportStoreSqliteTests.cs`

- [x] Stamp cached retainers with `OwnerCharacterName` and `OwnerHomeWorld` when a retainer window closes.
- [x] Add a strict `RetainerOwnerScope` path for Restock and Workshop availability; legacy unscoped cache entries are excluded when a current character scope is available.
- [x] Filter HTTP inventory reporter retainer payloads by the current character scope.
- [x] Carry retainer owner fields through plugin payloads, receiver models, inventory snapshot views, browser scopes, and dashboard models.
- [x] Preserve existing normalized SQLite storage by reconstructing retainer owner fields from the snapshot character/home world on readback.
