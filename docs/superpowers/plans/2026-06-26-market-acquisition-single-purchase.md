# Market Acquisition Single Purchase Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the first live purchase slice: execute exactly one guarded purchase candidate on the current world, then stop and report the result.

**Architecture:** Reuse the existing live dry-run as the purchase candidate source. Add a pure selector/revalidator that can be tested without Dalamud, then isolate unsafe market-board activation/confirmation behind a small adapter. The first UI surface is an explicit one-shot button that only appears after a ready live dry-run.

**Tech Stack:** C# 12, Dalamud API 15, FFXIVClientStructs, xUnit.

---

### Task 1: Candidate Revalidation

**Files:**
- Create: `MarketMafioso/MarketAcquisition/MarketBoardPurchaseModels.cs`
- Create: `MarketMafioso/MarketAcquisition/MarketBoardPurchasePlanner.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketBoardPurchasePlannerTests.cs`

- [x] Write failing tests for selecting the first `WouldBuy` row and revalidating it against a fresh live listing read.
- [x] Run `dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketBoardPurchasePlannerTests" -v minimal` and confirm the tests fail because the planner does not exist.
- [x] Implement models and planner:
  - `MarketBoardPurchaseCandidate`
  - `MarketBoardPurchaseRevalidation`
  - `MarketBoardPurchaseResult`
  - `MarketBoardPurchasePlanner.SelectFirstCandidate(...)`
  - `MarketBoardPurchasePlanner.RevalidateCandidate(...)`
- [x] Re-run the focused tests and confirm they pass.

### Task 2: Unsafe Purchase Adapter Boundary

**Files:**
- Create: `MarketMafioso/MarketAcquisition/MarketBoardPurchaseExecutor.cs`
- Test: `MarketMafioso.Tests/MarketAcquisition/MarketBoardPurchaseExecutorTests.cs`

- [x] Write failing tests showing the executor:
  - refuses when no candidate exists,
  - refuses when the fresh live read does not match the candidate,
  - calls the adapter once for the validated candidate,
  - stops after one attempt.
- [x] Run the executor test filter and confirm failure.
- [x] Implement `IMarketBoardPurchaseAdapter` and `MarketBoardPurchaseExecutor`.
- [x] Re-run focused tests and confirm they pass.

### Task 3: Dalamud Adapter And UI Surface

**Files:**
- Create: `MarketMafioso/MarketAcquisition/DalamudMarketBoardPurchaseAdapter.cs`
- Modify: `MarketMafioso/Windows/MainWindow.cs`
- No `Plugin.cs` change was needed; `MainWindow` owns this explicit UI action.

- [x] Add a guarded one-shot `Buy First Safe Listing` button beside the live dry-run summary.
- [x] Wire it to the executor using the current `marketAcquisitionLiveDryRun` and a fresh `MarketBoardListingReader.ReadCurrentListings(...)`.
- [x] Adapter begins with a guarded current-row action: locate the matching current listing row, prime `LastPurchasedMarketboardItem`, send one listing callback, and wait for the purchase confirmation prompt. Do not loop purchases in this slice.
- [x] Preserve diagnostics: status message includes candidate quantity, unit price, total gil, and result classification; adapter logs candidate listing and retainer identities.

### Task 4: Verification

**Files:**
- No new files.

- [x] Run focused purchase tests.
- [x] Run `dotnet build "MarketMafioso/MarketMafioso.csproj" -c Debug`.
- [x] Run `dotnet format "MarketMafioso.sln" --verify-no-changes`.
- [x] Deploy with `MarketMafioso/tools/Deploy-DevPlugin.ps1` only after the build succeeds.

Deployment note: `Deploy-DevPlugin.ps1` verifies the AppData dev-plugin target and the project Debug build also syncs the dedicated `_deployed\MarketMafioso` folder. Use the final deploy script output as the source of truth for the visible manifest version and DLL hash.

### Self-Review

- The plan deliberately buys at most one listing.
- It keeps the unsafe client interaction behind one adapter.
- It does not add multi-world batch purchasing yet.
- It allows favorable live drift only through the existing dry-run candidate selection and a fresh pre-action revalidation.
