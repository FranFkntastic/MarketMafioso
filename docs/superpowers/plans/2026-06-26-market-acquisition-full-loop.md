# Market Acquisition Full Loop Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convert the proven one-shot market purchase mechanisms into a full route loop that buys all safe live candidates for each confirmed world batch, advances across planned worlds, and reports terminal lifecycle state.

**Architecture:** Keep the server/dashboard as the intent and audit surface while the plugin owns live execution. Extend the existing pure purchase planner/executor and route runner with batch purchase state, then wire the ImGui controls to those services. Unsafe market-board interactions stay isolated in `DalamudMarketBoardPurchaseAdapter`.

**Tech Stack:** C# 12, .NET 8 plugin target, Dalamud API 15, FFXIVClientStructs, ASP.NET Core server, SQLite request store, xUnit.

---

### Task 1: Batch Purchase Models And Selection

**Files:**
- Create: `MarketMafioso/MarketAcquisition/MarketBoardPurchaseBatchModels.cs`
- Modify: `MarketMafioso/MarketAcquisition/MarketBoardPurchasePlanner.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketBoardPurchaseBatchPlannerTests.cs`

- [ ] Write failing tests for `MarketBoardPurchasePlanner.BuildBatch(...)`:
  - `BuildBatch_TargetQuantity_SelectsCheapestSafeCandidatesUntilTarget`
  - `BuildBatch_TargetQuantity_AllowsWholeStackOverage`
  - `BuildBatch_AllBelowThreshold_SelectsEverySafeCandidate`
  - `BuildBatch_PositiveGilCapStopsBeforeExceeded`
  - `BuildBatch_SkipsHqMismatch`
  - `BuildBatch_SkipsAboveThreshold`
- [ ] Run:
  - `dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketBoardPurchaseBatchPlannerTests" -v minimal`
  - Expected: fail because `MarketBoardPurchaseBatchModels.cs` and `BuildBatch` do not exist.
- [ ] Add `MarketBoardPurchaseBatchModels.cs` with:
  - `MarketBoardPurchaseBatch`
  - `MarketBoardPurchaseBatchRow`
  - `MarketBoardPurchaseBatchTotals`
  - row statuses `WouldBuy`, `SkippedPriceAboveThreshold`, `SkippedHqMismatch`, `SkippedBudgetExceeded`, `SkippedTargetSatisfied`
- [ ] Implement `MarketBoardPurchasePlanner.BuildBatch(MarketAcquisitionLiveDryRun dryRun, MarketAcquisitionRequestView request, uint alreadyPurchasedQuantity, uint alreadySpentGil)`.
- [ ] Re-run the focused test filter and confirm all batch planner tests pass.
- [ ] Commit:
  - `git add MarketMafioso/MarketAcquisition/MarketBoardPurchaseBatchModels.cs MarketMafioso/MarketAcquisition/MarketBoardPurchasePlanner.cs MarketMafioso.Tests/MarketAcquisition/MarketBoardPurchaseBatchPlannerTests.cs`
  - `git commit -m "Add market acquisition purchase batch planner"`

### Task 2: Purchase Attempt Audit Models

**Files:**
- Create: `MarketMafioso/MarketAcquisition/MarketBoardPurchaseAuditModels.cs`
- Modify: `MarketMafioso/MarketAcquisition/MarketBoardPurchaseSession.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketBoardPurchaseSessionTests.cs`

- [ ] Write failing tests:
  - `RecordCompletedPurchase_AddsAuditRowAndTotals`
  - `RecordSkippedPurchase_AddsClassifiedAuditRow`
  - `RecordUnknownFailure_MarksTerminal`
- [ ] Run the session test filter and confirm the new tests fail.
- [ ] Add audit models:
  - `MarketBoardPurchaseAuditRow`
  - `MarketBoardPurchaseAuditSummary`
  - `MarketBoardPurchaseTerminalStatus`
- [ ] Extend `MarketBoardPurchaseSession` to track:
  - purchased quantity,
  - spent gil,
  - audit rows,
  - terminal status.
- [ ] Re-run `MarketBoardPurchaseSessionTests`.
- [ ] Commit:
  - `git add MarketMafioso/MarketAcquisition/MarketBoardPurchaseAuditModels.cs MarketMafioso/MarketAcquisition/MarketBoardPurchaseSession.cs MarketMafioso.Tests/MarketAcquisition/MarketBoardPurchaseSessionTests.cs`
  - `git commit -m "Track market acquisition purchase audit state"`

### Task 3: Batch Purchase Executor

**Files:**
- Create: `MarketMafioso/MarketAcquisition/MarketBoardPurchaseBatchExecutor.cs`
- Modify: `MarketMafioso/MarketAcquisition/MarketBoardPurchaseExecutor.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketBoardPurchaseBatchExecutorTests.cs`

- [ ] Write failing tests:
  - `ExecuteNext_RevalidatesCandidateBeforeAdapterCall`
  - `ExecuteNext_RebuildsCandidatesAfterCompletedPurchase`
  - `ExecuteNext_StopsOnUnknownFailure`
  - `ExecuteNext_CompletesWorldWhenNoSafeCandidatesRemain`
  - `ExecuteNext_StopsWhenGilCapWouldBeExceeded`
- [ ] Run the batch executor test filter and confirm failure.
- [ ] Implement `MarketBoardPurchaseBatchExecutor` as a pure coordinator around:
  - current request,
  - current world,
  - current live dry-run,
  - fresh listing reads,
  - `MarketBoardPurchaseExecutor`.
- [ ] Keep adapter calls one-at-a-time. The executor returns after each attempt so the framework loop can wait for confirmation/listing removal.
- [ ] Re-run batch executor tests.
- [ ] Commit:
  - `git add MarketMafioso/MarketAcquisition/MarketBoardPurchaseBatchExecutor.cs MarketMafioso/MarketAcquisition/MarketBoardPurchaseExecutor.cs MarketMafioso.Tests/MarketAcquisition/MarketBoardPurchaseBatchExecutorTests.cs`
  - `git commit -m "Add guarded purchase batch executor"`

### Task 4: Route Runner Purchase Progress

**Files:**
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionRouteRunner.cs`
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionGuidedRouteSession.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteRunnerTests.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionGuidedRouteSessionTests.cs`

- [ ] Write failing tests:
  - `RecordWorldPurchaseBatch_AdvancesStopWhenTargetSatisfied`
  - `RecordWorldPurchaseBatch_AdvancesStopWhenWorldUnderProcured`
  - `RecordWorldPurchaseBatch_CompletesRouteWhenFinalStopEnds`
  - `RecordWorldPurchaseBatch_FailsRouteOnUnknownPurchaseResult`
- [ ] Run route/session test filters and confirm failure.
- [ ] Add route runner methods:
  - `BeginWorldBatchConfirmation(...)`
  - `RecordWorldBatchConfirmed(...)`
  - `RecordWorldPurchaseProgress(...)`
  - `RecordWorldPurchaseBatchComplete(...)`
  - `RecordWorldPurchaseBatchFailed(...)`
- [ ] Store per-stop live purchased quantity and live spent gil separately from planned dry-run quantities.
- [ ] Re-run route/session tests.
- [ ] Commit:
  - `git add MarketMafioso/MarketAcquisition/MarketAcquisitionRouteRunner.cs MarketMafioso/MarketAcquisition/MarketAcquisitionGuidedRouteSession.cs MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteRunnerTests.cs MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionGuidedRouteSessionTests.cs`
  - `git commit -m "Integrate purchase progress into acquisition routes"`

### Task 5: Server Progress And Terminal Audit

**Files:**
- Modify: `MarketMafioso.Server/MarketAcquisitionModels.cs`
- Modify: `MarketMafioso.Server/MarketAcquisitionRequestStore.cs`
- Modify: `MarketMafioso.Server/Program.cs`
- Test: `MarketMafioso.Server.Tests/MarketAcquisitionRequestEndpointTests.cs`

- [ ] Write failing server tests:
  - `Progress_WithPurchaseAuditRows_PersistsLatestEvent`
  - `Complete_WithPurchaseSummary_MarksTerminal`
  - `Fail_WithPurchaseFailure_MarksTerminal`
  - `Progress_DoesNotStoreSecrets`
- [ ] Run:
  - `dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisitionRequestEndpointTests" -v minimal`
- [ ] Add purchase audit fields to lifecycle request models.
- [ ] Persist purchase summary/audit JSON in existing acquisition lifecycle event storage.
- [ ] Ensure terminal states are immutable and duplicate idempotent terminal updates replay safely.
- [ ] Re-run server tests.
- [ ] Commit:
  - `git add MarketMafioso.Server/MarketAcquisitionModels.cs MarketMafioso.Server/MarketAcquisitionRequestStore.cs MarketMafioso.Server/Program.cs MarketMafioso.Server.Tests/MarketAcquisitionRequestEndpointTests.cs`
  - `git commit -m "Persist market acquisition purchase audit progress"`

### Task 6: Plugin World-Batch Confirmation UI

**Files:**
- Modify: `MarketMafioso/Windows/MainWindow.cs`
- Modify: `MarketMafioso/Windows/MarketAcquisitionDiagnosticsWindow.cs`

- [ ] Add `AwaitingBatchConfirmation` UI state in the Market Acquisition tab.
- [ ] Show item, world, data center, HQ policy, candidate count, cheapest unit, highest unit, estimated spend, remaining target quantity, and remaining gil cap.
- [ ] Add `Confirm This World Batch` and `Reconcile Again` buttons.
- [ ] Disable confirmation when live listings are stale, current world mismatches, current item mismatches, no candidate exists, or max unit price is missing.
- [ ] Move verbose candidate rows and purchase audit rows to `MarketAcquisitionDiagnosticsWindow`.
- [ ] Run plugin build:
  - `dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug`
- [ ] Commit:
  - `git add MarketMafioso/Windows/MainWindow.cs MarketMafioso/Windows/MarketAcquisitionDiagnosticsWindow.cs`
  - `git commit -m "Add market acquisition world batch confirmation UI"`

### Task 7: Plugin Batch Execution Loop

**Files:**
- Modify: `MarketMafioso/Windows/MainWindow.cs`
- Modify: `MarketMafioso/MarketAcquisition/DalamudMarketBoardPurchaseAdapter.cs`
- Modify: `MarketMafioso/MarketAcquisition/MarketBoardPurchaseBatchExecutor.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketBoardPurchaseBatchExecutorTests.cs`

- [ ] Add framework monitor path for active purchase batch sessions.
- [ ] After confirmation, execute one candidate.
- [ ] Wait for confirmation prompt and listing removal using existing `MarketBoardPurchaseSession`.
- [ ] Re-read live listings after each completed purchase.
- [ ] Rebuild safe candidates after each read.
- [ ] Continue until current world is complete, under-procured, budget exhausted, or failed.
- [ ] Stop route on unknown purchase result.
- [ ] Re-run focused purchase tests.
- [ ] Run plugin build.
- [ ] Commit:
  - `git add MarketMafioso/Windows/MainWindow.cs MarketMafioso/MarketAcquisition/DalamudMarketBoardPurchaseAdapter.cs MarketMafioso/MarketAcquisition/MarketBoardPurchaseBatchExecutor.cs MarketMafioso.Tests/MarketAcquisition/MarketBoardPurchaseBatchExecutorTests.cs`
  - `git commit -m "Run confirmed market acquisition purchase batches"`

### Task 8: Route Continuation And Reranking

**Files:**
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionGuidedRouteSession.cs`
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionRouteRunner.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionGuidedRouteSessionTests.cs`

- [ ] Write failing tests:
  - `RerankRemainingStops_PreservesCurrentDataCenterBeforeCrossing`
  - `RerankRemainingStops_PrioritizesCheaperRemainingLiveOpportunity`
  - `RerankRemainingStops_DoesNotReopenCompletedStops`
- [ ] Implement reranking over remaining pending stops after each world batch.
- [ ] Keep completed/failed/skipped stop order immutable for audit display.
- [ ] Re-run guided route session tests.
- [ ] Commit:
  - `git add MarketMafioso/MarketAcquisition/MarketAcquisitionGuidedRouteSession.cs MarketMafioso/MarketAcquisition/MarketAcquisitionRouteRunner.cs MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionGuidedRouteSessionTests.cs`
  - `git commit -m "Rerank remaining acquisition route stops after live batches"`

### Task 9: Hardening And Recovery

**Files:**
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionClaimPersistence.cs`
- Modify: `MarketMafioso/MarketAcquisition/MarketAcquisitionRouteRunner.cs`
- Modify: `MarketMafioso/Windows/MainWindow.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionClaimPersistenceTests.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteRunnerTests.cs`

- [ ] Add tests for plugin reload during accepted-but-not-running state.
- [ ] Add tests for plugin reload during running/purchasing state producing recoverable status.
- [ ] Persist accepted request identity and claim token, but not transient UI automation step.
- [ ] On plugin load, show recovered request state and require explicit restart or dashboard recovery for stale running routes.
- [ ] Add visible statuses for stale server claim, rejected progress, terminal-state conflict, and auth failure.
- [ ] Re-run claim persistence and route runner tests.
- [ ] Commit:
  - `git add MarketMafioso/MarketAcquisition/MarketAcquisitionClaimPersistence.cs MarketMafioso/MarketAcquisition/MarketAcquisitionRouteRunner.cs MarketMafioso/Windows/MainWindow.cs MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionClaimPersistenceTests.cs MarketMafioso.Tests/MarketAcquisition/MarketAcquisitionRouteRunnerTests.cs`
  - `git commit -m "Harden market acquisition route recovery"`

### Task 10: Final Verification And Deployment

**Files:**
- Modify: `docs/design/2026-06-25-market-acquisition-roadmap.md`
- Modify: `docs/superpowers/specs/2026-06-26-market-acquisition-full-loop-design.md`

- [ ] Update the roadmap to mark the full-loop implementation status.
- [ ] Run focused plugin tests:
  - `dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketBoardPurchaseBatch|FullyQualifiedName~MarketBoardPurchaseSession|FullyQualifiedName~MarketAcquisitionRouteRunner|FullyQualifiedName~MarketAcquisitionGuidedRouteSession|FullyQualifiedName~MarketAcquisitionClaimPersistence" -v minimal`
- [ ] Run focused server tests:
  - `dotnet test "MarketMafioso.Server.Tests/MarketMafioso.Server.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisitionRequestEndpointTests" -v minimal`
- [ ] Run plugin build:
  - `dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug`
- [ ] Run format verification:
  - `dotnet format "MarketMafioso.sln" --verify-no-changes`
- [ ] Deploy plugin:
  - `MarketMafioso/tools/Deploy-DevPlugin.ps1`
- [ ] Commit final docs:
  - `git add docs/design/2026-06-25-market-acquisition-roadmap.md docs/superpowers/specs/2026-06-26-market-acquisition-full-loop-design.md`
  - `git commit -m "Document market acquisition full loop completion"`
- [ ] Push `local-dev`.
- [ ] If server files changed, confirm the GitHub Actions deploy for `Deploy MarketMafioso Dev Receiver to VPS` succeeds and smoke-check `/api/marketmafioso/health`.

## Execution Notes

- Do not run the full test suite after every tiny change. Use focused test filters per task, then broader verification only at the end.
- Do not deploy from a branch accidentally. Use `MarketMafioso/tools/Deploy-DevPlugin.ps1` and verify the reported target DLL hash.
- Do not let dashboard cancel/resend become remote control for active purchases.
- Do not continue route execution after `UnknownFailure`, ambiguous UI, wrong item, wrong world, inventory full, or insufficient gil.
- Keep verbose diagnostics out of the main plugin control surface.
