# Market Purchase Confirmation State Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make market-board purchase automation distinguish between submitting a confirmation prompt and proving the purchase completed, with diagnostics precise enough to explain failures.

**Architecture:** Keep listing selection, confirmation submission, listing-removal verification, audit reporting, and route advancement as separate transitions. A purchase only updates bought/spent totals after the observed market-board state proves the candidate listing disappeared, the result window closed, or the listing set became empty.

**Tech Stack:** C# 12, Dalamud addon interop, xUnit tests in `MarketMafioso.Tests`, focused plugin deploy through `MarketMafioso/tools/Deploy-DevPlugin.ps1`.

---

## Current Execution Status

Last updated: 2026-06-29

- Completed: purchase-session state now exposes `MarketBoardPurchaseSessionPhase`, separating waiting for confirmation, waiting for listing removal, completed, and failed outcomes.
- Completed: session tests cover confirmation submission and listing-removal state transitions.
- Completed: timeout wording now identifies the guarded listing and treats unresolved removal as an unknown outcome instead of implying successful purchase completion.
- Partial: richer confirmation prompt fields and full route-log snapshots are still uneven. Future diagnostics should make prompt text, addon name, selected listing id, and revalidation result visible in the same snapshot.
- Pending live validation: run a route that purchases multiple listings from one result page and verify the route advances only after listing removal or accepted equivalent state.

---

## Evidence From `route-20260629-162004.log`

- The first Electrum Ingot listing was purchased and audited: listing `4011019252972417`, quantity `4`, total `3,200`.
- The second candidate was listing `4011019250599116`, quantity `1`, unit `996`.
- The second candidate stayed present for 69 polling cycles, so the purchase was not proven complete.
- The route state still said confirmation was accepted, which means the confirmation boundary is overconfident.

## Current Risk

`ConfirmationAccepted` currently means "MarketMafioso clicked yes on a purchase-looking SelectYesno prompt." Future code treats that as if the game accepted the intended purchase. That can break:

- **Purchase totals:** totals are safe today because they update only after listing removal, but misleading status text says the purchase was accepted.
- **Next listing selection:** the route can stall instead of selecting the next safe listing because it is waiting for a listing that was never bought.
- **Multi-item routing:** an unproven confirmation blocks the current item, so the route never reaches later items on the same world.
- **World completion:** a false in-flight purchase prevents the world batch from closing cleanly.
- **Diagnostics:** the log records after-confirmation listing reads but not the confirmation prompt itself, so future failures are hard to distinguish from listing-reader failures.

## Files

- Modify: `MarketMafioso/MarketAcquisition/MarketBoardPurchaseModels.cs`
  - Add prompt/confirmation diagnostic fields to `MarketBoardPurchaseResult`.
- Modify: `MarketMafioso/MarketAcquisition/DalamudMarketBoardPurchaseAdapter.cs`
  - Rename semantic status from `ConfirmationAccepted` to `ConfirmationSubmitted`.
  - Return sanitized prompt text and candidate details in `MarketBoardPurchaseResult`.
  - Keep prompt detection conservative; do not claim completion here.
- Modify: `MarketMafioso/MarketAcquisition/MarketBoardPurchaseSession.cs`
  - Transition from `WaitingForConfirmation` to `WaitingForListingRemoval` only on `ConfirmationSubmitted`.
  - Reword messages to say the confirmation was submitted, not that the purchase was accepted.
  - Include revalidation status/message in fresh-read snapshots.
  - Treat timeout as an unknown purchase outcome, not an implied failed purchase.
- Modify: `MarketMafioso/Windows/MainWindow.cs`
  - Record a diagnostic automation snapshot when confirmation is submitted, pending, or unexpected.
  - Update UI status coloring to recognize `ConfirmationSubmitted`.
  - Keep totals and route advancement gated behind `Completed`.
- Modify: `MarketMafioso.Tests/MarketAcquisition/MarketBoardPurchaseSessionTests.cs`
  - Update old `ConfirmationAccepted` tests to `ConfirmationSubmitted`.
  - Add a test for timeout wording when the listing remains present.
- Modify: `MarketMafioso.Tests/MarketAcquisition/DalamudMarketBoardPurchaseAdapterTests.cs`
  - Add or update prompt-classification tests if the adapter has testable seams.

## Task 1: Rename the Misleading Confirmation Status

- [ ] **Step 1: Update session tests first**

Change session tests that currently construct:

```csharp
new MarketBoardPurchaseResult
{
    Status = "ConfirmationAccepted",
    Message = "Accepted.",
    Candidate = candidate,
}
```

to:

```csharp
new MarketBoardPurchaseResult
{
    Status = "ConfirmationSubmitted",
    Message = "Submitted the market-board purchase confirmation.",
    Candidate = candidate,
}
```

- [ ] **Step 2: Run the focused session tests and confirm failure**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketBoardPurchaseSessionTests" -v minimal
```

Expected before implementation: tests fail where production still expects `ConfirmationAccepted`.

- [ ] **Step 3: Update `MarketBoardPurchaseSession.RecordConfirmationAttempt`**

Replace the accepted-status branch with:

```csharp
if (result.Status.Equals("ConfirmationSubmitted", StringComparison.OrdinalIgnoreCase))
{
    return this with
    {
        Status = "WaitingForListingRemoval",
        Message = "Purchase confirmation submitted; waiting for the guarded listing to disappear.",
        DeadlineUtc = nowUtc.Add(listingRemovalWatchdog),
    };
}
```

- [ ] **Step 4: Run the focused session tests**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketBoardPurchaseSessionTests" -v minimal
```

Expected: session tests pass after references are updated.

## Task 2: Make Confirmation Attempts Observable

- [ ] **Step 1: Extend `MarketBoardPurchaseResult`**

Add fields:

```csharp
public string? ConfirmationPromptText { get; init; }
public string? ConfirmationAddonName { get; init; }
```

- [ ] **Step 2: Update `TryConfirmPendingPurchase`**

When no prompt is visible, keep returning `ConfirmationPending`.

When a prompt is visible and looks purchase-related, submit it but return:

```csharp
return new MarketBoardPurchaseResult
{
    Status = "ConfirmationSubmitted",
    Message = $"Submitted market-board purchase confirmation: {text}",
    Candidate = candidate,
    ConfirmationPromptText = text,
    ConfirmationAddonName = SelectYesNoAddon,
};
```

When a prompt is visible but unexpected, preserve the prompt:

```csharp
return Fail(
    "UnexpectedConfirmation",
    $"A SelectYesno prompt appeared, but it did not look like a market-board purchase prompt: {text}",
    candidate) with
{
    ConfirmationPromptText = text,
    ConfirmationAddonName = SelectYesNoAddon,
};
```

- [ ] **Step 3: Add a confirmation snapshot in `MainWindow.MonitorMarketBoardPurchase`**

Immediately after `TryConfirmPendingPurchase`, record a `MarketBoardAutomationSnapshot` with:

```csharp
step: "BuyListing"
phase: "Confirmation"
expected: "PurchasePrompt"
observed: marketBoardPurchaseResult.Status
outcome: ConfirmationSubmitted => InProgress, ConfirmationPending => InProgress, UnexpectedConfirmation => Fatal
details: candidate listing id, retainer id, quantity, unit price, total gil, prompt text
```

- [ ] **Step 4: Run focused acquisition tests**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisition|FullyQualifiedName~MarketBoard" -v minimal
```

Expected: all focused tests pass.

## Task 3: Make Listing-Removal Diagnostics Explain the Stall

- [ ] **Step 1: Add revalidation details to `CreateFreshReadSnapshot`**

Add:

```csharp
var revalidation = freshRead.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase)
    ? MarketBoardPurchasePlanner.RevalidateCandidate(Candidate, freshRead)
    : MarketBoardPurchaseRevalidation.Fail(freshRead.Status, freshRead.Message);
```

Include:

```csharp
["revalidationStatus"] = revalidation.Status,
["revalidationMessage"] = revalidation.Message,
["candidateStillPresent"] = (!revalidation.Status.Equals("ListingMissing", StringComparison.OrdinalIgnoreCase)).ToString(),
```

- [ ] **Step 2: Reword timeout status**

Change timeout status/message to:

```csharp
Status = "PurchaseOutcomeUnknown",
Message = $"Purchase confirmation was submitted, but the guarded listing is still present or unreadable: {revalidation.Message}",
```

- [ ] **Step 3: Update tests**

Add a focused test asserting that a still-present listing after the deadline returns `PurchaseOutcomeUnknown`, not `ListingRemovalTimeout`.

- [ ] **Step 4: Run session tests**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketBoardPurchaseSessionTests" -v minimal
```

Expected: session tests pass.

## Task 4: Audit Future Process Effects

- [ ] **Step 1: UI status handling**

Update `MainWindow` status coloring:

```csharp
marketBoardPurchaseResult.Status is "PurchaseSelectionSent" or "ConfirmationSubmitted" ? ColHeader : ColError
```

- [ ] **Step 2: Route advancement**

Confirm `activeWorldPurchasedQuantity`, `activeWorldSpentGil`, `activeLinePurchasedQuantity`, and `activeLineSpentGil` are updated only inside the `session.Status == "Completed"` block.

- [ ] **Step 3: Dashboard lifecycle**

Confirm no dashboard lifecycle endpoint is called during `ConfirmationSubmitted`; dashboard progress should still be sent only through `ReportConfirmedPurchase` or route stop/failure/completion paths.

- [ ] **Step 4: Multi-item routing**

Confirm `BeginNextWorldPurchase()` is called only after `Completed`, `NoCandidate`, result-window closure, or no listings. Do not let `ConfirmationSubmitted` advance to the next item.

## Task 5: Deploy for Live Verification

- [ ] **Step 1: Run focused tests**

Run:

```powershell
dotnet test "MarketMafioso.Tests/MarketMafioso.Tests.csproj" -c Debug --filter "FullyQualifiedName~MarketAcquisition|FullyQualifiedName~MarketBoard" -v minimal
```

- [ ] **Step 2: Deploy the dev plugin**

Run:

```powershell
.\MarketMafioso\tools\Deploy-DevPlugin.ps1
```

Expected: script reports matching source and target hashes plus the visible manifest version.

- [ ] **Step 3: Live-test the same failure shape**

Use a cheap listing. Expected diagnostics:

- confirmation snapshot appears before listing-removal snapshots;
- if the prompt was submitted but the row remains, the route says `PurchaseOutcomeUnknown`;
- no bought/spent totals change unless a purchase audit is recorded;
- if the row disappears, purchase totals update and the next candidate begins.

## Self-Review

- The plan addresses the observed false premise: confirmation submission is not proof of purchase.
- The plan keeps purchase totals gated behind observed completion, so it should not create accidental overcounting.
- The plan improves diagnostics before changing recovery behavior, which avoids hiding a still-unknown UI automation problem.
- The plan deliberately does not auto-retry unknown purchase outcomes yet. That belongs after one instrumented live run proves whether the prompt was wrong, stale, or unprocessed.
